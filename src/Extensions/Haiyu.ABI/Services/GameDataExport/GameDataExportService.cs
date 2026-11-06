using System.Buffers.Binary;
using System.Runtime.Versioning;
using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Storage;
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
public sealed partial class GameDataExportService
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
    private readonly Action<SessionKeyLoader> _loadKey;
    private readonly ExportModuleRegistry _registry;
    private readonly string _root;

    internal GameDataExportService(Func<IUdpCapture> capture, Action<SessionKeyLoader> loadKey,
        ExportModuleRegistry registry, string root)
    { _createCapture = capture; _loadKey = loadKey; _registry = registry; _root = root; }

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
        var context = new ExportCaptureContext(new JsonExportStore(_root));
        ExportCoordinator? coordinator = null;
        var captured = 0;
        var lastReport = Environment.TickCount64;
        var connections = new Dictionary<uint, KcpConnection>();
        var streams = new Dictionary<uint, KcpStreamRebuilder>();
        var states = new Dictionary<uint, CaptureConnectionState>();
        var endpoints = new Dictionary<uint, ConnectionKey>();
        var connectionIds = new Dictionary<ConnectionKey, uint>();
        uint nextConnectionId = 0;
        var sessionFamilies = new Dictionary<uint, uint>();
        var pendingResumes = new Dictionary<uint, (uint ConnectionId, string SessionId)>();
        var adaptive = capture as IAdaptiveUdpCapture;
        uint? lastVerifiedConnection = null;
        var keyReported = false;
        async Task<GameDataExportResult> CompleteAsync(string? error = null)
        {
            capture.Stop();
            if (coordinator is null) return new(false, error ?? "无法初始化导出模块。", null, null, captured, context.DecodedFrames, null);
            var pending = streams.Any(p => p.Value.SegmentCount > 0 && states.TryGetValue(p.Key, out var state) && state.Session.HasVerifiedTraffic);
            var results = await coordinator.FinishAsync(new(error, pending)).ConfigureAwait(false);
            return coordinator.LegacyResult(results);
        }

        void RemoveConnection(uint conv)
        {
            if (streams.Remove(conv, out var stream)) stream.Clear();
            connections.Remove(conv);
            if (endpoints.Remove(conv, out var endpoint)) connectionIds.Remove(endpoint);
            sessionFamilies.Remove(conv);
            pendingResumes.Remove(conv);
            if (states.Remove(conv, out var state))
            {
                coordinator?.SessionChanged(new(ExportSessionChangeKind.ConnectionRetired, state.Session.SessionId, null,
                    state.Session.ConfirmedPlayerId, conv, IsCurrent: false, Verified: state.Session.HasVerifiedTraffic));
                state.Dispose();
            }
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
            var batch = new MessageBatch(() => context.RememberLegacyMetadata(state.Session));
            state.Observer = batch;
            try { read(state); }
            catch (InvalidDataException ex) when ((!verifiedBefore && !state.Session.HasVerifiedTraffic) || !IsCurrentSession(conv))
            {
                // 未确认的候选连接不能让已确认的游戏会话终止。
                DropCandidate(conv, ex.Message);
                return;
            }

            if (!keyReported && state.Session.SessionKey is not null)
            {
                keyReported = true;
                progress.Report(new(GameDataExportStage.Decrypting, 20, "已取得会话密钥，等待服务器数据。",
                    SessionKeyAcquired: true));
            }
            if (state.Session.HasVerifiedTraffic && (state.Session.VerifiedFrames != framesBefore || (verifiedBefore && freshKcp?.Invoke() == true)))
            {
                // 新连接确认后淘汰同一会话的旧传输，防止旧的待写快照覆盖新数据。
                var family = sessionFamilies[conv];
                var identityChanged = state.Session.IdentityVersion != identityBefore;
                if (!identityChanged && !IsCurrentSession(conv))
                {
                    context.RememberLegacyMetadata(state.Session);
                    batch.Dispatch(coordinator!, false, state.Session.HasVerifiedTraffic);
                    return;
                }
                // 恢复通知只在有效流量确认后发出；候选换钥属于新登录，不能伪装成原登录恢复。
                if (pendingResumes.Remove(conv, out var resumed) && resumed.SessionId == state.Session.SessionId)
                    coordinator!.SessionChanged(new(ExportSessionChangeKind.TransportResumed, state.Session.SessionId,
                        resumed.SessionId, state.Session.ConfirmedPlayerId, conv, resumed.ConnectionId));
                foreach (var previous in states.Keys.Where(k => k != conv && sessionFamilies[k] == family).ToArray())
                    RemoveConnection(previous);
                // 旧连接的后台 KCP 包只能确认捕获接口，不能把当前玩家切回旧账号。
                if (identityChanged || state.Session.VerifiedFrames != framesBefore || lastVerifiedConnection is null)
                    lastVerifiedConnection = conv;
                adaptive?.ConfirmInterface(packet);
            }
            context.RememberLegacyMetadata(state.Session);
            batch.Dispatch(coordinator!, IsCurrentSession(conv), state.Session.HasVerifiedTraffic);
            context.DecodedFrames += batch.DecodedCount;

        }
        try
        {
            coordinator = new ExportCoordinator(context, _registry, BuiltInExportModules.Select(request));
            Report(progress, GameDataExportStage.Capturing, 0, "正在准备网卡");
            _loadKey(_keyLoader);
            capture.Start();
            Report(progress, GameDataExportStage.Capturing, 5, "正在等待游戏连接", captureReady: true);
            coordinator.QueueIndex();
            while (!cancellationToken.IsCancellationRequested)
            {
                coordinator.Checkpoint();
                if (coordinator.TakeProgressChanged()) progress.Report(coordinator.LegacyProgress());
                foreach (var pair in streams.ToArray())
                {
                    try { pair.Value.ValidatePending(); }
                    catch (InvalidDataException ex) when (!states[pair.Key].Session.HasVerifiedTraffic || !IsCurrentSession(pair.Key))
                    {
                        DropCandidate(pair.Key, ex.Message);
                    }
                }
                var packet = capture.TryRead();
                if (packet is not null)
                {
                    try
                    {
                        context.CapturedFrames = ++captured;
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
                                    pendingResumes[connectionId] = (previous, session.Session.SessionId);
                                    sessionFamilies[connectionId] = sessionFamilies[previous];
                                }
                                else
                                {
                                    states[connectionId] = new CaptureConnectionState(_keyLoader.CreateSession());
                                    sessionFamilies[connectionId] = connectionId;
                                }
                            }
                            var eventState = states[connectionId];
                            eventState.ConnectionId = connectionId;

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
                }
                coordinator.Checkpoint();
                if (coordinator.TakeProgressChanged())
                {
                    progress.Report(coordinator.LegacyProgress());
                }
                if (Environment.TickCount64 - lastReport >= 2000)
                {
                    lastReport = Environment.TickCount64;
                    coordinator.CheckpointMetadata();
                    progress.Report(coordinator.LegacyProgress());
                }
            }
            return await CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(progress, GameDataExportStage.Failed, 100, ex.Message);
            return await CompleteAsync(ex.Message).ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams.Values)
                stream.Clear();
            foreach (var state in states.Values)
                state.Dispose();
            _keyLoader.Dispose();
            if (coordinator is not null) await coordinator.DisposeAsync().ConfigureAwait(false);
            else await context.Output.DisposeAsync().ConfigureAwait(false);
        }
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

        using var rsa = accessor.CreateRsa()
            ?? throw new InvalidOperationException("保存的密钥不是有效的 RSA 私钥，请重新导入 PEM 文件。");
        keyLoader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    }

    private readonly record struct ConnectionKey(string ServerIp, int ServerPort, string ClientIp, int ClientPort, uint Conv)
    {
        internal static ConnectionKey From(UdpFrame packet, uint conv) =>
            new(packet.SrcIp, packet.SrcPort, packet.DstIp, packet.DstPort, conv);

        internal bool Matches(UdpFrame packet) => packet.SrcIp == ServerIp && packet.SrcPort == ServerPort
            && packet.DstIp == ClientIp && packet.DstPort == ClientPort;
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
