using System.Collections.ObjectModel;
using ABI.Models;

namespace Haiyu.ABI.Services.GameDataExport.Aggregation;

internal sealed record SourceProvenance(long? FirstMessageIndex, long? LastMessageIndex, DateTimeOffset ReceivedAt, uint? ConnectionId);

internal enum SourceRequirement { Optional, Required, Unknown }
internal enum SnapshotConsistency { LatestComplete, StrictBatch }

internal sealed record SourceDefinition(string Id, SourceRequirement Requirement = SourceRequirement.Required,
    int MaxEntries = 100_000, int MaxPages = 512, int MaxPendingDeltas = 128,
    TimeSpan? MaxAge = null, bool VersionedDeltas = false, TimeSpan? AssemblyTimeout = null);

/// <summary>来源的已确认基线与待组装状态分别保存；T 必须为不可变值。</summary>
internal sealed class SourceState<T>(SourceDefinition definition, Func<T, int> size, TimeProvider clock) where T : class
{
    private sealed record PendingDelta(Func<T, T> Reduce, SourceProvenance? Provenance);
    private readonly SortedDictionary<long, PendingDelta> _deltas = new();
    private readonly Dictionary<int, SourceProvenance?> _pageProvenance = new();
    private readonly Dictionary<int, T> _pages = new();
    private string? _assembly;
    private int? _pageCount;
    private long? _assemblyVersion;
    private string? _assemblyRound;
    private long _assemblyStarted;
    private long _receivedAt;
    private bool _awaitingBaseline;
    private SourceProvenance? _provenance;
    internal SourceDefinition Definition => definition;
    internal T? Value { get; private set; }
    internal long LocalVersion { get; private set; }
    internal long? ServerVersion { get; private set; }
    internal string? BatchId { get; private set; }
    internal string? Error { get; private set; }
    internal SourceRequirement Requirement { get; set; } = definition.Requirement;
    internal bool IsStale => Value is not null && definition.MaxAge is { } max && clock.GetElapsedTime(_receivedAt) >= max;
    internal bool Available => Value is not null && !_awaitingBaseline && !IsStale;

    internal bool Full(T value, long? version = null, string? batch = null, SourceProvenance? provenance = null)
    {
        if (version is { } next && ServerVersion is { } old && next <= old) return false;
        if (ServerVersion is not null && version is null) return Reject("full_version_unavailable", true);
        if (!Validate(value)) return false;
        Value = value;
        ServerVersion = version;
        BatchId = batch;
        _provenance = provenance;
        _receivedAt = clock.GetTimestamp();
        LocalVersion++;
        Error = null;
        _pages.Clear(); _pageProvenance.Clear(); _assembly = null; _pageCount = null; _assemblyVersion = null; _assemblyRound = null;
        foreach (var stale in _deltas.Keys.Where(v => version is { } baseline && v <= baseline).ToArray()) _deltas.Remove(stale);
        Replay();
        return true;
    }

    internal bool Page(string assembly, int index, int? total, bool last, T value,
        Func<IReadOnlyList<T>, T> combine, long? version = null, string? round = null, SourceProvenance? provenance = null)
    {
        if (index < 0 || index >= definition.MaxPages || total is <= 0 || total > definition.MaxPages)
            return Reject("invalid_page_range");
        if (version is { } next && ServerVersion is { } old && next <= old) return false;
        if (!Validate(value)) return false;
        ExpireAssembly();
        if (_assembly == assembly && (_assemblyVersion != version || _assemblyRound != round)) return Reject("page_batch_metadata_conflict");
        if (_assembly != assembly)
        {
            // 批次编号的顺序由模块证据决定；未提供版本时不得将晚到旧批次接入正在构建的批次。
            if (_assembly is not null && (version is null || _assemblyVersion is null)) return Reject("ambiguous_page_batch");
            if (_assemblyVersion is { } collecting && version is { } incoming && incoming <= collecting) return false;
            _pages.Clear(); _pageProvenance.Clear(); _pageCount = null; _assembly = assembly;
            _assemblyVersion = version; _assemblyRound = round; _assemblyStarted = clock.GetTimestamp();
        }
        if (last && total is { } declared && declared != index + 1) return Reject("page_end_conflict");
        var expected = total ?? (last ? index + 1 : (int?)null);
        if (_pageCount is { } known && expected is { } count && known != count) return Reject("page_count_conflict");
        _pageCount ??= expected;
        if (_pageCount is { } length && index >= length) return Reject("page_out_of_range");
        if (_pageCount is { } finalCount && _pages.Keys.Any(i => i >= finalCount)) return Reject("page_out_of_range");
        if (!_pages.ContainsKey(index) && _pages.Values.Sum(page => (long)size(page)) + size(value) > definition.MaxEntries)
            return Reject("source_entry_limit");
        if (_pages.TryAdd(index, value)) _pageProvenance[index] = provenance;
        if (_pageCount is not { } pages || _pages.Count != pages || Enumerable.Range(0, pages).Any(i => !_pages.ContainsKey(i))) return false;
        var combined = combine(Array.AsReadOnly(Enumerable.Range(0, pages).Select(i => _pages[i]).ToArray()));
        return Full(combined, version, round, _pageProvenance.Values.Aggregate((SourceProvenance?)null, Merge));
    }

    internal bool Delta(long? revision, Func<T, T> reducer, SourceProvenance? provenance = null)
    {
        if (!definition.VersionedDeltas || revision is null) return Reject("delta_order_unavailable", true);
        var next = revision.Value;
        if (ServerVersion is { } baseline && next <= baseline) return false;
        if (_deltas.ContainsKey(next)) return false;
        if (_deltas.Count >= definition.MaxPendingDeltas) return Reject("pending_delta_limit", true);
        _deltas.Add(next, new(reducer, provenance));
        var before = LocalVersion;
        Replay();
        return LocalVersion != before;
    }

    /// <summary>已验证连接的串行事件投影；只维护本地版本，不伪造服务器版本或补齐缺失基线。</summary>
    internal bool ApplyOrdered(Func<T, T> reducer, SourceProvenance provenance)
    {
        if (ServerVersion is not null) return Reject("ordered_delta_on_versioned_source", true);
        if (Value is null || !Available) return Reject("delta_requires_baseline", true);
        if (provenance.LastMessageIndex is not { } next) return Reject("delta_order_unavailable", true);
        if (_provenance?.LastMessageIndex is { } previous && next <= previous) return false;
        T value;
        try { value = reducer(Value); }
        catch (InvalidDataException) { return Reject("invalid_delta", true); }
        if (!Validate(value)) return Reject("source_entry_limit", true);
        _provenance = Merge(_provenance, provenance);
        _receivedAt = clock.GetTimestamp();
        if (ReferenceEquals(value, Value)) return false;
        Value = value;
        LocalVersion++;
        Error = null;
        return true;
    }

    internal void ExpireAssembly()
    {
        if (_assembly is null || clock.GetElapsedTime(_assemblyStarted) < (definition.AssemblyTimeout ?? TimeSpan.FromMinutes(2))) return;
        _pages.Clear(); _pageProvenance.Clear(); _assembly = null; _pageCount = null; _assemblyVersion = null; _assemblyRound = null;
        Error = "page_assembly_timeout";
    }
    private void Replay()
    {
        if (_deltas.Count > 0 && (Value is null || ServerVersion is null))
        { _awaitingBaseline = true; return; }
        while (Value is not null && ServerVersion is { } current && _deltas.Remove(current + 1, out var delta))
        {
            T value;
            try { value = delta.Reduce(Value); }
            catch (InvalidDataException) { _deltas[current + 1] = delta; Reject("invalid_delta", true); return; }
            if (!Validate(value)) { _deltas[current + 1] = delta; _awaitingBaseline = true; return; }
            _provenance = Merge(_provenance, delta.Provenance);
            Value = value; ServerVersion = current + 1; LocalVersion++; _receivedAt = clock.GetTimestamp();
        }
        _awaitingBaseline = _deltas.Count > 0;
        if (!_awaitingBaseline) Error = null;
    }

    private static SourceProvenance? Merge(SourceProvenance? first, SourceProvenance? next)
    {
        if (first is null) return next;
        if (next is null) return first;
        return new(first.FirstMessageIndex is { } a && next.FirstMessageIndex is { } b ? Math.Min(a, b) : first.FirstMessageIndex ?? next.FirstMessageIndex,
            first.LastMessageIndex is { } x && next.LastMessageIndex is { } y ? Math.Max(x, y) : next.LastMessageIndex ?? first.LastMessageIndex,
            first.ReceivedAt > next.ReceivedAt ? first.ReceivedAt : next.ReceivedAt, next.ConnectionId);
    }

    private bool Validate(T value) => size(value) <= definition.MaxEntries || Reject("source_entry_limit");
    internal bool Reject(string reason, bool awaitingBaseline = false)
    { Error = reason; _awaitingBaseline |= awaitingBaseline; return false; }

    internal GameDataExportSourceProgress Progress()
    {
        ExpireAssembly();
        return new(definition.Id,
        _awaitingBaseline ? "AwaitingBaseline" : IsStale ? "Stale" : _pages.Count > 0 ? "Collecting"
        : Error is not null ? "Invalid" : Value is null ? "Missing" : "Ready",
        Requirement != SourceRequirement.Optional, LocalVersion, ServerVersion, BatchId, Error,
            _provenance?.FirstMessageIndex, _provenance?.LastMessageIndex, _provenance?.ReceivedAt, _provenance?.ConnectionId);
    }
}

internal sealed record AggregateSnapshot<T>(string Epoch, long Revision, SnapshotConsistency Consistency,
    IReadOnlyDictionary<string, T> Values, GameDataExportSourceProgress[] Sources) where T : class;

/// <summary>通用多来源完成条件与版本向量；不包含任何消息编号或业务字段。</summary>
internal sealed class AggregateState<T> where T : class
{
    private readonly Dictionary<string, SourceState<T>> _sources;
    private readonly SnapshotConsistency _consistency;
    private string? _round;
    private string? _submittedVector;
    private long _revision;
    internal string Epoch { get; }
    internal long PersistedRevision { get; private set; }
    internal long CandidateRevision => _revision;
    internal AggregateState(string epoch, IEnumerable<SourceDefinition> sources, Func<T, int> size,
        SnapshotConsistency consistency = SnapshotConsistency.LatestComplete, TimeProvider? clock = null)
    {
        Epoch = epoch; _consistency = consistency;
        _sources = sources.ToDictionary(s => s.Id, s => new SourceState<T>(s, size, clock ?? TimeProvider.System), StringComparer.Ordinal);
        if (_sources.Count == 0) throw new ArgumentException("聚合至少需要一个来源。");
    }
    internal SourceState<T> Source(string id) => _sources[id];
    internal void BeginRound(string round) { _round = round; _submittedVector = null; }
    internal bool HasData => _sources.Values.Any(s => s.Value is not null);
    internal bool Ready
    {
        get
        {
            if (_sources.Values.Any(s => s.Requirement == SourceRequirement.Unknown)) return false;
            var required = _sources.Values.Where(s => s.Requirement == SourceRequirement.Required).ToArray();
            if (required.Any(s => !s.Available) || !_sources.Values.Any(s => s.Available)) return false;
            if (_round is not null && required.Any(s => s.BatchId != _round)) return false;
            if (_consistency == SnapshotConsistency.StrictBatch)
                return required.Length > 0 && required[0].BatchId is { } batch && required.All(s => s.BatchId == batch);
            return true;
        }
    }
    internal GameDataExportSourceProgress[] Progress() => _sources.Values.Select(s => s.Progress()).ToArray();
    internal AggregateSnapshot<T>? Build()
    {
        if (!Ready) return null;
        var vector = string.Join("|", _sources.Select(p => $"{p.Key}:{p.Value.LocalVersion}:{p.Value.Available}:{p.Value.Requirement}"));
        if (_submittedVector == vector) return null;
        _submittedVector = vector;
        return new(Epoch, ++_revision, _consistency, new ReadOnlyDictionary<string, T>(
            _sources.Where(p => p.Value.Available).ToDictionary(p => p.Key, p => p.Value.Value!)), Progress());
    }
    internal bool Acknowledge(AggregateSnapshot<T> snapshot)
    {
        if (snapshot.Epoch != Epoch || snapshot.Revision <= PersistedRevision || snapshot.Revision > _revision) return false;
        PersistedRevision = snapshot.Revision;
        return true;
    }
}
