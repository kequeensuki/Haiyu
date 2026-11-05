using ABI.Models;
using ABIRuntime;
using ABIRuntime.Abstractions;
using Haiyu.Common.Contracts;
using Haiyu.Services.Contracts;

namespace Haiyu.ViewModel.ToolkitsViewModel;

/// <summary>
/// 游戏数据导出：调用特权宿主的 GameDataExportService，
/// 抓取游戏登录流量并导出背包数据（普通道具 / 武器 / 声骸 / 限时道具）。
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

    public GameDataExportViewModel(IViewFactorys viewFactorys, WindowSession windowSession, IGameDataExportKeyService keyService)
    {
        ViewFactorys = viewFactorys;
        _windowSession = windowSession;
        _keyService = keyService;
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
        if (_launcher.LastProgress is { } progress)
        {
            var business = _launcher.LastBusinessProgress;
            ProgressMessage = business is { Stage: GameDataExportStage.Monitoring, OutputJsonPath: not null }
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

    private static string FormatResult(GameDataExportResult result)
    {
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
        base.OnDisposing();
    }

    public IViewFactorys ViewFactorys { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportKeyCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(ImportKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    public partial bool IsImportingKey { get; private set; }

    [ObservableProperty]
    public partial string KeyImportMessage { get; private set; } = string.Empty;

    private bool CanImportKey() => !IsDisposed && !IsExporting && !IsImportingKey;
    private bool CanStartExport() => !IsDisposed && !IsImportingKey;

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
        if (IsDisposed || IsImportingKey)
            return;
        try
        {
            _launcher.TryStart(out _);
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
