using ABI.Models;

namespace Haiyu.Services.Contracts;

public interface IGameDataExportCatalogService
{
    Task<GameDataExportModuleDescriptor[]> GetAsync(CancellationToken token = default);
}
