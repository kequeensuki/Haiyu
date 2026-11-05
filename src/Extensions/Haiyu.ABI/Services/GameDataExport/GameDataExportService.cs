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

    /// <summary>playerId 解析状态机（v2 §2.7：快速失败）。</summary>
    private readonly PlayerIdResolver _playerIdResolver = new();

    /// <summary>playerId 快速失败的原因（终止会话时写入进度消息与失败结果）。</summary>
    private string? _playerIdAbortReason;

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
        var updateCount = 0;
        var sessionStart = DateTimeOffset.Now;

        // ★ v2 P0-A8 快照语义：每 msgId 保留最新一份解析结果（替换式），
        //   内存恒定 = 当前数据集大小
        var normalByMsg = new Dictionary<ushort, List<GameNormalItem>>();
        var weaponsByMsg = new Dictionary<ushort, List<GameWeaponItem>>();
        var phantomsByMsg = new Dictionary<ushort, List<GamePhantomItem>>();
        var validTimeByMsg = new Dictionary<ushort, List<GameValidTimeItem>>();

        Report(progress, GameDataExportStage.Capturing, 0, "正在准备网卡");

        using var capture = new NpcapUdpCapture();
        // 0xEC 裸消息帧（非 KCP 通道）收集到此处，与 KCP 流一起参与帧解析
        var rawMessages = new List<byte[]>();

        try
        {
            LoadPrivateKey(_keyLoader);
            capture.Start();

            Report(progress, GameDataExportStage.Capturing, 5, "正在等待游戏连接");

            // ── v2 持续监控编排：抓包循环后台持续运行（取消时结束），
            //    编排层经「就绪信号 + 水位增量」消费数据 ──
            var pump = StartPump(capture, rawMessages, () => captured++, request.Scope, progress, cancellationToken);

            // 等待首次收齐（信号由抓包循环发出；取消时信号被 SetCanceled，在此抛 OCE）
            var primaryStream = await pump.ReadySignal.Task.ConfigureAwait(false);

            // ── 首轮：解析 + 快照 + 首次落盘 ──
            Report(progress, GameDataExportStage.Decrypting, 60, "正在解密数据");
            var firstStream = primaryStream.Build();
            foreach (var raw in rawMessages)
                TryResolveFromFrameBytes(raw, _keyLoader);
            rawMessages.Clear();
            ResolveSessionKey(firstStream, _keyLoader);

            if (_keyLoader.SessionKey is null)
            {
                return Failure(
                    "未能解出会话密钥。请确认抓包覆盖了登录阶段（从游戏启动前开始抓）。",
                    captured,
                    decoded,
                    null
                );
            }

            decoded += CollectInventory(
                firstStream, 0, _keyLoader.SessionKey!, request.Scope,
                normalByMsg, weaponsByMsg, phantomsByMsg, validTimeByMsg);
            pump.Watermarks[primaryStreamIdentity] = firstStream.Length;

            updateCount++;
            var lastResult = await BuildAndWriteAsync(
                normalByMsg, weaponsByMsg, phantomsByMsg, validTimeByMsg,
                captured, decoded, updateCount, sessionStart, progress,
                firstExport: true).ConfigureAwait(false);

            // ── 监控循环：数据变更（新目标帧）→ 增量解析快照 → 重导出 ──
            //   退出：取消（用户停止 / Haiyu 关闭 / 空转超时由 Launcher 驱动取消）。
            //   重登衔接：新 112 → NotifyRelogin + 换钥；新握手 → 新流自动纳入；
            //   每条流的水位独立跟踪（监控的是「流上新增的目标帧」）。
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);

                if (_keyLoader.SessionKey is null)
                    continue;

                var changed = false;
                foreach (var pair in pump.Streams)
                {
                    var stream = pair.Value;
                    var from = pump.Watermarks.TryGetValue(pair.Key, out var w) ? w : 0;

                    // 增量视图：该流水位之后的新字节（含新 notify 帧）
                    var view = stream.BuildViewFrom(from);
                    if (view.Length == 0)
                        continue;

                    var updated = CollectInventory(
                        view, 0, _keyLoader.SessionKey!, request.Scope,
                        normalByMsg, weaponsByMsg, phantomsByMsg, validTimeByMsg);
                    pump.Watermarks[pair.Key] = from + view.Length;

                    if (updated > 0)
                        changed = true;
                }

                if (!changed)
                    continue;

                updateCount++;
                decoded += 0; // decoded 语义：累计解析的目标帧数（快照模式下保持累计）
                lastResult = await BuildAndWriteAsync(
                    normalByMsg, weaponsByMsg, phantomsByMsg, validTimeByMsg,
                    captured, decoded, updateCount, sessionStart, progress,
                    firstExport: false).ConfigureAwait(false);
            }

            // ── 会话结束（取消）：返回最新快照 ──
            Report(progress, GameDataExportStage.Completed, 100, $"监控结束，共更新 {updateCount} 次");
            return lastResult;
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

    /// <summary>首轮导出流的标识（首个收齐的连接 conv；水位表用它做键）。</summary>
    private uint primaryStreamIdentity;

    /// <summary>
    /// 组装当前快照并落盘（首轮与每次更新共用）。
    /// </summary>
    private async Task<GameDataExportResult> BuildAndWriteAsync(
        Dictionary<ushort, List<GameNormalItem>> normalByMsg,
        Dictionary<ushort, List<GameWeaponItem>> weaponsByMsg,
        Dictionary<ushort, List<GamePhantomItem>> phantomsByMsg,
        Dictionary<ushort, List<GameValidTimeItem>> validTimeByMsg,
        int captured,
        int decoded,
        int updateCount,
        DateTimeOffset sessionStart,
        IProgress<GameDataExportProgress> progress,
        bool firstExport
    )
    {
        Report(progress, GameDataExportStage.Exporting, 85, firstExport ? "正在生成导出文件" : $"数据变更（第 {updateCount} 次），重新导出");

        var inventory = new GameInventoryData(
            FlattenNormals(normalByMsg),
            FlattenWeapons(weaponsByMsg),
            FlattenPhantoms(phantomsByMsg),
            FlattenValidTime(validTimeByMsg)
        );

        var sessionKeyHex = _keyLoader.SessionKey is not null
            ? Convert.ToHexString(_keyLoader.SessionKey).ToLowerInvariant()
            : string.Empty;
        var message = firstExport
            ? $"导出完成：普通道具 {inventory.NormalItems.Count}、武器 {inventory.Weapons.Count}、声骸 {inventory.Phantoms.Count}、限时道具 {inventory.ValidTimeItems.Count}"
            : $"数据已更新（第 {updateCount} 次）：普通道具 {inventory.NormalItems.Count}、武器 {inventory.Weapons.Count}、声骸 {inventory.Phantoms.Count}、限时道具 {inventory.ValidTimeItems.Count}";
        var result = new GameDataExportResult(
            true, message, inventory, null, captured, decoded, sessionKeyHex,
            _playerIdResolver.PlayerId, updateCount
        );

        var path = InventoryJsonWriter.TryWrite(result, sessionStart);
        if (path is not null)
            result = result with { OutputJsonPath = path };

        Report(progress, GameDataExportStage.Completed, 100, path is null
            ? (firstExport ? "导出完成（但 JSON 落盘失败）" : $"第 {updateCount} 次更新（JSON 落盘失败）")
            : (firstExport ? $"导出完成：{path}" : $"数据已更新：{path}"));

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 启动后台抓包循环（持续运行到取消），返回共享状态：
    /// Streams（全部连接的流，重登新握手自动纳入）、Watermarks（每流消费水位）、
    /// ReadySignal（首次收齐信号，携带首选流）。
    /// </summary>
    private PumpState StartPump(
        IUdpCapture capture,
        List<byte[]> rawMessages,
        Action onFrame,
        GameDataExportScope scope,
        IProgress<GameDataExportProgress> progress,
        CancellationToken cancellationToken
    )
    {
        var state = new PumpState();
        var expected = MsgIdFilter.GetTargetMsgIds(scope);

        // 后台任务：抓包循环本体（原 PumpAsync 的 while(true) 挪到这里）
        state.PumpTask = Task.Run(async () =>
        {
            var connections = new Dictionary<uint, KcpConnection>();
            var streams = state.Streams;
            var receivedByConnection = new Dictionary<uint, HashSet<ushort>>();
            var handshakeSeen = 0;
            var matchedSegments = 0;
            var totalFrames = 0;
            var lastReport = Environment.TickCount64;
            _ = expected; // 进度文案用

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = capture.TryRead();

                if (frame is null)
                {
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    totalFrames++;
                    onFrame();

                    // ① 登记所有握手响应（0xEE 自带 conv/mtu/crc）
                    if (
                        frame.Payload.Length >= 13
                        && frame.Payload.Span[0] == 0xEE
                        && HandshakeScanner.TryParseHandshakeResponse(
                            frame.Payload.Span, frame.SrcPort, frame.DstPort, out var conn)
                        && conn is not null
                        && !connections.ContainsKey(conn.Conv)
                    )
                    {
                        connections[conn.Conv] = conn;
                        streams[conn.Conv] = new KcpStreamRebuilder();
                        receivedByConnection[conn.Conv] = [];
                        handshakeSeen++;

                        Report(progress, GameDataExportStage.Capturing, 15,
                            $"识别到连接 #{handshakeSeen}：conv=0x{conn.Conv:X8} "
                            + $"{conn.ClientPort}↔{conn.ServerPort} mtu={conn.Mtu}");
                        continue;
                    }

                    if (connections.Count == 0)
                    {
                        var nowWait = Environment.TickCount64;
                        if (nowWait - lastReport >= 2000)
                        {
                            lastReport = nowWait;
                            Report(progress, GameDataExportStage.Capturing, 10,
                                $"等待握手中（UDP {totalFrames} 帧）");
                        }
                        continue;
                    }

                    // ② 0xEC 裸消息帧（长存拷贝进 rawMessages）
                    if (frame.Payload.Length >= 5 && frame.Payload.Span[0] == 0xEC)
                    {
                        var rawConv = BitConverter.ToUInt32(frame.Payload.Span[1..]);
                        if (connections.ContainsKey(rawConv))
                            rawMessages.Add(frame.Payload.Span[5..].ToArray());
                        continue;
                    }

                    // ③ KCP 段定位所属连接
                    if (frame.Payload.Length < 24 || frame.Payload.Span[0] is 0xED or 0xEE or 0xEC)
                        continue;

                    KcpConnection? owner = null;
                    foreach (var c in connections.Values)
                    {
                        if ((frame.SrcPort == c.ClientPort && frame.DstPort == c.ServerPort)
                            || (frame.SrcPort == c.ServerPort && frame.DstPort == c.ClientPort))
                        {
                            owner = c;
                            break;
                        }
                    }
                    if (owner is null)
                        continue;

                    if (!KcpSegment.TryParseMany(frame.Payload.Span, frame.Payload, owner.CrcEnabled, out var segments))
                        continue;

                    var stream = streams[owner.Conv];
                    var received = receivedByConnection[owner.Conv];
                    var advanced = false;

                    foreach (var segment in segments)
                    {
                        if (segment.Conv != owner.Conv)
                            continue;

                        matchedSegments++;

                        if (frame.DstPort != owner.ClientPort)
                            continue;

                        stream.Add(segment);
                        if (segment.Cmd == KcpSegment.CmdPush)
                            advanced = true;
                    }

                    if (!advanced)
                        continue;

                    // ④ 增量检查：解密钥 + 记录 msgId（playerId 状态机也在此驱动）
                    var keyResolved = _keyLoader.SessionKey is not null;
                    TryAdvance(stream, scope, received, ref keyResolved);

                    var now = Environment.TickCount64;
                    if (now - lastReport >= 2000)
                    {
                        lastReport = now;
                        var best = received.Count;
                        var percent = 5 + Math.Min(45, best * 45 / Math.Max(expected.Length, 1));
                        Report(progress, GameDataExportStage.Capturing, percent,
                            _keyLoader.SessionKey is not null
                                ? $"已取得会话密钥，收集背包数据（{best}/{expected.Length}）"
                                : $"连接 {connections.Count} 条，等待会话密钥（匹配 {matchedSegments} 段）");
                    }

                    // ⑤ 首次收齐：信号编排层（只触发一次；监控态不退出）
                    if (
                        _keyLoader.SessionKey is not null
                        && !state.FirstCollectionSignaled
                        && received.Count >= expected.Length
                    )
                    {
                        state.FirstCollectionSignaled = true;
                        primaryStreamIdentity = owner.Conv;
                        Report(progress, GameDataExportStage.Capturing, 55, "数据收集完成，进入持续监控");
                        state.ReadySignal.TrySetResult(stream);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单帧处理异常不终结抓包循环（与 v1 的坏帧容忍一致）
                    Report(progress, GameDataExportStage.Capturing, 50, $"帧处理异常（已忽略）：{ex.Message}");
                }
                finally
                {
                    frame.ReturnBuffer();
                }
            }

            // 抓包结束（取消）：清共享状态
            ReleaseCaptureState(streams, receivedByConnection);
        }, CancellationToken.None);

        return state;
    }

    private static List<GameNormalItem> FlattenNormals(Dictionary<ushort, List<GameNormalItem>> map) =>
        map.Values.SelectMany(v => v).ToList();
    private static List<GameWeaponItem> FlattenWeapons(Dictionary<ushort, List<GameWeaponItem>> map) =>
        map.Values.SelectMany(v => v).ToList();
    private static List<GamePhantomItem> FlattenPhantoms(Dictionary<ushort, List<GamePhantomItem>> map) =>
        map.Values.SelectMany(v => v).ToList();
    private static List<GameValidTimeItem> FlattenValidTime(Dictionary<ushort, List<GameValidTimeItem>> map) =>
        map.Values.SelectMany(v => v).ToList();

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

    /// <summary>抓包循环的共享状态（后台 PumpTask 与编排层共享）。</summary>
    private sealed class PumpState
    {
        /// <summary>
        /// 全部连接的流（conv → 流）。重登新握手自动纳入。
        /// ★ 跨线程容器：PumpTask 写入、编排层遍历，必须并发安全。
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<uint, KcpStreamRebuilder> Streams { get; } = new();

        /// <summary>每条流的消费水位（conv → 字节位移）。编排层独占读写（PumpTask 不触碰）。</summary>
        public Dictionary<uint, long> Watermarks { get; } = new();

        /// <summary>首次收齐信号（携带首选流）。</summary>
        public TaskCompletionSource<KcpStreamRebuilder> ReadySignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstCollectionSignaled;

        /// <summary>抓包后台任务（取消时自然结束）。</summary>
        public Task PumpTask { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// 处理单个 UDP 数据报（握手登记 / 0xEC 裸帧 / KCP 段入流 + 增量检查）。
    /// 拆出方法以便 try/finally 精确包裹租借缓冲的生存期。
    /// </summary>

    /// <summary>
    /// 释放抓包状态（流 / 连接表 / 每连接 msgId 集合）。
    /// ★ 宿主是常驻进程：会话结束必须清干净，防残留引用拖内存。
    /// 返回的流已不受本字典持有（字典本身被清空）。
    /// </summary>
    private static void ReleaseCaptureState(
        System.Collections.Concurrent.ConcurrentDictionary<uint, KcpStreamRebuilder> streams,
        Dictionary<uint, HashSet<ushort>> receivedByConnection
    )
    {
        foreach (var stream in streams.Values)
            stream.Clear();
        streams.Clear();
        receivedByConnection.Clear();
    }
    /// <summary>
    /// 增量检查入站流：解出会话密钥、记录已收到的目标 msgId。
    ///
    /// ★ v2 增量化（P0-A4）：只解析水位之后的新字节（<see cref="KcpStreamRebuilder.BuildIncremental"/>），
    ///   解析完推进水位并 <see cref="KcpStreamRebuilder.Compact"/> 收缩存储——
    ///   v1 每次全量重拼整条流是 O(N²)，持续监控下不可运行。
    ///   尾部半帧（数据不足）不推进水位，等后续段到齐再解析。
    /// </summary>
    private void TryAdvance(
        KcpStreamRebuilder incoming,
        GameDataExportScope scope,
        HashSet<ushort> received,
        ref bool keyResolved
    )
    {
        // 还没恢复私钥就没法解密钥，直接跳过（新字节留在流里，密钥到位后再解析）
        if (!_keyLoader.HasPrivateKey)
            return;

        var incremental = incoming.BuildIncremental();
        if (incremental.Length == 0)
            return;

        var expected = MsgIdFilter.GetTargetMsgIds(scope);
        var offset = 0;
        var parsedBytes = 0;
        var complete = true;

        while (offset < incremental.Length)
        {
            var before = offset;

            if (FrameSplitter.TryTake(incremental, ref offset, out var frame))
            {
                if (frame is not null)
                {
                    // 尝试解会话密钥（只成功一次）
                    if (!keyResolved && frame.MsgId == MsgIdFilter.ProtoKeyResponse)
                    {
                        if (_keyLoader.TryResolve(frame))
                        {
                            keyResolved = true;
                            _playerIdResolver.NotifyKeyResolved();
                        }
                    }
                    else
                    {
                        // 目标 msgId 记录（完成判定用）
                        if (expected.Contains(frame.MsgId))
                            received.Add(frame.MsgId);

                        // playerId 状态机：BasicInfoNotify 提取 / 失败计数
                        if (!_playerIdResolver.TryFeed(frame))
                        {
                            // 快速失败（v2 §2.7）：终止会话，不落盘
                            _playerIdAbortReason = _playerIdResolver.FailureReason;
                            return;
                        }
                    }
                }

                parsedBytes = offset;
                continue;
            }

            // 未切出帧：
            //   offset 前进 → 坏帧已跳过（可计入已解析）
            //   offset 未变 → 数据不足（半帧），从当前位置起留给下一轮
            if (offset == before)
            {
                complete = false;
                break;
            }
        }

        incoming.AdvanceConsumed(parsedBytes);
        if (complete && parsedBytes > 0)
        {
            // 全部解析完（无半帧悬挂）才收缩；有半帧时保留尾部段等待拼接
            incoming.Compact();
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

    /// <summary>
    /// 扫描流中目标 msgId 的帧，解密并映射成 DTO，按「快照语义」更新各类目。
    ///
    /// ★ v2 P0-A8 快照语义：同一 msgId 的 notify 是<b>全量重发</b>（服务器语义：
    ///   对应数据变更时重发该类目全部内容），新帧<b>替换</b>类目而非追加——
    ///   内存恒定 = 当前数据集大小，且消除 v1 的重复计数 bug。
    ///
    /// <param name="consumedFrom">流内起始位移：只解析该位移之后的新帧（增量）。</param>
    /// <returns>本类目是否有数据更新（有则触发落盘）。</returns>
    private static int CollectInventory(
        byte[] stream,
        int consumedFrom,
        byte[] sessionKey,
        GameDataExportScope scope,
        Dictionary<ushort, List<GameNormalItem>> normalByMsg,
        Dictionary<ushort, List<GameWeaponItem>> weaponsByMsg,
        Dictionary<ushort, List<GamePhantomItem>> phantomsByMsg,
        Dictionary<ushort, List<GameValidTimeItem>> validTimeByMsg
    )
    {
        var updated = 0;
        var offset = consumedFrom;

        while (offset < stream.Length)
        {
            var before = offset;

            if (FrameSplitter.TryTake(stream, ref offset, out var frame))
            {
                if (frame is not null && MsgIdFilter.IsTarget(frame.MsgId, scope))
                {
                    var plain = FrameCipher.Decrypt(frame, sessionKey);
                    if (plain.Length == 0)
                        continue;

                    switch (frame.MsgId)
                    {
                        case MsgIdFilter.NormalItemResponse:
                        {
                            if (InventoryMapper.MapNormalItems(plain) is { } list)
                            {
                                normalByMsg[frame.MsgId] = list;
                                updated++;
                            }
                            break;
                        }

                        case MsgIdFilter.WeaponItemResponse:
                        {
                            if (InventoryMapper.MapWeapons(plain) is { } list)
                            {
                                weaponsByMsg[frame.MsgId] = list;
                                updated++;
                            }
                            break;
                        }

                        case MsgIdFilter.PhantomItemResponse:
                        {
                            if (InventoryMapper.MapPhantoms(plain) is { } list)
                            {
                                phantomsByMsg[frame.MsgId] = list;
                                updated++;
                            }
                            break;
                        }

                        case MsgIdFilter.ValidTimeItemResponse:
                        {
                            if (InventoryMapper.MapValidTimeItems(plain) is { } list)
                            {
                                validTimeByMsg[frame.MsgId] = list;
                                updated++;
                            }
                            break;
                        }
                    }
                }

                continue;
            }

            if (offset == before)
                break; // 半帧，留给下一轮
        }

        return updated;
    }

    /// <summary>把四类快照映射合并为最终 DTO 列表（会话收尾/落盘时调用）。</summary>
    private static List<T> Flatten<T>(IEnumerable<Dictionary<ushort, List<T>>> maps) =>
        maps.SelectMany(m => m.Values).SelectMany(v => v).ToList();

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
