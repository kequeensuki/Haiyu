using System.Collections.ObjectModel;
using ABI.Models;

namespace Haiyu.ABI.Services.GameDataExport.Aggregation;

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
