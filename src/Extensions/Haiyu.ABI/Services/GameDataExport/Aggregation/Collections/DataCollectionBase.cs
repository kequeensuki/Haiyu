using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;

/// <summary>默认新增去重、按键更新、幂等删除。派生类仅重写业务差异。</summary>
internal abstract class DataCollectionBase<TKey, TEntity, TChange> : IDataCollection<TKey, TEntity, TChange>
    where TKey : notnull where TEntity : class
{
    private readonly Func<TEntity, TKey> _key;
    private readonly Func<TKey, bool> _valid;
    private readonly IComparer<TKey>? _order;
    protected Dictionary<TKey, TEntity> Items { get; private set; } = new();

    protected DataCollectionBase(IEnumerable<TEntity> baseline, Func<TEntity, TKey> key, Func<TKey, bool> valid, IComparer<TKey>? order = null)
    {
        _key = key; _valid = valid; _order = order;
        foreach (var entity in baseline)
        {
            var id = key(entity);
            if (!valid(id) || !Items.TryAdd(id, entity))
                throw new InvalidDataException("完整基线包含无效或重复的对象标识。");
        }
    }
    public virtual ChangeResult Add(TEntity entity)
    {
        var key = _key(entity);
        return !_valid(key) ? ChangeResult.Invalid("invalid_item_key")
            : Items.TryAdd(key, entity) ? ChangeResult.Changed : ChangeResult.Unchanged;
    }
    public virtual ChangeResult Update(TKey key, TChange change)
    {
        if (!_valid(key)) return ChangeResult.Invalid("invalid_item_key");
        if (!Items.TryGetValue(key, out var current)) return ChangeResult.Missing;
        return Replace(key, Merge(current, change));
    }
    public virtual ChangeResult Remove(TKey key) =>
        Items.Remove(key) ? ChangeResult.Changed : ChangeResult.Unchanged;

    internal virtual bool IsComplete => true;

    protected abstract TEntity Merge(TEntity current, TChange change);
    protected virtual bool Equivalent(TEntity first, TEntity second) => EqualityComparer<TEntity>.Default.Equals(first, second);
    protected ChangeResult Replace(TKey key, TEntity value)
    {
        if (!_valid(key) || !EqualityComparer<TKey>.Default.Equals(key, _key(value)))
            return ChangeResult.Invalid("item_identity_changed");
        if (Items.TryGetValue(key, out var previous) && Equivalent(previous, value)) return ChangeResult.Unchanged;
        Items[key] = value;
        return ChangeResult.Changed;
    }
    public IReadOnlyList<TEntity> Snapshot()
    {
        // 键只需支持相等性；输出需要排序时，由业务集合显式提供比较器。
        var values = _order is null ? Items.Values.AsEnumerable()
            : Items.OrderBy(pair => pair.Key, _order).Select(pair => pair.Value);
        return Array.AsReadOnly(values.ToArray());
    }

    /// <summary>批内任一操作失败或抛异常时整体回滚；对象必须不可变，快照不会被后续修改。</summary>
    internal ChangeResult ApplyBatch(Func<IDataCollection<TKey, TEntity, TChange>, ChangeResult> apply)
    {
        var previous = Items;
        Items = new(previous);
        var committed = false;
        try
        {
            var result = apply(this);
            if (!result.Succeeded) return result;
            var changed = previous.Count != Items.Count || previous.Any(pair =>
                !Items.TryGetValue(pair.Key, out var current) || !Equivalent(pair.Value, current));
            committed = true;
            return changed ? ChangeResult.Changed : ChangeResult.Unchanged;
        }
        finally { if (!committed) Items = previous; }
    }
}
