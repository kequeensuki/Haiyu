using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Roles;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Roles;

internal sealed class RoleEquipmentMessageAdapter : CollectionMessageAdapter<int, RoleEquipmentState, RoleEquipmentChange>
{
    internal RoleEquipmentMessageAdapter() : base("roleEquipment", items => new RoleEquipmentCollection(items), r =>
        64 + (r.Slots?.Count ?? 0) * 4L + ((r.BaseProps?.Count ?? 0) + (r.AddProps?.Count ?? 0)) * 32L)
    {
        Full(WavesMessageIds.PhantomItemResponse, ReadFull);
        On(WavesMessageIds.RoleChangeNotify, 0, (m, c) => c.Remove(m.Int32(12)));
        On(WavesMessageIds.PhantomEquipInfoNotify, 0, (m, c) => Each(m.Messages(2), p => c.Update(p.Int32(1), new(Equip(p)))));
        On(WavesMessageIds.PhantomPutOnResponse, 1, (m, c) => Each(m.Messages(12), p => c.Update(p.Int32(1), new(Equip(p)))));
        On(WavesMessageIds.PhantomAutoPutResponse, 3, (m, c) => Each(m.Messages(4), p => c.Update(p.Int32(1), new(Equip(p)))));
        On(WavesMessageIds.UsePhantomEquipGroupResponse, 11, (m, c) => Each(m.Messages(5), p => c.Update(p.Int32(1), new(Equip(p)))));
        On(WavesMessageIds.RolePhantomPropUpdateNotify, 0, (m, c) => Each(m.Messages(7), p => c.Update(p.Int32(1), new(Props(p)))));
        On(WavesMessageIds.RolePropResponse, 11, (m, c) => Each(m.Messages(2).Where(p => p.Message(4) is not null), p =>
        { var props = RoleMessageAdapter.Required(p, 4); return c.Update(props.Int32(1), new(Props(props))); }));
    }
    private static IEnumerable<RoleEquipmentState> ReadFull(ProtocolMessage message)
    {
        var slots = message.Messages(8).Select(Equip).ToArray();
        var props = message.Messages(5).Select(Props).ToArray();
        if (slots.Select(r => r.Id).Distinct().Count() != slots.Length || props.Select(r => r.Id).Distinct().Count() != props.Length)
            throw new InvalidDataException("完整装备列表包含重复角色。");
        return slots.Concat(props).GroupBy(r => r.Id).Select(g => new RoleEquipmentState(g.Key,
            g.Select(r => r.Slots).FirstOrDefault(v => v is not null),
            g.Select(r => r.BaseProps).FirstOrDefault(v => v is not null), g.Select(r => r.AddProps).FirstOrDefault(v => v is not null)));
    }
    private static RoleEquipmentState Equip(ProtocolMessage m) => new(m.Int32(1), m.Int32s(2), null, null);
    private static RoleEquipmentState Props(ProtocolMessage m) => new(m.Int32(1), null,
        RoleMessageAdapter.Values(m.Messages(2)), RoleMessageAdapter.Values(m.Messages(3)));
}
