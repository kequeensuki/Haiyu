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

    /// <summary>生成带时间戳的 JSON 文件路径，并确保目录存在。（v1 保留：回放工具用）</summary>
    public static string BuildJsonPath(DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(OutputDirectory);
        return Path.Combine(
            OutputDirectory,
            $"game-data-{timestamp:yyyyMMdd-HHmmss}.json"
        );
    }

    /// <summary>
    /// v2 会话路径：<c>GameDataExport\{playerId}\game-data-{sessionStart}.json</c>。
    /// 同一玩家多次会话 = 同目录多个文件（时间戳不同），历史可查。
    /// </summary>
    public static string BuildSessionPath(int playerId, DateTimeOffset sessionStart)
    {
        var directory = Path.Combine(OutputDirectory, playerId.ToString());
        Directory.CreateDirectory(directory);
        return Path.Combine(
            directory,
            $"game-data-{sessionStart:yyyyMMdd-HHmmss}.json"
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
    /// <remarks>
    /// ★ v2 原子写（持续监控下的更新语义）：
    ///   ① playerId 已知（&gt;0）→ 会话路径 <c>{playerId}\game-data-{ts}.json</c>，
    ///      同一会话的每次更新覆盖同一文件；
    ///   ② 写同目录 <c>.tmp</c> → <see cref="File.Replace"/>（NTFS 原子）/
    ///      目标不存在时 <see cref="File.Move"/>（首次写无替换对象）；
    ///      任何一步失败保留旧文件不动（数据安全），返回 null；
    ///   ③ playerId 未取得（v1 兼容路径 / 阶段 2 快速失败前的过渡）→ v1 平铺路径。
    /// </remarks>
    public static string? TryWrite(GameDataExportResult result, DateTimeOffset timestamp)
    {
        try
        {
            var path = result.PlayerId > 0
                ? GameDataExportPaths.BuildSessionPath(result.PlayerId, timestamp)
                : GameDataExportPaths.BuildJsonPath(timestamp);
            using var document = BuildDocument(result, timestamp);
            return WriteAtomicTo(path, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>测试入口：沿用同样的路径及错误处理。</summary>
    internal static string? TryWriteToRoot(string rootDirectory, GameDataExportResult result, DateTimeOffset timestamp)
    {
        try
        {
            var path = result.PlayerId > 0
                ? Path.Combine(rootDirectory, result.PlayerId.ToString(), $"game-data-{timestamp:yyyyMMdd-HHmmss}.json")
                : Path.Combine(rootDirectory, $"game-data-{timestamp:yyyyMMdd-HHmmss}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var document = BuildDocument(result, timestamp);
            return WriteAtomicTo(path, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>原子写：tmp → Replace/Move。失败保留旧文件，返回 null。</summary>
    private static string? WriteAtomicTo(string path, JsonDocument document)
    {
        var tmpPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            ))
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                document.WriteTo(writer);
                writer.Flush();
            }

            if (File.Exists(path))
                File.Replace(tmpPath, path, null); // NTFS 原子替换
            else
                File.Move(tmpPath, path);          // 首次写：无替换对象

            return path;
        }
        catch (IOException)
        {
            TryDeleteQuiet(tmpPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteQuiet(tmpPath);
            return null;
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程（tmp 残留无害，下次 Create 覆盖）
        }
    }

    internal static byte[] Encode(GameDataExportResult result, DateTimeOffset timestamp)
    {
        using var document = BuildDocument(result, timestamp);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) document.WriteTo(writer);
        return buffer.ToArray();
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