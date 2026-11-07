using System.Collections.ObjectModel;

namespace Haiyu.ABI.Services.GameDataExport.Protocol;

internal sealed record ProtocolDecodeResult(string Status, string? MessageType, ProtocolMessage? Message,
    bool HasUnknownFields, string? Error);

/// <summary>冻结的协议字段视图，业务消费者不访问可变解析器或 JSON 中间表示。</summary>
internal sealed class ProtocolMessage
{
    internal ProtocolMessage(ProtocolCatalog.Message schema, Dictionary<int, object> fields, bool unknown)
    {
        Schema = schema;
        Fields = new ReadOnlyDictionary<int, object>(fields);
        HasUnknownFields = unknown;
    }
    internal ProtocolCatalog.Message Schema { get; }
    internal IReadOnlyDictionary<int, object> Fields { get; }
    internal bool HasUnknownFields { get; }
    internal bool Has(int field) => Fields.ContainsKey(field);
    internal ProtocolMessage? Message(int field) => Fields.GetValueOrDefault(field) as ProtocolMessage;
    internal IReadOnlyList<int> Int32s(int field) => Fields.TryGetValue(field, out var value)
        ? Array.AsReadOnly(((IReadOnlyList<object>)value).Cast<int>().ToArray()) : Array.Empty<int>();
    internal int Int32(int field) => Fields.TryGetValue(field, out var value) ? value switch
    { int i => i, uint i => unchecked((int)i), _ => throw new InvalidDataException("字段不是 int32") } : 0;
    internal uint UInt32(int field) => Fields.TryGetValue(field, out var value) ? value switch
    { uint number => number, int number => unchecked((uint)number), _ => throw new InvalidDataException("字段不是 uint32") } : 0;
    internal long Int64(int field) => Fields.TryGetValue(field, out var value) ? value switch
    { long i => i, ulong i => unchecked((long)i), int i => i, _ => throw new InvalidDataException("字段不是 int64") } : 0;
    internal string String(int field) => Fields.TryGetValue(field, out var value)
        ? value as string ?? throw new InvalidDataException("字段不是 string") : "";
    internal bool Bool(int field) => Fields.TryGetValue(field, out var value)
        ? value is bool boolean ? boolean : throw new InvalidDataException("字段不是 bool") : false;
    internal IReadOnlyList<string> Strings(int field) => Fields.TryGetValue(field, out var value)
        ? Array.AsReadOnly(((IReadOnlyList<object>)value).Cast<string>().ToArray()) : Array.Empty<string>();
    internal IReadOnlyDictionary<string, int> Int32Map(int field) => Fields.TryGetValue(field, out var value)
        ? ((IReadOnlyDictionary<string, object>)value).ToDictionary(pair => pair.Key, pair => pair.Value switch
            { int number => number, uint number => unchecked((int)number), _ => throw new InvalidDataException("map 值不是 int32") }, StringComparer.Ordinal)
        : new Dictionary<string, int>();
    internal IReadOnlyList<ProtocolMessage> Messages(int field) => Fields.TryGetValue(field, out var value)
        ? Array.AsReadOnly(((IReadOnlyList<object>)value).Cast<ProtocolMessage>().ToArray()) : Array.Empty<ProtocolMessage>();
}
