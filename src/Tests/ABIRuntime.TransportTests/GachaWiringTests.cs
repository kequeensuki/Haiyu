using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Modules;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    /// <summary>
    /// 抽卡模块必须在生产组合根里真实注册：目录可见、可选择、可创建，
    /// 且缺少 record_id 时报告 NoData 而不是抛错或伪造文件。
    /// </summary>
    private static async Task TestGachaModuleWiringAsync()
    {
        var descriptors = BuiltInExportModules.Create().Describe();
        Equal(true, descriptors.Any(descriptor => descriptor.Id == "gacha"));

        // 通过目录选择该模块后必须能真正创建（否则运行时抛"未知导出模块"）。
        var ids = BuiltInExportModules.Select(new GameDataExportRequest(GameDataExportScope.Gacha));
        Equal(1, ids.Length);
        Equal("gacha", ids[0]);

        var root = Path.Combine(Path.GetTempPath(), "Haiyu.GachaWiring." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new Haiyu.ABI.Services.GameDataExport.Storage.JsonExportStore(root);
            var context = new Haiyu.ABI.Services.GameDataExport.Modules.ExportCaptureContext(store);
            var endpoint = new FakeGachaEndpoint();
            var registry = BuiltInExportModules.Create(gachaEndpoint: () => endpoint);

            // 1) 缺 record_id：报告 NoData，且绝不发起网络请求、不产生文件。
            await using (var missing = new Haiyu.ABI.Services.GameDataExport.Modules.GachaRecordExportModule(
                context, new GameGachaRequest(123), endpoint))
            {
                var result = await missing.CompleteAsync(new ExportStopContext(null, false));
                Equal(GameDataExportModuleState.NoData, result.State);
                Equal(0, endpoint.Calls.Count);
            }

            // 2) 有 record_id 但 playerId 为 0：同样不请求。
            await using (var noPlayer = new Haiyu.ABI.Services.GameDataExport.Modules.GachaRecordExportModule(
                context, new GameGachaRequest(0, "record"), endpoint))
            {
                Equal(GameDataExportModuleState.NoData, (await noPlayer.CompleteAsync(new ExportStopContext(null, false))).State);
                Equal(0, endpoint.Calls.Count);
            }

            // 3) 参数齐备：走探测并落盘到 <playerId>/gacha/record.json。
            endpoint.Respond = (serverId, _) => serverId == Haiyu.ABI.Services.GameDataExport.Gacha.GachaServerIds.Cn
                ? (0, [GachaWire("2026-11-01 10:00:00", 1101, 5, "角色", "五星")])
                : (Haiyu.ABI.Services.GameDataExport.Gacha.GachaQueryResult.ServerMismatch, []);
            await using (var ready = new Haiyu.ABI.Services.GameDataExport.Modules.GachaRecordExportModule(
                context, new GameGachaRequest(4242, "record-x", "", "zh-Hans"), endpoint))
            {
                var result = await ready.CompleteAsync(new ExportStopContext(null, false));
                Equal(GameDataExportModuleState.Completed, result.State);
                await store.FlushAsync();
                var expected = Path.Combine(root, "4242", "gacha", "record.json");
                Equal(true, File.Exists(expected));
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(expected));
                // 探测覆盖池 1，其余 9 个池各返回 1 条 → 共 10 条、10 个池。
                Equal(10, document.RootElement.GetProperty("totals").GetProperty("count").GetInt32());
                Equal(10, document.RootElement.GetProperty("totals").GetProperty("poolCount").GetInt32());
                Equal("cn", document.RootElement.GetProperty("query").GetProperty("serverArea").GetString());
                Equal("probe", document.RootElement.GetProperty("query").GetProperty("serverResolvedBy").GetString());
                // 探测的池 1 不应被重复请求：一次探测 + 9 个其余池。
                Equal(10, endpoint.Calls.Count);
                Equal(1, endpoint.Calls.Count(call => call.PoolType == 1));
                // 池 1 记录带官方可读标签，而不是接口返回的文本。
                var item = document.RootElement.GetProperty("pools")[0].GetProperty("items")[0];
                Equal("角色活动唤取", item.GetProperty("poolLabel").GetString());
                // 缓存与记录同目录，每玩家自包含。
                Equal(true, File.Exists(Path.Combine(root, "4242", "gacha", "servers.json")));
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
