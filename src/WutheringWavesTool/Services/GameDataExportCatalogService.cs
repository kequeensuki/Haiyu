using ABI.Models;
using Haiyu.Services.Contracts;

namespace Haiyu.Services;

/// <summary>从宿主组合根读取模块目录；界面无需维护第二份模块清单。</summary>
public sealed class GameDataExportCatalogService(ABIRuntimeService runtime) : IGameDataExportCatalogService
{
    public async Task<GameDataExportModuleDescriptor[]> GetAsync(CancellationToken token = default)
    {
        if (!await runtime.Initialize(Waves.Settings.AppSettings.ABIRuntimeSavePath).WaitAsync(token).ConfigureAwait(false)
            || runtime.Runtime is null) throw new InvalidOperationException(LanguageService.GetString("Toolkit_GameDataExportAbiMissing"));
        var result = await runtime.Runtime.InvokeAsync(ABIRuntime.Contract.GameDataExportModuleCatalogContract,
            new GameDataExportModuleCatalogRequest(), cancellationToken: token).ConfigureAwait(false);
        return result.Data?.Modules ?? throw new InvalidOperationException(result.Message);
    }
}
