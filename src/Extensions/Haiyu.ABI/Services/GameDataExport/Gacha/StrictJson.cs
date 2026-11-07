using System.Globalization;
using System.Text.Json;

namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>外部响应和历史账本的必需字段校验；非法结构不能退化为空数据。</summary>
internal static class StrictJson
{
    internal static JsonElement Property(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException($"抽卡数据字段 {name} 缺失或类型错误。");
        return value;
    }

    internal static string String(JsonElement parent, string name, bool allowEmpty = false, bool allowNumber = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            throw new InvalidDataException($"抽卡数据字段 {name} 缺失。");
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()
            : allowNumber && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (text is null || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw new InvalidDataException($"抽卡数据字段 {name} 不是有效文字。");
        return text;
    }

    internal static int Int(JsonElement parent, string name, int min = 0, bool allowString = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            throw new InvalidDataException($"抽卡数据字段 {name} 缺失。");
        int number;
        var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out number)
            : allowString && value.ValueKind == JsonValueKind.String
                ? int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                : SetInvalid(out number);
        if (!valid || number < min) throw new InvalidDataException($"抽卡数据字段 {name} 不是有效整数。");
        return number;
    }

    private static bool SetInvalid(out int value) { value = 0; return false; }
}
