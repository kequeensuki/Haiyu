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

    private object? _gameExitSource;

    public async Task ObserveGameExitAsync(Waves.Core.Contracts.Events.IGameEventPublisher<Waves.Core.Models.GameContextOutputArgs> source)
    {
        lock (_gate)
            if (_disposed || ReferenceEquals(_gameExitSource, source)) return;
        var subscription = await source.SubscribeAsync(args =>
        {
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_gameExitSource, source)
                    && args.Type == Waves.Core.Models.Enums.GameContextActionType.GameExit)
                    NotifyGameExited();
            }
            return ValueTask.CompletedTask;
        });
        IDisposable? previous;
        lock (_gate)
        {
            previous = _disposed ? subscription : _gameExitSubscription;
            if (!_disposed)
            {
                _gameExitSubscription = subscription;
                _gameExitSource = source;
            }
        }
        previous?.Dispose();
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
