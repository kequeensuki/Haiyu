// 旧版诊断投影，仅用于保留线协议回归断言；生产代码使用 InventoryExportModule。
using ABI.Models;

namespace Haiyu.ABI.Services.GameDataExport.Export;

internal enum InventoryMessageResult { NotHandled, Updated, Invalid }

/// <summary>背包快照消费者：只接收明文消息，维护四类快照及持久化版本。</summary>
internal sealed class InventoryCaptureState(GameDataExportScope scope)
{
    internal Dictionary<ushort, List<GameNormalItem>> NormalItems { get; } = new();
    internal Dictionary<ushort, List<GameWeaponItem>> Weapons { get; } = new();
    internal Dictionary<ushort, List<GamePhantomItem>> Phantoms { get; } = new();
    internal Dictionary<ushort, List<GameValidTimeItem>> ValidTimeItems { get; } = new();
    internal int DecodedFrames { get; private set; }
    internal int DecodedOnConnection { get; private set; }
    internal int Version { get; private set; }
    internal int ParsedCategories => NormalItems.Count + Weapons.Count + Phantoms.Count + ValidTimeItems.Count;
    internal bool IsComplete => ParsedCategories == MsgIdFilter.GetTargetMsgIds(scope).Length && DecodedFrames > 0;

    internal InventoryMessageResult Apply(ushort messageId, byte[] plain)
    {
        if (!MsgIdFilter.IsTarget(messageId, scope)) return InventoryMessageResult.NotHandled;
        var parsed = messageId switch
        {
            MsgIdFilter.NormalItemResponse => Store(NormalItems, messageId, InventoryMapper.MapNormalItems(plain)),
            MsgIdFilter.WeaponItemResponse => Store(Weapons, messageId, InventoryMapper.MapWeapons(plain)),
            MsgIdFilter.PhantomItemResponse => Store(Phantoms, messageId, InventoryMapper.MapPhantoms(plain)),
            MsgIdFilter.ValidTimeItemResponse => Store(ValidTimeItems, messageId, InventoryMapper.MapValidTimeItems(plain)),
            _ => false
        };
        if (!parsed) return InventoryMessageResult.Invalid;
        DecodedFrames++;
        DecodedOnConnection++;
        Version++;
        return InventoryMessageResult.Updated;
    }

    internal void Reset()
    {
        NormalItems.Clear();
        Weapons.Clear();
        Phantoms.Clear();
        ValidTimeItems.Clear();
    }

    internal InventoryCaptureState ForkForReconnect()
    {
        var next = new InventoryCaptureState(scope) { DecodedFrames = DecodedFrames, Version = Version };
        foreach (var pair in NormalItems) next.NormalItems[pair.Key] = pair.Value.ToList();
        foreach (var pair in Weapons) next.Weapons[pair.Key] = pair.Value.ToList();
        foreach (var pair in Phantoms) next.Phantoms[pair.Key] = pair.Value.ToList();
        foreach (var pair in ValidTimeItems) next.ValidTimeItems[pair.Key] = pair.Value.ToList();
        return next;
    }

    private static bool Store<T>(Dictionary<ushort, List<T>> map, ushort id, List<T>? items)
    {
        if (items is null) return false;
        map[id] = items;
        return true;
    }
}
