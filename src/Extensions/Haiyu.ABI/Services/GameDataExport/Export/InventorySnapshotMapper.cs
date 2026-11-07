using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>业务快照到稳定 ABI/JSON 格式的唯一映射，不参与协议解析或业务合并。</summary>
internal static class InventorySnapshotMapper
{
    internal static GameInventoryData Map(IReadOnlyDictionary<string, InventorySnapshot> values) => new(
        Array.AsReadOnly(((InventorySnapshot<NormalItemState>)values["normalItems"]).Items.Select(item =>
            new GameNormalItem(item.Id, item.Count, item.ExpireTime)).ToArray()),
        Array.AsReadOnly(((InventorySnapshot<WeaponState>)values["weapons"]).Items.Select(item =>
            new GameWeaponItem(item.Id, item.IncrId, item.Level, item.Exp, item.Breach, item.Resonance, item.RoleId)).ToArray()),
        Array.AsReadOnly(((InventorySnapshot<PhantomState>)values["phantoms"]).Items.Select(item =>
            new GamePhantomItem(item.Id, item.IncrId, item.Level, item.Exp, Props(item.MainProps), Props(item.SubProps),
                item.FetterGroupId, item.SkinId, item.Count, Props(item.UnacknowledgedProps),
                item.LockedProps, item.AddTime, item.FuncValue)).ToArray()),
        Array.AsReadOnly(((InventorySnapshot<ValidTimeItemState>)values["validTimeItems"]).Items.Select(item =>
            new GameValidTimeItem(item.Id, item.Count, item.IncrId, item.ExpireTime)).ToArray()));

    private static IReadOnlyList<GamePhantomProp> Props(IReadOnlyList<PhantomProperty> props) =>
        Array.AsReadOnly(props.Select(prop => new GamePhantomProp(prop.Id, prop.Value)).ToArray());
}
