using System.Buffers.Binary;
using System.Runtime.Versioning;
using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Security;
using Haiyu.ABI.Services.GameDataExport.Session;

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
    private readonly Func<IUdpCapture> _createCapture;
    private readonly Func<EventExportSession> _createEvents;
    private readonly Action<SessionKeyLoader> _loadKey;
    private readonly Func<GameDataExportResult, DateTimeOffset, string?> _write;

    public GameDataExportService() : this(() => new MultiInterfaceUdpCapture(), LoadPrivateKey, InventoryJsonWriter.TryWrite) { }

    internal GameDataExportService(Func<IUdpCapture> createCapture, Action<SessionKeyLoader> loadKey,
        Func<GameDataExportResult, DateTimeOffset, string?> write, Func<EventExportSession>? createEvents = null)
    {
        _createCapture = createCapture;
        _createEvents = createEvents ?? (() => new EventExportSession(GameDataExportPaths.OutputDirectory));
        _loadKey = loadKey;
        _write = write;
    }

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
        using var capture = _createCapture();
        EventExportSession? events = null;
        var sessionStart = DateTimeOffset.Now;
        var captured = 0;
        var decodedTotal = 0;
        // 次数属于玩家，不属于抓包连接；切网重连或切回同一账号时继续累计。
        var updatesByPlayer = new Dictionary<int, int>();
        var snapshotFailures = new Dictionary<int, string>();
        var summariesByPlayer = new Dictionary<int, GameDataExportPlayerSummary>();
        var writtenVersions = new Dictionary<uint, int>();
        var lastAttempts = new Dictionary<uint, long>();
        var lastReport = Environment.TickCount64;
        var connections = new Dictionary<uint, KcpConnection>();
        var streams = new Dictionary<uint, KcpStreamRebuilder>();
        var states = new Dictionary<uint, CaptureConnectionState>();
        var endpoints = new Dictionary<uint, ConnectionKey>();
        var connectionIds = new Dictionary<ConnectionKey, uint>();
        uint nextConnectionId = 0;
        var sessionFamilies = new Dictionary<uint, uint>();
        var adaptive = capture as IAdaptiveUdpCapture;
        uint? lastVerifiedConnection = null;
        var keyReported = false;
        GameDataExportResult? lastResult = null;

        void RememberExport(GameDataExportResult result)
        {
            if (!result.Success)
            {
                snapshotFailures[result.PlayerId] = result.Message;
                return;
            }
            snapshotFailures.Remove(result.PlayerId);
            if (result is not { OutputJsonPath: not null, Inventory: not null }) return;
            updatesByPlayer[result.PlayerId] = result.UpdateCount;
            summariesByPlayer[result.PlayerId] = new(result.PlayerId, result.UpdateCount,
                result.Inventory.NormalItems.Count, result.Inventory.Weapons.Count,
                result.Inventory.Phantoms.Count, result.Inventory.ValidTimeItems.Count, result.OutputJsonPath);
        }

        async Task<GameDataExportResult> CompleteAsync(GameDataExportResult result, bool failed = false)
        {
            capture.Stop();
            var pending = events is not null && streams.Any(p => p.Value.SegmentCount > 0
                && states.TryGetValue(p.Key, out var state) && state.Session.HasVerifiedTraffic);
            var eventStats = events is null ? null : await events.FinishAsync(failed ? result.Message
                : pending ? "停止时仍有未完成的 KCP 重组数据，捕获可能不完整。" : null).ConfigureAwait(false);
            if (snapshotFailures.Count > 0)
                result = result with { Success = false, Message = string.Join("；", snapshotFailures.OrderBy(p => p.Key)
                    .Select(p => $"玩家 {p.Key}：{p.Value}")) };
            if (eventStats is not null)
            {
                if (eventStats.Error is not null)
                    result = result with { Success = false, Message = eventStats.Error + (result.Success ? "" : "\n" + result.Message) };
                else if (eventStats.Written > 0 && (result.Success || !request.Scope.HasFlag(GameDataExportScope.Inventory)))
                    result = result with { Success = true, Message = "消息事件导出已结束。" };
            }
            return result with { PlayerSummaries = summariesByPlayer.Values.OrderBy(s => s.PlayerId).ToArray(), EventExport = eventStats };
        }

        void RemoveConnection(uint conv)
        {
            if (streams.Remove(conv, out var stream)) stream.Clear();
            connections.Remove(conv);
            if (endpoints.Remove(conv, out var endpoint)) connectionIds.Remove(endpoint);
            sessionFamilies.Remove(conv);
            writtenVersions.Remove(conv);
            lastAttempts.Remove(conv);
            if (states.Remove(conv, out var state)) state.Dispose();
        }

        void DropCandidate(uint conv, string reason)
        {
            RemoveConnection(conv);
            Report(progress, GameDataExportStage.Capturing, 10, $"忽略无法验证的候选连接：{reason}");
        }

        bool IsCurrentSession(uint conv) => lastVerifiedConnection is not { } current
            || !sessionFamilies.TryGetValue(current, out var family) || sessionFamilies[conv] == family;

        void Consume(uint conv, Action<CaptureConnectionState> read, UdpFrame packet, Func<bool>? freshKcp = null)
        {
            var state = states[conv];
            var verifiedBefore = state.Session.HasVerifiedTraffic;
            var framesBefore = state.Session.VerifiedFrames;
            var identityBefore = state.Session.IdentityVersion;
            var decodedBefore = state.Inventory.DecodedOnConnection;
            try { read(state); }
            catch (InvalidDataException ex) when ((!verifiedBefore && !state.Session.HasVerifiedTraffic) || !IsCurrentSession(conv))
            {
                // 未确认的候选连接不能让已确认的游戏会话终止。
                DropCandidate(conv, ex.Message);
                return;
            }
            finally { decodedTotal += state.Inventory.DecodedOnConnection - decodedBefore; }
            if (!keyReported && state.Session.SessionKey is not null)
            {
                keyReported = true;
                progress.Report(new(GameDataExportStage.Decrypting, 20, "已取得会话密钥，等待玩家身份及背包数据。",
                    SessionKeyAcquired: true));
            }
            if (state.Session.HasVerifiedTraffic && (state.Session.VerifiedFrames != framesBefore || (verifiedBefore && freshKcp?.Invoke() == true)))
            {
                // 新连接确认后淘汰同一会话的旧传输，防止旧的待写快照覆盖新数据。
                var family = sessionFamilies[conv];
                var identityChanged = state.Session.IdentityVersion != identityBefore;
                if (!identityChanged && !IsCurrentSession(conv)) return;
                foreach (var previous in states.Keys.Where(k => k != conv && sessionFamilies[k] == family).ToArray())
                    RemoveConnection(previous);
                // 旧连接的后台 KCP 包只能确认捕获接口，不能把当前玩家切回旧账号。
                if (identityChanged || state.Session.VerifiedFrames != framesBefore || lastVerifiedConnection is null)
                    lastVerifiedConnection = conv;
                adaptive?.ConfirmInterface(packet);
            }
        }
        try
        {
            if (request.Scope.HasFlag(GameDataExportScope.AllServerMessages))
            {
                events = _createEvents();
                progress = new EventProgress(progress, events);
            }
            Report(progress, GameDataExportStage.Capturing, 0, "正在准备网卡");
            _loadKey(_keyLoader);
            capture.Start();
            Report(progress, GameDataExportStage.Capturing, 5, "正在等待游戏连接", captureReady: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                events?.ThrowIfFailed();
                foreach (var pair in streams.ToArray())
                {
                    try { pair.Value.ValidatePending(); }
                    catch (InvalidDataException ex) when (!states[pair.Key].Session.HasVerifiedTraffic || !IsCurrentSession(pair.Key))
                    {
                        DropCandidate(pair.Key, ex.Message);
                    }
                }
                var decodedBefore = decodedTotal;
                var packet = capture.TryRead();
                if (packet is not null)
                {
                    try
                    {
                        captured++;
                        if (HandshakeScanner.TryParseHandshakeResponse(
                            packet.Payload.Span, packet.SrcPort, packet.DstPort, out var connection)
                            && connection is not null)
                        {
                            var endpoint = ConnectionKey.From(packet, connection.Conv);
                            if (!connectionIds.TryGetValue(endpoint, out var connectionId))
                            {
                                connectionId = ++nextConnectionId;
                                connectionIds.Add(endpoint, connectionId);
                                streams[connectionId] = new KcpStreamRebuilder();
                                if (adaptive is { NetworkGeneration: > 0 } && lastVerifiedConnection is { } previous
                                    && states.TryGetValue(previous, out var session) && session.Session.SessionKey is not null)
                                {
                                    states[connectionId] = session.ForkForReconnect();
                                    writtenVersions[connectionId] = writtenVersions.GetValueOrDefault(previous);
                                    sessionFamilies[connectionId] = sessionFamilies[previous];
                                }
                                else
                                {
                                    states[connectionId] = new CaptureConnectionState(_keyLoader.CreateSession(), request.Scope);
                                    sessionFamilies[connectionId] = connectionId;
                                }
                            }
                            var eventState = states[connectionId];
                            eventState.ConnectionId = connectionId;
                            eventState.Observer = events;
                            // 重连可改变本地地址/端口，但不会因此清除密钥及玩家信息。
                            connections[connectionId] = connection;
                            endpoints[connectionId] = endpoint;
                        }
                        else if (packet.Payload.Length >= 5 && packet.Payload.Span[0] == RawFrameTag)
                        {
                            var conv = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span[1..]);
                            if (connectionIds.TryGetValue(ConnectionKey.From(packet, conv), out var connectionId))
                                Consume(connectionId, s => s.ConsumeRaw(packet.Payload.Span[5..].ToArray()), packet);
                        }
                        else
                        {
                            foreach (var pair in connections.ToArray())
                            {
                                if (!connections.TryGetValue(pair.Key, out var owner)) continue;
                                if (!endpoints[pair.Key].Matches(packet))
                                    continue;
                                if (!KcpSegment.TryParseMany(packet.Payload.Span, packet.Payload,
                                    owner.CrcEnabled, out var segments))
                                    continue;
                                var matching = segments.Where(s => s.Conv == owner.Conv && s.Cmd == KcpSegment.CmdPush).ToArray();
                                if (matching.Length == 0) continue;
                                var stream = streams[pair.Key];
                                var hasFreshSegments = false;
                                Consume(pair.Key, s =>
                                {
                                    var before = stream.SegmentCount;
                                    foreach (var segment in matching) stream.Add(segment);
                                    hasFreshSegments = stream.SegmentCount > before;
                                    s.Consume(stream);
                                }, packet, () => hasFreshSegments);
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

                while (adaptive?.TakeDiagnostic() is { } diagnostic)
                    Report(progress, GameDataExportStage.Capturing, 10, diagnostic);

                if (decodedTotal > decodedBefore)
                    Report(progress, GameDataExportStage.Capturing, 50, "已解析新的背包数据", hasDataUpdate: true);

                foreach (var pair in states)
                {
                    if (!IsCurrentSession(pair.Key)) continue;
                    var state = pair.Value;
                    if (state.Session.TakeDiagnostic() is { } diagnostic)
                        Report(progress, GameDataExportStage.Capturing, 20, diagnostic);
                    if (!state.Session.PlayerId.OnTick())
                        throw new InvalidOperationException(state.Session.PlayerId.FailureReason);
                    if (state.Session.PlayerId.Current == PlayerIdResolver.Phase.Failed)
                        throw new InvalidOperationException(state.Session.PlayerId.FailureReason);
                    if (!state.Ready)
                        continue;
                    if (writtenVersions.GetValueOrDefault(pair.Key) == state.Inventory.Version)
                        continue;
                    var now = Environment.TickCount64;
                    if (lastAttempts.TryGetValue(pair.Key, out var lastAttempt) && now - lastAttempt < 1000)
                        continue;
                    lastAttempts[pair.Key] = now;
                    // Both protobuf snapshots and playerId are ready before any file is written.
                    var playerId = state.Session.PlayerId.PlayerId;
                    var updateCount = updatesByPlayer.GetValueOrDefault(playerId) + 1;
                    lastResult = await BuildAndWriteAsync(
                        state.Inventory.NormalItems, state.Inventory.Weapons, state.Inventory.Phantoms, state.Inventory.ValidTimeItems,
                        captured, state.Inventory.DecodedFrames, updateCount, sessionStart, progress,
                        firstExport: updateCount == 1, playerId: playerId, sessionKey: state.Session.SessionKey!).ConfigureAwait(false);
                    if (lastResult.OutputJsonPath is not null)
                    {
                        writtenVersions[pair.Key] = state.Inventory.Version;
                    }
                    RememberExport(lastResult);
                }
                if (Environment.TickCount64 - lastReport >= 2000)
                {
                    lastReport = Environment.TickCount64;
                    if (events is not null)
                    {
                        events.Checkpoint();
                        var stats = events.Snapshot();
                        progress.Report(new(GameDataExportStage.Monitoring, 100,
                            $"消息导出中：已接收 {stats.Received}，已写入 {stats.Written}，解析成功 {stats.Decoded}，未知协议 {stats.Unknown}，未解析 {stats.Failed}。",
                            EventExport: stats));
                    }
                    var current = lastVerifiedConnection is { } id && states.TryGetValue(id, out var active)
                        ? active : states.Values.OrderByDescending(s => s.Inventory.ParsedCategories).FirstOrDefault();
                    if (current is { Ready: true } && lastResult is { Success: true }
                        && lastResult.PlayerId == current.Session.PlayerId.PlayerId)
                    {
                        ReportMonitoring(progress, lastResult);
                    }
                    else if (current is { Ready: true } && lastResult is { Success: false }
                        && lastResult.PlayerId == current.Session.PlayerId.PlayerId)
                    {
                        Report(progress, GameDataExportStage.Exporting, 85, lastResult.Message);
                    }
                    else
                    {
                        var message = current?.Session.SessionKey is null
                            ? $"等待游戏登录与会话密钥（UDP {captured} 帧）"
                            : current.Session.PlayerId.Current != PlayerIdResolver.Phase.Resolved
                                ? "已取得会话密钥，正在等待玩家身份信息"
                                : $"玩家 {current.Session.PlayerId.PlayerId}：正在解析背包数据（{current.Inventory.ParsedCategories}/4）";
                        Report(progress, GameDataExportStage.Capturing, 10 + (current?.Inventory.ParsedCategories ?? 0) * 10, message);
                    }
                }
            }
            foreach (var pair in states)
            {
                if (!IsCurrentSession(pair.Key)) continue;
                var state = pair.Value;
                if (state.Ready && writtenVersions.GetValueOrDefault(pair.Key) != state.Inventory.Version)
                {
                    var playerId = state.Session.PlayerId.PlayerId;
                    var updateCount = updatesByPlayer.GetValueOrDefault(playerId) + 1;
                    lastResult = await BuildAndWriteAsync(
                        state.Inventory.NormalItems, state.Inventory.Weapons, state.Inventory.Phantoms, state.Inventory.ValidTimeItems,
                        captured, state.Inventory.DecodedFrames, updateCount, sessionStart, progress,
                        firstExport: updateCount == 1, playerId: playerId,
                        sessionKey: state.Session.SessionKey!).ConfigureAwait(false);
                    RememberExport(lastResult);
                }
            }
            return await CompleteAsync(lastResult ?? Failure("监控已停止，尚未取得完整背包及玩家身份信息，未生成导出文件。", captured, decodedTotal, null)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(progress, GameDataExportStage.Failed, 100, ex.Message);
            return await CompleteAsync(Failure(ex.Message, captured, decodedTotal, null), failed: true).ConfigureAwait(false);
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

        var path = _write(result, sessionStart);
        if (path is not null)
            result = result with { OutputJsonPath = path };

        if (path is null)
            result = result with { Success = false, Message = "JSON 写入失败，保留上次文件，稍后重试。" };
        if (path is null)
            Report(progress, GameDataExportStage.Exporting, 85, result.Message);
        else
            ReportMonitoring(progress, result);

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    private static void ReportMonitoring(IProgress<GameDataExportProgress> progress, GameDataExportResult result) =>
        progress.Report(new(GameDataExportStage.Monitoring, 100,
            $"监控中：玩家 {result.PlayerId}，已更新 {result.UpdateCount} 次\n最新文件：{result.OutputJsonPath}",
            PlayerId: result.PlayerId, OutputJsonPath: result.OutputJsonPath, UpdateCount: result.UpdateCount));

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

        using var rsa = accessor.CreateRsa()
            ?? throw new InvalidOperationException("保存的密钥不是有效的 RSA 私钥，请重新导入 PEM 文件。");
        keyLoader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    }

    private static GameDataExportResult Failure(
        string message,
        int captured,
        int decoded,
        string? sessionKeyHex
    ) =>
        new(false, message, null, null, captured, decoded, sessionKeyHex);

    private readonly record struct ConnectionKey(string ServerIp, int ServerPort, string ClientIp, int ClientPort, uint Conv)
    {
        internal static ConnectionKey From(UdpFrame packet, uint conv) =>
            new(packet.SrcIp, packet.SrcPort, packet.DstIp, packet.DstPort, conv);

        internal bool Matches(UdpFrame packet) => packet.SrcIp == ServerIp && packet.SrcPort == ServerPort
            && packet.DstIp == ClientIp && packet.DstPort == ClientPort;
    }

    private sealed class EventProgress(IProgress<GameDataExportProgress> inner, EventExportSession events)
        : IProgress<GameDataExportProgress>
    {
        public void Report(GameDataExportProgress value) => inner.Report(value with { EventExport = events.Snapshot() });
    }

    private static void Report(
        IProgress<GameDataExportProgress> progress,
        GameDataExportStage stage,
        int percentage,
        string message,
        bool captureReady = false,
        bool hasDataUpdate = false
    ) => progress.Report(new GameDataExportProgress(stage, percentage, message, captureReady, hasDataUpdate));
}
