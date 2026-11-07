using ABI.Models;
using Haiyu.ABI.Protocol;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static async Task TestWeaponMutationChainAsync()
    {
        await using var f = new NotificationFixture();
        f.Full();
        await f.Flush();
        WeaponItem Weapon(int id, int role = 0) => new() { Id = 10, IncrId = id, WeaponLevel = 1, RoleId = role };
        f.Feed(WavesMessageIds.WeaponItemAddNotify, new WeaponItemAddNotify { WeaponItemList = { Weapon(101, 1), Weapon(102, 2) } });
        f.Feed(WavesMessageIds.WeaponLevelUpResponse, new WeaponLevelUpResponse { IncId = 101, WeaponLevel = 40, WeaponExp = 123 });
        f.Feed(WavesMessageIds.WeaponBreachResponse, new WeaponBreachResponse { IncId = 101, WeaponBreach = 3 });
        f.Feed(WavesMessageIds.WeaponResonUpResponse, new WeaponResonUpResponse { IncId = 101, ReaonLevel = 4 });
        f.Feed(WavesMessageIds.EquipTakeOnResponse, new EquipTakeOnResponse
            { DataList = { new RoleLoadEquipData { RoleId = 2, EquipIncId = 101 } } });
        var result = await f.Flush();
        var weapon = result.Inventory!.Weapons.Single(w => w.IncrId == 101);
        Equal(40, weapon.WeaponLevel); Equal(123, weapon.WeaponExp);
        Equal(3, weapon.WeaponBreach); Equal(4, weapon.WeaponResonLevel); Equal(2, weapon.RoleId);
        Equal(0, result.Inventory.Weapons.Single(w => w.IncrId == 102).RoleId);
        // 重复绝对值不制造新版本；皮肤槽位不能改变武器归属。
        f.Feed(WavesMessageIds.WeaponLevelUpResponse, new WeaponLevelUpResponse { IncId = 101, WeaponLevel = 40, WeaponExp = 123 });
        f.Feed(WavesMessageIds.EquipTakeOnNotify, new EquipTakeOnNotify
            { DataList = { new RoleLoadEquipData { RoleId = 9, EquipIncId = 999, Pos = (EquipPos)1 } } });
        Equal(result.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.WeaponLevelUpResponse, new WeaponLevelUpResponse { ErrorCode = (ErrorCode)1, IncId = 999 });
        f.Feed(WavesMessageIds.WeaponBreachResponse, new WeaponBreachResponse { ErrorCode = (ErrorCode)1, IncId = 999 });
        f.Feed(WavesMessageIds.WeaponResonUpResponse, new WeaponResonUpResponse { ErrorCode = (ErrorCode)1, IncId = 999 });
        f.Feed(WavesMessageIds.EquipTakeOnResponse, new EquipTakeOnResponse
            { ErrorCode = (ErrorCode)1, DataList = { new RoleLoadEquipData { RoleId = 2, EquipIncId = 999 } } });
        Equal(result.UpdateCount, (await f.Flush()).UpdateCount);
        // 角色编号为零、实例已知也可以明确表示卸下；不能清除所有未装备武器。
        f.Feed(WavesMessageIds.EquipTakeOnNotify, new EquipTakeOnNotify
            { DataList = { new RoleLoadEquipData { EquipIncId = 101 } } });
        Equal(0, (await f.Flush()).Inventory!.Weapons.Single(w => w.IncrId == 101).RoleId);
        f.Feed(WavesMessageIds.EquipTakeOnNotify, new EquipTakeOnNotify
            { DataList = { new RoleLoadEquipData { RoleId = 2, EquipIncId = 101 } } });
        await f.Flush();
        f.Feed(WavesMessageIds.EquipTakeOnNotify, new EquipTakeOnNotify { DataList = { new RoleLoadEquipData { RoleId = 2 } } });
        Equal(0, (await f.Flush()).Inventory!.Weapons.Single(w => w.IncrId == 101).RoleId);
        f.Feed(WavesMessageIds.WeaponItemRemoveNotify, new WeaponItemRemoveNotify { WeaponItemIncrIdList = { 101 } });
        Equal(false, (await f.Flush()).Inventory!.Weapons.Any(w => w.IncrId == 101));
        f.Feed(WavesMessageIds.WeaponItemAddNotify, new WeaponItemAddNotify { WeaponItemList = { Weapon(101) } });
        result = await f.Flush();
        Equal(1, result.Inventory!.Weapons.Single(w => w.IncrId == 101).WeaponLevel);
        var previous = File.ReadAllText(result.OutputJsonPath!);
        // 漏掉新增后收到修改：不静默忽略，也不合成一个不完整对象。
        f.Feed(WavesMessageIds.WeaponLevelUpResponse, new WeaponLevelUpResponse { IncId = 999, WeaponLevel = 70 });
        f.Feed(WavesMessageIds.WeaponItemAddNotify, new WeaponItemAddNotify { WeaponItemList = { Weapon(999) } });
        var stale = await f.Flush();
        Equal(previous, File.ReadAllText(result.OutputJsonPath!));
        Equal("AwaitingBaseline", stale.Modules!.Single().Sources.Single(s => s.SourceId == "weapons").State);
        f.Feed(WavesMessageIds.WeaponItemResponse, new WeaponItemResponse
            { WeaponItemList = { new WeaponItem { Id = 10, IncrId = 999, WeaponLevel = 70 } } });
        result = await f.Flush();
        Equal(70, result.Inventory!.Weapons.Single().WeaponLevel);
        Equal("Ready", result.Modules!.Single().Sources.Single(s => s.SourceId == "weapons").State);
        Equal(GameDataExportModuleState.Completed, (await f.Coordinator.FinishAsync(new(null, false))).Single().State);
    }

    private static async Task TestMutationDesyncRecoveryAsync()
    {
        await using var f = new NotificationFixture();
        PhantomItem Phantom(int id, int count = 1) => new() { Id = 30, IncrId = id, StackCount = count, PhantomLevel = 5, AddTime = 42 };
        f.Full(phantoms: new() { PhantomItemList = { Phantom(301, 3) } });
        var first = await f.Flush();
        f.Feed(WavesMessageIds.PhantomPutOnResponse, new PhantomPutOnResponse { StackSplitInfos = {
            new PhantomStackSplitInfo { OperateItem = Phantom(301), RemainderItem = Phantom(302, 2) } } });
        Equal(2, (await f.Flush()).Inventory!.Phantoms.Count);
        f.Feed(WavesMessageIds.PhantomAutoPutResponse, new PhantomAutoPutResponse { StackMergeInfos = {
            new PhantomStackMergeInfo { MergedIncrId = 302, RepresentativeItem = Phantom(301, 3) } } });
        var merged = await f.Flush();
        Equal(3, merged.Inventory!.Phantoms.Single().StackCount);
        f.Feed(WavesMessageIds.ItemFuncValueUpdateNotify, new ItemFuncValueUpdateNotify { IncrId = 301, FuncValue = 8 });
        Equal(8, (await f.Flush()).Inventory!.Phantoms.Single().FuncValue);
        f.Feed(WavesMessageIds.PhantomBatchPolishResponse, new PhantomBatchPolishResponse { UpdateInfos = {
            new PhantomItem { Id = 30, IncrId = 301, StackCount = 3, PhantomLevel = 5, FetterGroupId = 7 } } });
        Equal(7, (await f.Flush()).Inventory!.Phantoms.Single().FetterGroupId);
        f.Feed(WavesMessageIds.PhantomPolishResponse, new PhantomPolishResponse
            { UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, StackCount = 3, PhantomLevel = 5, FetterGroupId = 9 } });
        Equal(9, (await f.Flush()).Inventory!.Phantoms.Single().FetterGroupId);
        f.Feed(WavesMessageIds.PhantomItemRemoveNotify, new PhantomItemRemoveNotify { PhantomItemIncrIdList = { 301 } });
        Equal(0, (await f.Flush()).Inventory!.Phantoms.Count);
        f.Feed(WavesMessageIds.PhantomItemAddNotify, new PhantomItemAddNotify { PhantomItemList = { Phantom(301) } });
        var saved = await f.Flush();
        Equal(1, saved.Inventory!.Phantoms.Single().StackCount);
        var bytes = File.ReadAllText(saved.OutputJsonPath!);
        f.Feed(WavesMessageIds.PhantomSkinChangeResponse, new PhantomSkinChangeResponse { ErrorCode = (ErrorCode)1 });
        Equal(saved.UpdateCount, (await f.Flush()).UpdateCount);
        // 成功但没有目标和值的响应必须显式失同步，不猜测皮肤或批量标记。
        f.Feed(WavesMessageIds.PhantomSkinChangeResponse, new PhantomSkinChangeResponse());
        var stale = await f.Flush();
        Equal("AwaitingBaseline", stale.Modules!.Single().Sources.Single(s => s.SourceId == "phantoms").State);
        Equal(bytes, File.ReadAllText(saved.OutputJsonPath!));
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { PhantomItemList = {
            new PhantomItem { Id = 30, IncrId = 301, StackCount = 1, SkinId = 99 } } });
        Equal(99, (await f.Flush()).Inventory!.Phantoms.Single().SkinId);
        f.Feed(WavesMessageIds.PhantomBatchOperResponse, new PhantomBatchOperResponse());
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "phantoms").State);
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { PhantomItemList = { Phantom(301) } });
        await f.Flush();
        f.Feed(WavesMessageIds.NormalItemUpdateNotify, new NormalItemUpdateNotify { NormalItemList = { new NormalItem { Id = 999, Count = 5 } } });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "normalItems").State);
        f.Feed(WavesMessageIds.NormalItemResponse, new NormalItemResponse { NormalItemList = { new NormalItem { Id = 999, Count = 5 } } });
        Equal(5, (await f.Flush()).Inventory!.NormalItems.Single().Count);
        // 同一通知中先合法后非法的变更，不能部分提交。
        f.Feed(WavesMessageIds.PhantomItemUpdateNotify, new PhantomItemUpdateNotify { UpdateInfo = {
            new PhantomItem { Id = 30, IncrId = 301, StackCount = 2 }, Phantom(999) } });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "phantoms").State);
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { PhantomItemList = { Phantom(301) } });
        await f.Flush();
        f.Feed(WavesMessageIds.ValidTimeItemUpdateNotify, new ValidTimeItemUpdateNotify
            { ItemList = { new ValidTimeItem { Id = 20, IncrId = 888, Count = 2 } } });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "validTimeItems").State);
        f.Feed(WavesMessageIds.ValidTimeItemResponse, new ValidTimeItemResponse());
        await f.Flush();
        f.Feed(WavesMessageIds.PhantomIdentifyResponse, new PhantomIdentifyResponse { UpdateInfo = Phantom(999) });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "phantoms").State);
        f.Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { PhantomItemList = { Phantom(301) } });
        await f.Flush();
        f.Feed(WavesMessageIds.ItemFuncValueUpdateNotify, new ItemFuncValueUpdateNotify { IncrId = 999, FuncValue = 8 });
        var unknown = await f.Flush();
        Equal("AwaitingBaseline", unknown.Modules!.Single().Sources.Single(s => s.SourceId == "phantoms").State);
        Equal("AwaitingBaseline", unknown.Modules!.Single().Sources.Single(s => s.SourceId == "weapons").State);
        Equal(GameDataExportModuleState.Failed, (await f.Coordinator.FinishAsync(new(null, false))).Single().State);
    }
}
