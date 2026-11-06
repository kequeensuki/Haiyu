using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Events;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>组合根与旧请求兼容边界；新增模块只需在此注册。</summary>
internal static class BuiltInExportModules
{
    internal static ExportModuleRegistry Create(Func<GameDataExportResult, DateTimeOffset, string?>? legacyWrite = null,
        Func<EventExportSession>? legacyEvents = null) => new([
        new(InventoryExportModule.Definition, context => new InventoryExportModule(context, legacyWrite)),
        new(ServerMessagesExportModule.Definition, context => new ServerMessagesExportModule(context, legacyEvents?.Invoke()))
    ]);

    internal static string[] Select(GameDataExportRequest request)
    {
        if (request.ModuleIds is not null) return request.ModuleIds;
        if ((request.Scope & ~(GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages)) != 0)
            throw new ArgumentException("不支持的旧导出范围。");
        var ids = new List<string>();
        if (request.Scope.HasFlag(GameDataExportScope.Inventory)) ids.Add(InventoryExportModule.Definition.Id);
        if (request.Scope.HasFlag(GameDataExportScope.AllServerMessages)) ids.Add(ServerMessagesExportModule.Definition.Id);
        return ids.ToArray();
    }
}
