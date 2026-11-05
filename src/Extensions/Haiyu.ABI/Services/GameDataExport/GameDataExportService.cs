using System.Runtime.Versioning;
using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Security;

namespace Haiyu.ABI.Services.GameDataExport;

/// <summary>
/// 游戏数据导出 —— 捕获 → 解密 → 导出。
///
/// <para>
/// <b>范围</b>：只做三件事。不维护连接（无窗口/重传/ACK/心跳/超时），
/// 不处理明文业务帧，不做 zlib 解压。
/// </para>
///
/// <para>
/// <b>为什么需要会话密钥</b>：<c>112 ProtoKeyResponse</c> 本身是明文，
/// 用它里面的 RSA 密文解出 32 字节会话密钥，再去解其他帧。
/// </para>
/// <para>
/// ★ Windows-only：Npcap 抓包 / 凭据管理器 / DPAPI 全是 Win32 依赖。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameDataExportService
    : IPrivilegedService<GameDataExportRequest, GameDataExportProgress, GameDataExportResult>
{
    /// <summary>
    /// 密钥逻辑名（密钥管理方案 §1：来源与使用解耦——代码只出现逻辑名，不出现密钥）。
    /// 对应：环境变量 <c>HAIYU_SECRET_WAVES_KCP</c> / 凭据管理器
    /// <c>Haiyu/Secret/waves.kcp</c> / DPAPI 文件 <c>waves.kcp.dpapi</c>。
    /// </summary>
    private const string SecretId = "waves.kcp";

    /// <summary>DPAPI 加密文件路径（密钥管理方案 §1 第 ③ 层）。</summary>
    private static string DpapiPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Waves",
            "waves.kcp.dpapi"
        );

    /// <summary>裸消息帧标志（native sub_1412FE710 的 case 0xEC）。</summary>
    private const byte RawFrameTag = 0xEC;

    /// <summary>
    /// 会话密钥装载器。
    /// 作为字段而非局部变量，是因为抓包循环里需要增量解析（<see cref="TryAdvance"/>）。
    /// </summary>
    private readonly SessionKeyLoader _keyLoader = new();

    public PrivilegedServiceContract<
        GameDataExportRequest,
        GameDataExportProgress,
        GameDataExportResult
    > Contract => ABIRuntime.Contract.GameDataExportContract;

    public async ValueTask<GameDataExportResult> ExecuteAsync(
        GameDataExportRequest request,
        IProgress<GameDataExportProgress> progress,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var captured = 0;
        var decoded = 0;
        var normalItems = new List<GameNormalItem>();
        var weapons = new List<GameWeaponItem>();
        var phantoms = new List<GamePhantomItem>();
        var validTimeItems = new List<GameValidTimeItem>();

        Report(progress, GameDataExportStage.Capturing, 0, "正在准备网卡");

        using var capture = new NpcapUdpCapture();
        // 0xEC 裸消息帧（非 KCP 通道）收集到此处，与 KCP 流一起参与帧解析
        var rawMessages = new List<byte[]>();

        try
        {
            LoadPrivateKey(_keyLoader);
            capture.Start();

            Report(progress, GameDataExportStage.Capturing, 5, "正在等待游戏连接");

            // ── 抓包循环：退出条件不是「取消」，而是「数据够了」或「调用方取消」──
            //   ★ 关键：cancel 只表示「停止抓包」，不表示「丢弃结果」。
            //     之前把 cancel 当异常向上抛，导致抓到数据也白抓。
            var incoming = await PumpAsync(
                capture,
                rawMessages,
                () => captured++,
                request.Scope,
                progress,
                cancellationToken
            ).ConfigureAwait(false);

            Report(progress, GameDataExportStage.Decrypting, 60, "正在解密数据");

            // 只依赖入站方向：112 与背包数据都在「服务器→客户端」
            var incomingStream = incoming.Build();

            // 先解 112 拿密钥（它一定在入站方向）
            // 裸消息帧也要参与：112 可能不走 KCP
            foreach (var raw in rawMessages)
                TryResolveFromFrameBytes(raw, _keyLoader);

            ResolveSessionKey(incomingStream, _keyLoader);

            if (_keyLoader.SessionKey is null)
            {
                return Failure(
                    "未能解出会话密钥。请确认抓包覆盖了登录阶段（从游戏启动前开始抓）。",
                    captured,
                    decoded,
                    null
                );
            }

            // 再按 msgId 收集背包数据
            decoded = CollectInventory(
                incomingStream,
                _keyLoader.SessionKey!,
                request.Scope,
                normalItems,
                weapons,
                phantoms,
                validTimeItems
            );

            Report(progress, GameDataExportStage.Exporting, 85, "正在生成导出文件");

            var inventory = new GameInventoryData(
                normalItems,
                weapons,
                phantoms,
                validTimeItems
            );

            var sessionKeyHex = Convert.ToHexString(_keyLoader.SessionKey!).ToLowerInvariant();
            var result = new GameDataExportResult(
                true,
                $"导出完成：普通道具 {normalItems.Count}、武器 {weapons.Count}、声骸 {phantoms.Count}、限时道具 {validTimeItems.Count}",
                inventory,
                null,
                captured,
                decoded,
                sessionKeyHex
            );

            var path = InventoryJsonWriter.TryWrite(result, DateTimeOffset.Now);

            Report(
                progress,
                GameDataExportStage.Completed,
                100,
                path is null ? "导出完成（但 JSON 落盘失败）" : $"导出完成：{path}"
            );

            return path is null
                ? result
                : result with
                {
                    OutputJsonPath = path,
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(progress, GameDataExportStage.Failed, 100, ex.Message);
            return Failure(ex.Message, captured, decoded, null);
        }
    }

    /// <summary>
    /// 抓包主循环。
    ///
    /// <para>
    /// <b>退出条件（任一满足）</b>：
    /// <list type="number">
    ///   <item>已拿到会话密钥 + 所有目标 msgId 都收到 → 主动停止（正常完成）</item>
    ///   <item>调用方取消 → 停止，但<b>不丢弃已抓到的数据</b></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ★ 取消语义：<c>cancel</c> 只表示「停止抓包」，不表示「抛异常丢弃结果」。
    ///   之前把 <c>OperationCanceledException</c> 向上抛，导致抓到数据也白抓。
    ///   会话密钥一次登录只下发一次，抓到了就必须处理完。
    /// </para>
    /// <para>
    /// ★ 完成判定：只在<b>已解出会话密钥</b>且<b>所有目标 msgId 都收到</b>时才提前退出。
    ///   没拿到密钥前绝不退出——否则会漏掉后续重发的 112。
    /// </para>
    /// </summary>
    /// <summary>
    /// 从多条连接流里挑出「最可能承载目标数据」的那条。
    ///
    /// 优先级：已收齐全部目标 msgId 且解出密钥 → 段数最多。
    /// 多连接并存时（心跳 / 场景 / 数据），只有其中一条会下发背包数据；
    /// 段数最多者通常是数据连接，但心跳连接长会话下段数也可能反超，
    /// 所以完成判定必须以「该连接真正收齐」为第一优先级。
    /// </summary>
    private static KcpStreamRebuilder SelectBestStream(
        Dictionary<uint, KcpStreamRebuilder> streams,
        Dictionary<uint, HashSet<ushort>> receivedByConnection,
        ushort[] expected
    )
    {
        if (streams.Count == 0)
            return new KcpStreamRebuilder();

        if (streams.Count == 1)
            return streams.Values.First();

        // ① 优先：某条连接已收齐全部目标 msgId（该流一定承载背包数据）
        foreach (var (conv, received) in receivedByConnection)
        {
            if (expected.Length > 0 && received.Count >= expected.Length)
                return streams[conv];
        }

        // ② 兜底：段数最多者
        return streams.Values.OrderByDescending(s => s.SegmentCount).First();
    }

    /// <summary>
    /// 抓包主循环。
    ///
    /// <para>
    /// <b>退出条件（任一满足）</b>：
    /// <list type="number">
    ///   <item>某条连接已解出会话密钥且收齐所有目标 msgId → 主动停止（正常完成）</item>
    ///   <item>调用方取消 → 停止，但<b>不丢弃已抓到的数据</b></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ★ 取消语义：<c>cancel</c> 只表示「停止抓包」，不表示「抛异常丢弃结果」。
    ///   会话密钥一次登录只下发一次，抓到了就必须处理完。
    /// </para>
    /// <para>
    /// ★ 完成判定必须<b>按连接</b>：游戏并发多条 KCP 连接（心跳 / 场景 / 数据），
    ///   若把不同连接收到的 msgId 混在一张全局表里，「收齐」会被误判，
    ///   而选中段数最多的流又可能不含背包数据 → 静默丢数据。
    /// </para>
    /// </summary>
    private async Task<KcpStreamRebuilder> PumpAsync(
        IUdpCapture capture,
        List<byte[]> rawMessages,
        Action onFrame,
        GameDataExportScope scope,
        IProgress<GameDataExportProgress> progress,
        CancellationToken cancellationToken
    )
    {
        var expected = MsgIdFilter.GetTargetMsgIds(scope);
        var lastReport = Environment.TickCount64;

        // 诊断计数
        var totalFrames = 0;
        var handshakeSeen = 0;
        var matchedSegments = 0;

        // ── 多连接追踪 ──
        //   ★ 不锁定「第一个握手」：游戏会并发建立多条 KCP 连接
        //     （心跳 / 数据 / 场景…），只有其中一条会下发 112 和背包数据。
        //   策略：所有 0xEE 握手都登记，收到的 KCP 段按 (conv, 端口对) 归入各自流；
        //         完成判定按「单条连接收齐」。
        var connections = new Dictionary<uint, KcpConnection>();
        var streams = new Dictionary<uint, KcpStreamRebuilder>();

        // 每条连接各自收到的目标 msgId（★ 不能共享，见方法注释）
        var receivedByConnection = new Dictionary<uint, HashSet<ushort>>();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Report(progress, GameDataExportStage.Capturing, 50, "收到停止信号，开始解析已捕获数据");
                return SelectBestStream(streams, receivedByConnection, expected);
            }

            var frame = capture.TryRead();

            if (frame is null)
            {
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            totalFrames++;
            onFrame();

            // ① 登记所有握手响应（0xEE 自带 conv/mtu/crc，无需配对 0xED）
            if (
                frame.Payload.Length >= 13
                && frame.Payload[0] == 0xEE
                && HandshakeScanner.TryParseHandshakeResponse(
                    frame.Payload,
                    frame.SrcPort,
                    frame.DstPort,
                    out var conn
                )
                && conn is not null
                && !connections.ContainsKey(conn.Conv)
            )
            {
                connections[conn.Conv] = conn;
                streams[conn.Conv] = new KcpStreamRebuilder();
                receivedByConnection[conn.Conv] = [];
                handshakeSeen++;

                Report(
                    progress,
                    GameDataExportStage.Capturing,
                    15,
                    $"识别到连接 #{handshakeSeen}：conv=0x{conn.Conv:X8} "
                        + $"{conn.ClientPort}↔{conn.ServerPort} mtu={conn.Mtu}"
                );

                continue;
            }

            if (connections.Count == 0)
            {
                // 尚未识别到任何连接，只报进度
                var nowWait = Environment.TickCount64;
                if (nowWait - lastReport >= 2000)
                {
                    lastReport = nowWait;
                    Report(
                        progress,
                        GameDataExportStage.Capturing,
                        10,
                        $"等待握手中（UDP {totalFrames} 帧）"
                    );
                }

                continue;
            }

            // ② 0xEC 裸消息帧：剥 5 字节头后作为独立帧收集
            if (frame.Payload.Length >= 5 && frame.Payload[0] == 0xEC)
            {
                var rawConv = BitConverter.ToUInt32(frame.Payload, 1);
                if (connections.ContainsKey(rawConv))
                    rawMessages.Add(frame.Payload.AsSpan(5).ToArray());

                continue;
            }

            // ③ KCP 段：按端口对找到所属连接
            //   首字节 0xEC/0xED/0xEE 的包是握手/裸帧，不是段（且可能短于 24B）
            if (frame.Payload.Length < 24 || frame.Payload[0] is 0xED or 0xEE or 0xEC)
                continue;

            KcpConnection? owner = null;
            foreach (var c in connections.Values)
            {
                var isOut = frame.SrcPort == c.ClientPort && frame.DstPort == c.ServerPort;
                var isIn = frame.SrcPort == c.ServerPort && frame.DstPort == c.ClientPort;
                if (isOut || isIn)
                {
                    owner = c;
                    break;
                }
            }

            if (owner is null)
                continue;

            // ★ 一个 UDP 数据报可能承载多个 KCP 段（ACK+PUSH 拼包，实测帧 83：
            //   ACK(sn=0) + PUSH(sn=0, 112 ProtoKeyResponse)）。必须解析全部段，
            //   否则 112 会被静默丢弃（见 KcpSegment.TryParseMany 的说明）。
            if (!KcpSegment.TryParseMany(frame.Payload, owner.CrcEnabled, out var segments))
                continue;

            // 每个数据报只做一次进度相关的流重扫（TryAdvance 内部会扫整条流）
            var stream = streams[owner.Conv];
            var received = receivedByConnection[owner.Conv];
            var advanced = false;

            foreach (var segment in segments)
            {
                if (segment.Conv != owner.Conv)
                    continue;

                matchedSegments++;

                // 只关心入站方向（服务器→客户端）：112 与背包数据都在这里
                if (frame.DstPort != owner.ClientPort)
                    continue;

                stream.Add(segment);

                if (segment.Cmd == KcpSegment.CmdPush)
                    advanced = true;
            }

            if (!advanced)
                continue;

            // ④ 增量检查（按连接）：解密钥 + 记录该连接收到的目标 msgId。
            //   keyResolved 不再跨连接共享：哪条流出现 112 就在哪条流上继续。
            var keyResolved = _keyLoader.SessionKey is not null;
            TryAdvance(stream, scope, received, ref keyResolved);

            var now = Environment.TickCount64;
            if (now - lastReport >= 2000)
            {
                lastReport = now;
                var best = received.Count;
                var percent = 5 + Math.Min(45, best * 45 / Math.Max(expected.Length, 1));
                Report(
                    progress,
                    GameDataExportStage.Capturing,
                    percent,
                    _keyLoader.SessionKey is not null
                        ? $"已取得会话密钥，收集背包数据（{best}/{expected.Length}）"
                        : $"连接 {connections.Count} 条，等待会话密钥"
                        + $"（匹配 {matchedSegments} 段）"
                );
            }

            // ⑤ 完成条件（按连接）：该连接密钥已解 + 收齐全部目标 msgId
            if (
                _keyLoader.SessionKey is not null
                && received.Count >= expected.Length
            )
            {
                Report(progress, GameDataExportStage.Capturing, 55, "数据收集完成，停止抓包");
                return streams[owner.Conv];
            }
        }
    }
    /// <summary>
    /// 在不消耗抓包循环的前提下，增量检查入站流：
    /// 解出会话密钥、记录已收到的目标 msgId。
    ///
    /// 每次都从头重扫完整流——数据量小（一次会话几百 KB），
    /// 比维护增量解析状态可靠得多。
    /// </summary>
    private void TryAdvance(
        KcpStreamRebuilder incoming,
        GameDataExportScope scope,
        HashSet<ushort> received,
        ref bool keyResolved
    )
    {
        // 还没恢复私钥就没法解密钥，直接跳过
        if (!_keyLoader.HasPrivateKey)
            return;

        var stream = incoming.Build();
        if (stream.Length == 0)
            return;

        var expected = MsgIdFilter.GetTargetMsgIds(scope);

        foreach (var frame in EnumerateFrames(stream))
        {
            // 尝试解会话密钥（只成功一次）
            if (!keyResolved && frame.MsgId == MsgIdFilter.ProtoKeyResponse)
            {
                if (_keyLoader.TryResolve(frame))
                    keyResolved = true;

                continue;
            }

            if (expected.Contains(frame.MsgId))
                received.Add(frame.MsgId);
        }
    }


    /// <summary>
    /// 从一个「独立的帧字节串」里尝试解出会话密钥。
    /// 用于 0xEC 裸消息帧——它不参与 KCP 流重组，自带完整帧。
    /// </summary>
    private static void TryResolveFromFrameBytes(byte[] frameBytes, SessionKeyLoader keyLoader)
    {
        if (frameBytes.Length < 11)
            return;

        var offset = 0;
        if (!FrameSplitter.TryTake(frameBytes, ref offset, out var frame) || frame is null)
            return;

        if (frame.MsgId == MsgIdFilter.ProtoKeyResponse)
            keyLoader.TryResolve(frame);
    }

    /// <summary>
    /// 遍历流中的所有帧。
    ///
    /// ★ 不能用 <c>while (FrameSplitter.TryTake(...))</c>：那会在第一个坏帧处直接退出，
    ///   把后面所有帧都丢掉。这里用 offset 是否为判断依据——
    ///   只要 offset 还在前进或未到末尾就继续。
    /// </summary>
    private static IEnumerable<GatewayFrame> EnumerateFrames(byte[] stream)
    {
        var offset = 0;

        while (offset < stream.Length)
        {
            var before = offset;

            if (FrameSplitter.TryTake(stream, ref offset, out var frame))
            {
                if (frame is not null)
                    yield return frame;

                continue;
            }

            // 未切出帧：
            //   offset 前进 → 坏帧已跳过，继续
            //   offset 未变 → 数据不足，没有更多可解析内容
            if (offset == before)
                yield break;
        }
    }

    /// <summary>从入站流里找 112 并解出会话密钥。</summary>
    private static void ResolveSessionKey(byte[] stream, SessionKeyLoader keyLoader)
    {
        foreach (var frame in EnumerateFrames(stream))
        {
            if (keyLoader.TryResolve(frame))
                return;
        }
    }

    /// <summary>扫描入站流，把目标 msgId 的载荷解密并映射成 DTO。</summary>
    private static int CollectInventory(
        byte[] stream,
        byte[] sessionKey,
        GameDataExportScope scope,
        List<GameNormalItem> normalItems,
        List<GameWeaponItem> weapons,
        List<GamePhantomItem> phantoms,
        List<GameValidTimeItem> validTimeItems
    )
    {
        var decoded = 0;

        foreach (var frame in EnumerateFrames(stream))
        {
            if (!MsgIdFilter.IsTarget(frame.MsgId, scope))
                continue;

            var plain = FrameCipher.Decrypt(frame, sessionKey);
            if (plain.Length == 0)
                continue;

            switch (frame.MsgId)
            {
                case MsgIdFilter.NormalItemResponse:
                {
                    if (InventoryMapper.MapNormalItems(plain) is { } normalList)
                    {
                        normalItems.AddRange(normalList);
                        decoded++;
                    }
                    break;
                }

                case MsgIdFilter.WeaponItemResponse:
                {
                    if (InventoryMapper.MapWeapons(plain) is { } weaponList)
                    {
                        weapons.AddRange(weaponList);
                        decoded++;
                    }
                    break;
                }

                case MsgIdFilter.PhantomItemResponse:
                {
                    if (InventoryMapper.MapPhantoms(plain) is { } phantomList)
                    {
                        phantoms.AddRange(phantomList);
                        decoded++;
                    }
                    break;
                }

                case MsgIdFilter.ValidTimeItemResponse:
                {
                    if (InventoryMapper.MapValidTimeItems(plain) is { } validTimeList)
                    {
                        validTimeItems.AddRange(validTimeList);
                        decoded++;
                    }
                    break;
                }
            }
        }

        return decoded;
    }

    /// <summary>
    /// 解析 RSA 私钥（密钥管理方案四层回退：环境变量 → 凭据管理器 → DPAPI → 引导文案）。
    ///
    /// <para>
    /// ★ 为什么必须走 <see cref="SecretAccessor"/> 而不是只读环境变量：
    ///   本服务运行在<b>特权宿主进程</b>（rundll32），与 UI 进程是两个进程——
    ///   UI 侧启动后再设置环境变量对宿主<b>不可见</b>。
    ///   凭据管理器 / DPAPI 文件是跨进程的用户级存储，宿主可直接读取，
    ///   这也是真实部署下唯一可靠的注入通道。
    /// </para>
    /// </summary>
    private static void LoadPrivateKey(SessionKeyLoader keyLoader)
    {
        using var accessor = new SecretAccessor(SecretId, DpapiPath);
        if (!accessor.TryResolve(out var error))
        {
            throw new InvalidOperationException(error ?? "未找到 RSA 私钥。");
        }

        keyLoader.LoadPrivateKeyPem(accessor.GetPem()!);
    }

    private static GameDataExportResult Failure(
        string message,
        int captured,
        int decoded,
        string? sessionKeyHex
    ) =>
        new(false, message, null, null, captured, decoded, sessionKeyHex);

    private static void Report(
        IProgress<GameDataExportProgress> progress,
        GameDataExportStage stage,
        int percentage,
        string message
    ) => progress.Report(new GameDataExportProgress(stage, percentage, message));
}
