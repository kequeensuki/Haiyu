using ABI.Models;
using ABIRuntime;
using ABIRuntime.Abstractions;
using Haiyu.Common.Contracts;

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
    public GameDataExportViewModel(IViewFactorys viewFactorys)
    {
        ViewFactorys = viewFactorys;
    }

    public IViewFactorys ViewFactorys { get; }

    [ObservableProperty]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial int ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>
    /// 开始导出（v2：经 Launcher 发起/复用会话）。
    /// 若自动启动已建立会话，进入监控显示态而非重复发起。
    /// </summary>
    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (IsExporting)
        {
            return;
        }

        IsExporting = true;
        HasResult = false;
        ProgressValue = 0;
        ResultMessage = string.Empty;
        ProgressMessage = LanguageService.GetString("Toolkit_GameDataExportPreparing");

        try
        {
            var launcher = Instance.Host.Services.GetRequiredService<Services.GameDataExportLauncher>();

            // 幂等：已有会话（自动启动）直接进入监控显示，不重复发起
            launcher.TryStart(out var session);

            // 会话完成（v2 语义：收尾时才返回最终结果）
            var export = await session;

            HasResult = true;
            ResultMessage = export.Success
                ? string.Format(
                    LanguageService.GetString("Toolkit_GameDataExportDone"),
                    export.Inventory?.NormalItems.Count ?? 0,
                    export.Inventory?.Weapons.Count ?? 0,
                    export.Inventory?.Phantoms.Count ?? 0,
                    export.Inventory?.ValidTimeItems.Count ?? 0
                ) + (export.OutputJsonPath is null
                    ? string.Empty
                    : $"\n{export.OutputJsonPath}")
                : export.Message;
        }
        catch (OperationCanceledException)
        {
            HasResult = true;
            ResultMessage = LanguageService.GetString("Toolkit_GameDataExportStopped");
        }
        catch (Exception ex)
        {
            HasResult = true;
            ResultMessage = ex.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>停止导出（宿主收尾落盘返回最新快照；会话由 Launcher 管理）。</summary>
    [RelayCommand]
    private async Task StopExportAsync()
    {
        var launcher = Instance.Host.Services.GetRequiredService<Services.GameDataExportLauncher>();
        await launcher.StopAsync();
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

    private async Task ShowTipAsync(string message, Symbol symbol)
    {
        await AppContext.WindowManager.Shell.TipShow.ShowMessageAsync(message, symbol);
    }
}
