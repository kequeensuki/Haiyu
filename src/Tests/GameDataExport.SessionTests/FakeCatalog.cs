using ABI.Models;
using Haiyu.Services.Contracts;

internal sealed class FakeCatalog : IGameDataExportCatalogService
{
    internal int Calls { get; private set; }
    public Task<GameDataExportModuleDescriptor[]> GetAsync(CancellationToken token = default)
    {
        Calls++;
        return Task.FromResult<GameDataExportModuleDescriptor[]>(
        [new("inventory", "inventory", "背包", true, true), new("server-messages", "server-messages", "消息", false, false),
         new("future", "future", "新模块", false, false)]);
    }
}

internal static class FakeSelections
{
    internal static Haiyu.Services.GameDataExportSelectionService Create(FakeCatalog? catalog = null)
    {
        List<string>? saved = null;
        return new(_ => Task.FromResult(saved?.ToList()), ids => { saved = ids.ToList(); return Task.CompletedTask; }, catalog ?? new());
    }
}
