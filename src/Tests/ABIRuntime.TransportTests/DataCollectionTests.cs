using Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Protocol;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    // 独立于背包类型的新对象验证默认实现可复用，不能依赖中央操作枚举。
    private sealed record ExportEntry(int Id, int Value);
    private sealed record ExportEntryChange(int Value);
    private sealed class ExportEntryCollection(IEnumerable<ExportEntry> entries)
        : DataCollectionBase<int, ExportEntry, ExportEntryChange>(entries, entry => entry.Id, key => key > 0)
    {
        protected override ExportEntry Merge(ExportEntry current, ExportEntryChange change) => current with { Value = change.Value };
    }
    private sealed record EntryKey(string Category, int Id);
    private sealed record CompositeEntry(EntryKey Key, int Value);
    private sealed class CompositeEntryCollection(IEnumerable<CompositeEntry> entries)
        : DataCollectionBase<EntryKey, CompositeEntry, ExportEntryChange>(entries, entry => entry.Key, key => key.Id > 0)
    {
        protected override CompositeEntry Merge(CompositeEntry current, ExportEntryChange change) => current with { Value = change.Value };
    }
    private static Task TestDataCollectionContractAsync()
    {
        // 自定义复合键只提供相等性，没有 IComparable，默认快照不能隐式要求可排序。
        var composite = new CompositeEntryCollection([new(new("a", 1), 10), new(new("b", 1), 20)]);
        Equal(2, composite.Snapshot().Count);
        Equal(ChangeStatus.Changed, composite.Update(new("b", 1), new(30)).Status);
        Equal(30, composite.Snapshot().Single(item => item.Key == new EntryKey("b", 1)).Value);
        var collection = new ExportEntryCollection([new(1, 10)]);
        IDataCollection<int, ExportEntry, ExportEntryChange> contract = collection;
        var frozen = contract.Snapshot();
        Equal(ChangeStatus.Unchanged, contract.Add(new(1, 999)).Status);
        Equal(ChangeStatus.Changed, contract.Update(1, new(20)).Status);
        Equal(10, frozen.Single().Value);
        Equal(ChangeStatus.Unchanged, contract.Update(1, new(20)).Status);
        Equal(ChangeStatus.MissingTarget, contract.Update(999, new(1)).Status);
        Equal(ChangeStatus.Invalid, contract.Add(new(0, 1)).Status);
        Equal(ChangeStatus.Changed, contract.Remove(1).Status);
        Equal(ChangeStatus.Unchanged, contract.Remove(1).Status);
        Equal(ChangeStatus.Changed, contract.Add(new(1, 30)).Status);
        var failed = collection.ApplyBatch(items =>
        {
            items.Add(new(2, 10));
            items.Update(1, new(99));
            return items.Update(999, new(1));
        });
        Equal(ChangeStatus.MissingTarget, failed.Status);
        Equal(30, collection.Snapshot().Single().Value);
        try
        {
            collection.ApplyBatch(items => { items.Remove(1); throw new InvalidDataException("failure"); });
            throw new Exception("事务异常未传播");
        }
        catch (InvalidDataException) { }
        Equal(30, collection.Snapshot().Single().Value);
        Equal(ChangeStatus.Unchanged, collection.ApplyBatch(items =>
        {
            items.Add(new(2, 10));
            return items.Remove(2);
        }).Status);
        try
        {
            _ = new ExportEntryCollection([new(1, 1), new(1, 2)]);
            throw new Exception("重复基线未被拒绝");
        }
        catch (InvalidDataException) { }
        return Task.CompletedTask;
    }

    private static Task TestDomainCollectionOverridesAsync()
    {
        var ordered = new NormalItemCollection([new(9, 1, 0), new(1, 1, 0)]);
        Equal(true, ordered.Snapshot().Select(item => item.Id).SequenceEqual(new[] { 1, 9 }));
        var normal = new NormalItemCollection([new(1, 10, 123)]);
        normal.Update(1, new(0));
        Equal(new NormalItemState(1, 0, 123), normal.Snapshot().Single());
        var timed = new ValidTimeItemCollection([new(1, 11, 3, 100), new(1, 12, 4, 200)]);
        timed.Update((1, 12), new(2, 300));
        timed.Remove((1, 11));
        Equal(new ValidTimeItemState(1, 12, 2, 300), timed.Snapshot().Single());
        var weapons = new WeaponCollection([new(10, 101, 1, 0, 0, 1, 1, 0), new(10, 102, 1, 0, 0, 1, 2, 0)]);
        weapons.Add(new(10, 101, 20, 123, 2, 3, 1, 0)); // 覆盖默认重复新增行为。
        Equal(20, weapons.Snapshot().Single(w => w.IncrId == 101).Level);
        Equal(ChangeStatus.Changed, weapons.Update(101, new WeaponEquipmentChanged(2)).Status);
        Equal(0, weapons.Snapshot().Single(w => w.IncrId == 102).RoleId);
        Equal(ChangeStatus.Unchanged, weapons.Update(101, new WeaponEquipmentChanged(2)).Status);
        Equal(ChangeStatus.Unchanged, weapons.ApplyBatch(items => items.Update(101, new WeaponEquipmentChanged(2))).Status);
        var phantom = new PhantomState(30, 301, 1, 0, Array.AsReadOnly(new[] { new PhantomProperty(1, 10) }),
            [], 1, 0, [], [], 3, 42, 0);
        var phantoms = new PhantomCollection([phantom]);
        var snapshot = phantoms.Snapshot();
        phantoms.Update(301, new PhantomCultivationChanged(phantom with { Level = 20, Count = 0, AddTime = 0 }));
        Equal(20, phantoms.Snapshot().Single().Level);
        Equal(3, phantoms.Snapshot().Single().Count);
        Equal(42L, phantoms.Snapshot().Single().AddTime);
        Equal(1, snapshot.Single().Level);
        Equal(ChangeStatus.Invalid, phantoms.Update(301, new PhantomInventoryChanged(phantom with { IncrId = 999, Count = 0 })).Status);
        Equal(1, phantoms.Snapshot().Count);
        // 复杂领域操作即使通过接口单独调用，也不能在缺失目标后留下半次拆堆。
        IDataCollection<int, PhantomState, PhantomChange> phantomContract = phantoms;
        var missing = phantomContract.Update(0, new PhantomStacksChanged(
            [new(phantom with { Count = 1 }, phantom with { IncrId = 302, Count = 2 })],
            [], [phantom with { IncrId = 999 }]));
        Equal(ChangeStatus.MissingTarget, missing.Status);
        Equal(1, phantoms.Snapshot().Count);
        Equal(3, phantoms.Snapshot().Single().Count);

        Equal(ChangeStatus.Unchanged, phantoms.ApplyBatch(items => items.Update(301,
            new PhantomCultivationChanged(phantoms.Snapshot().Single() with
                { MainProps = Array.AsReadOnly(new[] { new PhantomProperty(1, 10) }) }))).Status);
        return Task.CompletedTask;
    }

    private sealed class EntryMessageAdapter : InventoryMessageAdapter<int, ExportEntry, ExportEntryChange>
    {
        internal EntryMessageAdapter() : base("entries", items => new ExportEntryCollection(items), _ => 128)
        {
            Full(65000, 3, item => new(item.Int32(1), item.Int32(2)));
            On(65001, 0, (m, items) => Each(m.Messages(3), item => items.Update(item.Int32(1), new(item.Int32(2)))));
        }
    }
    private static Task TestInventoryAdapterExtensionAsync()
    {
        var adapter = new EntryMessageAdapter();
        var registry = new InventoryMessageRegistry([adapter]);
        Equal("entries", registry.Sources.Single());
        var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
        // 独立 wire 向量：使用已有载荷形状验证新增数据类型无需改中央分支。
        var model = decoder.Decode(WavesMessageIds.NormalItemResponse, [26, 4, 8, 1, 16, 2]).Message!;
        var full = registry.Handlers[65000].Decode(model, _ => null).Single().Full!();
        Equal(2, ((InventorySnapshot<ExportEntry>)full).Items.Single().Value);
        var changed = decoder.Decode(WavesMessageIds.NormalItemResponse, [26, 4, 8, 1, 16, 7]).Message!;
        var apply = registry.Handlers[65001].Decode(changed, _ => full).Single().Apply!;
        var updated = apply(full);
        Equal(7, ((InventorySnapshot<ExportEntry>)updated).Items.Single().Value);
        Equal(2, ((InventorySnapshot<ExportEntry>)full).Items.Single().Value);
        Equal(true, ReferenceEquals(updated, apply(updated)));
        try
        {
            _ = new InventoryMessageRegistry([adapter, adapter]);
            throw new Exception("重复消息注册未被拒绝");
        }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }
}
