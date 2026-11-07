using System.Globalization;
using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Gacha;

namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>把接口记录归一成业务记录键（含 occurrence），并读回旧文件做跨会话累积。</summary>
internal static class GachaRecordNormalizer
{
    /// <summary>
    /// 归一一批接口记录。occurrence 是同一键在**本次响应**内的序号（0,1,2…），
    /// 因此重复拉同一批数据得到相同键，天然幂等；十连出两把同名三星也不会互相吞掉。
    /// </summary>
    internal static IReadOnlyList<GachaRecordState> Normalize(
        IEnumerable<GachaWireRecord> records, int cardPoolType)
    {
        var seen = new Dictionary<(string Time, int ResourceId, int QualityLevel, string ResourceType, string Name, int Count), int>();
        var result = new List<GachaRecordState>();
        foreach (var record in records)
        {
            var shape = (record.Time, record.ResourceId, record.QualityLevel, record.ResourceType, record.Name,
                record.Count);
            var occurrence = seen.TryGetValue(shape, out var previous) ? previous + 1 : 0;
            seen[shape] = occurrence;
            var key = new GachaRecordKey(cardPoolType, record.Time, record.ResourceId, record.QualityLevel,
                record.ResourceType, record.Name, record.Count, occurrence);
            // 接口返回的 cardPoolType 可能是空或裸数字（实测 8/9/12/13），
            // 因此标签以请求时的池编号查官方表为准，接口文本只在表未收录时兜底。
            var label = GachaPoolTypes.Label(cardPoolType);
            if (label is null && !string.IsNullOrWhiteSpace(record.CardPoolType))
                label = record.CardPoolType;
            result.Add(new GachaRecordState(key, label ?? GachaPoolTypes.DisplayLabel(cardPoolType)));
        }
        return result;
    }
}

/// <summary>
/// 抽卡记录的跨会话累积存储。
/// <para>
/// 文件：<c>&lt;root&gt;/&lt;playerId&gt;/gacha/record.json</c>（与既有的 game-data.json 分离）。
/// 合并原则：<b>只增不删</b>。官方接口只保留有限时间，过期记录必须留在本地。
/// </para>
/// <para>
/// 读取旧文件失败（损坏/版本不符）时<b>拒绝写入</b>并报错，绝不覆盖——
/// 宁可本次不更新，也不能用新数据抹掉历史。
/// </para>
/// </summary>
internal sealed class GachaRecordStore
{
    internal const string RecordFileName = "record.json";
    internal const int SchemaVersion = 1;

    internal static string RelativePath(int playerId) =>
        Path.Combine(playerId.ToString(CultureInfo.InvariantCulture), "gacha", RecordFileName);

    /// <summary>读取旧文件中的记录。文件不存在返回空集合；损坏抛异常（由调用方保留旧文件并报错）。</summary>
    internal static IReadOnlyList<GachaRecordState> LoadExisting(string path)
    {
        if (!File.Exists(path)) return [];
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("无法读取已有的抽卡记录文件，本次不写入以免覆盖历史：" + ex.Message, ex);
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var version = root.TryGetProperty("schemaVersion", out var versionElement)
                && versionElement.TryGetInt32(out var parsedVersion) ? parsedVersion : 0;
            if (version != SchemaVersion)
                throw new InvalidDataException(
                    $"已有的抽卡记录 schemaVersion={version} 与当前 {SchemaVersion} 不符，本次不写入以免丢数据。");

            var result = new List<GachaRecordState>();
            if (!root.TryGetProperty("pools", out var pools) || pools.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var pool in pools.EnumerateArray())
            {
                if (pool.ValueKind != JsonValueKind.Object) continue;
                if (!pool.TryGetProperty("cardPoolType", out var typeElement)
                    || !typeElement.TryGetInt32(out var cardPoolType)) continue;
                if (!pool.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    result.Add(new GachaRecordState(new GachaRecordKey(
                        cardPoolType,
                        ReadString(item, "time"),
                        ReadInt32(item, "resourceId"),
                        ReadInt32(item, "qualityLevel"),
                        ReadString(item, "resourceType"),
                        ReadString(item, "name"),
                        ReadInt32(item, "count"),
                        ReadInt32(item, "occurrence")),
                        ReadString(item, "poolLabel")));
                }
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("已有的抽卡记录文件不是合法 JSON，本次不写入以免覆盖历史。", ex);
        }
    }

    /// <summary>
    /// 合并旧记录与新记录，生成待落盘的 DTO。只增：旧记录全部保留，新记录按键补齐。
    /// </summary>
    internal static GameGachaData Build(
        int playerId,
        IEnumerable<GachaRecordState> existing,
        IEnumerable<GachaRecordState> fetched,
        GameGachaQuery query,
        DateTimeOffset updatedAt)
    {
        var collection = new GachaRecordCollection(existing);
        fetched.ToList().ForEach(record => collection.Add(record));

        var all = collection.Snapshot();
        var pools = all
            .GroupBy(record => record.CardPoolType)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var ordered = group.OrderBy(record => record.Key).ToArray();
                return new GameGachaPool(group.Key, ordered.Length, ordered.Select(ToDto).ToArray());
            })
            .ToArray();

        var times = all.Select(record => record.Time)
            .Where(time => !string.IsNullOrWhiteSpace(time))
            .OrderBy(time => time, StringComparer.Ordinal)
            .ToArray();

        return new GameGachaData(
            SchemaVersion,
            playerId,
            updatedAt.ToString("O"),
            query,
            new GameGachaTotals(all.Count, pools.Length,
                times.Length > 0 ? times[0] : null,
                times.Length > 0 ? times[^1] : null),
            pools);
    }

    private static GameGachaRecordItem ToDto(GachaRecordState record) => new(
        record.CardPoolType, record.PoolLabel, record.ResourceId, record.QualityLevel,
        record.ResourceType, record.Name, record.Count, record.Time, record.Occurrence);

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : 0;
}
