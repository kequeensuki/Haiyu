using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

internal sealed class PhantomMessageAdapter : InventoryMessageAdapter<int, PhantomState, PhantomChange>
{
    internal PhantomMessageAdapter() : base("phantoms", items => new PhantomCollection(items), item =>
        768 + 48L * (item.MainProps.Count + item.SubProps.Count + item.UnacknowledgedProps.Count) + 8L * item.LockedProps.Count)
    {
        Full(WavesMessageIds.PhantomItemResponse, 3, Read);
        On(WavesMessageIds.PhantomItemAddNotify, 0, (m, c) => Each(m.Messages(3), item => c.Add(Read(item))));
        On(WavesMessageIds.PhantomItemRemoveNotify, 0, (m, c) => Each(m.Int32s(13), c.Remove));
        On(WavesMessageIds.PhantomItemUpdateNotify, 0, (m, c) => Each(m.Messages(4), item => Inventory(c, item)));
        On(WavesMessageIds.PhantomLevelUpResponse, 11, (m, c) =>
        {
            var item = Read(Required(m, 14));
            return c.Update(item.IncrId, new PhantomUpgradeChanged(item, m.Message(13) is { } split ? ReadSplit(split) : null));
        });
        On(WavesMessageIds.PhantomIdentifyResponse, 8, (m, c) =>
        {
            var item = Read(Required(m, 11));
            return c.Update(item.IncrId, new PhantomCultivationChanged(item));
        });
        On(WavesMessageIds.PhantomVicePolishAckResponse, 15, (m, c) => Inventory(c, Required(m, 10)));
        On(WavesMessageIds.PhantomPutOnResponse, 1, (m, c) => Stacks(c, m.Messages(7), m.Messages(11), []));
        On(WavesMessageIds.PhantomAutoPutResponse, 3, (m, c) => Stacks(c, m.Messages(15), m.Messages(5), []));
        On(WavesMessageIds.UsePhantomEquipGroupResponse, 11, (m, c) => Stacks(c, m.Messages(6), m.Messages(14), []));
        On(WavesMessageIds.PhantomPolishResponse, 4, (m, c) =>
            Stacks(c, Optional(m.Message(7)), Optional(m.Message(10)), [Required(m, 14)]));
        On(WavesMessageIds.PhantomBatchPolishResponse, 6, (m, c) =>
            Stacks(c, m.Messages(14), m.Messages(10), m.Messages(12)));
        Invalidate(WavesMessageIds.PhantomSkinChangeResponse, 15);
        Invalidate(WavesMessageIds.PhantomBatchOperResponse, 13);
    }
    internal InventoryMessageChange FunctionChange(ProtocolMessage message) =>
        Delta(message, (m, c) => c.Update(m.Int32(1), new PhantomFunctionChanged(m.Int32(14))));
    private static ChangeResult Inventory(IDataCollection<int, PhantomState, PhantomChange> items, ProtocolMessage message)
    {
        var item = Read(message);
        return items.Update(item.IncrId, new PhantomInventoryChanged(item));
    }
    private static ChangeResult Stacks(IDataCollection<int, PhantomState, PhantomChange> items,
        IEnumerable<ProtocolMessage> splits, IEnumerable<ProtocolMessage> merges, IEnumerable<ProtocolMessage> updates) =>
        items.Update(0, new PhantomStacksChanged(
            Array.AsReadOnly(splits.Select(ReadSplit).ToArray()),
            Array.AsReadOnly(merges.Select(merge => new PhantomStackMerge(merge.Int32(1),
                merge.Message(2) is { } representative ? Read(representative) : null)).ToArray()),
            Array.AsReadOnly(updates.Select(Read).ToArray())));
    private static PhantomStackSplit ReadSplit(ProtocolMessage split) => new(
        split.Message(1) is { } operate ? Read(operate) : null,
        split.Message(3) is { } remainder ? Read(remainder) : null);
    private static ProtocolMessage Required(ProtocolMessage message, int field) => message.Message(field)
        ?? throw new InvalidDataException("成功响应缺少声骸对象。");
    private static IEnumerable<ProtocolMessage> Optional(ProtocolMessage? message) => message is null ? [] : [message];
    private static IReadOnlyList<PhantomProperty> Props(ProtocolMessage item, int field) =>
        Array.AsReadOnly(item.Messages(field).Select(prop => new PhantomProperty(prop.Int32(1), prop.Int32(2))).ToArray());
    private static PhantomState Read(ProtocolMessage item) => new(item.Int32(1), item.Int32(2), item.Int32(4), item.Int32(5),
        Props(item, 6), Props(item, 7), item.Int32(8), item.Int32(9), Props(item, 10),
        item.Int32s(11), item.Int32(12), item.Int64(13), item.Int32(3));
}
