using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Haiyu.ABI.Services.GameDataExport.Protocol;
/// <summary>使用静态 schema 解码 protobuf wire 数据；无反射、无动态类型构造，适用于 NativeAOT。</summary>
internal sealed class ProtocolDecoder(ProtocolCatalog catalog)
{
    private sealed class Builder(ProtocolCatalog.Message schema)
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

    internal ProtocolDecodeResult Decode(int msgId, byte[] bytes)
    {
        if (!catalog.Ids.TryGetValue(msgId, out var name))
            return new("unknown_message", null, null, false, null);
        return DecodeType(name, bytes);
    }

    internal ProtocolDecodeResult DecodeType(string name, byte[] bytes)
    {
        try
        {
            var budget = 1_000_000;
            var node = ReadMessage(name, new Reader(bytes, 0, bytes.Length), 0, ref budget);
            return new(node.Unknown ? "decoded_with_unknown_fields" : "decoded", name, Freeze(node), node.Unknown, null);
        }
        catch (Exception ex)when (ex is InvalidDataException or DecoderFallbackException or OverflowException or OutputLimitException)
        {
            return new("parse_failed", name, null, false, ex.Message);
        }
    }

    private Builder ReadMessage(string name, Reader reader, int depth, ref int budget, Builder? existing = null)
    {
        if (depth > 64)
            throw new InvalidDataException("recursion_limit");
        var node = existing ?? new Builder(catalog.Messages[name]);
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
                        value = ReadValue(field.Type, entry, depth + 1, ref budget, value as Builder);
                    else
                    {
                        node.Unknown = true;
                        entry.Skip(w, n, depth + 1);
                    }
                }

                if (value is Builder nested && nested.Unknown)
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
            var item = ReadValue(field.Type, reader, depth + 1, ref budget, field.Repeated ? null : previous as Builder);
            if (item is Builder childBuilder && childBuilder.Unknown)
                node.Unknown = true;
            if (field.Repeated)
                List(node, number).Add(item);
            else
                node.Values[number] = item;
        }

        return node;
    }

    private static List<object> List(Builder node, int number)
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
    private object Default(string type) => catalog.Messages.TryGetValue(type, out var m) ? new Builder(m) : type switch
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
    private object ReadValue(string type, Reader r, int depth, ref int budget, Builder? previous = null)
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
    private static ProtocolMessage Freeze(Builder node) => new(node.Schema,
        node.Values.ToDictionary(p => p.Key, p => FreezeValue(p.Value)), node.Unknown);

    private static object FreezeValue(object value) => value switch
    {
        Builder child => Freeze(child),
        List<object> list => Array.AsReadOnly(list.Select(FreezeValue).ToArray()),
        Dictionary<string, object> map => new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(
            map.ToDictionary(p => p.Key, p => FreezeValue(p.Value))),
        byte[] bytes => (ReadOnlyMemory<byte>)bytes,
        _ => value
    };
}
