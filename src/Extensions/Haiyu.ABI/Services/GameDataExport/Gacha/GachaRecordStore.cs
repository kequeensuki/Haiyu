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
        var seen = new Dictionary<(string Time, int ResourceId, int QualityLevel, int Count), int>();
        var result = new List<GachaRecordState>();
        foreach (var record in records)
        {
            var shape = (record.Time, record.ResourceId, record.QualityLevel, record.Count);
            var occurrence = seen.TryGetValue(shape, out var previous) ? previous + 1 : 0;
            seen[shape] = occurrence;
            var key = new GachaRecordKey(cardPoolType, record.Time, record.ResourceId, record.QualityLevel,
                record.Count, occurrence);
            // 接口返回的 cardPoolType 可能是空或裸数字（实测 8/9/12/13），
            // 因此标签以请求时的池编号查官方表为准，接口文本只在表未收录时兜底。
            var label = GachaPoolTypes.Label(cardPoolType);
            if (label is null && !string.IsNullOrWhiteSpace(record.CardPoolType))
                label = record.CardPoolType;
            result.Add(new GachaRecordState(key, label ?? GachaPoolTypes.DisplayLabel(cardPoolType), record.ResourceType, record.Name));
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
    internal static IReadOnlyList<GachaRecordState> LoadExisting(string path, int? expectedPlayerId = null)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (StrictJson.Int(root, "schemaVersion") != SchemaVersion)
                throw new InvalidDataException("抽卡历史版本不兼容，保留原文件。");
            var player = StrictJson.Int(root, "playerId", min: 1);
            if (expectedPlayerId is { } expectedPlayer && player != expectedPlayer)
                throw new InvalidDataException("抽卡历史的玩家身份不匹配，保留原文件。");
            _ = StrictJson.String(root, "updatedAt");
            var query = StrictJson.Property(root, "query", JsonValueKind.Object);
            foreach (var field in new[] { "serverId", "serverArea", "recordId", "cardPoolId", "languageCode", "serverResolvedBy" })
                _ = StrictJson.String(query, field, allowEmpty: field is "cardPoolId");
            foreach (var field in new[] { "fetchedPoolTypes", "failedPoolTypes" })
                foreach (var pool in StrictJson.Property(query, field, JsonValueKind.Array).EnumerateArray())
                    if (pool.ValueKind != JsonValueKind.Number || !pool.TryGetInt32(out var id) || id <= 0)
                        throw new InvalidDataException("抽卡历史查询池结构损坏。");
            var totals = StrictJson.Property(root, "totals", JsonValueKind.Object);
            var pools = StrictJson.Property(root, "pools", JsonValueKind.Array);
            var result = new List<GachaRecordState>();
            var poolIds = new HashSet<int>();
            var legacyKeys = new HashSet<(GachaRecordKey Key, string Type, string Name)>();
            foreach (var pool in pools.EnumerateArray())
            {
                var type = StrictJson.Int(pool, "cardPoolType", min: 1);
                if (!poolIds.Add(type)) throw new InvalidDataException("抽卡历史池重复。");
                var items = StrictJson.Property(pool, "items", JsonValueKind.Array);
                if (StrictJson.Int(pool, "count") != items.GetArrayLength())
                    throw new InvalidDataException("抽卡历史池计数不一致。");
                foreach (var item in items.EnumerateArray())
                {
                    if (StrictJson.Int(item, "cardPoolType", min: 1) != type)
                        throw new InvalidDataException("抽卡历史条目池身份不一致。");
                    var key = new GachaRecordKey(type, StrictJson.String(item, "time"),
                        StrictJson.Int(item, "resourceId", min: 1), StrictJson.Int(item, "qualityLevel", min: 1),
                        StrictJson.Int(item, "count", min: 1), StrictJson.Int(item, "occurrence"));
                    var state = new GachaRecordState(key, StrictJson.String(item, "poolLabel", allowEmpty: true),
                        StrictJson.String(item, "resourceType"), StrictJson.String(item, "name"));
                    if (!legacyKeys.Add((key, state.ResourceType, state.Name)))
                        throw new InvalidDataException("抽卡历史存在重复身份。");
                    result.Add(state);
                }
            }
            if (StrictJson.Int(totals, "count") != result.Count || StrictJson.Int(totals, "poolCount") != poolIds.Count)
                throw new InvalidDataException("抽卡历史总计数不一致。");
            var times = result.Select(r => r.Time).Order(StringComparer.Ordinal).ToArray();
            foreach (var (field, expected) in new[] { ("firstTime", times.FirstOrDefault()), ("lastTime", times.LastOrDefault()) })
            {
                if (!totals.TryGetProperty(field, out var value) ||
                    (expected is null ? value.ValueKind != JsonValueKind.Null
                        : value.ValueKind != JsonValueKind.String || value.GetString() != expected))
                    throw new InvalidDataException("抽卡历史时间范围不一致。");
            }
            // v1 曾将翻译纳入键；保留各稳定身份的最大 occurrence，不累加翻译副本。
            return result.DistinctBy(record => record.Key).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("无法安全读取抽卡历史，保留原文件：" + ex.Message, ex);
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
        var collection = new GachaRecordCollection(existing.DistinctBy(record => record.Key));
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

}
