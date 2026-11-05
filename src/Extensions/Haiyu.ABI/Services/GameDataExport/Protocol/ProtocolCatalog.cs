using System.Collections.Frozen;
using System.Text.Json;

namespace Haiyu.ABI.Services.GameDataExport.Protocol;
/// <summary>编译期生成的协议元数据；只读资源加载，不扫描 CLR 类型或官方程序集。</summary>
internal sealed class ProtocolCatalog
{
    internal sealed record Field(int Number, string Name, string Type, bool Repeated, string? MapKey, string? Oneof);
    internal sealed record Message(string Name, FrozenDictionary<int, Field> Fields);
    internal static readonly Lazy<ProtocolCatalog> Default = new(Load);
    internal readonly Dictionary<string, Message> Messages = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<int, string>> Enums = new(StringComparer.Ordinal);
    internal readonly Dictionary<int, string> Ids = new();
    internal string SourceCommit { get; private set; } = "";
    internal string Version { get; private set; } = "";

    private static ProtocolCatalog Load()
    {
        using var stream = typeof(ProtocolCatalog).Assembly.GetManifestResourceStream("Haiyu.WavesProtocolSchema") ?? throw new InvalidDataException("缺少生成的游戏协议元数据。");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var catalog = new ProtocolCatalog
        {
            Version = root.GetProperty("sourceHash").GetString()!,
            SourceCommit = root.GetProperty("sourceCommit").GetString()!
        };
        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var fields = m.GetProperty("fields").EnumerateArray().Select(f => new Field(f.GetProperty("number").GetInt32(), f.GetProperty("name").GetString()!, f.GetProperty("type").GetString()!, f.GetProperty("repeated").GetBoolean(), f.GetProperty("mapKey").GetString(), f.GetProperty("oneof").GetString())).ToFrozenDictionary(f => f.Number);
            var name = m.GetProperty("name").GetString()!;
            catalog.Messages.Add(name, new(name, fields));
        }

        foreach (var e in root.GetProperty("enums").EnumerateArray())
            catalog.Enums.Add(e.GetProperty("name").GetString()!, e.GetProperty("values").EnumerateArray().GroupBy(v => v.GetProperty("number").GetInt32()).ToDictionary(g => g.Key, g => g.First().GetProperty("originalName").GetString() ?? g.First().GetProperty("name").GetString()!));
        foreach (var id in root.GetProperty("ids").EnumerateObject())
            catalog.Ids.Add(int.Parse(id.Name, System.Globalization.CultureInfo.InvariantCulture), id.Value.GetString()!);
        return catalog;
    }
}
