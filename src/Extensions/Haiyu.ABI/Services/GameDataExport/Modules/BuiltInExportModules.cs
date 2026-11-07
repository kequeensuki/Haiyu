using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Gacha;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>组合根与旧请求兼容边界；新增模块只需在此注册。</summary>
internal static class BuiltInExportModules
{
    /// <param name="gachaRequest">
    /// 可选的显式抽卡参数；生产默认从已验证会话消息取得。为 null 时仍注册 <c>gacha</c> 模块，
    /// 但没有 record_id 时模块自身报告 NoData，不发起请求也不创建空文件。
    /// </param>
    /// <param name="gachaEndpoint">抽卡 HTTP 端点；测试注入假端点，生产传 null 由服务创建。</param>
    internal static ExportModuleRegistry Create(Func<GameDataExportResult, DateTimeOffset, string?>? legacyWrite = null,
        Func<EventExportSession>? legacyEvents = null,
        GameGachaRequest? gachaRequest = null,
        Func<IGachaRecordEndpoint>? gachaEndpoint = null) => new([
        new(InventoryExportModule.Definition, context => new InventoryExportModule(context, legacyWrite)),
        new(ServerMessagesExportModule.Definition, context => new ServerMessagesExportModule(context, legacyEvents?.Invoke())),
        new(ItemChangeMessages.Definition, context => new ServerMessagesExportModule(context,
            descriptor: ItemChangeMessages.Definition, messageIds: ItemChangeMessages.MessageIds)),
        // 抽卡记录：唯一主动请求模块，参数缺失时自行 NoData。端点默认按需创建，避免无谓的 HttpClient。
        new(GachaRecordExportModule.Definition, context => new GachaRecordExportModule(context,
            gachaRequest ?? new GameGachaRequest(), gachaEndpoint?.Invoke() ?? HttpGachaRecordEndpoint.Shared))
    ]);

    internal static string[] Select(GameDataExportRequest request)
    {
        if (request.ModuleIds is not null) return request.ModuleIds;
        if ((request.Scope & ~(GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages
            | GameDataExportScope.Gacha)) != 0)
            throw new ArgumentException("不支持的旧导出范围。");
        var ids = new List<string>();
        if (request.Scope.HasFlag(GameDataExportScope.Inventory)) ids.Add(InventoryExportModule.Definition.Id);
        if (request.Scope.HasFlag(GameDataExportScope.AllServerMessages)) ids.Add(ServerMessagesExportModule.Definition.Id);
        if (request.Scope.HasFlag(GameDataExportScope.Gacha)) ids.Add(GachaRecordExportModule.Definition.Id);
        return ids.ToArray();
    }
}
