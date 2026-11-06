using ABI.Models;
using Haiyu.Services.Contracts;

internal sealed class FakeCatalog : IGameDataExportCatalogService
{
    public Task<GameDataExportModuleDescriptor[]> GetAsync(CancellationToken token = default) => Task.FromResult<GameDataExportModuleDescriptor[]>(
        [new("inventory", "inventory", "背包", true, true), new("server-messages", "server-messages", "消息", false, false),
         new("future", "future", "新模块", false, false)]);
}
