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
    /// 开始导出。执行流程：
    /// 等待游戏登录（用户此时启动/重登游戏）→ 拿会话密钥 → 解密背包 → 落盘 JSON。
    /// 密钥缺失时返回的引导文案来自特权服务（密钥管理方案 §4.4）。
    /// </summary>
    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (IsExporting)
        {
            return;
        }

        // Initialize 自带弹窗兜底，但失败（如 ABI 包未导入）会抛异常——
        // 必须接住转为页面内提示，不能让它炸成全局错误对话框
        try
        {
            var initialized = await AppContext.ABIRuntimeService.Initialize(
                Waves.Settings.AppSettings.ABIRuntimeSavePath
            );
            if (!initialized || AppContext.ABIRuntimeService.Runtime is null)
            {
                HasResult = true;
                ResultMessage = LanguageService.GetString("Toolkit_GameDataExportAbiMissing");
                return;
            }
        }
        catch (Exception ex)
        {
            HasResult = true;
            ResultMessage = LanguageService.GetString("Toolkit_GameDataExportAbiMissing")
                + $"\n{ex.Message}";
            return;
        }

        IsExporting = true;
        HasResult = false;
        ProgressValue = 0;
        ResultMessage = string.Empty;
        ProgressMessage = LanguageService.GetString("Toolkit_GameDataExportPreparing");

        try
        {
            var progress = new Progress<IPrivilegedProgress<GameDataExportProgress>>(
                report =>
                {
                    if (report.Data is not { } data)
                    {
                        return;
                    }

                    ProgressValue = report.Percentage;
                    ProgressMessage = data.Message;
                }
            );

            var result = await AppContext.ABIRuntimeService.Runtime.InvokeAsync(
                Contract.GameDataExportContract,
                new GameDataExportRequest(GameDataExportScope.Inventory),
                progress,
                CTS.Token
            );

            if (result.Data is { } export)
            {
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
            else
            {
                HasResult = true;
                ResultMessage = result.Message;
            }
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

    /// <summary>停止导出（特权服务会保留已抓到的数据并尽量完成解析）。</summary>
    [RelayCommand]
    private void StopExport()
    {
        CTS.Cancel();
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
