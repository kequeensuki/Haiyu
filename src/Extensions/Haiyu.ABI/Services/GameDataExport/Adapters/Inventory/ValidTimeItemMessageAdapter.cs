using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

internal sealed class ValidTimeItemMessageAdapter : InventoryMessageAdapter<(int Id, int IncrId), ValidTimeItemState, ValidTimeItemChange>
{
    internal ValidTimeItemMessageAdapter() : base("validTimeItems", items => new ValidTimeItemCollection(items), _ => 256)
    {
        Full(WavesMessageIds.ValidTimeItemResponse, 14, Read);
        On(WavesMessageIds.ValidTimeItemAddNotify, 0, (m, c) => Each(m.Messages(12), item => c.Add(Read(item))));
        On(WavesMessageIds.ValidTimeItemUpdateNotify, 0, (m, c) => Each(m.Messages(13), item =>
            c.Update((item.Int32(1), item.Int32(3)), new(item.Int32(2), item.Int64(4)))));
        On(WavesMessageIds.ValidTimeItemRemoveNotify, 0, (m, c) =>
            Each(m.Messages(8), item => c.Remove((item.Int32(1), item.Int32(2)))));
    }
    private static ValidTimeItemState Read(ProtocolMessage item) => new(item.Int32(1), item.Int32(3), item.Int32(2), item.Int64(4));
}
