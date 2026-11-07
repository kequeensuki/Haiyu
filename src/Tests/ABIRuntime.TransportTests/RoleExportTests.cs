using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Protocol;
using MemoryPack;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static roleInfo OwnedRole(int id, int level = 1) => new()
    {
        RoleId = id, Name = "角色" + id, Level = level, Exp = 4, Breakthrough = 1,
        Skills = { new ArrayIntInt { Key = 100, Value = 1 }, new ArrayIntInt { Key = 101, Value = 2 } },
        BaseProp = { new ArrayIntInt { Key = 1, Value = 100 }, new ArrayIntInt { Key = 2, Value = 50 } },
        AddProp = { new ArrayIntInt { Key = 1, Value = 20 } },
        SkillNodeState = { new ArraySkillNode { SkillNodeId = 10, IsActive = true, SkillId = 100 } }
    };
    private static async Task TestRoleDataChainAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        Equal(true, (await f.Flush()).Roles is null);
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1), OwnedRole(2) } });
        var result = await f.Flush();
        Equal(2, result.Roles!.Items.Count);
        Equal(120L, result.Roles.Items[0].Attributes.Single(v => v.Id == 1).Value);
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 1, SkillInfo = new ArrayIntInt { Key = 100, Value = 5 } });
        f.Feed(WavesMessageIds.PbRolePropsNotify, new PbRolePropsNotify { RoleId = 1, BaseProp = { new ArrayIntInt { Key = 1, Value = 0 } } });
        f.Feed(WavesMessageIds.PbRoleExpNotify, new PbRoleExpNotify { RoleId = 1, Level = 90, Exp = 5 });
        f.Feed(WavesMessageIds.PbOverRoleResponse, new PbOverRoleResponse { RoleId = 1, Breakthrough = 6 });
        f.Feed(WavesMessageIds.RoleChangeNameNotify, new RoleChangeNameNotify { RoleId = 1, Name = "new" });
        f.Feed(WavesMessageIds.RoleSkillNodeNotify, new RoleSkillNodeNotify { RoleId = 1 });
        result = await f.Flush(); var role = result.Roles!.Items[0];
        Equal(5L, role.Skills.Single(v => v.Id == 100).Value); Equal(2L, role.Skills.Single(v => v.Id == 101).Value);
        Equal(0L, role.Attributes.Single().Value); Equal(0, role.AddProps.Count); Equal(0, role.SkillNodes.Count);
        Equal(90, role.Level); Equal(5, role.Exp); Equal(6, role.Breakthrough); Equal("new", role.Name);
        // 查询按键合并；机器人角色不能进入账号列表。
        f.Feed(WavesMessageIds.RolePropResponse, new RolePropResponse { RoleProps = {
            new RolePropInfo { RoleId = 1, BaseProp = { new ArrayIntInt { Key = 2, Value = 30 } } },
            new RolePropInfo { RoleId = 999, BaseProp = { new ArrayIntInt { Key = 1, Value = 999 } } } } });
        role = (await f.Flush()).Roles!.Items[0]; Equal(2, role.BaseProps.Count); Equal(0L, role.BaseProps.Single(v => v.Id == 1).Value);
        f.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify { Role = OwnedRole(3) });
        f.Feed(WavesMessageIds.RoleSkillQuickLevelUpResponse, new RoleSkillQuickLevelUpResponse { RoleInfo = OwnedRole(3, 20) });
        f.Feed(WavesMessageIds.RoleChangeNotify, new RoleChangeNotify { SourceRoleId = 2, RoleInfo = OwnedRole(4) });
        result = await f.Flush(); Equal("1,3,4", string.Join(',', result.Roles!.Items.Select(r => r.Id)));
        Equal(20, result.Roles.Items.Single(r => r.Id == 3).Level);
        var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options), ABIMemoryPack.Options)!;
        Equal(3, round.Roles!.Items.Count); Equal(100, round.Roles.Items.Single(r => r.Id == 3).SkillNodes.Single().SkillId);
        using var json = JsonDocument.Parse(File.ReadAllText(result.OutputJsonPath!));
        Equal(3, json.RootElement.GetProperty("roles").GetProperty("items").GetArrayLength());
        var stable = result.UpdateCount;
        f.Feed(WavesMessageIds.PbOverRoleResponse, new PbOverRoleResponse { RoleId = 1, Breakthrough = 6 });
        Equal(stable, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.PbGetRoleListResponse, new PbGetRoleListResponse());
        Equal(0, (await f.Flush()).Roles!.Items.Count); // 空全量明确删除所有旧角色。
    }

    private static async Task TestRoleEquipmentChainAsync()
    {
        await using var f = new NotificationFixture();
        // 装备及属性先于角色列表到达。
        f.Full(phantoms: new PhantomItemResponse { PhantomItemList = {
            new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 25, StackCount = 3, FetterGroupId = 7 },
            new PhantomItem { Id = 31, IncrId = 302, PhantomLevel = 1, StackCount = 1 } },
            EquipInfo = { new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 301, 0, 302 } } },
            PropInfo = { new RolePhantomPropInfo { RoleId = 1, BaseProp = { new ArrayIntInt { Key = 1, Value = 10 } } } } });
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1), OwnedRole(2) } });
        var result = await f.Flush(); var role = result.Roles!.Items[0];
        Equal("ready", role.EquipmentStatus); Equal(5, role.Echoes.Count); Equal(true, role.Echoes[0].IsMain);
        Equal(25, role.Echoes[0].Item!.PhantomLevel); Equal(7, role.Echoes[0].Item!.FetterGroupId);
        Equal("empty", role.Echoes[1].Status); Equal(302, role.Echoes[2].IncrId); Equal(10L, role.EchoBaseProps!.Single().Value);
        f.Feed(WavesMessageIds.PhantomIdentifyResponse, new PhantomIdentifyResponse { UpdateInfo = new PhantomItem {
            Id = 31, IncrId = 302, PhantomLevel = 20, PhantomSubProp = { new PhantomPropInfo { PhantomPropId = 4, Value = 500 } } } });
        role = (await f.Flush()).Roles!.Items[0]; Equal(20, role.Echoes[2].Item!.PhantomLevel); Equal(500, role.Echoes[2].Item!.SubProps.Single().Value);
        f.Feed(WavesMessageIds.PhantomPutOnResponse, new PhantomPutOnResponse {
            StackSplitInfos = { new PhantomStackSplitInfo {
                OperateItem = new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 25, StackCount = 1, FetterGroupId = 7 },
                RemainderItem = new PhantomItem { Id = 30, IncrId = 303, PhantomLevel = 25, StackCount = 2, FetterGroupId = 7 } } }, EquipInfoList = {
            new RolePhantomEquipInfo { RoleId = 2, PhantomItemIncrId = { 301 } } } });
        result = await f.Flush(); Equal(0, result.Roles!.Items[0].Echoes[0].IncrId); Equal(301, result.Roles.Items[1].Echoes[0].IncrId);
        Equal(1, result.Roles.Items[1].Echoes[0].Item!.StackCount); Equal(2, result.Inventory!.Phantoms.Single(p => p.IncrId == 303).StackCount);
        f.Feed(WavesMessageIds.PhantomAutoPutResponse, new PhantomAutoPutResponse { EquipInfoList = { new RolePhantomEquipInfo { RoleId = 2 } } });
        Equal(true, (await f.Flush()).Roles!.Items[1].Echoes.All(e => e.Status == "empty"));
        f.Feed(WavesMessageIds.UsePhantomEquipGroupResponse, new UsePhantomEquipGroupResponse { EquipInfoList = {
            new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 302, 999 } } } });
        role = (await f.Flush()).Roles!.Items[0]; Equal("incomplete", role.EquipmentStatus); Equal("unresolved", role.Echoes[1].Status);
        f.Feed(WavesMessageIds.PhantomItemAddNotify, new PhantomItemAddNotify { PhantomItemList = { new PhantomItem { Id = 33, IncrId = 999, PhantomLevel = 26, StackCount = 1 } } });
        Equal("invalid_level", (await f.Flush()).Roles!.Items[0].Echoes[1].Status);
        f.Feed(WavesMessageIds.PhantomEquipInfoNotify, new PhantomEquipInfoNotify { EquipInfo = { new RolePhantomEquipInfo { RoleId = 1 } } });
        f.Feed(WavesMessageIds.RolePhantomPropUpdateNotify, new RolePhantomPropUpdateNotify { PropInfo = { new RolePhantomPropInfo { RoleId = 1 } } });
        role = (await f.Flush()).Roles!.Items[0]; Equal("ready", role.EquipmentStatus); Equal(0, role.EchoBaseProps!.Count);
        Equal(true, role.Echoes.All(e => e.Status == "empty"));
        f.Feed(WavesMessageIds.PhantomEquipInfoNotify, new PhantomEquipInfoNotify { EquipInfo = {
            new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 301 } } } });
        f.Feed(WavesMessageIds.RoleChangeNotify, new RoleChangeNotify { SourceRoleId = 1, RoleInfo = OwnedRole(3) });
        f.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify { Role = OwnedRole(1) });
        result = await f.Flush(); Equal(true, result.Roles!.Items.Single(r => r.Id == 1).Echoes.All(e => e.Status == "empty"));
        // 同一个角色在完整装备列表内重复出现，不能取第一条掩盖矛盾。
        var beforeDuplicate = File.ReadAllText(result.OutputJsonPath!);
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { EquipInfo = {
            new RolePhantomEquipInfo { RoleId = 1 }, new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 301 } } } });
        await f.Flush(); Equal(beforeDuplicate, File.ReadAllText(result.OutputJsonPath!));
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse());
        Equal("ready", (await f.Flush()).Roles!.Items.Single(r => r.Id == 1).EquipmentStatus);

    }

    private static async Task TestRoleRecoveryAndIsolationAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 1, SkillInfo = new ArrayIntInt { Key = 100, Value = 2 } });
        Equal(0, (await f.Flush()).PlayerSummaries!.Length);
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1) } });
        var good = await f.Flush(); var before = File.ReadAllText(good.OutputJsonPath!);
        f.Feed(WavesMessageIds.PbGetRoleListResponse, new PbGetRoleListResponse { Code = (ErrorCode)1 });
        f.Feed(WavesMessageIds.RoleSkillQuickLevelUpResponse, new RoleSkillQuickLevelUpResponse { ErrorCode = (ErrorCode)1 });
        f.Feed(WavesMessageIds.PbOverRoleResponse, new PbOverRoleResponse { Code = (ErrorCode)1, RoleId = 1, Breakthrough = 99 });
        f.Feed(WavesMessageIds.PhantomPutOnResponse, new PhantomPutOnResponse { ErrorCode = (ErrorCode)1 });
        Equal(good.UpdateCount, (await f.Flush()).UpdateCount);
        // 未捕获新角色的新增，不能凭单技能补成一个空角色。
        f.Feed(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 2, SkillInfo = new ArrayIntInt { Key = 100, Value = 2 } });
        await f.Flush(); Equal(before, File.ReadAllText(good.OutputJsonPath!));
        f.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify { Role = OwnedRole(2) });
        await f.Flush(); Equal(before, File.ReadAllText(good.OutputJsonPath!));
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1), OwnedRole(2) } });
        await f.Flush(); Equal(before, File.ReadAllText(good.OutputJsonPath!)); // 后续通知不能证明完整性，不能解除失同步。
        f.Feed(WavesMessageIds.PbGetRoleListResponse, new PbGetRoleListResponse { RoleList = { OwnedRole(1), OwnedRole(2) } });
        good = await f.Flush(); Equal(2, good.Roles!.Items.Count); before = File.ReadAllText(good.OutputJsonPath!);
        // 无效批次回滚之前已经执行的转移，保留磁盘；装备全量才能恢复。
        f.Feed(WavesMessageIds.PhantomEquipInfoNotify, new PhantomEquipInfoNotify { EquipInfo = {
            new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 123 } },
            new RolePhantomEquipInfo { RoleId = 2, PhantomItemIncrId = { 1, 2, 3, 4, 5, 6 } } } });
        await f.Flush(); Equal(before, File.ReadAllText(good.OutputJsonPath!));
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse());
        good = await f.Flush(); Equal("ready", good.Roles!.Items[0].EquipmentStatus);
        f.Full(session: "login-B", player: 222);
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(3) } }, "login-B", 222);
        var other = await f.Flush(); Equal(222, other.PlayerId); Equal(3, other.Roles!.Items.Single().Id);
        f.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify { Role = OwnedRole(4) }, current: false);
        Equal(other.UpdateCount, (await f.Flush()).UpdateCount);
    }
    private static async Task TestRoleListMergeAndOptionalResponseAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        f.Feed(WavesMessageIds.RoleSkillQuickLevelUpResponse, new RoleSkillQuickLevelUpResponse());
        var initial = await f.Flush();
        Equal(true, initial.Inventory is not null); Equal(true, initial.Roles is null);
        var source = initial.Modules!.Single().Sources.Single(s => s.SourceId == "roles");
        Equal(false, source.Required); Equal("Missing", source.State);
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1), OwnedRole(2) } });
        var full = await f.Flush(); Equal(2, full.Roles!.Items.Count);
        var changedRole = OwnedRole(1, 9); changedRole.Skills.Clear(); changedRole.Skills.Add(new ArrayIntInt { Key = 100, Value = 9 });
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { changedRole } });
        var partial = await f.Flush(); Equal(2, partial.Roles!.Items.Count);
        Equal(9, partial.Roles.Items.Single(r => r.Id == 1).Level); Equal(1, partial.Roles.Items.Single(r => r.Id == 2).Level);
        Equal(9L, partial.Roles.Items[0].Skills.Single(s => s.Id == 100).Value);
        Equal(2L, partial.Roles.Items[0].Skills.Single(s => s.Id == 101).Value);
        changedRole.Skills[0] = new ArrayIntInt { Key = 100, Value = 10 };
        f.Feed(WavesMessageIds.RoleSkillQuickLevelUpResponse, new RoleSkillQuickLevelUpResponse { RoleInfo = changedRole });
        partial = await f.Flush();
        Equal(10L, partial.Roles!.Items[0].Skills.Single(s => s.Id == 100).Value);
        Equal(2L, partial.Roles.Items[0].Skills.Single(s => s.Id == 101).Value);
        f.Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify());
        f.Feed(WavesMessageIds.RoleSkillQuickLevelUpResponse, new RoleSkillQuickLevelUpResponse());
        Equal(partial.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.PbGetRoleListResponse, new PbGetRoleListResponse());
        Equal(0, (await f.Flush()).Roles!.Items.Count); // 明确全量响应的空列表才删除旧角色。
    }

}
