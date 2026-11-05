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
/// 被动捕获后由 FrameCipher 解密/解压，再解析业务快照。
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
    /// 作为字段而非局部变量，是因为抓包循环里需要增量解析（单消费者解析链）。
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        using var capture = new NpcapUdpCapture();
        var sessionStart = DateTimeOffset.Now;
        var captured = 0;
        var updates = 0;
        var writtenVersions = new Dictionary<uint, int>();
        var lastWrite = Environment.TickCount64;
        var lastReport = lastWrite;
        var connections = new Dictionary<uint, KcpConnection>();
        var streams = new Dictionary<uint, KcpStreamRebuilder>();
        var states = new Dictionary<uint, InventoryCaptureState>();
        GameDataExportResult? lastResult = null;
        try
        {
            Report(progress, GameDataExportStage.Capturing, 0, "正在准备网卡");
            LoadPrivateKey(_keyLoader);
            capture.Start();
            Report(progress, GameDataExportStage.Capturing, 5, "正在等待游戏连接");
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = capture.TryRead();
                if (packet is not null)
                {
                    try
                    {
                        captured++;
                        if (HandshakeScanner.TryParseHandshakeResponse(
                            packet.Payload.Span, packet.SrcPort, packet.DstPort, out var connection)
                            && connection is not null && !connections.ContainsKey(connection.Conv))
                        {
                            connections[connection.Conv] = connection;
                            streams[connection.Conv] = new KcpStreamRebuilder();
                            states[connection.Conv] = new InventoryCaptureState(_keyLoader.CreateSession(), request.Scope);
                        }
                        else if (packet.Payload.Length >= 5 && packet.Payload.Span[0] == RawFrameTag)
                        {
                            var conv = BitConverter.ToUInt32(packet.Payload.Span[1..]);
                            if (connections.TryGetValue(conv, out var owner) && packet.DstPort == owner.ClientPort)
                                states[conv].Consume(packet.Payload.Span[5..].ToArray());
                        }
                        else
                        {
                            foreach (var pair in connections)
                            {
                                var owner = pair.Value;
                                if (packet.SrcPort != owner.ServerPort || packet.DstPort != owner.ClientPort)
                                    continue;
                                if (!KcpSegment.TryParseMany(packet.Payload.Span, packet.Payload,
                                    owner.CrcEnabled, out var segments))
                                    continue;
                                foreach (var segment in segments)
                                    if (segment.Conv == owner.Conv)
                                        streams[pair.Key].Add(segment);
                                states[pair.Key].Consume(streams[pair.Key]);
                            }
                        }
                    }
                    finally
                    {
                        packet.ReturnBuffer();
                    }
                }
                else
                {
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                }

                foreach (var pair in states)
                {
                    var state = pair.Value;
                    if (!state.PlayerId.OnTick())
                        throw new InvalidOperationException(state.PlayerId.FailureReason);
                    if (state.PlayerId.Current == PlayerIdResolver.Phase.Failed)
                        throw new InvalidOperationException(state.PlayerId.FailureReason);
                    if (!state.Ready)
                        continue;
                    if (writtenVersions.GetValueOrDefault(pair.Key) == state.Version)
                        continue;
                    var now = Environment.TickCount64;
                    if (lastResult is not null && now - lastWrite < 1000)
                        continue;
                    // Both protobuf snapshots and playerId are ready before any file is written.
                    lastResult = await BuildAndWriteAsync(
                        state.NormalItems, state.Weapons, state.Phantoms, state.ValidTimeItems,
                        captured, state.DecodedFrames, ++updates, sessionStart, progress,
                        firstExport: lastResult is null, playerId: state.PlayerId.PlayerId, sessionKey: state.SessionKey!).ConfigureAwait(false);
                    writtenVersions[pair.Key] = state.Version;
                    lastWrite = now;
                }
                if (Environment.TickCount64 - lastReport >= 2000 && lastResult is null)
                {
                    lastReport = Environment.TickCount64;
                    var best = states.Values.OrderByDescending(s => s.ParsedCategories).FirstOrDefault();
                    var message = !states.Values.Any(s => s.SessionKey is not null)
                        ? $"等待游戏登录与会话密钥（UDP {captured} 帧）"
                        : best?.ParsedCategories == 4 && best.PlayerId.PlayerId <= 0
                            ? "背包数据已解析（4/4），等待玩家身份信息"
                            : $"已取得会话密钥，解析背包数据（{best?.ParsedCategories ?? 0}/4）";
                    Report(progress, GameDataExportStage.Capturing, 10 + (best?.ParsedCategories ?? 0) * 10, message);
                }
            }
            foreach (var pair in states)
            {
                var state = pair.Value;
                if (state.Ready && writtenVersions.GetValueOrDefault(pair.Key) != state.Version)
                    lastResult = await BuildAndWriteAsync(
                        state.NormalItems, state.Weapons, state.Phantoms, state.ValidTimeItems,
                        captured, state.DecodedFrames, ++updates, sessionStart, progress,
                        firstExport: lastResult is null, playerId: state.PlayerId.PlayerId,
                        sessionKey: state.SessionKey!).ConfigureAwait(false);
            }
            return lastResult ?? Failure("监控已停止，尚未取得完整背包及玩家身份信息，未生成导出文件。", captured, states.Values.Sum(s => s.DecodedFrames), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(progress, GameDataExportStage.Failed, 100, ex.Message);
            return Failure(ex.Message, captured, states.Values.Sum(s => s.DecodedFrames), null);
        }
        finally
        {
            foreach (var stream in streams.Values)
                stream.Clear();
            foreach (var state in states.Values)
                state.Dispose();
            _keyLoader.Dispose();
        }
    }

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
        bool firstExport,
        int playerId,
        byte[] sessionKey
    )
    {
        if (playerId <= 0 || decoded <= 0)
            throw new InvalidOperationException("未取得有效玩家身份或未成功解析背包，禁止落盘。");
        Report(progress, GameDataExportStage.Exporting, 85, firstExport ? "正在生成导出文件" : $"数据变更（第 {updateCount} 次），重新导出");

        var inventory = new GameInventoryData(
            Flatten(normalByMsg),
            Flatten(weaponsByMsg),
            Flatten(phantomsByMsg),
            Flatten(validTimeByMsg)
        );

        var sessionKeyHex = Convert.ToHexString(sessionKey).ToLowerInvariant();
        var message = firstExport
            ? $"导出完成：普通道具 {inventory.NormalItems.Count}、武器 {inventory.Weapons.Count}、声骸 {inventory.Phantoms.Count}、限时道具 {inventory.ValidTimeItems.Count}"
            : $"数据已更新（第 {updateCount} 次）：普通道具 {inventory.NormalItems.Count}、武器 {inventory.Weapons.Count}、声骸 {inventory.Phantoms.Count}、限时道具 {inventory.ValidTimeItems.Count}";
        var result = new GameDataExportResult(
            true, message, inventory, null, captured, decoded, sessionKeyHex,
            playerId, updateCount
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

    private static List<T> Flatten<T>(Dictionary<ushort, List<T>> map) => map.Values.SelectMany(v => v).ToList();
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
