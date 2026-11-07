using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

internal sealed class NormalItemMessageAdapter : InventoryMessageAdapter<int, NormalItemState, NormalItemChange>
{
    internal NormalItemMessageAdapter() : base("normalItems", items => new NormalItemCollection(items), _ => 256)
    {
        Full(WavesMessageIds.NormalItemResponse, 3, Read);
        On(WavesMessageIds.NormalItemAddNotify, 0, (m, c) => Each(m.Messages(9), item => c.Add(Read(item))));
        On(WavesMessageIds.NormalItemUpdateNotify, 0, (m, c) =>
            Each(m.Messages(4), item => c.Update(item.Int32(1), new(item.Int32(2)))));
        On(WavesMessageIds.NormalItemRemoveNotify, 0, (m, c) => Each(m.Int32s(8), c.Remove));
    }
    private static NormalItemState Read(ProtocolMessage item) => new(item.Int32(1), item.Int32(2), item.Int64(4));
}
