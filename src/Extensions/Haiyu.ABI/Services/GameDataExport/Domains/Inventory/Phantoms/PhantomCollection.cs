using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;

internal sealed class PhantomCollection(IEnumerable<PhantomState> items)
    : DataCollectionBase<int, PhantomState, PhantomChange>(items, item => item.IncrId, key => key > 0, Comparer<int>.Default)
{
    public override ChangeResult Add(PhantomState entity)
    {
        if (Items.TryGetValue(entity.IncrId, out var previous))
            entity = PreserveInventory(entity, previous, entity.Count <= 0, entity.Count <= 0 || KeepAddTime(entity, previous));
        return Replace(entity.IncrId, entity);
    }
    public override ChangeResult Update(int key, PhantomChange change)
    {
        if (change is PhantomStacksChanged stacks) return ApplyBatch(_ => ApplyStacks(stacks));
        if (key <= 0) return ChangeResult.Invalid("invalid_item_key");
        if (change is PhantomUpgradeChanged upgrade)
            return upgrade.Value.IncrId != key ? ChangeResult.Invalid("item_identity_changed")
                : ApplyBatch(_ => ApplyUpgrade(key, upgrade));
        if (change is PhantomInventoryChanged inventory && inventory.Value.IncrId != key)
            return ChangeResult.Invalid("item_identity_changed");
        if (change is PhantomInventoryChanged { Value.Count: 0 }) return Remove(key);
        if (change is PhantomRemainderAdded remainder)
        {
            var entity = Items.TryGetValue(key, out var previous)
                ? PreserveInventory(remainder.Value, previous, true, true) : remainder.Value;
            return Replace(key, entity);
        }
        return base.Update(key, change);
    }
    private ChangeResult ApplyStacks(PhantomStacksChanged stacks)
    {
        foreach (var split in stacks.Splits)
        {
            var result = Split(split);
            if (!result.Succeeded) return result;
        }
        foreach (var merge in stacks.Merges)
        {
            if (merge.Representative is { IncrId: > 0 } representative)
            {
                var result = Update(representative.IncrId, new PhantomInventoryChanged(representative));
                if (!result.Succeeded) return result;
            }
            if (merge.MergedIncrId > 0) Remove(merge.MergedIncrId);
        }
        foreach (var item in stacks.Updates)
        {
            var result = Update(item.IncrId, new PhantomInventoryChanged(item));
            if (!result.Succeeded) return result;
        }
        return ChangeResult.Changed; // 共享事务比较最终值，批内修改相互抵消时也不制造新版本。
    }
    private ChangeResult ApplyUpgrade(int key, PhantomUpgradeChanged upgrade)
    {
        if (upgrade.Split is { } split)
        {
            var result = Split(split);
            if (!result.Succeeded) return result;
            // 同一响应已明确删除目标，后续养成信息不能复活它，也不能误判为缺失新增。
            if (split.Operate is { Count: 0 } removed && removed.IncrId == key && !Items.ContainsKey(key))
                return ChangeResult.Changed;
        }
        return base.Update(key, new PhantomCultivationChanged(upgrade.Value));
    }
    private ChangeResult Split(PhantomStackSplit split)
    {
        if (split.Operate is { IncrId: > 0 } operate)
        {
            var result = Update(operate.IncrId, new PhantomInventoryChanged(operate));
            if (!result.Succeeded) return result;
        }
        return split.Remainder is { IncrId: > 0 } remainder
            ? Update(remainder.IncrId, new PhantomRemainderAdded(remainder)) : ChangeResult.Unchanged;
    }
    protected override PhantomState Merge(PhantomState current, PhantomChange change) => change switch
    {
        PhantomInventoryChanged inventory => PreserveInventory(inventory.Value, current, false, KeepAddTime(inventory.Value, current)),
        PhantomCultivationChanged cultivation => PreserveInventory(cultivation.Value, current, true, true),
        PhantomFunctionChanged function => current with { FuncValue = function.FuncValue },
        _ => throw new InvalidDataException("不支持的声骸变更。")
    };
    private static bool KeepAddTime(PhantomState next, PhantomState previous) => next.Id == previous.Id && next.AddTime <= 0;
    private static PhantomState PreserveInventory(PhantomState next, PhantomState previous, bool quantity, bool time) =>
        next with { Count = quantity ? previous.Count : next.Count, AddTime = time ? previous.AddTime : next.AddTime };
    protected override bool Equivalent(PhantomState first, PhantomState second) =>
        ReferenceEquals(first, second) || (first with { MainProps = second.MainProps, SubProps = second.SubProps,
            UnacknowledgedProps = second.UnacknowledgedProps, LockedProps = second.LockedProps } == second
        && first.MainProps.SequenceEqual(second.MainProps) && first.SubProps.SequenceEqual(second.SubProps)
        && first.UnacknowledgedProps.SequenceEqual(second.UnacknowledgedProps) && first.LockedProps.SequenceEqual(second.LockedProps));
}
