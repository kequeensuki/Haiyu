using ABI.Models;
using ABIRuntime.Abstractions;

namespace Haiyu.Services;

public sealed partial class GameDataExportLauncher
{
    public GameDataExportLauncher() : this(InvokeExportAsync, IsGameRunning) { }

    private static async Task<GameDataExportResult> InvokeExportAsync(
        IProgress<IPrivilegedProgress<GameDataExportProgress>> progress, CancellationToken token)
    {
        var runtime = Instance.Host.Services.GetRequiredService<ABIRuntimeService>();
        var initialized = await runtime.Initialize(Waves.Settings.AppSettings.ABIRuntimeSavePath)
            .WaitAsync(token).ConfigureAwait(false);
        if (!initialized || runtime.Runtime is null)
            return Fail(LanguageService.GetString("Toolkit_GameDataExportAbiMissing"));
        var result = await runtime.Runtime.InvokeAsync(ABIRuntime.Contract.GameDataExportContract,
            new GameDataExportRequest(GameDataExportScope.Inventory), progress, token).ConfigureAwait(false);
        return result.Data ?? Fail(result.Message);
    }

    private static bool IsGameRunning()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Client-Win64-Shipping");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch
        {
            // 无法确认进程已退出时保留会话，避免误结束仍运行的游戏。
            return true;
        }
    }
}
