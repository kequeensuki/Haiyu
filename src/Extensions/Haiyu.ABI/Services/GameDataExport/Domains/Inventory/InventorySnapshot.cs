namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory;

/// <summary>由 SourceState 唯一管理基线和版本；这里仅保存冻结的业务对象。</summary>
internal abstract class InventorySnapshot
{
    internal abstract int Count { get; }
    internal abstract long RetainedBytes { get; }
}

internal sealed class InventorySnapshot<TEntity>(IReadOnlyList<TEntity> items, long retainedBytes) : InventorySnapshot
{
    internal IReadOnlyList<TEntity> Items { get; } = items;
    internal override int Count => Items.Count;
    internal override long RetainedBytes => retainedBytes;
}
