using System.Text.Json;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private sealed class NotificationFixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "haiyu-notifications-" + Guid.NewGuid().ToString("N"));
        internal readonly ExportTestClock Clock = new();
        internal readonly ExportCaptureContext Context;
        internal readonly ExportCoordinator Coordinator;
        private readonly ProtocolJsonDecoder _decoder = new(ProtocolCatalog.Default.Value);
        internal NotificationFixture(params string[] modules)
        {
            Context = new(new JsonExportStore(Root), Clock);
            Coordinator = new(Context, BuiltInExportModules.Create(), modules.Length == 0 ? ["inventory"] : modules);
        }
        internal void Feed(ushort id, IMessage message, string session = "login-A", int player = 111, bool current = true)
        {
            var bytes = message.ToByteArray();
            Coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, bytes), bytes, player, session, 1, null,
                _decoder.Decode(id, bytes), IsCurrentSession: current));
        }
        internal void Full(NormalItemResponse? normal = null, WeaponItemResponse? weapons = null, PhantomItemResponse? phantoms = null,
            ValidTimeItemResponse? timed = null, string session = "login-A", int player = 111)
        {
            Feed(WavesMessageIds.NormalItemResponse, normal ?? new(), session, player);
            Feed(WavesMessageIds.WeaponItemResponse, weapons ?? new(), session, player);
            Feed(WavesMessageIds.PhantomItemResponse, phantoms ?? new(), session, player);
            Feed(WavesMessageIds.ValidTimeItemResponse, timed ?? new(), session, player);
        }
        internal async Task<GameDataExportResult> Flush()
        {
            Clock.Advance(TimeSpan.FromSeconds(2));
            Coordinator.Checkpoint();
            await Context.Output.FlushAsync();
            return Coordinator.LegacyResult(Coordinator.Progress());
        }
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static async Task TestInventoryNotificationsAsync()
    {
        await using var fixture = new NotificationFixture();
        fixture.Full(new() { NormalItemList = { new NormalItem { Id = 1, Count = 10, ExpireTime = 123 } } },
            new() { WeaponItemList = { new WeaponItem { Id = 10, IncrId = 101 } } }, timed: new()
            { ItemList = { new ValidTimeItem { Id = 20, IncrId = 201, Count = 5, ExpireTime = 1000 },
                new ValidTimeItem { Id = 20, IncrId = 202, Count = 6, ExpireTime = 2000 } } });
        var before = await fixture.Flush();
        fixture.Feed(WavesMessageIds.NormalItemUpdateNotify, new NormalItemUpdateNotify
            { NormalItemList = { new NormalItem { Id = 1, Count = 7 } } });
        fixture.Feed(WavesMessageIds.NormalItemAddNotify, new NormalItemAddNotify
            { NormalItemList = { new NormalItem { Id = 2, Count = 4 }, new NormalItem { Id = 1, Count = 999 } } });
        fixture.Feed(WavesMessageIds.WeaponItemAddNotify, new WeaponItemAddNotify
            { WeaponItemList = { new WeaponItem { Id = 11, IncrId = 102, WeaponLevel = 20 } } });
        fixture.Feed(WavesMessageIds.WeaponItemRemoveNotify, new WeaponItemRemoveNotify { WeaponItemIncrIdList = { 101 } });
        fixture.Feed(WavesMessageIds.ValidTimeItemUpdateNotify, new ValidTimeItemUpdateNotify
            { ItemList = { new ValidTimeItem { Id = 20, IncrId = 202, Count = 3, ExpireTime = 3000 } } });
        fixture.Feed(WavesMessageIds.ValidTimeItemRemoveNotify, new ValidTimeItemRemoveNotify
            { ItemList = { new RemoveValidTimeItem { ItemId = 20, IncrId = 201 } } });
        fixture.Feed(WavesMessageIds.ValidTimeItemAddNotify, new ValidTimeItemAddNotify
            { ItemList = { new ValidTimeItem { Id = 20, IncrId = 203, Count = 8 } } });
        var result = await fixture.Flush();
        Equal(7, result.Inventory!.NormalItems.Single(item => item.Id == 1).Count);
        Equal(123L, result.Inventory.NormalItems.Single(item => item.Id == 1).ExpireTime);
        Equal(2, result.Inventory.NormalItems.Count);
        Equal(102, result.Inventory.Weapons.Single().IncrId);
        Equal(2, result.Inventory.ValidTimeItems.Count);
        Equal(3000L, result.Inventory.ValidTimeItems.Single(item => item.IncrId == 202).ExpireTime);
        Equal(before.UpdateCount + 1, result.UpdateCount);
        // 同一绝对数量、重复新增、删除不存在的对象都不制造新的落盘版本。
        fixture.Feed(WavesMessageIds.NormalItemUpdateNotify, new NormalItemUpdateNotify
            { NormalItemList = { new NormalItem { Id = 1, Count = 7 } } });
        fixture.Feed(WavesMessageIds.NormalItemRemoveNotify, new NormalItemRemoveNotify { NormalItemIdList = { 999 } });
        Equal(result.UpdateCount, (await fixture.Flush()).UpdateCount);
        fixture.Feed(WavesMessageIds.NormalItemRemoveNotify, new NormalItemRemoveNotify { NormalItemIdList = { 1 } });
        Equal(2, (await fixture.Flush()).Inventory!.NormalItems.Single().Id);
    }

    private static async Task TestPhantomMutationResponsesAsync()
    {
        Equal(22489, WavesMessageIds.PhantomLevelUpResponse);
        Equal(21034, WavesMessageIds.PhantomIdentifyResponse);
        Equal(22926, WavesMessageIds.PhantomVicePolishAckResponse);
        await using var fixture = new NotificationFixture();
        fixture.Full(phantoms: new() { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 301, StackCount = 3 } } });
        await fixture.Flush();
        var upgraded = new PhantomItem { Id = 30, IncrId = 301, StackCount = 1, PhantomLevel = 5, PhantomExp = 12,
            FuncValue = 8, AddTime = 42, UnAckSubProp = { new PhantomPropInfo { PhantomPropId = 101, Value = 1234 } }, LockPropIndex = { 2 } };
        fixture.Feed(WavesMessageIds.PhantomLevelUpResponse, new PhantomLevelUpResponse
        {
            UpdateInfo = upgraded,
            StackSplitInfo = new PhantomStackSplitInfo { OperateItem = upgraded,
                RemainderItem = new PhantomItem { Id = 30, IncrId = 302, StackCount = 2 } },
            ItemMap = { { 1, 999 } },
        });
        var result = await fixture.Flush();
        Equal(2, result.Inventory!.Phantoms.Count);
        var item = result.Inventory.Phantoms.Single(item => item.IncrId == 301);
        Equal(5, item.PhantomLevel);
        Equal(1234, item.UnAckSubProps!.Single().Value);
        Equal(2, item.LockPropIndices!.Single());
        Equal(42L, item.AddTime);
        var copy = MemoryPack.MemoryPackSerializer.Deserialize<GameDataExportResult>(
            MemoryPack.MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options), ABIMemoryPack.Options)!;
        Equal(1234, copy.Inventory!.Phantoms.Single(item => item.IncrId == 301).UnAckSubProps!.Single().Value);
        Equal(0, result.Inventory.NormalItems.Count); // 展示返还不当作库存增量。
        using (var json = JsonDocument.Parse(File.ReadAllText(result.OutputJsonPath!)))
            Equal(1234, json.RootElement.GetProperty("inventory").GetProperty("phantoms").GetProperty("items")[0]
                .GetProperty("unAckSubProp")[0].GetProperty("value").GetInt32());
        fixture.Feed(WavesMessageIds.PhantomIdentifyResponse, new PhantomIdentifyResponse { UpdateInfo = new PhantomItem
            { Id = 30, IncrId = 301, StackCount = 1, PhantomLevel = 5,
                PhantomSubProp = { new PhantomPropInfo { PhantomPropId = 102, Value = 2345 } } } });
        item = (await fixture.Flush()).Inventory!.Phantoms.Single(item => item.IncrId == 301);
        Equal(2345, item.SubProps.Single().Value);
        Equal(0, item.UnAckSubProps!.Count); // 完整覆盖，不能把旧的未确认词条合并回来。
        fixture.Feed(WavesMessageIds.PhantomVicePolishAckResponse, new PhantomVicePolishAckResponse { UpdateInfo = new PhantomItem
            { Id = 30, IncrId = 301, StackCount = 1, PhantomLevel = 10 } });
        var confirmed = await fixture.Flush();
        Equal(10, confirmed.Inventory!.Phantoms.Single(item => item.IncrId == 301).PhantomLevel);
        fixture.Feed(WavesMessageIds.PhantomLevelUpResponse, new PhantomLevelUpResponse
            { ErrorCode = (ErrorCode)1, UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, StackCount = 0 } });
        Equal(confirmed.UpdateCount, (await fixture.Flush()).UpdateCount);
        fixture.Feed(WavesMessageIds.PhantomItemAddNotify, new PhantomItemAddNotify
            { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 302, StackCount = 4 }, new PhantomItem { Id = 31, IncrId = 303, StackCount = 1 } } });
        fixture.Feed(WavesMessageIds.PhantomItemUpdateNotify, new PhantomItemUpdateNotify
            { UpdateInfo = { new PhantomItem { Id = 30, IncrId = 302, StackCount = 0 } } });
        fixture.Feed(WavesMessageIds.PhantomItemRemoveNotify, new PhantomItemRemoveNotify { PhantomItemIncrIdList = { 303 } });
        Equal(301, (await fixture.Flush()).Inventory!.Phantoms.Single().IncrId);
    }

    private static async Task TestInventoryModelFieldSemanticsAsync()
    {
        await using var fixture = new NotificationFixture();
        fixture.Full(weapons: new() { WeaponItemList = { new WeaponItem { Id = 10, IncrId = 101, WeaponLevel = 5, RoleId = 1 } } },
            phantoms: new() { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 301, StackCount = 2, AddTime = 42,
                PhantomLevel = 5, UnAckSubProp = { new PhantomPropInfo { PhantomPropId = 1, Value = 100 } }, LockPropIndex = { 2 } } } });
        await fixture.Flush();
        var weapon = new WeaponItemAddNotify { WeaponItemList = { new WeaponItem { Id = 10, IncrId = 101, WeaponLevel = 20, RoleId = 2 } } };
        fixture.Feed(WavesMessageIds.WeaponItemAddNotify, weapon);
        fixture.Feed(WavesMessageIds.PhantomLevelUpResponse, new PhantomLevelUpResponse
            { UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 10, AddTime = 999 } });
        var result = await fixture.Flush();
        Equal(20, result.Inventory!.Weapons.Single().WeaponLevel);
        Equal(2, result.Inventory.Weapons.Single().RoleId);
        var phantom = result.Inventory.Phantoms.Single();
        Equal(10, phantom.PhantomLevel);
        Equal(2, phantom.StackCount);
        Equal(42L, phantom.AddTime);
        Equal(0, phantom.UnAckSubProps!.Count);
        Equal(0, phantom.LockPropIndices!.Count);
        fixture.Feed(WavesMessageIds.WeaponItemAddNotify, weapon);
        Equal(result.UpdateCount, (await fixture.Flush()).UpdateCount);
        fixture.Feed(WavesMessageIds.PhantomIdentifyResponse, new PhantomIdentifyResponse
            { UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 10,
                PhantomSubProp = { new PhantomPropInfo { PhantomPropId = 2, Value = 200 } } } });
        phantom = (await fixture.Flush()).Inventory!.Phantoms.Single();
        Equal(2, phantom.StackCount);
        Equal(42L, phantom.AddTime);
        Equal(200, phantom.SubProps.Single().Value);
        fixture.Feed(WavesMessageIds.PhantomItemAddNotify, new PhantomItemAddNotify
            { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 15, AddTime = 1234 } } });
        phantom = (await fixture.Flush()).Inventory!.Phantoms.Single();
        Equal(15, phantom.PhantomLevel);
        Equal(2, phantom.StackCount);
        Equal(42L, phantom.AddTime);
        Equal(0, phantom.SubProps.Count);
        fixture.Feed(WavesMessageIds.PhantomItemUpdateNotify, new PhantomItemUpdateNotify
            { UpdateInfo = { new PhantomItem { Id = 30, IncrId = 301, StackCount = 1, PhantomLevel = 20 } } });
        phantom = (await fixture.Flush()).Inventory!.Phantoms.Single();
        Equal(1, phantom.StackCount);
        Equal(20, phantom.PhantomLevel);
        Equal(42L, phantom.AddTime);
        fixture.Feed(WavesMessageIds.PhantomVicePolishAckResponse, new PhantomVicePolishAckResponse
            { UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, StackCount = 1, PhantomLevel = 25, AddTime = 84 } });
        phantom = (await fixture.Flush()).Inventory!.Phantoms.Single();
        Equal(25, phantom.PhantomLevel);
        Equal(84L, phantom.AddTime);
        fixture.Feed(WavesMessageIds.PhantomItemUpdateNotify, new PhantomItemUpdateNotify
            { UpdateInfo = { new PhantomItem { Id = 31, IncrId = 301, StackCount = 1 } } });
        Equal(0L, (await fixture.Flush()).Inventory!.Phantoms.Single().AddTime); // 配置变化不能沿用旧获得时间。
        fixture.Feed(WavesMessageIds.PhantomLevelUpResponse, new PhantomLevelUpResponse
        {
            StackSplitInfo = new PhantomStackSplitInfo { OperateItem = new PhantomItem { Id = 31, IncrId = 301, StackCount = 0 },
                RemainderItem = new PhantomItem { Id = 31, IncrId = 302, StackCount = 2, AddTime = 100 } },
            UpdateInfo = new PhantomItem { Id = 31, IncrId = 301, PhantomLevel = 30 },
        });
        phantom = (await fixture.Flush()).Inventory!.Phantoms.Single();
        Equal(302, phantom.IncrId); // 养成响应不能复活拆分操作已经删除的实例。
        Equal(2, phantom.StackCount);
        Equal(100L, phantom.AddTime);
        fixture.Feed(WavesMessageIds.PhantomVicePolishAckResponse, new PhantomVicePolishAckResponse
            { UpdateInfo = new PhantomItem { Id = 31, IncrId = 302, StackCount = 0 } });
        Equal(0, (await fixture.Flush()).Inventory!.Phantoms.Count); // 确认响应仍遵循库存删除语义。
    }

    private static async Task TestNotificationBaselineAndEventsAsync()
    {
        var source = new SourceState<string>(new("ordered"), _ => 1, TimeProvider.System);
        SourceProvenance At(long index) => new(index, index, DateTimeOffset.UtcNow, 1);
        Equal(false, source.ApplyOrdered(_ => "partial", At(1)));
        Equal(false, source.Available);
        Equal(true, source.Full("baseline", provenance: At(10)));
        Equal(false, source.ApplyOrdered(_ => "old", At(9)));
        Equal(false, source.ApplyOrdered(_ => "duplicate", At(10)));
        Equal(true, source.ApplyOrdered(_ => "latest", At(11)));
        Equal(false, source.ApplyOrdered(value => value, At(13)));
        Equal(false, source.ApplyOrdered(_ => "out-of-order", At(12)));
        Equal("latest", source.Value);
        Equal(true, source.ServerVersion is null);
        Equal(false, source.ApplyOrdered(_ => throw new InvalidDataException(), At(14)));
        Equal(false, source.Available);
        Equal(true, source.Full("recovered", provenance: At(15)));
        Equal(true, source.Available);
        await using var fixture = new NotificationFixture("inventory", "item-changes");
        var delta = new NormalItemAddNotify { Reason = 14000, NoTips = true,
            NormalItemList = { new NormalItem { Id = 1, Count = 10 } } };
        fixture.Feed(WavesMessageIds.NormalItemAddNotify, delta);
        Equal(0, (await fixture.Flush()).PlayerSummaries!.Length);
        fixture.Full();
        var result = await fixture.Flush();
        Equal(0, result.Inventory!.NormalItems.Count); // 全量到达后不重放此前可能已包含的增量。
        fixture.Feed(WavesMessageIds.NormalItemAddNotify, delta, current: false);
        Equal(result.UpdateCount, (await fixture.Flush()).UpdateCount);
        fixture.Full(session: "login-B", player: 222);
        fixture.Feed(WavesMessageIds.NormalItemAddNotify, delta, "login-B", 222);
        fixture.Feed(WavesMessageIds.ItemRewardNotify, new ItemRewardNotify(), "login-B", 222);
        fixture.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify(), "login-B", 222);
        fixture.Feed(WavesMessageIds.RoleSkinAddNotify, new RoleSkinAddNotify(), "login-B", 222);
        fixture.Feed(WavesMessageIds.WeaponSkinAddNotify, new WeaponSkinAddNotify(), "login-B", 222);
        fixture.Feed(WavesMessageIds.PhantomSkinAddNotify, new PhantomSkinAddNotify(), "login-B", 222);
        fixture.Feed(WavesMessageIds.PhantomVicePolishResponse, new PhantomVicePolishResponse(), "login-B", 222);
        fixture.Feed(WavesMessageIds.AddUnlockedGuideNotify, new AddUnlockedGuideNotify(), "login-B", 222);
        var modules = await fixture.Coordinator.FinishAsync(new(null, false));
        var events = modules.Single(module => module.ModuleId == "item-changes");
        Equal(17L, events.Counters.Single(counter => counter.PlayerId is null && counter.Name == "written").Value);
        var inventory = modules.Single(module => module.ModuleId == "inventory");
        Equal(0L, inventory.Counters.Single(counter => counter.PlayerId == 111 && counter.Name == "normalItems").Value);
        Equal(1L, inventory.Counters.Single(counter => counter.PlayerId == 222 && counter.Name == "normalItems").Value);
        Check(events.Artifacts.Any(artifact => artifact.Path.Contains("modules" + Path.DirectorySeparatorChar + "item-changes")),
            "筛选事件必须使用自己的模块目录。");
        await using var standalone = new NotificationFixture("item-changes");
        standalone.Feed(WavesMessageIds.PbRoleActiveNotify, new PbRoleActiveNotify());
        var onlyEvents = await standalone.Coordinator.FinishAsync(new(null, false));
        Equal("item-changes", onlyEvents.Single().ModuleId);
        Equal(1L, onlyEvents.Single().Counters.Single(counter => counter.PlayerId is null && counter.Name == "written").Value);
    }
}
