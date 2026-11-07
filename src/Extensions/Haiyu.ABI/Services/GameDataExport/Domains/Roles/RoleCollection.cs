using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Roles;

internal sealed class RoleCollection(IEnumerable<RoleState> baseline)
    : DataCollectionBase<int, RoleState, RoleChange>(baseline, r => r.Id, id => id > 0, Comparer<int>.Default)
{
    internal override bool IsComplete => Items.Values.All(role => role.IsInitialized);
    public override ChangeResult Update(int key, RoleChange change)
    {
        if (change is RoleReplaced full)
        {
            var value = full.Value;
            // RoleInstance.RefreshRoleInfo 逐技能赋值，不清空未出现的技能。
            // 明确的全量基线通过构造新集合替换，不经过此增量路径。
            if (Items.TryGetValue(key, out var previous) && previous.IsInitialized)
                value = value with { Skills = MergeValues(previous.Skills, value.Skills) };
            return Replace(key, value);
        }
        if (key <= 0) return ChangeResult.Invalid("invalid_role_id");
        if (!Items.ContainsKey(key))
        {
            if (change is RoleAttributesChanged { IgnoreMissing: true }) return ChangeResult.Unchanged;
            // 正常获得角色时，属性/技能/节点会先于 Active 到达。保留待初始化标识，
            // 阻止不完整快照落盘；完整角色对象到达后才能解除，不能凭零散字段伪造拥有角色。
            var pending = new RoleState(key, "", 0, 0, 0, [], [], [], [], IsInitialized: false);
            return Replace(key, Merge(pending, change));
        }
        return base.Update(key, change);
    }
    protected override RoleState Merge(RoleState current, RoleChange change) => change switch
    {
        RoleSkillChanged skill => current with { Skills = MergeValues(current.Skills, [skill.Value]) },
        RoleAttributesChanged props => current with
        {
            BaseProps = props.MergeKeys ? MergeValues(current.BaseProps, props.BaseProps) : props.BaseProps,
            AddProps = props.MergeKeys ? MergeValues(current.AddProps, props.AddProps) : props.AddProps
        },
        RoleLevelChanged level => current with { Level = level.Level, Exp = level.Exp },
        RoleBreakthroughChanged breach => current with { Breakthrough = breach.Breakthrough },
        RoleNameChanged name => current with { Name = name.Name },
        RoleNodesChanged nodes => current with { SkillNodes = nodes.Nodes },
        _ => throw new InvalidDataException("不支持的角色变更。")
    };
    protected override bool Equivalent(RoleState a, RoleState b) =>
        a.IsInitialized == b.IsInitialized && a.Id == b.Id && a.Name == b.Name && a.Level == b.Level && a.Exp == b.Exp && a.Breakthrough == b.Breakthrough
        && a.Skills.SequenceEqual(b.Skills) && a.BaseProps.SequenceEqual(b.BaseProps)
        && a.AddProps.SequenceEqual(b.AddProps) && a.SkillNodes.SequenceEqual(b.SkillNodes);
    private static IReadOnlyList<RoleValue> MergeValues(IReadOnlyList<RoleValue> current, IEnumerable<RoleValue> patch) =>
        Array.AsReadOnly(current.Concat(patch).GroupBy(p => p.Id).Select(g => g.Last()).OrderBy(p => p.Id).ToArray());
}

internal sealed class RoleEquipmentCollection : DataCollectionBase<int, RoleEquipmentState, RoleEquipmentChange>
{
    internal RoleEquipmentCollection(IEnumerable<RoleEquipmentState> baseline)
        : base(baseline, r => r.Id, id => id > 0, Comparer<int>.Default)
    {
        var seen = new HashSet<int>();
        foreach (var role in Items.Values)
        {
            Validate(role);
            if (role.Slots is { } slots && slots.Where(id => id > 0).Any(id => !seen.Add(id)))
                throw new InvalidDataException("声骸被重复装备。");
        }
    }
    private static void Validate(RoleEquipmentState value)
    {
        if (value.Slots is not { } slots) return;
        if (slots.Count > 5 || slots.Any(id => id < 0) || slots.Where(id => id > 0).Distinct().Count() != slots.Count(id => id > 0))
            throw new InvalidDataException("声骸槽位无效。");
    }
    public override ChangeResult Update(int key, RoleEquipmentChange change)
    {
        var value = change.Value;
        Validate(value);
        if (value.Slots is { } slots)
        {
            var equipped = slots.Where(id => id > 0).ToHashSet();
            foreach (var other in Items.Values.Where(r => r.Id != key && r.Slots is not null).ToArray())
                Replace(other.Id, other with { Slots = Array.AsReadOnly(other.Slots!.Select(id => equipped.Contains(id) ? 0 : id).ToArray()) });
        }
        if (Items.TryGetValue(key, out var current)) value = Merge(current, new(value));
        return Replace(key, value);
    }
    protected override RoleEquipmentState Merge(RoleEquipmentState current, RoleEquipmentChange change) =>
        change.Value with { Slots = change.Value.Slots ?? current.Slots,
            BaseProps = change.Value.BaseProps ?? current.BaseProps, AddProps = change.Value.AddProps ?? current.AddProps };
    protected override bool Equivalent(RoleEquipmentState a, RoleEquipmentState b) => a.Id == b.Id
        && Equal(a.Slots, b.Slots) && Equal(a.BaseProps, b.BaseProps) && Equal(a.AddProps, b.AddProps);
    private static bool Equal<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}
