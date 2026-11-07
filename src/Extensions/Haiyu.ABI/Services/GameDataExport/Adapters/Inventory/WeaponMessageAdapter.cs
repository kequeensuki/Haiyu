using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

internal sealed class WeaponMessageAdapter : CollectionMessageAdapter<int, WeaponState, WeaponChange>
{
    internal WeaponMessageAdapter() : base("weapons", items => new WeaponCollection(items), _ => 512)
    {
        Full(WavesMessageIds.WeaponItemResponse, 6, Read);
        On(WavesMessageIds.WeaponItemAddNotify, 0, (m, c) => Each(m.Messages(11), item => c.Add(Read(item))));
        On(WavesMessageIds.WeaponItemRemoveNotify, 0, (m, c) => Each(m.Int32s(14), c.Remove));
        On(WavesMessageIds.WeaponLevelUpResponse, 11, (m, c) => c.Update(m.Int32(4), new WeaponLevelChanged(m.Int32(1), m.Int32(9))));
        On(WavesMessageIds.WeaponBreachResponse, 7, (m, c) => c.Update(m.Int32(4), new WeaponBreachChanged(m.Int32(11))));
        On(WavesMessageIds.WeaponResonUpResponse, 1, (m, c) => c.Update(m.Int32(11), new WeaponResonanceChanged(m.Int32(15))));
        On(WavesMessageIds.EquipTakeOnResponse, 2, (m, c) => Equipment(m, 1, c));
        On(WavesMessageIds.EquipTakeOnNotify, 0, (m, c) => Equipment(m, 5, c));
    }
    internal CollectionMessageChange FunctionChange(ProtocolMessage message) =>
        Delta(message, (m, c) => c.Update(m.Int32(1), new WeaponFunctionChanged(m.Int32(14))));
    private static ChangeResult Equipment(ProtocolMessage message, int field, IDataCollection<int, WeaponState, WeaponChange> items) =>
        Each(message.Messages(field).Where(equip => equip.Int32(2) == 0), equip =>
            items.Update(equip.Int32(3), new WeaponEquipmentChanged(equip.Int32(1))));
    private static WeaponState Read(ProtocolMessage item) => new(item.Int32(1), item.Int32(2), item.Int32(4),
        item.Int32(5), item.Int32(6), item.Int32(7), item.Int32(8), item.Int32(3));
}
