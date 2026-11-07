using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;

internal sealed class WeaponCollection(IEnumerable<WeaponState> items)
    : DataCollectionBase<int, WeaponState, WeaponChange>(items, item => item.IncrId, key => key > 0, Comparer<int>.Default)
{
    // 武器新增通知同时刷新完整养成对象。
    public override ChangeResult Add(WeaponState entity) => Replace(entity.IncrId, entity);
    public override ChangeResult Update(int key, WeaponChange change)
    {
        if (change is not WeaponEquipmentChanged equipment) return base.Update(key, change);
        if (equipment.RoleId < 0 || key < 0 || (key == 0 && equipment.RoleId == 0))
            return ChangeResult.Invalid("invalid_equipment_identity");
        if (key > 0 && !Items.ContainsKey(key)) return ChangeResult.Missing;
        var changed = false;
        foreach (var id in Items.Keys.ToArray())
            if (id == key || (equipment.RoleId > 0 && Items[id].RoleId == equipment.RoleId))
                changed |= Replace(id, Items[id] with { RoleId = id == key ? equipment.RoleId : 0 }).Status == ChangeStatus.Changed;
        return changed ? ChangeResult.Changed : ChangeResult.Unchanged;
    }
    protected override WeaponState Merge(WeaponState current, WeaponChange change) => change switch
    {
        WeaponLevelChanged level => current with { Level = level.Level, Exp = level.Exp },
        WeaponBreachChanged breach => current with { Breach = breach.Breach },
        WeaponResonanceChanged resonance => current with { Resonance = resonance.Resonance },
        WeaponFunctionChanged function => current with { FuncValue = function.FuncValue },
        _ => throw new InvalidDataException("不支持的武器变更。")
    };
}
