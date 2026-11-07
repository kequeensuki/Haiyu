using System.Globalization;
using ABI.Models;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Gacha;
using Haiyu.ABI.Services.GameDataExport.Gacha;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>
/// 从已验证会话的 GachaInfoResponse 获取凭证，在停止阶段按玩家查询并累积历史。
/// 同账号只采用最新登录代次；不同账号分别输出，历史内容来自 HTTP。
/// </summary>
internal sealed class GachaRecordExportModule(ExportCaptureContext context, GameGachaRequest request,
    IGachaRecordEndpoint endpoint) : IGameDataExportModule, ILegacyExportProjection
{
    internal static readonly GameDataExportModuleDescriptor Definition =
        new("gacha", "Toolkit_GameDataExportGacha", "抽卡记录（跨会话累积）", false, false);
    private sealed class SessionState(string session, long order, GameGachaRequest parameters)
    {
        internal readonly string Session = session;
        internal readonly long Order = order;
        internal GameGachaRequest Parameters = parameters;
        internal string? Error;
    }
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, GameGachaSummary> _summaries = new();
    private readonly Dictionary<int, GameDataExportArtifact> _artifacts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GameDataExportModuleState? _ended;
    private GameGachaSummary? _summary;
    private string? _error;
    private long _revision;
    public long ProgressRevision => _revision;
    public GameDataExportModuleDescriptor Descriptor => Definition;
    public IReadOnlySet<ushort> MessageIds { get; } = new HashSet<ushort> { WavesMessageIds.GachaInfoResponse };

    private SessionState Get(string session, int playerId)
    {
        if (!_sessions.TryGetValue(session, out var state))
        {
            if (_sessions.Count >= 4096) throw new IOException("抽卡会话数量达到上限。");
            // 测试/高级调用的选项不向生产会话传播账号或凭证。
            state = new(session, ++_revision, request with { PlayerId = playerId, RecordId = null, CardPoolId = null });
            _sessions.Add(session, state);
        }
        if (playerId > 0)
        {
            if (state.Parameters.PlayerId > 0 && state.Parameters.PlayerId != playerId)
                throw new InvalidDataException("抽卡会话玩家身份冲突。");
            state.Parameters = state.Parameters with { PlayerId = playerId };
        }
        return state;
    }

    public void Consume(MessageObservation message)
    {
        if (_ended is not null || !message.IsCurrentSession || !message.ConnectionVerified
            || message.SessionId.StartsWith("unverified-", StringComparison.Ordinal)) return;
        var state = Get(message.SessionId, message.PlayerId ?? 0);
        _revision++;
        if (message.Status is not null || message.Protocol?.Message is not { } model
            || !message.Protocol.DecodeStatus.StartsWith("decoded", StringComparison.Ordinal))
        {
            state.Parameters = state.Parameters with { RecordId = null };
            state.Error = "抽卡凭证消息解码失败，保留历史。";
            return;
        }
        if (model.Int32(6) != 0) return; // 失败响应不刷新凭证。
        var recordId = model.String(15);
        if (string.IsNullOrWhiteSpace(recordId))
        {
            state.Parameters = state.Parameters with { RecordId = null };
            state.Error = "抽卡凭证消息缺少 record_id。";
            return;
        }
        var resource = model.Messages(1).Select(info => info.String(12)).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        state.Parameters = state.Parameters with { RecordId = recordId, CardPoolId = resource ?? "" };
        state.Error = null;
    }

    public void OnSessionChanged(ExportSessionChange change)
    {
        if (!change.Verified || _ended is not null) return;
        if (change.Kind == ExportSessionChangeKind.IdentityResolved && change.PlayerId is > 0
            && (change.IsCurrent || _sessions.ContainsKey(change.SessionId)))
            Get(change.SessionId, change.PlayerId.Value);
        else if (change.IsCurrent && change.Kind == ExportSessionChangeKind.LoginStarted)
            Get(change.SessionId, 0);
    }

    public void Checkpoint(bool stopping = false) => context.Output.ThrowIfFailed();
    public GameDataExportModuleResult GetProgress() => new(Definition.Id, Definition.DisplayName,
        _ended ?? GameDataExportModuleState.Waiting, _error,
        _summaries.Values.SelectMany(summary => new GameDataExportCounter[] {
            new("gachaRecords", summary.RecordCount, summary.PlayerId), new("gachaPools", summary.PoolCount, summary.PlayerId)
        }).ToArray(), [], _artifacts.Values.ToArray());

    public async ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ended is not null) return GetProgress();
            if (stop.Error is not null || stop.IncompleteTransport)
            {
                _error = stop.Error ?? "停止时传输不完整，跳过抽卡记录拉取。";
                _ended = GameDataExportModuleState.Incomplete;
                return GetProgress();
            }
            // 保留显式调用接口；生产默认只使用消息取得的会话参数。
            if (_sessions.Count == 0 && request.PlayerId > 0)
                _sessions.Add("explicit-gacha", new("explicit-gacha", ++_revision, request));
            var states = _sessions.Values.Where(s => s.Parameters.PlayerId > 0)
                .GroupBy(s => s.Parameters.PlayerId).Select(g => g.MaxBy(s => s.Order)!).ToArray();
            var outcomes = new List<GameDataExportModuleState>();
            var errors = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var state in states)
            {
                var parameters = state.Parameters;
                if (string.IsNullOrWhiteSpace(parameters.RecordId))
                {
                    outcomes.Add(state.Error is null ? GameDataExportModuleState.NoData : GameDataExportModuleState.Failed);
                    errors.Add(state.Error ?? "未取得抽卡记录凭证（record_id），本次不拉取。");
                    continue;
                }
                try
                {
                    var complete = await FetchAndStoreAsync(state.Session, parameters, timeout.Token).ConfigureAwait(false);
                    outcomes.Add(complete ? GameDataExportModuleState.Completed : GameDataExportModuleState.Incomplete);
                    if (!complete) errors.Add("部分抽卡池查询失败，已保存成功池的数据，结果不完整。");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException
                    or OperationCanceledException or InvalidOperationException)
                {
                    outcomes.Add(GameDataExportModuleState.Failed);
                    errors.Add(ex is OperationCanceledException ? "抽卡查询超时，保留历史。" : ex.Message);
                }
            }
            if (_sessions.Values.Any(s => s.Parameters.PlayerId <= 0 && s.Parameters.RecordId is not null))
            {
                outcomes.Add(GameDataExportModuleState.Incomplete);
                errors.Add("抽卡凭证未绑定玩家身份。");
            }
            _ended = outcomes.Contains(GameDataExportModuleState.Failed) ? GameDataExportModuleState.Failed
                : outcomes.Contains(GameDataExportModuleState.Incomplete) ? GameDataExportModuleState.Incomplete
                : outcomes.Count == 0 || outcomes.All(s => s == GameDataExportModuleState.NoData) ? GameDataExportModuleState.NoData
                : outcomes.Contains(GameDataExportModuleState.NoData) ? GameDataExportModuleState.Incomplete
                : GameDataExportModuleState.Completed;
            _error = errors.Count > 0 ? string.Join("；", errors.Distinct()) : null;
            _revision++;
            return GetProgress();
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> FetchAndStoreAsync(string session, GameGachaRequest parameters, CancellationToken token)
    {
        var player = parameters.PlayerId;
        var now = context.Clock.GetUtcNow();
        var playerText = player.ToString(CultureInfo.InvariantCulture);
        // 游戏语言来源尚未接入；保持既有简体中文缺省，不从应用 UI 语言猜测。
        var language = string.IsNullOrWhiteSpace(parameters.LanguageCode) ? "zh-Hans" : parameters.LanguageCode;
        var cardPool = parameters.CardPoolId ?? "";
        var recordId = parameters.RecordId!;
        var pools = parameters.PoolTypes is { Length: > 0 } requested ? requested.Distinct().ToArray() : GachaPoolTypes.All;
        if (pools.Any(pool => pool <= 0)) throw new InvalidDataException("抽卡请求池编号无效。");
        var cache = LoadCache(player);
        var given = parameters.ServerIds is { Length: 1 } ids ? ids[0] : null;
        var preferred = parameters.ServerIds is { Length: > 0 } candidates
            ? GachaServerIds.RegionOf(candidates[0]) : GachaRegion.Cn;
        var resolver = new GachaServerResolver(endpoint, cache);
        var resolution = await resolver.ResolveAsync(player, given, preferred, playerText, cardPool, language, recordId,
            false, token).ConfigureAwait(false);
        var fetched = new List<GachaRecordState>();
        var succeeded = new List<int>();
        var failed = new List<int>();
        for (var attempt = 0; ; attempt++)
        {
            fetched.Clear(); succeeded.Clear(); failed.Clear();
            if (resolution.FirstRecords is { } first && pools.Contains(resolution.FirstPoolType))
            {
                fetched.AddRange(GachaRecordNormalizer.Normalize(first, resolution.FirstPoolType));
                succeeded.Add(resolution.FirstPoolType);
            }
            var retry = false;
            foreach (var pool in pools)
            {
                if (succeeded.Contains(pool)) continue;
                GachaQueryResult result;
                try
                {
                    result = await endpoint.QueryAsync(resolution.ServerId, resolution.IsCn, pool, playerText,
                        cardPool, language, recordId, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
                { failed.Add(pool); continue; }
                if (result.Code == GachaQueryResult.InvalidRecord)
                    throw new InvalidDataException(GachaServerResolver.DescribeFailure(result));
                if (result.Code == GachaQueryResult.ServerMismatch)
                {
                    cache.Remove(player);
                    await PersistCacheAsync(player, cache, now).ConfigureAwait(false);
                    if (resolution.ResolvedBy == "cache" && attempt == 0)
                    {
                        resolution = await resolver.ResolveAsync(player, null,
                            resolution.IsCn ? GachaRegion.Global : GachaRegion.Cn, playerText, cardPool, language,
                            recordId, true, token).ConfigureAwait(false);
                        retry = true;
                        break;
                    }
                    throw new InvalidDataException("抽卡区服在查询期间不匹配，本次不写入历史。");
                }
                if (!result.IsSuccess) { failed.Add(pool); continue; }
                fetched.AddRange(GachaRecordNormalizer.Normalize(result.Records, pool));
                succeeded.Add(pool);
            }
            if (!retry) break;
        }
        if (succeeded.Count == 0) throw new InvalidDataException("所有抽卡池查询失败，保留历史且不创建空记录文件。");
        token.ThrowIfCancellationRequested();
        var relative = GachaRecordStore.RelativePath(player);
        context.Output.Claim(relative, session);
        await context.Output.FlushAsync().ConfigureAwait(false);
        var existing = GachaRecordStore.LoadExisting(context.Output.Resolve(relative), player);
        var data = GachaRecordStore.Build(player, existing, fetched,
            new(resolution.ServerId, resolution.Area, recordId, cardPool, language, resolution.ResolvedBy, succeeded, failed), now);
        OutputReceipt? receipt = null;
        context.Output.Snapshot(Definition.Id, relative, session, 1,
            JsonExportStore.Encode(data, ABIJsonContext.Default.GameGachaData), result => receipt = result);
        // Flush 自行 drain 回执，不等待调度器以后才能执行的回调。
        await context.Output.FlushAsync().ConfigureAwait(false);
        if (receipt is null || receipt.Error is not null || receipt.Superseded || receipt.Path is null)
            throw new IOException(receipt?.Error ?? "抽卡记录写入未确认，保留历史。");
        _summary = new(player, data.Totals.Count, data.Totals.PoolCount, resolution.ServerId, resolution.Area, receipt.Path,
            failed.Count == 0 ? null : $"抽卡池查询失败：{string.Join(",", failed)}");
        _summaries[player] = _summary;
        _artifacts[player] = new(Definition.Id, player, session, receipt.Path, "json", 1,
            failed.Count == 0 ? "Complete" : "Partial", []);
        await PersistCacheAsync(player, cache, now).ConfigureAwait(false);
        return failed.Count == 0;
    }

    private GachaServerCache LoadCache(int player)
    {
        var path = context.Output.Resolve(GachaServerCache.RelativePath(player));
        try { return GachaServerCache.Parse(File.Exists(path) ? File.ReadAllBytes(path) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return GachaServerCache.Parse(null); }
    }
    private async Task PersistCacheAsync(int player, GachaServerCache cache, DateTimeOffset now)
    {
        try { await JsonExportStore.WriteAtomicAsync(context.Output.Resolve(GachaServerCache.RelativePath(player)), cache.Encode(now)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 缓存可重建。 */ }
    }
    public GameDataExportResult ProjectResult(GameDataExportResult result) => result with { Gacha = _summary };
    public GameDataExportProgress ProjectProgress(GameDataExportProgress progress) => progress;
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}
