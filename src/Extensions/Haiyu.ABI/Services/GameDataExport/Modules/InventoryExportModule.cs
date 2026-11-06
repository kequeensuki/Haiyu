using System.Globalization;
using ABI.Models;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>四种独立全量响应的 LatestComplete 投影；不假设服务器提供跨响应事务编号。</summary>
internal sealed class InventoryExportModule(ExportCaptureContext context,
    Func<GameDataExportResult, DateTimeOffset, string?>? legacyWrite = null) : IGameDataExportModule, ILegacyExportProjection
{
    internal static readonly GameDataExportModuleDescriptor Definition = new("inventory", "Toolkit_GameDataExportInventory", "背包快照", true, true);
    private static readonly Dictionary<ushort, string> Sources = new()
    {
        [WavesMessageIds.NormalItemResponse] = "normalItems",
        [WavesMessageIds.WeaponItemResponse] = "weapons",
        [WavesMessageIds.PhantomItemResponse] = "phantoms",
        [WavesMessageIds.ValidTimeItemResponse] = "validTimeItems",
    };
    private sealed class State(string session, int player, TimeProvider clock)
    {
        internal int Player = player;
        internal readonly AggregateState<ProtocolMessage> Aggregate = new(session, Sources.Values.Select(id => new SourceDefinition(id)),
            m => m.Fields.Values.OfType<IReadOnlyList<object>>().Sum(list => list.Count), clock: clock);
        internal AggregateSnapshot<ProtocolMessage>? Candidate;
        internal bool Pending;
        internal bool Superseded;
        internal long? LastAttempt;
        internal int Decoded;
        internal readonly Dictionary<string, int> Sizes = new();
        internal string? Error;
    }
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<int, GameDataExportPlayerSummary> _players = new();
    private readonly Dictionary<string, GameDataExportArtifact> _artifacts = new(StringComparer.Ordinal);
    private State? _current;
    private long _progressRevision;
    private int _reportedDecoded;
    public long ProgressRevision => _progressRevision;
    private GameDataExportResult? _last;
    private GameDataExportModuleState? _ended;
    public GameDataExportModuleDescriptor Descriptor => Definition;
    public IReadOnlySet<ushort> MessageIds { get; } = Sources.Keys.ToHashSet();

    public void Consume(MessageObservation message)
    {
        if (!message.IsCurrentSession || !message.ConnectionVerified || message.SessionId.StartsWith("unverified-", StringComparison.Ordinal)) return;
        var state = Get(message.SessionId, message.PlayerId ?? 0);
        _current = state;
        if (message.Status is not null || message.Protocol?.Message is not { } model
            || !message.Protocol.DecodeStatus.StartsWith("decoded", StringComparison.Ordinal))
        {
            state.Error = "背包消息解密或 protobuf 解析失败，保留上次文件。";
            return;
        }
        var sourceId = Sources[message.Frame.MsgId];
        var size = message.Plain?.Length ?? 0;
        var total = _states.Values.Sum(s => s.Sizes.Values.Sum(v => (long)v)) - state.Sizes.GetValueOrDefault(sourceId) + size;
        if (total > 32 * 1024 * 1024) throw new IOException("背包聚合状态超过内存预算。");
        state.Sizes[sourceId] = size;
        state.Decoded++;
        _progressRevision++;
        if (!state.Aggregate.Source(sourceId).Full(model, provenance: new(message.EventIndex, message.EventIndex,
            message.ReceivedAt ?? context.Clock.GetUtcNow(), message.ConnectionId)))
            state.Error = "背包来源超过限制。";
    }
    private State Get(string session, int player)
    {
        if (_states.TryGetValue(session, out var state))
        {
            if (player > 0 && state.Player != player)
            {
                if (state.Player > 0) throw new InvalidDataException("会话玩家身份冲突。");
                state.Player = player;
                Claim(state, session);
            }
            return state;
        }
        if (_states.Count >= 4096) throw new IOException("背包会话数量达到上限。");
        state = new(session, player, context.Clock);
        _states.Add(session, state);
        if (player > 0) Claim(state, session);
        return state;
    }
    private void Claim(State state, string session)
    {
        context.Output.Claim(Relative(state.Player), session);
        // 同一玩家的新登录代次拥有目标；旧代次的迟到回执不能覆盖它。
        foreach (var old in _states.Values.Where(s => s != state && s.Player == state.Player)) old.Superseded = true;
    }
    public void OnSessionChanged(ExportSessionChange change)
    {
        if (!change.IsCurrent || !change.Verified) return;
        _progressRevision++;
        if (change.Kind == ExportSessionChangeKind.IdentityResolved && change.PlayerId is > 0)
            _current = Get(change.SessionId, change.PlayerId.Value);
        else if (change.Kind == ExportSessionChangeKind.LoginStarted) _current = Get(change.SessionId, 0);
        else if (change.Kind == ExportSessionChangeKind.Logout) _current = null;
    }
    private string Relative(int player) => Path.Combine(player.ToString(CultureInfo.InvariantCulture), $"game-data-{context.StartedAt:yyyyMMdd-HHmmss}.json");
    public void Checkpoint(bool stopping = false)
    {
        foreach (var state in _states.Values)
        {
            if (state.Player <= 0 || state.Pending || state.Superseded || (!stopping && state.LastAttempt is { } lastAttempt
                && context.Clock.GetElapsedTime(lastAttempt) < TimeSpan.FromSeconds(1))) continue;
            state.Candidate ??= state.Aggregate.Build();
            if (state.Candidate is not { } snapshot) continue;
            var data = Map(snapshot.Values);
            var count = _players.GetValueOrDefault(state.Player)?.UpdateCount + 1 ?? 1;
            var result = new GameDataExportResult(true, "背包快照已更新。", data, null,
                context.CapturedFrames, state.Decoded, context.LegacyKey(snapshot.Epoch), state.Player, count);
            var bytes = InventoryJsonWriter.Encode(result, context.StartedAt);
            state.Pending = true;
            state.LastAttempt = context.Clock.GetTimestamp();
            try
            {
                context.Output.Snapshot(Definition.Id, Relative(state.Player), snapshot.Epoch, snapshot.Revision, bytes,
                    receipt => Acknowledge(state, snapshot, result, receipt),
                    legacyWrite is null ? null : () => legacyWrite(result, context.StartedAt));
            }
            catch
            {
                state.Pending = false;
                throw;
            }
        }
    }
    private void Acknowledge(State state, AggregateSnapshot<ProtocolMessage> snapshot, GameDataExportResult result, OutputReceipt receipt)
    {
        state.Pending = false;
        _progressRevision++;
        if (receipt.Superseded) { state.Candidate = null; return; }
        if (receipt.Error is not null) { state.Error = receipt.Error; return; }
        if (!state.Aggregate.Acknowledge(snapshot)) return;
        state.Candidate = null;
        state.Error = null;
        result = result with { UpdateCount = (_players.GetValueOrDefault(state.Player)?.UpdateCount ?? 0) + 1 };
        _last = result with { OutputJsonPath = receipt.Path };
        var data = result.Inventory!;
        _players[state.Player] = new(state.Player, result.UpdateCount, data.NormalItems.Count, data.Weapons.Count,
            data.Phantoms.Count, data.ValidTimeItems.Count, receipt.Path!);
        _artifacts[Relative(state.Player)] = new(Definition.Id, state.Player, snapshot.Epoch, receipt.Path!, "json", snapshot.Revision,
            snapshot.Consistency.ToString(), snapshot.Sources.Select(source => source with { SessionId = snapshot.Epoch, PlayerId = state.Player }).ToArray());
    }
    public GameDataExportModuleResult GetProgress() => new(Definition.Id, Definition.DisplayName,
        _ended ?? (_current?.Aggregate.Ready == true ? GameDataExportModuleState.Running : GameDataExportModuleState.Waiting),
        string.Join("；", _states.Values.Where(s => s.Error is not null).Select(s => $"玩家 {s.Player}：{s.Error}")) is { Length: > 0 } error ? error : null,
        new GameDataExportCounter[] { new("updates", _players.Values.Sum(s => (long)s.UpdateCount)), new("players", _players.Count),
            new("currentPlayer", _current?.Player ?? 0), new("currentPlayerUpdates", _players.GetValueOrDefault(_current?.Player ?? 0)?.UpdateCount ?? 0),
            new("candidateRevision", _current?.Aggregate.CandidateRevision ?? 0), new("persistedRevision", _current?.Aggregate.PersistedRevision ?? 0) }
            .Concat(_players.Values.SelectMany(player => new GameDataExportCounter[]
            {
                new("updates", player.UpdateCount, player.PlayerId),
                new("normalItems", player.NormalItemCount, player.PlayerId),
                new("weapons", player.WeaponCount, player.PlayerId),
                new("phantoms", player.PhantomCount, player.PlayerId),
                new("validTimeItems", player.ValidTimeItemCount, player.PlayerId),
            })).ToArray(),
        _states.SelectMany(pair => pair.Value.Aggregate.Progress().Select(source => source with
        { SessionId = pair.Key, PlayerId = pair.Value.Player > 0 ? pair.Value.Player : null })).ToArray(), _artifacts.Values.ToArray());
    public async ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop)
    {
        await context.Output.FlushAsync().ConfigureAwait(false);
        Checkpoint(true);
        await context.Output.FlushAsync().ConfigureAwait(false);
        // 写入期间到达的新向量不能被旧回执清除；停止时再完成一次候选提交。
        Checkpoint(true);
        await context.Output.FlushAsync().ConfigureAwait(false);
        _ended = _states.Values.Any(s => s.Error is not null) ? GameDataExportModuleState.Failed
            : stop.Error is not null || stop.IncompleteTransport ? GameDataExportModuleState.Incomplete
            : !_states.Values.Any(s => s.Aggregate.HasData) && _artifacts.Count == 0 ? GameDataExportModuleState.NoData
            : _states.Values.Any(s => !s.Superseded && (s.Player <= 0 || !s.Aggregate.Ready || s.Candidate is not null))
                ? GameDataExportModuleState.Incomplete : GameDataExportModuleState.Completed;
        return GetProgress();
    }
    public GameDataExportResult ProjectResult(GameDataExportResult result) => result with
    {
        Inventory = _last?.Inventory, OutputJsonPath = _last?.OutputJsonPath, PlayerId = _last?.PlayerId ?? 0,
        UpdateCount = _last?.UpdateCount ?? 0, SessionKeyHex = _last?.SessionKeyHex,
        DecodedFrames = _states.Values.Sum(s => s.Decoded), PlayerSummaries = _players.Values.OrderBy(p => p.PlayerId).ToArray()
    };
    public GameDataExportProgress ProjectProgress(GameDataExportProgress progress)
    {
        var decoded = _states.Values.Sum(s => s.Decoded);
        var changed = decoded != _reportedDecoded;
        _reportedDecoded = decoded;
        var player = _current?.Player ?? 0;
        var summary = _players.GetValueOrDefault(player);
        return progress with { PlayerId = player, UpdateCount = summary?.UpdateCount ?? 0,
            Stage = _current?.Aggregate.PersistedRevision > 0 ? GameDataExportStage.Monitoring : GameDataExportStage.Capturing,
            HasDataUpdate = changed,
            OutputJsonPath = summary?.OutputJsonPath,
            Message = player > 0 ? $"玩家 {player}：已更新 {summary?.UpdateCount ?? 0} 次" : progress.Message };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static GameInventoryData Map(IReadOnlyDictionary<string, ProtocolMessage> values) => new(
        Array.AsReadOnly(values["normalItems"].Messages(3).Select(item => new GameNormalItem(
            item.Int32(1), item.Int32(2), item.Int64(4))).ToArray()),
        Array.AsReadOnly(values["weapons"].Messages(6).Select(item => new GameWeaponItem(
            item.Int32(1),
            item.Int32(2),
            item.Int32(4),
            item.Int32(5),
            item.Int32(6),
            item.Int32(7),
            item.Int32(8)
        )).ToArray()),
        Array.AsReadOnly(values["phantoms"].Messages(3).Select(item => new GamePhantomItem(
            item.Int32(1),
            item.Int32(2),
            item.Int32(4),
            item.Int32(5),
            Props(item, 6),
            Props(item, 7),
            item.Int32(8),
            item.Int32(9),
            item.Int32(12)
        )).ToArray()),
        Array.AsReadOnly(values["validTimeItems"].Messages(14).Select(item => new GameValidTimeItem(
            item.Int32(1), item.Int32(2), item.Int32(3), item.Int64(4))).ToArray())
    );

    private static IReadOnlyList<GamePhantomProp> Props(ProtocolMessage message, int field) =>
        Array.AsReadOnly(message.Messages(field).Select(item => new GamePhantomProp(item.Int32(1), item.Int32(2))).ToArray());
}
