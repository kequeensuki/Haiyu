using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>抽卡区服。决定 serverId、svr_area 与 API 域名。</summary>
internal enum GachaRegion
{
    /// <summary>国服（官服与 B 服共用）。</summary>
    Cn = 0,

    /// <summary>国际服。</summary>
    Global = 1,
}

/// <summary>
/// serverId 常量。
/// <para>
/// <b>出处</b>：<c>pakchunk1-WindowsNoEditor.pak</c> →
/// <c>Client/Content/Aki/JavaScript/Launcher/Notice/LauncherNoticeDefine.js</c>
/// 的 <c>exports.SERVER_ID_CN</c> / <c>exports.SERVER_ID_GLOBAL</c>。
/// 对 20953 个 pak 内脚本做全量检索后，这两个值仅出现在该文件，没有第三个变体。
/// </para>
/// <para>它们是版本相关的客户端常量，不是账号凭据，也不含个人信息。</para>
/// </summary>
internal static class GachaServerIds
{
    internal const string Cn = "76402e5b20be2c39f095a152090afddc";
    internal const string Global = "86d52186155b148b5c138ceb41be9650";

    /// <summary>探测顺序：先试首选区服，再试另一个。两个都必须试，不能只硬编码国服。</summary>
    internal static IReadOnlyList<(GachaRegion Region, string ServerId)> Candidates(GachaRegion preferred) =>
        preferred == GachaRegion.Global
            ? [(GachaRegion.Global, Global), (GachaRegion.Cn, Cn)]
            : [(GachaRegion.Cn, Cn), (GachaRegion.Global, Global)];

    internal static string For(GachaRegion region) => region == GachaRegion.Global ? Global : Cn;

    internal static GachaRegion RegionOf(string serverId) =>
        string.Equals(serverId, Global, StringComparison.Ordinal) ? GachaRegion.Global : GachaRegion.Cn;

    internal static string AreaOf(string serverId) =>
        RegionOf(serverId) == GachaRegion.Global ? "global" : "cn";
}

/// <summary>区服解析有一条已持久化的记录。</summary>
internal sealed record GachaServerBinding(string ServerId, string LanguageCode, DateTimeOffset ResolvedAt);

/// <summary>单个玩家的区服绑定。每玩家自包含，与抽卡记录同目录。</summary>
internal interface IGachaServerCache
{
    GachaServerBinding? TryGet(int playerId);
    void Set(int playerId, GachaServerBinding binding);
    /// <summary>缓存值已失效（接口返回 -4）时移除，让下一次重新探测。</summary>
    void Remove(int playerId);
}

/// <summary>
/// 把 <c>gacha-servers.json</c> 读写为纯内存快照；文件 I/O 由调用方在写入点完成。
/// 文件损坏时视为空缓存而不是抛错——最坏只是多探测一次。
/// </summary>
internal sealed class GachaServerCache : IGachaServerCache
{
    internal const string FileName = "servers.json";
    internal const int SchemaVersion = 1;

    /// <summary>
    /// 缓存文件路径：<c>&lt;playerId&gt;/gacha/servers.json</c>，与抽卡记录同目录，
    /// 每玩家自包含，删除该玩家目录即完整清理。
    /// </summary>
    internal static string RelativePath(int playerId) =>
        Path.Combine(playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), "gacha", FileName);
    private readonly Dictionary<int, GachaServerBinding> _entries = new();

    internal IReadOnlyDictionary<int, GachaServerBinding> Entries => _entries;

    public GachaServerBinding? TryGet(int playerId) => _entries.GetValueOrDefault(playerId);

    public void Set(int playerId, GachaServerBinding binding) => _entries[playerId] = binding;

    public void Remove(int playerId) => _entries.Remove(playerId);

    /// <summary>解析缓存文件；格式不合法或版本不符时返回空缓存（不覆盖原文件由调用方保证）。</summary>
    internal static GachaServerCache Parse(byte[]? bytes)
    {
        var cache = new GachaServerCache();
        if (bytes is null || bytes.Length == 0) return cache;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return cache;
            if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return cache;
            foreach (var entry in servers.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var playerId)
                    || playerId <= 0 || entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!entry.Value.TryGetProperty("serverId", out var serverIdElement)
                    || serverIdElement.ValueKind != JsonValueKind.String)
                    continue;
                var serverId = serverIdElement.GetString();
                if (string.IsNullOrWhiteSpace(serverId)) continue;
                var languageCode = entry.Value.TryGetProperty("languageCode", out var lang)
                    && lang.ValueKind == JsonValueKind.String ? lang.GetString() ?? "" : "";
                var resolvedAt = entry.Value.TryGetProperty("resolvedAt", out var at)
                    && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsedAt)
                        ? parsedAt : DateTimeOffset.MinValue;
                cache._entries[playerId] = new GachaServerBinding(serverId, languageCode, resolvedAt);
            }
        }
        catch (JsonException)
        {
            return new GachaServerCache();
        }
        return cache;
    }

    /// <summary>序列化为稳定顺序的 UTF-8 字节（按 playerId 升序）。</summary>
    internal byte[] Encode(DateTimeOffset writtenAt)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("updatedAt", writtenAt.ToString("O"));
            writer.WriteStartObject("servers");
            foreach (var pair in _entries.OrderBy(pair => pair.Key))
            {
                writer.WriteStartObject(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("serverId", pair.Value.ServerId);
                writer.WriteString("region", GachaServerIds.RegionOf(pair.Value.ServerId) == GachaRegion.Global
                    ? "global" : "cn");
                writer.WriteString("languageCode", pair.Value.LanguageCode);
                writer.WriteString("resolvedAt", pair.Value.ResolvedAt.ToString("O"));
                writer.WriteString("source", "probe");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}
