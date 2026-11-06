using System.Globalization;
using System.Text.Json;

namespace Haiyu.ABI.Services.GameDataExport.Protocol;

internal sealed class ProtocolJsonWriter(ProtocolCatalog catalog)
{
    internal byte[] Encode(ProtocolMessage message)
    {
        using var output = new LimitedBuffer(16 * 1024 * 1024);
        using (var json = new Utf8JsonWriter(output)) WriteNode(json, message);
        return output.ToArray();
    }
    private void WriteNode(Utf8JsonWriter json, ProtocolMessage node)
    {
        json.WriteStartObject();
        foreach (var(number, value)in node.Fields.OrderBy(p => p.Key))
        {
            var field = node.Schema.Fields[number];
            json.WritePropertyName(char.ToLowerInvariant(field.Name[0]) + field.Name[1..]);
            if (field.MapKey != null)
            {
                json.WriteStartObject();
                foreach (var(key, item)in (IReadOnlyDictionary<string, object>)value)
                {
                    json.WritePropertyName(key);
                    WriteValue(json, field.Type, item);
                }

                json.WriteEndObject();
            }
            else if (field.Repeated)
            {
                json.WriteStartArray();
                foreach (var item in (IReadOnlyList<object>)value)
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
        if (value is ProtocolMessage node)
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
            case ReadOnlyMemory<byte> bytes:
                json.WriteBase64StringValue(bytes.Span);
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
