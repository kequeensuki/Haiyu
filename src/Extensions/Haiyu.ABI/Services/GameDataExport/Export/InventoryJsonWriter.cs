using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ABI.Models;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>
/// 导出产物落盘路径。
/// 输出目录<b>写死</b>（不暴露给调用方，也不需要设置项）。
/// </summary>
public static class GameDataExportPaths
{
    /// <summary><c>%USERPROFILE%\Documents\Waves\GameDataExport</c></summary>
    public static string OutputDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Waves",
            "GameDataExport"
        );

    /// <summary>生成带时间戳的 JSON 文件路径，并确保目录存在。</summary>
    public static string BuildJsonPath(DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(OutputDirectory);
        return Path.Combine(
            OutputDirectory,
            $"game-data-{timestamp:yyyyMMdd-HHmmss}.json"
        );
    }
}

/// <summary>
/// 把导出结果写成 JSON 文件。
///
/// ★ 用源生成的 <see cref="ABIJsonContext"/>（AOT 安全，无反射）。
/// ★ UTF-8 无 BOM；缩进输出（数据量不大，可读性优先）。
/// ★ 落盘失败不抛出——数据已经随 Result 返回了，写盘只是附带动作。
/// </summary>
public static class InventoryJsonWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>尝试落盘，成功返回文件路径，失败返回 <c>null</c>。</summary>
    public static string? TryWrite(GameDataExportResult result, DateTimeOffset timestamp)
    {
        try
        {
            var path = GameDataExportPaths.BuildJsonPath(timestamp);
            var document = BuildDocument(result, timestamp);

            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
            using var writer = new Utf8JsonWriter(stream, WriterOptions);
            document.WriteTo(writer);
            writer.Flush();
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonDocument BuildDocument(
        GameDataExportResult result,
        DateTimeOffset timestamp
    )
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("exportedAt", timestamp.ToString("O"));
            writer.WriteString("scope", "Inventory");
            writer.WriteString("sessionKeyHex", result.SessionKeyHex);

            writer.WriteStartObject("stats");
            writer.WriteNumber("capturedFrames", result.CapturedFrames);
            writer.WriteNumber("decodedFrames", result.DecodedFrames);
            writer.WriteEndObject();

            writer.WriteStartObject("inventory");
            WriteSection(writer, "normalItems", result.Inventory?.NormalItems);
            WriteSection(writer, "weapons", result.Inventory?.Weapons);
            WriteSection(writer, "phantoms", result.Inventory?.Phantoms);
            WriteSection(writer, "validTimeItems", result.Inventory?.ValidTimeItems);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    private static void WriteSection<T>(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyList<T>? items
    )
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("count", items?.Count ?? 0);
        writer.WritePropertyName("items");

        // ★ 必须用源生成的 JsonTypeInfo 重载：本项目 PublishAot=true，
        //   JsonSerializer.Serialize<T>(writer, value, options) 带
        //   RequiresUnreferencedCode/RequiresDynamicCode（编译期报 IL2026/IL3050），
        //   AOT 下可能运行时抛 NotSupportedException。
        //   四种集合形态均已在 ABIJsonContext 注册（见其 attribute 列表），
        //   ResolveTypeInfo 返回 null 属于编码遗漏——抛异常而非静默降级。
        var typeInfo = ResolveTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"类型 {typeof(T).Name} 未在 ABIJsonContext 注册为 IReadOnlyList<T>，"
                    + "无法在 AOT 下序列化。"
            );

        // 可空性注解差异：Serialize 重载的 jsonTypeInfo 形参是 T?，
        // 源生成的属性是不可空形态——用 null 包容运算符对齐（运行时无影响）
        JsonSerializer.Serialize<IReadOnlyList<T>?>(writer, items, typeInfo!);

        writer.WriteEndObject();
    }

    /// <summary>
    /// 从 <see cref="ABIJsonContext"/> 解析 <c>IReadOnlyList&lt;T&gt;</c> 的源生成元数据。
    /// 四种集合形态都在 ABIJsonContext 上注册（见其 attribute 列表）。
    /// </summary>
    private static JsonTypeInfo<IReadOnlyList<T>>? ResolveTypeInfo<T>() =>
        typeof(T) switch
        {
            var t when t == typeof(GameNormalItem) =>
                ABIJsonContext.Default.IReadOnlyListGameNormalItem as JsonTypeInfo<IReadOnlyList<T>>,
            var t when t == typeof(GameWeaponItem) =>
                ABIJsonContext.Default.IReadOnlyListGameWeaponItem as JsonTypeInfo<IReadOnlyList<T>>,
            var t when t == typeof(GamePhantomItem) =>
                ABIJsonContext.Default.IReadOnlyListGamePhantomItem as JsonTypeInfo<IReadOnlyList<T>>,
            var t when t == typeof(GameValidTimeItem) =>
                ABIJsonContext.Default.IReadOnlyListGameValidTimeItem as JsonTypeInfo<IReadOnlyList<T>>,
            _ => null,
        };
}