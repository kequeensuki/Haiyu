using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Roles;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Roles;

internal sealed class RoleMessageAdapter : CollectionMessageAdapter<int, RoleState, RoleChange>
{
    internal RoleMessageAdapter() : base("roles", items => new RoleCollection(items), r =>
        128 + r.Name.Length * 2L + (r.Skills.Count + r.BaseProps.Count + r.AddProps.Count) * 32L + r.SkillNodes.Count * 40L)
    {
        InitialFullThenMerge(WavesMessageIds.PbGetRoleListNotify, 7, ReadRole, role => role.Id, role => new RoleReplaced(role));
        FullChecked(WavesMessageIds.PbGetRoleListResponse, 2, m => m.Messages(7).Select(ReadRole));
        On(WavesMessageIds.PbRoleActiveNotify, 0, (m, c) => Upsert(c, ReadRole(Required(m, 6))));
        // 官方成功回调允许 RoleInfo 缺席；无对象的 ACK 不创建来源或触发失同步。
        On(WavesMessageIds.RoleSkillQuickLevelUpResponse, 14, (m, c) => Upsert(c, ReadRole(Required(m, 15))),
            when: m => m.Message(15) is not null);
        On(WavesMessageIds.PbRoleSkillLevelNotify, 0, (m, c) =>
        {
            var skill = Required(m, 10); var values = Values([skill]);
            return c.Update(m.Int32(5), new RoleSkillChanged(values.Single()));
        });
        On(WavesMessageIds.PbRolePropsNotify, 0, (m, c) => c.Update(m.Int32(8),
            new RoleAttributesChanged(Values(m.Messages(14)), Values(m.Messages(1)))));
        // RolePropResponse 也会返回试用/机器人角色：仅更新账号已有角色，逐键合并，与官方回调一致。
        On(WavesMessageIds.RolePropResponse, 11, (m, c) => Each(m.Messages(2), p => c.Update(p.Int32(1),
            new RoleAttributesChanged(Values(p.Messages(2)), Values(p.Messages(3)), MergeKeys: true, IgnoreMissing: true))));
        On(WavesMessageIds.PbRoleExpNotify, 0, (m, c) => c.Update(m.Int32(5), new RoleLevelChanged(m.Int32(4), m.Int32(9))));
        On(WavesMessageIds.PbOverRoleResponse, 12, (m, c) => c.Update(m.Int32(5), new RoleBreakthroughChanged(m.Int32(2))));
        On(WavesMessageIds.RoleChangeNameNotify, 0, (m, c) => c.Update(m.Int32(8), new RoleNameChanged(m.String(5))));
        On(WavesMessageIds.RoleSkillNodeNotify, 0, (m, c) => c.Update(m.Int32(13), new RoleNodesChanged(Nodes(m.Messages(8)))));
        On(WavesMessageIds.RoleChangeNotify, 0, (m, c) =>
        {
            var role = ReadRole(Required(m, 10));
            var removed = c.Remove(m.Int32(12));
            return removed.Succeeded ? Upsert(c, role) : removed;
        });
    }
    private static ChangeResult Upsert(IDataCollection<int, RoleState, RoleChange> c, RoleState role) => c.Update(role.Id, new RoleReplaced(role));
    internal static ProtocolMessage Required(ProtocolMessage m, int field) => m.Message(field) ?? throw new InvalidDataException("角色消息缺少对象。");
    internal static IReadOnlyList<RoleValue> Values(IEnumerable<ProtocolMessage> values)
    {
        // 客户端逐键赋值：重复属性/技能 ID 合法，后值覆盖前值。
        var result = new Dictionary<int, RoleValue>();
        foreach (var entry in values)
        {
            var id = entry.Int32(1);
            if (id <= 0) throw new InvalidDataException("角色属性/技能标识无效。");
            result[id] = new(id, entry.Int32(2));
        }
        return Array.AsReadOnly(result.Values.OrderBy(v => v.Id).ToArray());
    }
    private static IReadOnlyList<RoleSkillNode> Nodes(IEnumerable<ProtocolMessage> values)
    {
        var result = values.Select(v => new RoleSkillNode(v.Int32(1), v.Bool(2), v.Int32(3))).OrderBy(v => v.Id).ToArray();
        if (result.Any(v => v.Id <= 0) || result.Select(v => v.Id).Distinct().Count() != result.Length)
            throw new InvalidDataException("角色技能节点无效或重复。");
        return Array.AsReadOnly(result);
    }
    private static RoleState ReadRole(ProtocolMessage m) => new(m.Int32(1), m.String(2), m.Int32(3), m.Int32(4), m.Int32(5),
        Values(m.Messages(6)), Values(m.Messages(14)), Values(m.Messages(15)), Nodes(m.Messages(19)));
}
