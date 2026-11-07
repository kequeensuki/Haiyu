using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Domains.Gacha;
using Haiyu.ABI.Services.GameDataExport.Gacha;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>
/// 抽卡记录导出模块。
/// <para>
/// 与其它模块不同：数据不来自 TCP 下行，而是一次 HTTP 查询。
/// 因此本模块<b>不消费消息</b>（<see cref="MessageIds"/> 为 null），只在启动时执行一次拉取，
/// 产物落在 <c>&lt;playerId&gt;/gacha/record.json</c>，与 game-data.json 分离。
/// </para>
/// <para>
/// 参数由主程序通过 <see cref="GameGachaRequest"/> 提供；抓不到 <c>GachaInfoResponse</c>
/// 时没有 record_id，模块报告 NoData 且不发起请求，不伪造空文件。
/// </para>
/// </summary>
internal sealed class GachaRecordExportModule(
    ExportCaptureContext context,
    GameGachaRequest request,
    IGachaRecordEndpoint endpoint) : IGameDataExportModule, ILegacyExportProjection
{
    internal static readonly GameDataExportModuleDescriptor Definition =
        new("gacha", "Toolkit_GameDataExportGacha", "抽卡记录（跨会话累积）", false, false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GameDataExportModuleState? _ended;
    private GameGachaSummary? _summary;
    private string? _error;
    private bool _completed;
    private string? _outputPath;

    public GameDataExportModuleDescriptor Descriptor => Definition;

    /// <summary>不消费任何协议消息：抽卡记录不来自下行。</summary>
    public IReadOnlySet<ushort>? MessageIds => null;

    public void Consume(MessageObservation message) { }

    public void OnSessionChanged(ExportSessionChange change) { }

    /// <summary>拉取与合并是异步的，放在 Complete 阶段执行一次；Checkpoint 不触发网络。</summary>
    public void Checkpoint(bool stopping = false) => context.Output.ThrowIfFailed();

    public GameDataExportModuleResult GetProgress() => new(Definition.Id, Definition.DisplayName,
        _ended ?? (_completed ? GameDataExportModuleState.Completed : GameDataExportModuleState.Waiting),
        _error,
        _summary is { } summary
            ? [new("gachaRecords", summary.RecordCount, summary.PlayerId),
               new("gachaPools", summary.PoolCount, summary.PlayerId)]
            : [],
        [], []);

    public async ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_completed)
                return GetProgress();
            _completed = true;

            if (request.PlayerId <= 0 || string.IsNullOrWhiteSpace(request.RecordId))
            {
                // 没有 record_id 就没有查询凭证。不创建空文件，交由 UI 显示"未收到".
                _error = "未取得抽卡记录凭证（record_id），本次不拉取。";
                _ended = GameDataExportModuleState.NoData;
                return GetProgress();
            }

            if (stop.Error is not null || stop.IncompleteTransport)
            {
                _error = stop.Error ?? "停止时传输不完整，跳过抽卡记录拉取。";
                _ended = GameDataExportModuleState.Incomplete;
                return GetProgress();
            }

            try
            {
                await FetchAndStoreAsync(context.Clock.GetUtcNow()).ConfigureAwait(false);
                _ended = GameDataExportModuleState.Completed;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException
                or TaskCanceledException)
            {
                _error = ex.Message;
                _ended = GameDataExportModuleState.Failed;
            }
            return GetProgress();
        }
        finally { _gate.Release(); }
    }

    private async Task FetchAndStoreAsync(DateTimeOffset now)
    {
        var playerIdText = request.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var languageCode = string.IsNullOrWhiteSpace(request.LanguageCode) ? "zh-Hans" : request.LanguageCode!;
        var recordId = request.RecordId!;
        var cardPoolId = request.CardPoolId ?? "";
        var poolTypes = request.PoolTypes is { Length: > 0 } ? request.PoolTypes : GachaPoolTypes.All;

        // 1) 区服判定：缓存 → 探测（两个常量按偏好顺序）。命中顺带带回首批数据。
        var cache = LoadCache();
        var preferred = request.ServerIds is { Length: > 0 } ids && !string.IsNullOrWhiteSpace(ids[0])
            ? GachaServerIds.RegionOf(ids[0])
            : GachaRegion.Cn;
        var resolver = new GachaServerResolver(endpoint, cache);
        var resolution = await resolver.ResolveAsync(request.PlayerId, PickGivenServer(request.ServerIds),
            preferred, playerIdText, cardPoolId, languageCode, recordId, refresh: false,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var fetched = new List<GachaRecordState>();
        var succeeded = new List<int>();
        var failed = new List<int>();

        if (resolution.FirstRecords is { } firstRecords)
        {
            fetched.AddRange(GachaRecordNormalizer.Normalize(firstRecords, resolution.FirstPoolType));
            succeeded.Add(resolution.FirstPoolType);
        }

        // 2) 逐池拉取；探测已覆盖的池不重复请求。
        foreach (var poolType in poolTypes)
        {
            if (succeeded.Contains(poolType)) continue;
            GachaQueryResult result;
            try
            {
                result = await endpoint.QueryAsync(resolution.ServerId, resolution.IsCn, poolType, playerIdText,
                    cardPoolId, languageCode, recordId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                failed.Add(poolType);
                continue;
            }

            if (result.IsSuccess)
            {
                // 注意：code=0 且 0 条是合法命中（该池没抽过），不是失败。
                fetched.AddRange(GachaRecordNormalizer.Normalize(result.Records, poolType));
                succeeded.Add(poolType);
            }
            else
            {
                failed.Add(poolType);
                if (result.Code == GachaQueryResult.ServerMismatch)
                {
                    // 缓存值失效（换服/合服）：清掉缓存，让下一次重新探测。
                    cache.Remove(request.PlayerId);
                    await PersistCacheAsync(cache, now).ConfigureAwait(false);
                }
            }
        }

        // 3) 读旧文件 → 只增合并 → 原子写。
        var path = context.Output.Resolve(GachaRecordStore.RelativePath(request.PlayerId));
        var existing = GachaRecordStore.LoadExisting(path);
        var query = new GameGachaQuery(resolution.ServerId, resolution.Area, recordId, cardPoolId, languageCode,
            resolution.ResolvedBy, succeeded, failed);
        var data = GachaRecordStore.Build(request.PlayerId, existing, fetched, query, now);
        var bytes = JsonExportStore.Encode(data, ABIJsonContext.Default.GameGachaData);

        // 抽卡是"读旧文件 → 合并 → 原子写"的一次性动作（跨会话累积需要对旧文件读改写），
        // 因此直接走存储的原子写，而不是 Snapshot 的排队回执：回执由调用方串行 drain，
        // 在模块内部等待它会与调度顺序耦合（可能永远等不到）。
        _outputPath = await JsonExportStore.WriteAtomicAsync(path, bytes).ConfigureAwait(false)
            ?? throw new IOException("抽卡记录写入失败，保留上次文件。");

        await PersistCacheAsync(cache, now).ConfigureAwait(false);
        _summary = new GameGachaSummary(request.PlayerId, data.Totals.Count, data.Totals.PoolCount,
            resolution.ServerId, resolution.Area, _outputPath);
        if (failed.Count > 0)
            _error = $"部分池拉取失败：{string.Join(",", failed)}";
    }

    private static string Owner(int playerId) => "gacha:" + playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>显式给定的 serverId 才算 given；空数组表示"按缓存/探测"。</summary>
    private static string? PickGivenServer(string[]? serverIds) =>
        serverIds is { Length: 1 } && !string.IsNullOrWhiteSpace(serverIds[0]) ? serverIds[0] : null;

    /// <summary>缓存与抽卡记录同目录（<c>&lt;playerId&gt;/gacha/servers.json</c>），每玩家自包含。</summary>
    private GachaServerCache LoadCache()
    {
        var path = context.Output.Resolve(GachaServerCache.RelativePath(request.PlayerId));
        try
        {
            return GachaServerCache.Parse(File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GachaServerCache.Parse(null);
        }
    }

    /// <summary>缓存写失败不阻断导出：最坏下次多探测一次。</summary>
    private async Task PersistCacheAsync(GachaServerCache cache, DateTimeOffset now)
    {
        try
        {
            var path = context.Output.Resolve(GachaServerCache.RelativePath(request.PlayerId));
            await JsonExportStore.WriteAtomicAsync(path, cache.Encode(now)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public GameDataExportResult ProjectResult(GameDataExportResult result) => result with
    {
        Gacha = _summary,
    };

    /// <summary>进度里不新增字段：抽卡是单次拉取，失败原因已写在模块状态中。</summary>
    public GameDataExportProgress ProjectProgress(GameDataExportProgress progress) => progress;

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
