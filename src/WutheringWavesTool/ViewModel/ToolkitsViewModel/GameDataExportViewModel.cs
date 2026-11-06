using System.Collections.ObjectModel;
using ABI.Models;
using ABIRuntime;
using ABIRuntime.Abstractions;
using Haiyu.Common.Contracts;
using Haiyu.Services.Contracts;

namespace Haiyu.ViewModel.ToolkitsViewModel;

/// <summary>
/// 游戏数据导出：调用特权宿主的 GameDataExportService，
/// 观察应用级捕获会话，展示宿主目录中的模块选择及通用导出结果。
///
/// <para>
/// ★ 使用时机（协议决定，必须提示用户）：会话密钥只在游戏登录握手时下发一次，
///   导出必须在<b>游戏启动前</b>开始——先点导出，再启动/重登游戏。
/// </para>
/// </summary>
public sealed partial class GameDataExportViewModel : ViewModelBase
{
    private readonly Services.GameDataExportLauncher _launcher;
    private readonly Action _refreshOnUi;
    private readonly WindowSession _windowSession;
    private readonly IGameDataExportKeyService _keyService;
    private readonly IGameDataExportCatalogService _catalog;
    private readonly IGameDataExportSelectionService _selection;

    public GameDataExportViewModel(IViewFactorys viewFactorys, WindowSession windowSession, IGameDataExportKeyService keyService,
        IGameDataExportCatalogService catalog, IGameDataExportSelectionService selection)
    {
        ViewFactorys = viewFactorys;
        _windowSession = windowSession;
        _keyService = keyService;
        _catalog = catalog;
        _selection = selection;
        _launcher = Instance.Host.Services.GetRequiredService<Services.GameDataExportLauncher>();
        // 窗口仅观察应用会话，不持有 await session 的长期 continuation。
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("导出窗口必须在 UI 线程创建。");
        _refreshOnUi = () =>
        {
            if (dispatcher.HasThreadAccess)
            {
                if (!IsDisposed) SynchronizeSession();
            }
            else dispatcher.TryEnqueue(() => { if (!IsDisposed) SynchronizeSession(); });
        };
        _launcher.ProgressChanged += OnProgressChanged;
        _launcher.StateChanged += OnStateChanged;
        SynchronizeSession();
    }

    private void OnProgressChanged(string message, int percentage) => _refreshOnUi();
    private void OnStateChanged() => _refreshOnUi();

    private void SynchronizeSession()
    {
        IsExporting = _launcher.IsSessionActive;
        foreach (var item in Modules)
        {
            item.IsEnabled = !IsExporting;
            if (IsExporting && _launcher.CurrentModuleIds is { } ids) item.IsSelected = ids.Contains(item.Id, StringComparer.Ordinal);
        }
        if (_launcher.LastProgress is { } progress)
        {
            var business = _launcher.LastBusinessProgress;
            ProgressMessage = business?.Modules is { Length: > 0 } modules ? FormatModules(modules)
                : business?.EventExport is { } stats
                ? progress.Message + "\n" + FormatEventStats(stats)
                : business is { Stage: GameDataExportStage.Monitoring, OutputJsonPath: not null }
                ? $"监控中：玩家 {business.PlayerId}，已更新 {business.UpdateCount} 次\n最新文件：{business.OutputJsonPath}"
                : progress.Message;
            ProgressValue = progress.Percentage;
        }
        else if (IsExporting)
        {
            ProgressMessage = LanguageService.GetString("Toolkit_GameDataExportPreparing");
            ProgressValue = 0;
        }
        var result = _launcher.LastResult;
        HasResult = !IsExporting && result is not null;
        ResultMessage = HasResult ? FormatResult(result!) : string.Empty;
    }

    private static string FormatEventStats(GameDataExportEventStats stats) => string.Format(
        LanguageService.GetString("Toolkit_GameDataExportEventStats"), stats.Received, stats.Written,
        stats.Decoded, stats.Unknown, stats.Failed, stats.Unresolved, stats.ManifestPath);

    private static string FormatResult(GameDataExportResult result)
    {
        if (result.Modules is { Length: > 0 } modules) return result.Message + "\n" + FormatModules(modules) + "\n" + result.CaptureIndexPath;
        if (result.EventExport is { } events)
        {
            var message = result.Message + "\n" + FormatEventStats(events);
            foreach (var player in events.Players)
                message += "\n" + string.Format(LanguageService.GetString("Toolkit_GameDataExportPlayerEvents"),
                    player.PlayerId, player.Written, player.Decoded, player.Unknown, player.Failed);
            if (result.PlayerSummaries is { Length: > 0 })
                message += "\n\n" + LanguageService.GetString("Toolkit_GameDataExportInventorySummaryTitle")
                    + "\n" + FormatResult(result with { EventExport = null });
            return message;
        }
        if (result.PlayerSummaries is { Length: > 0 } summaries)
        {
            var heading = result.Success
                ? string.Format(LanguageService.GetString("Toolkit_GameDataExportSummaryDone"), summaries.Length)
                : result.Message;
            var players = summaries.Select(s => string.Format(
                LanguageService.GetString("Toolkit_GameDataExportPlayerSummary"),
                s.PlayerId, s.UpdateCount, s.NormalItemCount, s.WeaponCount,
                s.PhantomCount, s.ValidTimeItemCount, s.OutputJsonPath));
            return heading + "\n\n" + string.Join("\n\n", players);
        }
        return (result.Success
            ? string.Format(LanguageService.GetString("Toolkit_GameDataExportDone"),
                result.Inventory?.NormalItems.Count ?? 0, result.Inventory?.Weapons.Count ?? 0,
                result.Inventory?.Phantoms.Count ?? 0, result.Inventory?.ValidTimeItems.Count ?? 0)
            : result.Message) + (result.OutputJsonPath is null ? string.Empty : $"\n{result.OutputJsonPath}");
    }

    protected override void OnDisposing()
    {
        _launcher.ProgressChanged -= OnProgressChanged;
        _launcher.StateChanged -= OnStateChanged;
        foreach (var module in Modules) module.PropertyChanged -= OnModuleSelectionChanged;
        base.OnDisposing();
    }

    public IViewFactorys ViewFactorys { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportKeyCommand))]
    [NotifyPropertyChangedFor(nameof(CanChooseExportMode))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial int ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    public partial bool IsLoadingModules { get; private set; }

    public ObservableCollection<GameDataExportModuleSelection> Modules { get; } = [];

    [RelayCommand]
    private async Task LoadModulesAsync()
    {
        if (Modules.Count > 0 || IsDisposed || IsLoadingModules) return;
        IsLoadingModules = true;
        try
        {
            var descriptors = await _catalog.GetAsync(LifetimeToken);
            var selected = await _selection.GetAsync(LifetimeToken);
            if (IsDisposed) return;
            foreach (var descriptor in descriptors)
            {
                var name = LanguageService.GetString(descriptor.NameKey);
                if (string.IsNullOrWhiteSpace(name) || name == descriptor.NameKey) name = descriptor.DisplayName;
                var module = new GameDataExportModuleSelection(descriptor.Id, name,
                    selected is null ? descriptor.DefaultSelected : selected.Contains(descriptor.Id, StringComparer.Ordinal));
                module.PropertyChanged += OnModuleSelectionChanged;
                Modules.Add(module);
            }
            SynchronizeSession();
        }
        catch (OperationCanceledException) when (IsDisposed) { }
        catch (Exception ex) { if (!IsDisposed) { HasResult = true; ResultMessage = ex.Message; } }
        finally { if (!IsDisposed) IsLoadingModules = false; }
    }

    private void OnModuleSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(GameDataExportModuleSelection.IsSelected)) return;
        StartExportCommand.NotifyCanExecuteChanged();
        if (!IsExporting && !IsDisposed)
            _ = SaveSelectionAsync(Modules.Where(module => module.IsSelected).Select(module => module.Id).ToArray());
    }

    private async Task SaveSelectionAsync(string[] ids)
    {
        try { await _selection.SaveAsync(ids); }
        catch (Exception ex)
        {
            if (!IsDisposed) { HasResult = true; ResultMessage = ex.Message; }
        }
    }

    private static string DisplayName(string kind, string value)
    {
        var key = "Toolkit_GameDataExport" + kind + "_" + value;
        var name = LanguageService.GetString(key);
        return string.IsNullOrWhiteSpace(name) || name == key ? value : name;
    }

    private static string FormatModules(IEnumerable<GameDataExportModuleResult> modules) =>
        string.Join("\n\n", modules.Select(FormatModule));

    private static string FormatModule(GameDataExportModuleResult module)
    {
        var lines = new List<string> { module.DisplayName + "：" + DisplayName("State", module.State.ToString()) };
        if (!string.IsNullOrWhiteSpace(module.Error)) lines.Add(module.Error);
        var players = module.Counters.Select(counter => counter.PlayerId)
            .Concat(module.Sources.Select(source => source.PlayerId))
            .Concat(module.Artifacts.Select(artifact => artifact.PlayerId))
            .Where(player => player.HasValue).Distinct().ToArray();
        var currentPlayer = module.Counters.FirstOrDefault(counter => counter.PlayerId is null && counter.Name == "currentPlayer")?.Value;

        foreach (var player in new int?[] { null }.Concat(players))
        {
            var counters = module.Counters.Where(counter => counter.PlayerId == player);
            if (player is null)
            {
                // 玩家行已表达的身份和计数无需再在汇总行重复；多玩家时保留总计。
                counters = counters.Where(counter => counter.Name switch
                {
                    "currentPlayer" => !players.Any(id => id == currentPlayer),
                    "currentPlayerUpdates" => !module.Counters.Any(item => item.PlayerId is not null
                        && item.PlayerId == currentPlayer && item.Name == "updates" && item.Value == counter.Value),
                    "players" => players.Length != 1 || counter.Value != 1,
                    "updates" => players.Length != 1 || !module.Counters.Any(item => item.PlayerId == players[0]
                        && item.Name == "updates" && item.Value == counter.Value),
                    _ => true,
                });
            }
            else
            {
                lines.Add(DisplayName("Counter", player == currentPlayer ? "currentPlayer" : "player") + " " + player);
            }

            var statistics = string.Join("、", counters.Select(counter => DisplayName("Counter", counter.Name) + " " + counter.Value));
            if (statistics.Length > 0) lines.Add(statistics);
            foreach (var sources in module.Sources.Where(source => source.PlayerId == player).GroupBy(source => source.State))
            {
                lines.Add(DisplayName("State", sources.Key) + "："
                    + string.Join("、", sources.Select(source => DisplayName("Source", source.SourceId)).Distinct()));
            }
            lines.AddRange(module.Artifacts.Where(artifact => artifact.PlayerId == player).Select(artifact => artifact.Path).Distinct());
        }
        return string.Join("\n", lines);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    public partial bool IsImportingKey { get; private set; }

    [ObservableProperty]
    public partial string KeyImportMessage { get; private set; } = string.Empty;

    public bool CanChooseExportMode => !IsExporting;

    private bool CanImportKey() => !IsDisposed && !IsExporting && !IsImportingKey;
    private bool CanStartExport() => !IsDisposed && !IsImportingKey && !IsLoadingModules && Modules.Any(module => module.IsSelected);

    /// <summary>窗口只负责选择文件与展示结果，私钥校验和保存由业务服务完成。</summary>
    [RelayCommand(CanExecute = nameof(CanImportKey))]
    private async Task ImportKeyAsync()
    {
        if (!CanImportKey()) return;
        IsImportingKey = true;
        KeyImportMessage = string.Empty;
        try
        {
            var file = await _windowSession.Context.PickersService.GetFileOpenPicker([".pem"]);
            if (file is null || IsDisposed) return;
            // 选择文件期间，游戏自动启动也可能在应用级建立捕获会话。
            if (_launcher.IsSessionActive)
            {
                KeyImportMessage = LanguageService.GetString("Toolkit_GameDataExportKeyBusy");
                return;
            }
            await _keyService.ImportAsync(file.Path, LifetimeToken);
            if (!IsDisposed)
                KeyImportMessage = LanguageService.GetString("Toolkit_GameDataExportKeySaved");
        }
        catch (OperationCanceledException) when (IsDisposed) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
                KeyImportMessage = string.Format(LanguageService.GetString("Toolkit_GameDataExportKeyFailed"), ex.Message);
        }
        finally
        {
            if (!IsDisposed) IsImportingKey = false;
        }
    }

    /// <summary>
    /// 开始导出（v2：经 Launcher 发起/复用会话）。
    /// 若自动启动已建立会话，进入监控显示态而非重复发起。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private void StartExport()
    {
        if (!CanStartExport())
            return;
        try
        {
            _launcher.TryStart(out _, new GameDataExportRequest(GameDataExportScope.None,
                Modules.Where(module => module.IsSelected).Select(module => module.Id).ToArray()));
            SynchronizeSession();
        }
        catch (Exception ex)
        {
            HasResult = true;
            ResultMessage = ex.Message;
        }
    }

    /// <summary>只等待窗口生命周期内的停止操作；关闭窗口不取消后台监控。</summary>
    [RelayCommand]
    private async Task StopExportAsync()
    {
        try
        {
            var result = await _launcher.StopAsync().WaitAsync(LifetimeToken);
            if (!IsDisposed)
            {
                SynchronizeSession();
                if (_launcher.IsSessionActive && result is { Success: false })
                {
                    HasResult = true;
                    ResultMessage = result.Message;
                }
            }
        }
        catch (OperationCanceledException) when (IsDisposed) { }
    }

    /// <summary>打开导出目录（Windows 资源管理器）。</summary>
    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Waves",
            "GameDataExport"
        );
        if (Directory.Exists(folder))
        {
            WindowExtension.ShellExecute(
                IntPtr.Zero,
                "open",
                folder,
                string.Empty,
                string.Empty,
                WindowExtension.SW_SHOWNORMAL
            );
        }
    }

}
