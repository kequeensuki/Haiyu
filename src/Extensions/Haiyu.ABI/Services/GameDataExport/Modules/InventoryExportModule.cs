using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Adapters;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>独立全量基线与有序增删改的 LatestComplete 投影；不假设跨响应事务或服务器版本。</summary>
internal sealed class InventoryExportModule(ExportCaptureContext context,
    Func<GameDataExportResult, DateTimeOffset, string?>? legacyWrite = null) : IGameDataExportModule, ILegacyExportProjection
{
    internal static readonly GameDataExportModuleDescriptor Definition = new("inventory", "Toolkit_GameDataExportInventory", "玩家数据快照（背包、邮件、成就、角色）", true, true);
    private sealed class State(string session, int player, TimeProvider clock)
    {
        internal int Player = player;
        internal readonly AggregateState<CollectionSnapshot> Aggregate = new(session, PlayerDataMessageRegistry.Default.Sources.Select(id => new SourceDefinition(id, PlayerDataMessageRegistry.Default.OptionalSources.Contains(id) ? SourceRequirement.Optional : SourceRequirement.Required)),
            snapshot => snapshot.Count, clock: clock);
        internal AggregateSnapshot<CollectionSnapshot>? Candidate;
        internal bool Pending;
        internal bool Superseded;
        internal long? LastAttempt;
        internal int Decoded;
        internal readonly Dictionary<string, long> Sizes = new();
        internal string? Error;
    }
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<int, GameDataExportPlayerSummary> _players = new();
    private readonly Dictionary<int, int> _mailCounts = new();
    private readonly Dictionary<int, int> _achievementCounts = new();
    private readonly Dictionary<int, int> _roleCounts = new();
    private readonly Dictionary<string, GameDataExportArtifact> _artifacts = new(StringComparer.Ordinal);
    private State? _current;
    private long _progressRevision;
    private int _reportedDecoded;
    public long ProgressRevision => _progressRevision;
    private GameDataExportResult? _last;
    private GameDataExportModuleState? _ended;
    public GameDataExportModuleDescriptor Descriptor => Definition;
    public IReadOnlySet<ushort> MessageIds { get; } = PlayerDataMessageRegistry.Default.Handlers.Keys.ToHashSet();

    public void Consume(MessageObservation message)
    {
        if (!message.IsCurrentSession || !message.ConnectionVerified || message.SessionId.StartsWith("unverified-", StringComparison.Ordinal)) return;
        var state = Get(message.SessionId, message.PlayerId ?? 0);
        _current = state;
        var handler = PlayerDataMessageRegistry.Default.Handlers[message.Frame.MsgId];
        _progressRevision++;
        if (message.Status is not null || message.Protocol?.Message is not { } model
            || !message.Protocol.DecodeStatus.StartsWith("decoded", StringComparison.Ordinal))
        {
            foreach (var sourceId in handler.Sources)
            {
                var source = state.Aggregate.Source(sourceId);
                source.Requirement = SourceRequirement.Required;
                source.Reject("inventory_decode_failed", true);
            }
            state.Error = "游戏数据消息解密或 protobuf 解析失败，保留上次文件。";
            return;
        }
        state.Decoded++;
        var changes = handler.Decode(model, sourceId => state.Aggregate.Source(sourceId).Value);
        var provenance = new SourceProvenance(message.EventIndex, message.EventIndex,
            message.ReceivedAt ?? context.Clock.GetUtcNow(), message.ConnectionId);
        foreach (var change in changes)
        {
            var source = state.Aggregate.Source(change.SourceId);
            source.Requirement = SourceRequirement.Required;
            CollectionSnapshot Budget(CollectionSnapshot value)
            {
                var total = _states.Values.Sum(s => s.Sizes.Values.Sum())
                    - state.Sizes.GetValueOrDefault(change.SourceId) + value.RetainedBytes;
                if (total > 32 * 1024 * 1024) throw new IOException("背包聚合状态超过内存预算。");
                return value;
            }
            if (change.Full is { } readFull)
            {
                try { source.Full(Budget(readFull()), provenance: provenance); }
                catch (InvalidDataException) { source.Reject("invalid_inventory_baseline", true); }
            }
            else if (change.Error is { } error)
                source.Reject(error, true);
            else if (change.Apply is { } apply)
                source.ApplyOrdered(baseline => Budget(apply(baseline)), provenance);
            if (source.Value is { } retained) state.Sizes[change.SourceId] = retained.RetainedBytes;
        }
        state.Error = state.Aggregate.Progress().FirstOrDefault(source => source.Error is not null)?.Error;
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
    private static string Relative(int player) => GameDataExportPaths.PlayerSnapshotRelativePath(player);
    public void Checkpoint(bool stopping = false)
    {
        foreach (var state in _states.Values)
        {
            if (state.Player <= 0 || state.Pending || state.Superseded || (!stopping && state.LastAttempt is { } lastAttempt
                && context.Clock.GetElapsedTime(lastAttempt) < TimeSpan.FromSeconds(1))) continue;
            state.Candidate ??= state.Aggregate.Build();
            if (state.Candidate is not { } snapshot) continue;
            var data = PlayerDataSnapshotMapper.Map(snapshot.Values);
            var count = _players.GetValueOrDefault(state.Player)?.UpdateCount + 1 ?? 1;
            var result = new GameDataExportResult(true, "玩家数据快照已更新。", data, null,
                context.CapturedFrames, state.Decoded, context.LegacyKey(snapshot.Epoch), state.Player, count,
                Mails: PlayerDataSnapshotMapper.MapMails(snapshot.Values),
                Achievements: PlayerDataSnapshotMapper.MapAchievements(snapshot.Values),
                Roles: PlayerDataSnapshotMapper.MapRoles(snapshot.Values));
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
    private void Acknowledge(State state, AggregateSnapshot<CollectionSnapshot> snapshot, GameDataExportResult result, OutputReceipt receipt)
    {
        state.Pending = false;
        _progressRevision++;
        if (receipt.Superseded) { state.Candidate = null; return; }
        if (receipt.Error is not null) { state.Error = receipt.Error; return; }
        if (!state.Aggregate.Acknowledge(snapshot)) return;
        GameDataExportPaths.RemoveLegacySnapshots(receipt.Path!);
        state.Candidate = null;
        state.Error = state.Aggregate.Progress().FirstOrDefault(source => source.Error is not null)?.Error;
        result = result with { UpdateCount = (_players.GetValueOrDefault(state.Player)?.UpdateCount ?? 0) + 1 };
        _last = result with { OutputJsonPath = receipt.Path };
        if (result.Mails is { } mails) _mailCounts[state.Player] = mails.Items.Count;
        else _mailCounts.Remove(state.Player);
        if (result.Achievements is { } achievements) _achievementCounts[state.Player] = achievements.Items.Count;
        else _achievementCounts.Remove(state.Player);
        if (result.Roles is { } roles) _roleCounts[state.Player] = roles.Items.Count;
        else _roleCounts.Remove(state.Player);
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
            })).Concat(_mailCounts.Select(pair => new GameDataExportCounter("mails", pair.Value, pair.Key)))
            .Concat(_achievementCounts.Select(pair => new GameDataExportCounter("achievements", pair.Value, pair.Key)))
            .Concat(_roleCounts.Select(pair => new GameDataExportCounter("roles", pair.Value, pair.Key))).ToArray(),
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
        Inventory = _last?.Inventory, Mails = _last?.Mails, Achievements = _last?.Achievements, Roles = _last?.Roles, OutputJsonPath = _last?.OutputJsonPath, PlayerId = _last?.PlayerId ?? 0,
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

}
