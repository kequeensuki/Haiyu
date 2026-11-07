using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Domains.Gacha;
using Haiyu.ABI.Services.GameDataExport.Gacha;
using MemoryPack;

namespace ABIRuntime.TransportTests;

/// <summary>
/// 抽卡记录导出：区服探测、只增累积、跨会话幂等。
/// 使用可编程的假端点，绝不请求生产接口。
/// </summary>
internal static partial class Program
{
    /// <summary>按 (cardPoolType, serverId) 返回预设结果的假端点，并记录调用。</summary>
    private sealed class FakeGachaEndpoint : IGachaRecordEndpoint
    {
        internal readonly List<(string ServerId, int PoolType)> Calls = [];
        internal readonly List<(string ServerId, int PoolType, int Code)> Scripted = [];
        internal Func<string, int, (int Code, IReadOnlyList<GachaWireRecord> Records)> Respond = (_, _) => (0, []);

        public Task<GachaQueryResult> QueryAsync(string serverId, bool isCn, int cardPoolType, string playerId,
            string cardPoolId, string languageCode, string recordId, CancellationToken cancellationToken)
        {
            Calls.Add((serverId, cardPoolType));
            var (code, records) = Respond(serverId, cardPoolType);
            Scripted.Add((serverId, cardPoolType, code));
            return Task.FromResult(new GachaQueryResult(code, code == 0 ? "success" : "err", records));
        }
    }

    private static GachaWireRecord GachaWire(string time, int resourceId, int quality = 3,
        string type = "武器", string name = "三星武器", int count = 1) =>
        new(name, resourceId, quality, type, name, count, time);

    private static async Task TestGachaServerProbeAsync()
    {
        // 1) 国服常量命中：探测一次即停止，另一个常量不应被请求。
        var endpoint = new FakeGachaEndpoint
        {
            Respond = (serverId, _) => serverId == GachaServerIds.Cn
                ? (0, [GachaWire("2026-11-01 10:00:00", 21050023)])
                : (GachaQueryResult.ServerMismatch, []),
        };
        var cache = new GachaServerCache();
        var resolver = new GachaServerResolver(endpoint, cache);
        var resolution = await resolver.ResolveAsync(778899, null, GachaRegion.Cn, "778899", "", "zh-Hans",
            "record-1", refresh: false, CancellationToken.None);
        Equal(GachaServerIds.Cn, resolution.ServerId);
        Equal("cn", resolution.Area);
        Equal("probe", resolution.ResolvedBy);
        Equal(1, endpoint.Calls.Count); // 命中即停，不做多余请求。
        Equal(1, resolution.FirstRecords!.Count); // 探测顺带取回首批数据。
        Equal(1, resolution.FirstPoolType);
        Equal(GachaServerIds.Cn, cache.TryGet(778899)!.ServerId);

        // 2) 缓存命中后不再探测。
        var second = new FakeGachaEndpoint();
        var cached = await new GachaServerResolver(second, cache).ResolveAsync(778899, null, GachaRegion.Global,
            "778899", "", "zh-Hans", "record-1", refresh: false, CancellationToken.None);
        Equal("cache", cached.ResolvedBy);
        Equal(0, second.Calls.Count);

        // 3) 国际服偏好时先试 GLOBAL；-4 后回退 CN，且缓存记录的是命中项。
        var glEndpoint = new FakeGachaEndpoint
        {
            Respond = (serverId, _) => serverId == GachaServerIds.Global
                ? (GachaQueryResult.ServerMismatch, [])
                : (0, []),
        };
        var glCache = new GachaServerCache();
        var fallback = await new GachaServerResolver(glEndpoint, glCache).ResolveAsync(1001, null, GachaRegion.Global,
            "1001", "", "zh-Hans", "r", refresh: false, CancellationToken.None);
        Equal(GachaServerIds.Cn, fallback.ServerId);
        Equal(2, glEndpoint.Calls.Count);
        Equal(GachaServerIds.Global, glEndpoint.Calls[0].ServerId); // 先试偏好区服。

        // 4) code=0 且 0 条是合法命中，不能当成失败继续探测。
        var emptyEndpoint = new FakeGachaEndpoint { Respond = (_, _) => (0, []) };
        var emptyResolution = await new GachaServerResolver(emptyEndpoint, new GachaServerCache())
            .ResolveAsync(1002, null, GachaRegion.Cn, "1002", "", "zh-Hans", "r", false, CancellationToken.None);
        Equal(GachaServerIds.Cn, emptyResolution.ServerId);
        Equal(1, emptyEndpoint.Calls.Count);

        // 5) recordId 失效（-1）不换 serverId：换也没用，直接报错。
        var invalidEndpoint = new FakeGachaEndpoint { Respond = (_, _) => (GachaQueryResult.InvalidRecord, []) };
        var threw = false;
        try
        {
            await new GachaServerResolver(invalidEndpoint, new GachaServerCache())
                .ResolveAsync(1003, null, GachaRegion.Cn, "1003", "", "zh-Hans", "r", false, CancellationToken.None);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("code=-1"))
        {
            threw = true;
        }
        Equal(true, threw);
        Equal(1, invalidEndpoint.Calls.Count); // 没有重试第二个常量。

        // 6) 两个都 -4：报错且不猜。
        var bothEndpoint = new FakeGachaEndpoint { Respond = (_, _) => (GachaQueryResult.ServerMismatch, []) };
        var bothThrew = false;
        try
        {
            await new GachaServerResolver(bothEndpoint, new GachaServerCache())
                .ResolveAsync(1004, null, GachaRegion.Cn, "1004", "", "zh-Hans", "r", false, CancellationToken.None);
        }
        catch (InvalidDataException) { bothThrew = true; }
        Equal(true, bothThrew);
        Equal(2, bothEndpoint.Calls.Count);
    }

    private static async Task TestGachaAccumulationAsync()
    {
        await Task.Yield();

        // occurrence：同秒同名的重复抽取必须各自保留。
        var duplicates = GachaRecordNormalizer.Normalize([
            GachaWire("2026-11-01 10:00:00", 21050023),
            GachaWire("2026-11-01 10:00:00", 21050023),
        ], 1);
        Equal(2, duplicates.Count);
        Equal(0, duplicates[0].Occurrence);
        Equal(1, duplicates[1].Occurrence);

        // 只增：旧记录不被新结果删除（官方记录过期后本地仍要保留）。
        var existing = GachaRecordNormalizer.Normalize([
            GachaWire("2026-10-01 10:00:00", 1101, 5, "角色", "旧五星"),
        ], 1).ToList();
        var fresh = GachaRecordNormalizer.Normalize([
            GachaWire("2026-11-01 10:00:00", 21050023),
        ], 1).ToList();
        var query = new GameGachaQuery(GachaServerIds.Cn, "cn", "r", "", "zh-Hans", "probe", [1], []);
        var merged = GachaRecordStore.Build(5001, existing, fresh, query, DateTimeOffset.UnixEpoch);
        Equal(2, merged.Totals.Count);
        Equal(1, merged.Totals.PoolCount);
        Equal("2026-10-01 10:00:00", merged.Totals.FirstTime);
        Equal("2026-11-01 10:00:00", merged.Totals.LastTime);

        // 跨会话幂等：同一批数据重复合并不增加条目。
        var again = GachaRecordStore.Build(5001, existing.Concat(fresh), fresh, query, DateTimeOffset.UnixEpoch);
        Equal(2, again.Totals.Count);

        // 新数据里官方"少了"的记录不能让本地减少。
        var shrunk = GachaRecordStore.Build(5001, existing.Concat(fresh), fresh.Take(0), query, DateTimeOffset.UnixEpoch);
        Equal(2, shrunk.Totals.Count);

        // 池标签以请求时的编号查官方表为准：接口返回裸数字 "8" 时仍给出可读名。
        var labelled = GachaRecordNormalizer.Normalize([
            new GachaWireRecord("8", 1, 3, "武器", "x", 1, "2026-11-01 10:00:00"),
        ], 8);
        Equal(8, labelled[0].CardPoolType);
        Equal("角色新旅唤取", labelled[0].PoolLabel);

        // 官方页面 10 个池都有中文名；未收录编号显式标注而不是猜。
        Equal("角色活动唤取", GachaPoolTypes.Label(1));
        Equal("新手自选唤取", GachaPoolTypes.Label(6));
        Equal("武器忆旅唤取", GachaPoolTypes.Label(13));
        Equal(null, GachaPoolTypes.Label(7)); // 页面不可查的池不编造名称。
        Equal("未本地化池(7)", GachaPoolTypes.DisplayLabel(7));
        Equal(10, GachaPoolTypes.All.Length);

        // JSON 往返：文件节点与内存 DTO 形状一致。
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(merged, ABIJsonContext.Default.GameGachaData);
        var round = System.Text.Json.JsonSerializer.Deserialize(bytes, ABIJsonContext.Default.GameGachaData)!;
        Equal(2, round.Totals.Count);
        Equal(merged.Pools[0].Items.Count, round.Pools[0].Items.Count);

        // MemoryPack 往返：AOT 下同样可用。
        var packed = MemoryPackSerializer.Serialize(merged, ABIMemoryPack.Options);
        var unpacked = MemoryPackSerializer.Deserialize<GameGachaData>(packed, ABIMemoryPack.Options)!;
        Equal(2, unpacked.Totals.Count);
        Equal(merged.Pools[0].Items[0].Occurrence, unpacked.Pools[0].Items[0].Occurrence);
    }

    private static async Task TestGachaStoreRecoveryAsync()
    {
        await Task.Yield();
        var directory = Path.Combine(Path.GetTempPath(), "Haiyu.GachaStore." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "record.json");
            // 不存在视为空基线（首次导出）。
            Equal(0, GachaRecordStore.LoadExisting(path).Count);

            var query = new GameGachaQuery(GachaServerIds.Cn, "cn", "r", "", "zh-Hans", "probe", [1], []);
            var fresh = GachaRecordNormalizer.Normalize([GachaWire("2026-11-01 10:00:00", 1101, 5, "角色", "五星")], 1);
            var data = GachaRecordStore.Build(6001, [], fresh, query, DateTimeOffset.UnixEpoch);
            await File.WriteAllBytesAsync(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                data, ABIJsonContext.Default.GameGachaData));

            // 读回后与内存一致，可作为下一次合并的基线。
            var loaded = GachaRecordStore.LoadExisting(path);
            Equal(1, loaded.Count);
            Equal(1101, loaded[0].ResourceId);

            // 损坏文件必须拒绝写入，不能把历史覆盖成新数据。
            await File.WriteAllTextAsync(path, "{ not json");
            var threw = false;
            try { GachaRecordStore.LoadExisting(path); }
            catch (InvalidDataException) { threw = true; }
            Equal(true, threw);

            // 版本不符同样拒绝。
            await File.WriteAllTextAsync(path, "{\"schemaVersion\": 99, \"pools\": []}");
            var versionThrew = false;
            try { GachaRecordStore.LoadExisting(path); }
            catch (InvalidDataException) { versionThrew = true; }
            Equal(true, versionThrew);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }

        // 缓存文件解析：损坏时退化为空缓存（最坏多探测一次），不影响导出。
        Equal(0, GachaServerCache.Parse(System.Text.Encoding.UTF8.GetBytes("{ broken")).Entries.Count);
        Equal(0, GachaServerCache.Parse(null).Entries.Count);
        var cache = new GachaServerCache();
        cache.Set(7001, new GachaServerBinding(GachaServerIds.Global, "ja-JP", DateTimeOffset.UnixEpoch));
        var encoded = cache.Encode(DateTimeOffset.UnixEpoch);
        var reparsed = GachaServerCache.Parse(encoded);
        Equal(GachaServerIds.Global, reparsed.TryGet(7001)!.ServerId);
        Equal("ja-JP", reparsed.TryGet(7001)!.LanguageCode);
        reparsed.Remove(7001);
        Equal(null, reparsed.TryGet(7001));

        // 缓存与抽卡记录同目录，每玩家自包含。
        Equal(Path.Combine("7001", "gacha", "servers.json"), GachaServerCache.RelativePath(7001));
        Equal(Path.Combine("7001", "gacha", "record.json"), GachaRecordStore.RelativePath(7001));
    }

    private static async Task TestGachaWireParsingAsync()
    {
        await Task.Yield();
        // 响应包络：正常成功。
        var ok = HttpGachaRecordEndpoint.Parse(
            "{\"code\":0,\"message\":\"success\",\"data\":[{\"cardPoolType\":\"角色精准调谐\"," +
            "\"resourceId\":1301,\"qualityLevel\":5,\"resourceType\":\"角色\",\"name\":\"甲\"," +
            "\"count\":1,\"time\":\"2026-11-01 10:00:00\"}]}");
        Equal(0, ok.Code);
        Equal(1, ok.Records.Count);
        Equal(1301, ok.Records[0].ResourceId);
        Equal("2026-11-01 10:00:00", ok.Records[0].Time);

        // 成功但零条：合法的空池。
        var empty = HttpGachaRecordEndpoint.Parse("{\"code\":0,\"message\":\"success\",\"data\":[]}");
        Equal(true, empty.IsSuccess);
        Equal(true, empty.IsEmptySuccess);

        // serverId 不匹配。
        var mismatch = HttpGachaRecordEndpoint.Parse(
            "{\"code\":-4,\"message\":\"当前serverId查询不到区服信息!\",\"data\":[]}");
        Equal(GachaQueryResult.ServerMismatch, mismatch.Code);

        // recordId 失效。
        var invalid = HttpGachaRecordEndpoint.Parse(
            "{\"code\":-1,\"message\":\"请求游戏获取日志异常!\",\"data\":[]}");
        Equal(GachaQueryResult.InvalidRecord, invalid.Code);

        // 页面把 200 也当成功，这里折叠为 0。
        Equal(0, HttpGachaRecordEndpoint.Parse("{\"code\":200,\"data\":[]}").Code);
        // 裸数字的池显示文本（实测 8/9/12/13）不能解析失败。
        var numeric = HttpGachaRecordEndpoint.Parse(
            "{\"code\":0,\"data\":[{\"cardPoolType\":8,\"resourceId\":1,\"time\":\"2026-11-01 10:00:00\"}]}");
        Equal(1, numeric.Records.Count);
        Equal("8", numeric.Records[0].CardPoolType);
    }
}
