using ABI.Models;

namespace Haiyu.Services.Contracts;

public interface IGameDataExportSelectionService
{
    Task<string[]?> GetAsync(CancellationToken token = default);
    Task SaveAsync(string[] moduleIds);
    Task<GameDataExportRequest?> CreateRequestAsync(CancellationToken token = default);
}
