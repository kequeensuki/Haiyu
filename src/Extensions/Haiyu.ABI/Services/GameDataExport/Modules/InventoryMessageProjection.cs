using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>官方 InventoryController/PhantomBattleController 的消息适配；聚合器不解释道具字段。</summary>
internal static class InventoryMessageProjection
{
    internal enum Operation { Full, Add, Update, Remove, WeaponAdd, PhantomAdd, PhantomUpdate, PhantomCultivation, WeaponLevel, WeaponBreach, WeaponResonance, WeaponEquip, PhantomStacks, PhantomPolish, Invalidate, FuncValue }
    internal sealed record Rule(string Source, int ListField, Operation Operation, int PayloadField,
        int ErrorField = 0, int SplitField = 0, int UpdatesField = 0);
    internal static readonly IReadOnlyDictionary<ushort, Rule> Rules = new Dictionary<ushort, Rule>
    {
        [WavesMessageIds.WeaponLevelUpResponse] = new("weapons", 6, Operation.WeaponLevel, 4, 11),
        [WavesMessageIds.WeaponBreachResponse] = new("weapons", 6, Operation.WeaponBreach, 4, 7),
        [WavesMessageIds.WeaponResonUpResponse] = new("weapons", 6, Operation.WeaponResonance, 11, 1),
        [WavesMessageIds.EquipTakeOnResponse] = new("weapons", 6, Operation.WeaponEquip, 1, 2),
        [WavesMessageIds.EquipTakeOnNotify] = new("weapons", 6, Operation.WeaponEquip, 5),
        [WavesMessageIds.PhantomPolishResponse] = new("phantoms", 3, Operation.PhantomPolish, 14, 4, 7),
        [WavesMessageIds.PhantomBatchPolishResponse] = new("phantoms", 3, Operation.PhantomStacks, 10, 6, 14, 12),
        [WavesMessageIds.UsePhantomEquipGroupResponse] = new("phantoms", 3, Operation.PhantomStacks, 14, 11, 6),
        [WavesMessageIds.PhantomPutOnResponse] = new("phantoms", 3, Operation.PhantomStacks, 11, 1, 7),
        [WavesMessageIds.PhantomAutoPutResponse] = new("phantoms", 3, Operation.PhantomStacks, 5, 3, 15),
        [WavesMessageIds.PhantomSkinChangeResponse] = new("phantoms", 3, Operation.Invalidate, 0, 15),
        [WavesMessageIds.PhantomBatchOperResponse] = new("phantoms", 3, Operation.Invalidate, 0, 13),
        [WavesMessageIds.ItemFuncValueUpdateNotify] = new("phantoms", 3, Operation.FuncValue, 1),
        [WavesMessageIds.NormalItemResponse] = new("normalItems", 3, Operation.Full, 3),
        [WavesMessageIds.NormalItemAddNotify] = new("normalItems", 3, Operation.Add, 9),
        [WavesMessageIds.NormalItemUpdateNotify] = new("normalItems", 3, Operation.Update, 4),
        [WavesMessageIds.NormalItemRemoveNotify] = new("normalItems", 3, Operation.Remove, 8),
        [WavesMessageIds.WeaponItemResponse] = new("weapons", 6, Operation.Full, 6),
        [WavesMessageIds.WeaponItemAddNotify] = new("weapons", 6, Operation.WeaponAdd, 11),
        [WavesMessageIds.WeaponItemRemoveNotify] = new("weapons", 6, Operation.Remove, 14),
        [WavesMessageIds.PhantomItemResponse] = new("phantoms", 3, Operation.Full, 3),
        [WavesMessageIds.PhantomItemAddNotify] = new("phantoms", 3, Operation.PhantomAdd, 3),
        [WavesMessageIds.PhantomItemRemoveNotify] = new("phantoms", 3, Operation.Remove, 13),
        [WavesMessageIds.PhantomItemUpdateNotify] = new("phantoms", 3, Operation.PhantomUpdate, 4),
        [WavesMessageIds.PhantomLevelUpResponse] = new("phantoms", 3, Operation.PhantomCultivation, 14, 11, 13),
        [WavesMessageIds.PhantomIdentifyResponse] = new("phantoms", 3, Operation.PhantomCultivation, 11, 8),
        [WavesMessageIds.PhantomVicePolishAckResponse] = new("phantoms", 3, Operation.PhantomUpdate, 10, 15),
        [WavesMessageIds.ValidTimeItemResponse] = new("validTimeItems", 14, Operation.Full, 14),
        [WavesMessageIds.ValidTimeItemAddNotify] = new("validTimeItems", 14, Operation.Add, 12),
        [WavesMessageIds.ValidTimeItemUpdateNotify] = new("validTimeItems", 14, Operation.Update, 13),
        [WavesMessageIds.ValidTimeItemRemoveNotify] = new("validTimeItems", 14, Operation.Remove, 8),
    };

    internal static ProtocolMessage Full(Rule rule, ProtocolMessage message)
    {
        var keys = new HashSet<(int, int)>();
        foreach (var item in message.Messages(rule.ListField))
            if (!keys.Add(Key(rule.Source, item))) throw new InvalidDataException("全量背包包含重复道具标识。");
        return message;
    }

    internal static ProtocolMessage Apply(Rule rule, ProtocolMessage baseline, ProtocolMessage notification)
    {
        var items = baseline.Messages(rule.ListField).ToDictionary(item => Key(rule.Source, item));
        if (rule.Operation == Operation.Invalidate)
            throw new InvalidDataException("成功响应缺少目标标识或新值，需要完整基线。");
        if (rule.Operation is Operation.WeaponLevel or Operation.WeaponBreach or Operation.WeaponResonance)
        {
            var key = (notification.Int32(rule.PayloadField), 0);
            var previous = Require(items, key);
            items[key] = rule.Operation switch
            {
                Operation.WeaponLevel => Patch(previous, (4, notification.Int32(1)), (5, notification.Int32(9))),
                Operation.WeaponBreach => Patch(previous, (6, notification.Int32(11))),
                _ => Patch(previous, (7, notification.Int32(15)))
            };
        }
        else if (rule.Operation == Operation.FuncValue)
        {
            var key = (notification.Int32(1), 0);
            items[key] = Patch(Require(items, key), (3, notification.Int32(14)));
        }
        else if (rule.Operation == Operation.WeaponEquip)
        {
            foreach (var equip in notification.Messages(rule.PayloadField))
            {
                if (equip.Int32(2) != 0) continue; // 武器皮肤不修改武器实例。
                var role = equip.Int32(1);
                var id = equip.Int32(3);
                if (role < 0 || id < 0 || (role == 0 && id == 0)) throw new InvalidDataException("装备标识无效。");
                if (id > 0) Require(items, (id, 0));
                foreach (var key in items.Keys.ToArray())
                    if ((role > 0 && items[key].Int32(8) == role) || key == (id, 0))
                        items[key] = Patch(items[key], (8, key == (id, 0) ? role : 0));
            }
        }
        else if (rule.Operation is Operation.PhantomStacks or Operation.PhantomPolish)
        {
            if (rule.Operation == Operation.PhantomPolish)
            {
                if (notification.Message(rule.SplitField) is { } split) ApplySplit(items, split);
                if (notification.Message(10) is { } merge) ApplyMerge(items, merge);
                PutPhantom(items, notification.Message(rule.PayloadField)
                    ?? throw new InvalidDataException("成功响应缺少声骸对象。"), false);
            }
            else
            {
                foreach (var split in notification.Messages(rule.SplitField)) ApplySplit(items, split);
                foreach (var merge in notification.Messages(rule.PayloadField)) ApplyMerge(items, merge);
                if (rule.UpdatesField != 0)
                    foreach (var item in notification.Messages(rule.UpdatesField)) PutPhantom(items, item, false);
            }
        }
        else if (rule.Operation == Operation.Remove)
        {
            if (rule.Source == "validTimeItems")
                foreach (var item in notification.Messages(rule.PayloadField)) items.Remove((item.Int32(1), item.Int32(2)));
            else
                foreach (var id in notification.Int32s(rule.PayloadField)) items.Remove((id, 0));
        }
        else
        {
            if (rule.SplitField != 0 && notification.Message(rule.SplitField) is { } split)
            {
                // 升级响应先拆分堆叠，再应用目标完整对象；ItemMap 仅用于返还展示，不叠加普通道具。
                ApplySplit(items, split);
            }
            var updates = rule.ErrorField == 0 ? notification.Messages(rule.PayloadField)
                : notification.Message(rule.PayloadField) is { } single ? new[] { single } : throw new InvalidDataException("成功响应缺少声骸对象。");
            foreach (var item in updates)
            {
                var key = Key(rule.Source, item);
                switch (rule.Operation)
                {
                    case Operation.Add:
                        items.TryAdd(key, item);
                        break;
                    case Operation.WeaponAdd:
                        // OnAddWeaponItem 还会调用 WeaponModel.AddWeaponData，刷新已有实例的养成及装备信息。
                        items[key] = item;
                        break;
                    case Operation.Update:
                        if (items.TryGetValue(key, out var previous))
                        {
                            // 普通道具只更新绝对数量；限时道具同时更新过期时间。
                            var fields = previous.Fields.ToDictionary();
                            fields[2] = item.Int32(2);
                            if (rule.Source == "validTimeItems") fields[4] = item.Int64(4);
                            items[key] = new(previous.Schema, fields, previous.HasUnknownFields || item.HasUnknownFields);
                        }
                        else throw new InvalidDataException("更新对象不存在，需要完整基线。");
                        break;
                    case Operation.PhantomAdd:
                        // 库存可忽略缺失数量，但 OnAddPhantomItem 仍会完整刷新养成模型。
                        items[key] = items.TryGetValue(key, out var previousPhantom)
                            ? PreserveInventory(item, previousPhantom, item.Int32(12) <= 0,
                                item.Int32(12) <= 0 || KeepAddTime(item, previousPhantom)) : item;
                        break;
                    case Operation.PhantomUpdate:
                        PutPhantom(items, item, false);
                        break;
                    case Operation.PhantomCultivation:
                        // 升级/调谐只调用 SetData；库存数量和获得时间来自库存通道，不能据默认 0 删除。
                        if (items.TryGetValue(key, out var cultivated))
                            items[key] = PreserveInventory(item, cultivated, true, true);
                        else if (!(rule.SplitField != 0 && notification.Message(rule.SplitField)?.Message(1) is { } removed
                            && removed.Int32(2) == key.Item1 && removed.Int32(12) == 0))
                            throw new InvalidDataException("养成对象不存在，需要完整基线。");
                        break;
                }
            }
        }
        var ordered = items.OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2).Select(pair => pair.Value).ToArray();
        var old = baseline.Messages(rule.ListField);
        if (old.Count == ordered.Length && old.OrderBy(item => Key(rule.Source, item)).Zip(ordered).All(pair => Same(pair.First, pair.Second)))
            return baseline;
        var result = baseline.Fields.ToDictionary();
        result[rule.ListField] = Array.AsReadOnly(ordered.Cast<object>().ToArray());
        return new(baseline.Schema, result, baseline.HasUnknownFields || notification.HasUnknownFields);
    }

    private static (int, int) Key(string source, ProtocolMessage item)
    {
        var key = source switch
        {
            "normalItems" => (item.Int32(1), 0),
            "validTimeItems" => (item.Int32(1), item.Int32(3)),
            _ => (item.Int32(2), 0),
        };
        if (key.Item1 <= 0)
            throw new InvalidDataException("道具标识无效。");
        return key;
    }

    private static void PutPhantom(Dictionary<(int, int), ProtocolMessage> items, ProtocolMessage item, bool addOnly)
    {
        var key = Key("phantoms", item);
        if (!addOnly && item.Int32(12) == 0) { items.Remove(key); return; }
        if (!addOnly && !items.ContainsKey(key))
            throw new InvalidDataException("声骸更新对象不存在，需要完整基线。");
        items[key] = items.TryGetValue(key, out var previous)
            ? PreserveInventory(item, previous, addOnly, addOnly || KeepAddTime(item, previous)) : item;
    }

    private static ProtocolMessage Require(Dictionary<(int, int), ProtocolMessage> items, (int, int) key) =>
        key.Item1 > 0 && items.TryGetValue(key, out var value) ? value
        : throw new InvalidDataException("更新对象不存在，需要完整基线。");

    private static ProtocolMessage Patch(ProtocolMessage previous, params (int Field, int Value)[] changes)
    {
        var fields = previous.Fields.ToDictionary();
        foreach (var change in changes) fields[change.Field] = change.Value;
        return new(previous.Schema, fields, previous.HasUnknownFields);
    }

    private static void ApplyMerge(Dictionary<(int, int), ProtocolMessage> items, ProtocolMessage merge)
    {
        if (merge.Message(2) is { } representative && representative.Int32(2) > 0)
            PutPhantom(items, representative, false);
        if (merge.Int32(1) > 0) items.Remove((merge.Int32(1), 0));
    }

    private static void ApplySplit(Dictionary<(int, int), ProtocolMessage> items, ProtocolMessage split)
    {
        if (split.Message(1) is { } operate && operate.Int32(2) > 0) PutPhantom(items, operate, false);
        if (split.Message(3) is { } remainder && remainder.Int32(2) > 0) PutPhantom(items, remainder, true);
    }

    private static bool KeepAddTime(ProtocolMessage item, ProtocolMessage previous) =>
        item.Int32(1) == previous.Int32(1) && item.Int64(13) <= 0;

    private static ProtocolMessage PreserveInventory(ProtocolMessage item, ProtocolMessage previous, bool quantity, bool addTime)
    {
        if (!quantity && !addTime) return item;
        var fields = item.Fields.ToDictionary();
        foreach (var field in new[] { 12, 13 })
        {
            if (!(field == 12 ? quantity : addTime)) continue;
            if (previous.Fields.TryGetValue(field, out var value)) fields[field] = value;
            else fields.Remove(field);
        }
        return new(item.Schema, fields, item.HasUnknownFields || previous.HasUnknownFields);
    }

    private static bool Same(object first, object second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is ProtocolMessage a && second is ProtocolMessage b)
            return a.Schema.Name == b.Schema.Name && a.Fields.Count == b.Fields.Count
                && a.Fields.All(pair => b.Fields.TryGetValue(pair.Key, out var value) && Same(pair.Value, value));
        if (first is IReadOnlyList<object> x && second is IReadOnlyList<object> y)
            return x.Count == y.Count && x.Zip(y).All(pair => Same(pair.First, pair.Second));
        if (first is byte[] bytes && second is byte[] other) return bytes.AsSpan().SequenceEqual(other);
        return first.Equals(second);
    }

    // 按保留字段树估算预算，不能用最后一条小 Notify 的长度替代整个来源大小。
    internal static long RetainedBytes(ProtocolMessage message) => 64 + message.Fields.Sum(pair => 40 + Size(pair.Value));
    private static long Size(object value) => value switch
    {
        ProtocolMessage message => RetainedBytes(message),
        IReadOnlyList<object> items => 32 + items.Sum(item => 8 + Size(item)),
        IReadOnlyDictionary<object, object> map => 64 + map.Sum(pair => 48 + Size(pair.Key) + Size(pair.Value)),
        string text => 24 + text.Length * 2L,
        IReadOnlyDictionary<string, object> map => 64 + map.Sum(pair => 48 + Size(pair.Key) + Size(pair.Value)),
        ReadOnlyMemory<byte> bytes => 24 + bytes.Length,
        _ => 16,
    };
}
