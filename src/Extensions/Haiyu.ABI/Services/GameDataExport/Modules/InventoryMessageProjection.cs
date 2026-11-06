using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>官方 InventoryController/PhantomBattleController 的消息适配；聚合器不解释道具字段。</summary>
internal static class InventoryMessageProjection
{
    internal enum Operation { Full, Add, Update, Remove, PhantomAdd, PhantomUpdate }
    internal sealed record Rule(string Source, int ListField, Operation Operation, int PayloadField,
        int ErrorField = 0, int SplitField = 0);
    internal static readonly IReadOnlyDictionary<ushort, Rule> Rules = new Dictionary<ushort, Rule>
    {
        [WavesMessageIds.NormalItemResponse] = new("normalItems", 3, Operation.Full, 3),
        [WavesMessageIds.NormalItemAddNotify] = new("normalItems", 3, Operation.Add, 9),
        [WavesMessageIds.NormalItemUpdateNotify] = new("normalItems", 3, Operation.Update, 4),
        [WavesMessageIds.NormalItemRemoveNotify] = new("normalItems", 3, Operation.Remove, 8),
        [WavesMessageIds.WeaponItemResponse] = new("weapons", 6, Operation.Full, 6),
        [WavesMessageIds.WeaponItemAddNotify] = new("weapons", 6, Operation.Add, 11),
        [WavesMessageIds.WeaponItemRemoveNotify] = new("weapons", 6, Operation.Remove, 14),
        [WavesMessageIds.PhantomItemResponse] = new("phantoms", 3, Operation.Full, 3),
        [WavesMessageIds.PhantomItemAddNotify] = new("phantoms", 3, Operation.PhantomAdd, 3),
        [WavesMessageIds.PhantomItemRemoveNotify] = new("phantoms", 3, Operation.Remove, 13),
        [WavesMessageIds.PhantomItemUpdateNotify] = new("phantoms", 3, Operation.PhantomUpdate, 4),
        [WavesMessageIds.PhantomLevelUpResponse] = new("phantoms", 3, Operation.PhantomUpdate, 14, 11, 13),
        [WavesMessageIds.PhantomIdentifyResponse] = new("phantoms", 3, Operation.PhantomUpdate, 11, 8),
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
        if (rule.Operation == Operation.Remove)
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
                if (split.Message(1) is { } operate && operate.Int32(2) > 0) PutPhantom(items, operate, false);
                if (split.Message(3) is { } remainder && remainder.Int32(2) > 0) PutPhantom(items, remainder, true);
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
                    case Operation.Update:
                        if (items.TryGetValue(key, out var previous))
                        {
                            // 普通道具只更新绝对数量；限时道具同时更新过期时间。
                            var fields = previous.Fields.ToDictionary();
                            fields[2] = item.Int32(2);
                            if (rule.Source == "validTimeItems") fields[4] = item.Int64(4);
                            items[key] = new(previous.Schema, fields, previous.HasUnknownFields || item.HasUnknownFields);
                        }
                        break;
                    case Operation.PhantomAdd:
                        if (!items.ContainsKey(key) || item.Int32(12) > 0) items[key] = item;
                        break;
                    case Operation.PhantomUpdate:
                        PutPhantom(items, item, false);
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
        if (addOnly) items.TryAdd(key, item);
        else if (item.Int32(12) == 0) items.Remove(key);
        else items[key] = item;
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
