using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.ABI.Services.GameDataExport.Modules;

namespace Haiyu.ABI.Services.GameDataExport;

public sealed class GameDataExportModuleCatalogService : IPrivilegedService<GameDataExportModuleCatalogRequest, GameDataExportProgress, GameDataExportModuleCatalogResult>
{
    public PrivilegedServiceContract<GameDataExportModuleCatalogRequest, GameDataExportProgress, GameDataExportModuleCatalogResult> Contract => ABIRuntime.Contract.GameDataExportModuleCatalogContract;
    public ValueTask<GameDataExportModuleCatalogResult> ExecuteAsync(GameDataExportModuleCatalogRequest request,
        IProgress<GameDataExportProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GameDataExportModuleCatalogResult(BuiltInExportModules.Create().Describe()));
    }
}
