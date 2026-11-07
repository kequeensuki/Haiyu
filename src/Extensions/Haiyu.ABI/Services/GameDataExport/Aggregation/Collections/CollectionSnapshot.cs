namespace Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;

/// <summary>由 SourceState 唯一管理基线和版本；这里仅保存冻结的业务对象。</summary>
internal abstract class CollectionSnapshot
{
    internal abstract int Count { get; }
    internal abstract long RetainedBytes { get; }
}

internal sealed class CollectionSnapshot<TEntity>(IReadOnlyList<TEntity> items, long retainedBytes) : CollectionSnapshot
{
    internal IReadOnlyList<TEntity> Items { get; } = items;
    internal override int Count => Items.Count;
    internal override long RetainedBytes => retainedBytes;
}
