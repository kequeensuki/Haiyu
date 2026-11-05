using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Haiyu.ABI.Services.GameDataExport.Protocol;
/// <summary>使用静态 schema 解码 protobuf wire 数据；无反射、无动态类型构造，适用于 NativeAOT。</summary>
internal sealed class ProtocolJsonDecoder(ProtocolCatalog catalog)
{
    internal sealed record Result(string Status, string? MessageType, byte[]? Json, bool HasUnknownFields, string? Error);
    private sealed class Node(ProtocolCatalog.Message schema)
    {
        internal readonly ProtocolCatalog.Message Schema = schema;
        internal readonly Dictionary<int, object> Values = new();
        internal bool Unknown;
    }

    private sealed class Reader(byte[] data, int start, int end)
    {
        internal int Position = start;
        internal int Remaining => end - Position;

        internal ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (Position >= end)
                    throw new InvalidDataException("truncated_varint");
                var b = data[Position++];
                if (shift == 63 && b > 1)
                    throw new InvalidDataException("invalid_varint");
                value |= (ulong)(b & 127) << shift;
                if (b < 128)
                    return value;
            }

            throw new InvalidDataException("invalid_varint");
        }

        internal byte[] Bytes(int length)
        {
            if (length < 0 || length > Remaining)
                throw new InvalidDataException("truncated_payload");
            var bytes = data.AsSpan(Position, length).ToArray();
            Position += length;
            return bytes;
        }

        internal byte[] Delimited()
        {
            var n = Varint();
            if (n > int.MaxValue)
                throw new InvalidDataException("length_overflow");
            return Bytes((int)n);
        }

        internal Reader Child()
        {
            var n = Varint();
            if (n > (ulong)Remaining)
                throw new InvalidDataException("truncated_payload");
            var r = new Reader(data, Position, Position + (int)n);
            Position += (int)n;
            return r;
        }

        internal void Skip(int wire, int field, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("recursion_limit");
            switch (wire)
            {
                case 0:
                    Varint();
                    break;
                case 1:
                    Bytes(8);
                    break;
                case 2:
                    var child = Child();
                    break;
                case 3:
                    while (Remaining > 0)
                    {
                        var tag = Varint();
                        if ((tag & 7) == 4)
                        {
                            if ((int)(tag >> 3) != field)
                                throw new InvalidDataException("invalid_group");
                            return;
                        }

                        Skip((int)(tag & 7), (int)(tag >> 3), depth + 1);
                    }

                    throw new InvalidDataException("truncated_group");
                case 5:
                    Bytes(4);
                    break;
                default:
                    throw new InvalidDataException("invalid_wire_type");
            }
        }
    }

    internal Result Decode(int msgId, byte[] bytes)
    {
        if (!catalog.Ids.TryGetValue(msgId, out var name))
            return new("unknown_message", null, null, false, null);
        return DecodeType(name, bytes);
    }

    internal Result DecodeType(string name, byte[] bytes)
    {
        try
        {
            var budget = 1_000_000;
            var node = ReadMessage(name, new Reader(bytes, 0, bytes.Length), 0, ref budget);
            using var output = new LimitedBuffer(16 * 1024 * 1024);
            using (var json = new Utf8JsonWriter(output))
                WriteNode(json, node);
            return new(node.Unknown ? "decoded_with_unknown_fields" : "decoded", name, output.ToArray(), node.Unknown, null);
        }
        catch (Exception ex)when (ex is InvalidDataException or DecoderFallbackException or OverflowException or OutputLimitException)
        {
            return new(ex is OutputLimitException ? "json_limit" : "parse_failed", name, null, false, ex.Message);
        }
    }

    private Node ReadMessage(string name, Reader reader, int depth, ref int budget, Node? existing = null)
    {
        if (depth > 64)
            throw new InvalidDataException("recursion_limit");
        var node = existing ?? new Node(catalog.Messages[name]);
        while (reader.Remaining > 0)
        {
            if (--budget < 0)
                throw new InvalidDataException("field_budget_exceeded");
            var tag = reader.Varint();
            if (tag == 0 || tag > uint.MaxValue || (tag >> 3) > 536870911)
                throw new InvalidDataException("invalid_tag");
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (number == 0)
                throw new InvalidDataException("invalid_tag");
            if (!node.Schema.Fields.TryGetValue(number, out var field))
            {
                node.Unknown = true;
                reader.Skip(wire, number, depth);
                continue;
            }

            if (field.MapKey != null)
            {
                if (wire != 2)
                {
                    node.Unknown = true;
                    reader.Skip(wire, number, depth);
                    continue;
                }

                var entry = reader.Child();
                object key = Default(field.MapKey), value = Default(field.Type);
                while (entry.Remaining > 0)
                {
                    if (--budget < 0)
                        throw new InvalidDataException("field_budget_exceeded");
                    var t = entry.Varint();
                    var n = (int)(t >> 3);
                    var w = (int)(t & 7);
                    if (t == 0 || n == 0 || t > uint.MaxValue)
                        throw new InvalidDataException("invalid_tag");
                    if (n == 1 && w == Wire(field.MapKey))
                        key = ReadValue(field.MapKey, entry, depth + 1, ref budget);
                    else if (n == 2 && w == Wire(field.Type))
                        value = ReadValue(field.Type, entry, depth + 1, ref budget, value as Node);
                    else
                    {
                        node.Unknown = true;
                        entry.Skip(w, n, depth + 1);
                    }
                }

                if (value is Node nested && nested.Unknown)
                    node.Unknown = true;
                if (!node.Values.TryGetValue(number, out var map))
                    node.Values[number] = map = new Dictionary<string, object>(StringComparer.Ordinal);
                ((Dictionary<string, object>)map)[Key(key)] = value;
                continue;
            }

            if (field.Repeated && wire == 2 && Wire(field.Type) != 2)
            {
                var packed = reader.Child();
                var list = List(node, number);
                while (packed.Remaining > 0)
                {
                    if (--budget < 0)
                        throw new InvalidDataException("field_budget_exceeded");
                    list.Add(ReadValue(field.Type, packed, depth + 1, ref budget));
                }

                continue;
            }

            if (wire != Wire(field.Type))
            {
                node.Unknown = true;
                reader.Skip(wire, number, depth);
                continue;
            }

            if (field.Oneof != null)
                foreach (var other in node.Schema.Fields.Values.Where(f => f.Oneof == field.Oneof && f.Number != number))
                    node.Values.Remove(other.Number);
            node.Values.TryGetValue(number, out var previous);
            var item = ReadValue(field.Type, reader, depth + 1, ref budget, field.Repeated ? null : previous as Node);
            if (item is Node childNode && childNode.Unknown)
                node.Unknown = true;
            if (field.Repeated)
                List(node, number).Add(item);
            else
                node.Values[number] = item;
        }

        return node;
    }

    private static List<object> List(Node node, int number)
    {
        if (!node.Values.TryGetValue(number, out var list))
            node.Values[number] = list = new List<object>();
        return (List<object>)list;
    }

    private int Wire(string type) => type switch
    {
        "double" or "fixed64" or "sfixed64" => 1,
        "float" or "fixed32" or "sfixed32" => 5,
        "string" or "bytes" => 2,
        _ => catalog.Messages.ContainsKey(type) ? 2 : 0
    };
    private object Default(string type) => catalog.Messages.TryGetValue(type, out var m) ? new Node(m) : type switch
    {
        "string" => "",
        "bytes" => Array.Empty<byte>(),
        "bool" => false,
        "float" => 0f,
        "double" => 0d,
        "int64" or "sint64" or "sfixed64" => 0L,
        "uint64" or "fixed64" => 0UL,
        _ => 0
    };
    private object ReadValue(string type, Reader r, int depth, ref int budget, Node? previous = null)
    {
        if (catalog.Messages.ContainsKey(type))
            return ReadMessage(type, r.Child(), depth, ref budget, previous);
        if (catalog.Enums.ContainsKey(type))
            return unchecked((int)r.Varint());
        return type switch
        {
            "int32" => unchecked((int)r.Varint()),
            "uint32" => unchecked((uint)r.Varint()),
            "int64" => unchecked((long)r.Varint()),
            "uint64" => r.Varint(),
            "sint32" => Zig32((uint)r.Varint()),
            "sint64" => Zig64(r.Varint()),
            "bool" => r.Varint() != 0,
            "fixed32" => BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes(4)),
            "sfixed32" => BinaryPrimitives.ReadInt32LittleEndian(r.Bytes(4)),
            "fixed64" => BinaryPrimitives.ReadUInt64LittleEndian(r.Bytes(8)),
            "sfixed64" => BinaryPrimitives.ReadInt64LittleEndian(r.Bytes(8)),
            "float" => BinaryPrimitives.ReadSingleLittleEndian(r.Bytes(4)),
            "double" => BinaryPrimitives.ReadDoubleLittleEndian(r.Bytes(8)),
            "bytes" => r.Delimited(),
            "string" => new UTF8Encoding(false, true).GetString(r.Delimited()),
            _ => throw new InvalidDataException("unsupported_schema_type")};
    }

    private static int Zig32(uint n) => unchecked((int)((n >> 1) ^ (uint)-(int)(n & 1)));
    private static long Zig64(ulong n) => unchecked((long)((n >> 1) ^ (ulong)-(long)(n & 1)));
    private static string Key(object key) => key is bool b ? (b ? "true" : "false") : Convert.ToString(key, CultureInfo.InvariantCulture)!;
    private void WriteNode(Utf8JsonWriter json, Node node)
    {
        json.WriteStartObject();
        foreach (var(number, value)in node.Values.OrderBy(p => p.Key))
        {
            var field = node.Schema.Fields[number];
            json.WritePropertyName(char.ToLowerInvariant(field.Name[0]) + field.Name[1..]);
            if (field.MapKey != null)
            {
                json.WriteStartObject();
                foreach (var(key, item)in (Dictionary<string, object>)value)
                {
                    json.WritePropertyName(key);
                    WriteValue(json, field.Type, item);
                }

                json.WriteEndObject();
            }
            else if (field.Repeated)
            {
                json.WriteStartArray();
                foreach (var item in (List<object>)value)
                    WriteValue(json, field.Type, item);
                json.WriteEndArray();
            }
            else
                WriteValue(json, field.Type, value);
        }

        json.WriteEndObject();
    }

    private void WriteValue(Utf8JsonWriter json, string type, object value)
    {
        if (value is Node node)
        {
            WriteNode(json, node);
            return;
        }

        if (catalog.Enums.TryGetValue(type, out var names))
        {
            if (names.TryGetValue(Convert.ToInt32(value, CultureInfo.InvariantCulture), out var name))
                json.WriteStringValue(name);
            else
                json.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return;
        }

        switch (value)
        {
            case string text:
                json.WriteStringValue(text);
                break;
            case byte[] bytes:
                json.WriteBase64StringValue(bytes);
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case long n:
                json.WriteStringValue(n.ToString(CultureInfo.InvariantCulture));
                break;
            case ulong n:
                json.WriteStringValue(n.ToString(CultureInfo.InvariantCulture));
                break;
            case int n:
                json.WriteNumberValue(n);
                break;
            case uint n:
                json.WriteNumberValue(n);
                break;
            case float n:
                if (float.IsFinite(n))
                    json.WriteNumberValue(n);
                else
                    json.WriteStringValue(n.ToString(CultureInfo.InvariantCulture));
                break;
            case double n:
                if (double.IsFinite(n))
                    json.WriteNumberValue(n);
                else
                    json.WriteStringValue(n.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidDataException("unsupported_json_value");
        }
    }
}
