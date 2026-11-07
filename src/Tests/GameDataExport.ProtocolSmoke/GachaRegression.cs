using System.Net;
using System.Text;
using System.Text.Json;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Domains.Gacha;
using Haiyu.ABI.Services.GameDataExport.Gacha;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;
using MemoryPack;

namespace GameDataExportRegression;

/// <summary>托管与原生共用的匿名反例；只替换 HTTP 传输，不请求生产接口。</summary>
internal static class GachaRegression
{
    private const string ValidRecord = "{\"cardPoolType\":\"活动\",\"resourceId\":1101,\"qualityLevel\":5,\"resourceType\":\"角色\",\"name\":\"甲\",\"count\":1,\"time\":\"2026-11-01 10:00:00\"}";
    private static string Success(string records = "") => "{\"code\":0,\"data\":[" + records + "]}";
    private static string Failure(int code) => "{\"code\":" + code + ",\"data\":[]}";
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed record Query(string Player, string Record, string Pool, int Type, string Server, string Language);
    private sealed class Handler(Func<Query, string> respond) : HttpMessageHandler
    {
        internal readonly List<Query> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            var query = new Query(root.GetProperty("playerId").GetString()!, root.GetProperty("recordId").GetString()!,
                root.GetProperty("cardPoolId").GetString()!, root.GetProperty("cardPoolType").GetInt32(),
                root.GetProperty("serverId").GetString()!, root.GetProperty("languageCode").GetString()!);
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/gacha/record/query", "HTTP request route");
            Require(request.RequestUri!.Host == (query.Server == GachaServerIds.Global ? "gmserver-api.aki-game2.net" : "gmserver-api.aki-game2.com"), "HTTP region domain");
            Calls.Add(query);
            return new(HttpStatusCode.OK) { Content = new StringContent(respond(query), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "Haiyu.GachaRegression." + Guid.NewGuid().ToString("N"));
        internal readonly Handler Handler;
        private readonly HttpClient _client;
        internal readonly ExportCoordinator Coordinator;
        internal readonly ExportCaptureContext Context;
        private readonly ProtocolJsonDecoder _decoder = new(ProtocolCatalog.Default.Value);
        internal Fixture(Func<Query, string> respond, GameGachaRequest? parameters = null)
        {
            Handler = new(respond); _client = new(Handler);
            Context = new(new JsonExportStore(Root));
            Coordinator = new(Context, BuiltInExportModules.Create(gachaRequest: parameters,
                gachaEndpoint: () => new HttpGachaRecordEndpoint(_client)), ["gacha"]);
        }
        internal void Bind(string session, int player, bool current = true) => Coordinator.SessionChanged(
            new(ExportSessionChangeKind.IdentityResolved, session, null, player, 1, IsCurrent: current));
        internal void Login(string session) => Coordinator.SessionChanged(new(ExportSessionChangeKind.LoginStarted, session, null, null, 1));
        internal void Feed(string session, int? player, string record, bool current = true, bool verified = true, bool failure = false)
        {
            var payload = new GachaInfoResponse { RecordId = record, ErrorCode = failure ? (ErrorCode)1 : 0,
                GachaInfos = { new GachaInfo { ResourcesId = "fixture-pool" } } }.ToByteArray();
            Observe(session, player, payload, current, verified);
        }
        internal void Observe(string session, int? player, byte[] bytes, bool current = true, bool verified = true) =>
            Coordinator.Observe(new(new(GatewayMessageType.Push, 4, 1, 0, WavesMessageIds.GachaInfoResponse, 0, bytes), bytes,
                player, session, 1, null, _decoder.Decode(WavesMessageIds.GachaInfoResponse, bytes), current, verified));
        internal string PathFor(int player) => Context.Output.Resolve(GachaRecordStore.RelativePath(player));
        internal async Task<(GameDataExportModuleResult Module, GameDataExportResult Result)> Finish()
        {
            var modules = await Coordinator.FinishAsync(new(null, false));
            return (modules.Single(), Coordinator.LegacyResult(modules));
        }
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync(); _client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    internal static async Task ProductionAsync()
    {
        await using (var f = new Fixture(_ => Success(ValidRecord)))
        {
            f.Bind("a-old", 42); f.Feed("a-old", 42, "old");
            f.Login("a-new"); f.Bind("a-new", 42); f.Feed("a-new", 42, "fresh");
            f.Feed("a-old", 42, "late", current: false);
            f.Feed("unverified-other", 44, "untrusted", verified: false);
            f.Feed("b", null, "b-token"); f.Bind("b", 43, current: false);
            f.Feed("b", 43, "bad", failure: true);
            var (module, result) = await f.Finish();
            Require(result.Success && module.State == GameDataExportModuleState.Completed, "production completion");
            Require(module.Artifacts.Length == 2 && f.Handler.Calls.Count == 20, "all accounts/artifacts");
            Require(f.Handler.Calls.Where(q => q.Player == "42").All(q => q.Record == "fresh" && q.Pool == "fixture-pool"), "latest session credentials");
            Require(f.Handler.Calls.Where(q => q.Player == "43").All(q => q.Record == "b-token"), "identity binding and failed response");
            foreach (var player in new[] { 42, 43 })
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(f.PathFor(player)));
                Require(json.RootElement.GetProperty("playerId").GetInt32() == player &&
                    json.RootElement.GetProperty("totals").GetProperty("count").GetInt32() == 10, "per-account real file");
            }
            var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options), ABIMemoryPack.Options)!;
            Require(round.Gacha?.RecordCount == 10, "non-null gacha ABI roundtrip");
            using var index = JsonDocument.Parse(File.ReadAllBytes(f.Context.IndexPath));
            Require(index.RootElement.GetProperty("state").GetString() == "completed" &&
                index.RootElement.GetProperty("modules")[0].GetProperty("artifacts").GetArrayLength() == 2, "index matches files");
        }
        await using (var f = new Fixture(_ => Success()))
        {
            f.Bind("old", 42); f.Feed("old", 42, "old"); f.Login("new"); f.Bind("new", 42);
            f.Feed("old", 42, "late", current: false);
            var (module, _) = await f.Finish();
            Require(module.State == GameDataExportModuleState.NoData && f.Handler.Calls.Count == 0 && !File.Exists(f.PathFor(42)), "relogin must not reuse old token");
        }
        await using (var f = new Fixture(_ => Success()))
        {
            // 独立 wire 向量：field 15(string) = "wire"，成功码缺省为 0。
            f.Observe("wire-session", null, [122, 4, 119, 105, 114, 101]);
            f.Bind("wire-session", 45);
            Require((await f.Finish()).Result.Success && f.Handler.Calls.All(q => q.Record == "wire"), "independent credential wire");
        }
    }

    internal static async Task FailureStatesAsync()
    {
        foreach (var code in new[] { -1, 500 })
        {
            await using var f = new Fixture(_ => Failure(code), new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1, 2]));
            var (module, result) = await f.Finish();
            Require(module.State == GameDataExportModuleState.Failed && !result.Success && !File.Exists(f.PathFor(42)) && module.Artifacts.Length == 0, "all failed must not write or succeed");
            Require(f.Handler.Calls.Count == (code == -1 ? 1 : 2), "terminal credential error stops other pools");
        }
        await using (var f = new Fixture(q => q.Type == 2 ? Failure(500) : Success(ValidRecord), new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1, 2])))
        {
            var (module, result) = await f.Finish();
            Require(module.State == GameDataExportModuleState.Incomplete && !result.Success && module.Artifacts.Length == 1, "partial result state");
            using var json = JsonDocument.Parse(File.ReadAllBytes(f.PathFor(42)));
            Require(json.RootElement.GetProperty("query").GetProperty("failedPoolTypes")[0].GetInt32() == 2 &&
                json.RootElement.GetProperty("totals").GetProperty("count").GetInt32() == 1, "partial file metadata");
        }
        await using (var f = new Fixture(_ => Success(), new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1])))
        {
            Require((await f.Finish()).Result.Success && File.Exists(f.PathFor(42)), "empty pool is valid success");
            var before = File.ReadAllBytes(f.PathFor(42));
            Require((await f.Finish()).Result.Success && before.SequenceEqual(File.ReadAllBytes(f.PathFor(42))) && f.Handler.Calls.Count == 1, "complete is idempotent");
        }
    }

    internal static async Task CorruptHistoryAsync()
    {
        var query = new GameGachaQuery(GachaServerIds.Cn, "cn", "fixture", "", "zh-Hans", "given", [1], []);
        var normalized = GachaRecordNormalizer.Normalize(HttpGachaRecordEndpoint.Parse(Success(ValidRecord)).Records, 1);
        var valid = JsonSerializer.Serialize(GachaRecordStore.Build(42, [], normalized, query, DateTimeOffset.UnixEpoch), ABIJsonContext.Default.GameGachaData);
        var badInputs = new[] {
            "{broken", "[]", valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"),
            valid.Replace("\"playerId\":42", "\"playerId\":43"), valid.Replace("\"pools\":", "\"lostPools\":"),
            valid.Replace("\"resourceId\":1101", "\"resourceId\":\"1101\""),
            valid.Replace("\"count\":1", "\"count\":2"), valid.Replace("\"name\":", "\"missingName\":"),
            "{\"schemaVersion\":1,\"playerId\":42,\"totals\":{\"count\":1},\"pools\":[{\"cardPoolType\":1,\"count\":1,\"items\":\"damaged\"}]}"
        };
        foreach (var bad in badInputs)
        {
            Require(bad != valid, "corruption fixture must mutate input");
            await using var f = new Fixture(_ => Success(), new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1]));
            Directory.CreateDirectory(Path.GetDirectoryName(f.PathFor(42))!);
            await File.WriteAllTextAsync(f.PathFor(42), bad);
            var before = File.ReadAllBytes(f.PathFor(42));
            var (module, result) = await f.Finish();
            Require(!result.Success && module.State == GameDataExportModuleState.Failed && before.SequenceEqual(File.ReadAllBytes(f.PathFor(42))), "corrupt history must remain byte-identical");
        }
        await using (var f = new Fixture(_ => Success(), new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1])))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f.PathFor(42))!); await File.WriteAllTextAsync(f.PathFor(42), valid);
            Require((await f.Finish()).Result.Success && GachaRecordStore.LoadExisting(f.PathFor(42), 42).Count == 1, "valid older history retained on empty query");
        }
    }

    internal static Task LocaleIdentityAsync()
    {
        var cn = HttpGachaRecordEndpoint.Parse(Success(ValidRecord + "," + ValidRecord)).Records;
        var en = cn.Select(r => r with { Name = "A", ResourceType = "Role" }).ToArray();
        var old = GachaRecordNormalizer.Normalize(cn, 1);
        var query = new GameGachaQuery(GachaServerIds.Cn, "cn", "fixture", "", "en-US", "given", [1], []);
        var data = GachaRecordStore.Build(42, old, GachaRecordNormalizer.Normalize(en, 1).Reverse(), query, DateTimeOffset.UnixEpoch);
        Require(data.Totals.Count == 2, "language change must not double records or swallow same-second duplicates");
        Require(GachaRecordStore.Build(42, old, GachaRecordNormalizer.Normalize(en.Take(1), 1), query, DateTimeOffset.UnixEpoch).Totals.Count == 2, "shrunk window retains duplicates");
        var collection = new GachaRecordCollection(old);
        Require(collection.Remove(old[0].Key).Status == ChangeStatus.Invalid && collection.Update(old[0].Key, new GachaRecordAdded(old[0])).Status == ChangeStatus.Invalid, "append-only contract");
        return Task.CompletedTask;
    }

    internal static async Task CacheRecoveryAsync()
    {
        await using (var f = new Fixture(q => q.Server == GachaServerIds.Cn ? Failure(-4) : Success(ValidRecord), new(42, "fixture", PoolTypes: [2])))
        {
            var cache = new GachaServerCache(); cache.Set(42, new(GachaServerIds.Cn, "zh-Hans", DateTimeOffset.UnixEpoch));
            var path = f.Context.Output.Resolve(GachaServerCache.RelativePath(42)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, cache.Encode(DateTimeOffset.UnixEpoch));
            Require((await f.Finish()).Result.Success, "cached mismatch must recover now");
            Require(f.Handler.Calls.Count == 3 && f.Handler.Calls[1].Server == GachaServerIds.Global, "bounded reprobe");
            using var json = JsonDocument.Parse(File.ReadAllBytes(f.PathFor(42)));
            Require(json.RootElement.GetProperty("pools").GetArrayLength() == 1 && json.RootElement.GetProperty("pools")[0].GetProperty("cardPoolType").GetInt32() == 2, "probe pool must not leak into requested pool set");
            Require(GachaServerCache.Parse(File.ReadAllBytes(path)).TryGet(42)!.ServerId == GachaServerIds.Global, "updated cache");
        }
        await using (var f = new Fixture(_ => Failure(-4), new(42, "fixture", PoolTypes: [1])))
        {
            var cache = new GachaServerCache(); cache.Set(42, new(GachaServerIds.Cn, "zh-Hans", DateTimeOffset.UnixEpoch));
            var path = f.Context.Output.Resolve(GachaServerCache.RelativePath(42)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, cache.Encode(DateTimeOffset.UnixEpoch));
            Require(!(await f.Finish()).Result.Success && f.Handler.Calls.Count == 3 && !File.Exists(f.PathFor(42)), "both servers rejected must terminate");
        }
    }

    internal static async Task MalformedResponseAsync()
    {
        foreach (var body in new[] { "{", "[]", "{\"code\":0}", "{\"code\":0,\"data\":null}", "{\"code\":0,\"data\":\"not-an-array\"}",
            Success("null"), Success("{}"), Success(ValidRecord.Replace("\"count\":1", "\"count\":\"bad\"")), "{\"code\":\"0\",\"data\":[]}" })
        {
            var threw = false; try { HttpGachaRecordEndpoint.Parse(body); } catch (InvalidDataException) { threw = true; }
            Require(threw, "malformed successful response must be rejected");
            await using var f = new Fixture(_ => body, new(42, "fixture", ServerIds: [GachaServerIds.Cn], PoolTypes: [1]));
            Require(!(await f.Finish()).Result.Success && !File.Exists(f.PathFor(42)), "malformed HTTP cannot create successful empty ledger");
        }
        Require(!HttpGachaRecordEndpoint.Parse("{\"code\":-1,\"message\":\"成功\"}").IsSuccess, "failure code wins over message");
    }
}
