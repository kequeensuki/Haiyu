using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation;
using ABI.Models;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static async Task TestRoleRepeatedValuesAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        var role = OwnedRole(1);
        role.BaseProp.Add(new ArrayIntInt { Key = 1, Value = 150 });
        role.AddProp.Add(new ArrayIntInt { Key = 1, Value = 5 });
        role.Skills.Add(new ArrayIntInt { Key = 100, Value = 4 });
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { role } });
        var result = await f.Flush(); var exported = result.Roles!.Items.Single();
        Equal(150L, exported.BaseProps.Single(v => v.Id == 1).Value);
        Equal(155L, exported.Attributes.Single(v => v.Id == 1).Value);
        Equal(4L, exported.Skills.Single(v => v.Id == 100).Value);
        f.Feed(WavesMessageIds.PbRolePropsNotify, new PbRolePropsNotify { RoleId = 1,
            BaseProp = { new ArrayIntInt { Key = 1, Value = 200 }, new ArrayIntInt { Key = 1, Value = 210 } } });
        f.Feed(WavesMessageIds.RolePhantomPropUpdateNotify, new RolePhantomPropUpdateNotify { PropInfo = {
            new RolePhantomPropInfo { RoleId = 1, BaseProp = { new ArrayIntInt { Key = 1, Value = 10 }, new ArrayIntInt { Key = 1, Value = 12 } } } } });
        exported = (await f.Flush()).Roles!.Items.Single();
        Equal(210L, exported.Attributes.Single().Value); Equal(12L, exported.EchoBaseProps!.Single().Value);
        var saved = (await f.Flush()).OutputJsonPath!; var before = File.ReadAllText(saved);
        f.Feed(WavesMessageIds.PbRolePropsNotify, new PbRolePropsNotify { RoleId = 1, BaseProp = { new ArrayIntInt { Key = 0 } } });
        await f.Flush(); Equal(before, File.ReadAllText(saved)); // 无效 ID 仍必须拒绝，不放宽真正损坏的数据。
    }
    private static async Task TestRoleInitializationOrderAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1) } });
        var result = await f.Flush(); var before = File.ReadAllText(result.OutputJsonPath!);
        // 真实流量顺序：属性、节点、七个技能，然后才是 Active；此前角色不在账号列表。
        f.Feed(WavesMessageIds.PbRolePropsNotify, new PbRolePropsNotify { RoleId = 2, BaseProp = { new ArrayIntInt { Key = 1, Value = 999 } } });
        f.Feed(WavesMessageIds.RoleSkillNodeNotify, new RoleSkillNodeNotify { RoleId = 2, SkillNodeState = { new ArraySkillNode { SkillNodeId = 20 } } });
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 2, SkillInfo = new ArrayIntInt { Key = 100, Value = 9 } });
        await f.Flush(); Equal(before, File.ReadAllText(result.OutputJsonPath!));
        Equal("AwaitingEntities", f.Coordinator.Progress().Single().Sources.Single(s => s.SourceId == "roles").State);
        f.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify { Role = OwnedRole(2) });
        var recovered = await f.Flush(); Equal(2, recovered.Roles!.Items.Count);
        var activated = recovered.Roles.Items.Single(r => r.Id == 2);
        Equal(1L, activated.Skills.Single(s => s.Id == 100).Value); // 完整角色为权威事实，不重放此前初始化片段。
        Equal(100L, activated.BaseProps.Single(s => s.Id == 1).Value);
        Equal("Ready", recovered.Modules!.Single().Sources.Single(s => s.SourceId == "roles").State);
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 2, SkillInfo = new ArrayIntInt { Key = 100, Value = 2 } });
        Equal(2L, (await f.Flush()).Roles!.Items.Single(r => r.Id == 2).Skills.Single(s => s.Id == 100).Value);
        before = File.ReadAllText(result.OutputJsonPath!);
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 3, SkillInfo = new ArrayIntInt { Key = 100, Value = 2 } });
        await f.Flush(); Equal(before, File.ReadAllText(result.OutputJsonPath!)); // 新增真的遗漏时，不能输出假完整数据。
        Equal(GameDataExportModuleState.Incomplete, (await f.Coordinator.FinishAsync(new(null, false))).Single().State);
    }
    private sealed record EntityCompleteness(bool Complete);
    private static Task TestAggregateEntityCompletenessAsync()
    {
        var aggregate = new AggregateState<EntityCompleteness>("epoch", [new("entities")], _ => 1, complete: s => s.Complete);
        var source = aggregate.Source("entities");
        source.Full(new(true)); Equal(true, aggregate.Ready); var first = aggregate.Build()!;
        SourceProvenance At(long index) => new(index, index, DateTimeOffset.UtcNow, 1);
        source.ApplyOrdered(_ => new(false), At(1));
        Equal(true, source.Available); Equal(false, aggregate.Ready); Equal(true, aggregate.Build() is null);
        Equal("AwaitingEntities", aggregate.Progress().Single().State); Equal(true, aggregate.Progress().Single().Error is null);
        source.ApplyOrdered(_ => new(true), At(2));
        Equal(true, aggregate.Ready); Equal(true, aggregate.Build()!.Revision > first.Revision);
        source.Reject("corrupt", true);
        Equal(false, source.ApplyOrdered(_ => new(true), At(3))); Equal(false, aggregate.Ready);
        source.Full(new(true)); Equal(true, aggregate.Ready); // 硬失同步仍需要全量，不能由实体补齐绕过。
        return Task.CompletedTask;
    }
}
