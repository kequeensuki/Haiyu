using Haiyu.Models.Enums;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Waves.Core.GameContext.ContextsV2.Punish;
using Waves.Core.GameContext.ContextsV2.Waves;
using Waves.Core.Models.Enums;
using Waves.Core.Models.Options;

namespace Haiyu.ViewModel.GameViewModels;

partial class KuroGameContextViewModelV2
{
    [RelayCommand]
    async Task UpdateGameAsync()
    {
        if (_buttonAction == ButtonActionType.StartGame)
        {
            if (GameContext.GameType == GameType.Waves)
            {
                try
                {
                    await Instance.Host.Services.GetRequiredService<Services.GameDataExportLauncher>()
                        .ObserveGameExitAsync(GameContext.GameEventPublisher);
                }
                catch (Exception ex) { Logger.WriteError($"订阅游戏退出通知失败：{ex.Message}"); }
            }
            // ★ 游戏数据导出（v2 自动启动）：先让监控会话跑起来再启动游戏——
            //   会话密钥只在登录握手时下发，抓包必须先于游戏启动。
            //   失败静默降级（TipShow 提示，不阻断游戏启动）。
            if (GameContext.GameType == GameType.Waves && await AppSettings.GetGameDataExportEnableAsync() is true)
            {
                await StartGameDataExportSessionAsync();
            }

            if ((await GameContext.StartGameAsync()))
            {
                this.WallpaperService.PauseVideo();
            }
            if((await AppSettings.GetStartGameAllowCloseMainAsync()) == true)
            {
                this.AppContext.WindowManager.Shell.Hide();
            }
        }
        if (_buttonAction == ButtonActionType.PrepareUpdate)
        {
            var localVersion = await GameContext.GameLocalConfig.GetConfigAsync(
                GameLocalSettingName.LocalGameVersion
            );
            var result = await WindowManager.Shell.DialogManager.ShowUpdateGameDialogAsyncV2(
                this.GameContext.ContextName,
                UpdateGameType.UpdateGame, ResourceParameter
            );

            if (result == null)
                return;
            if (result.IsOk == false)
            {
                return;
            }
            _ =  Task.Run(async () => await GameContext.UpdateGameResourceAsync(result.DiffSavePath, result.Parameter));
        }
        if (_buttonAction == ButtonActionType.InstallPreDownload)
        {
            var diffDone = await GameContext.GameLocalConfig.GetConfigAsync(
                GameLocalSettingName.ProdDownloadFolderDone
            );
            var diffPath = await GameContext.GameLocalConfig.GetConfigAsync(
                GameLocalSettingName.ProdDownloadPath
            );
            if(bool.TryParse(diffDone,out var done) && done)
            {
                this.PauseIcon = "\uE769";
                _ = Task.Run(async () => await GameContext.StartInstallGameResource(InstallOption.CreateProdownlad(), ResourceParameter));
            }
            else
            {
                _buttonAction = ButtonActionType.PrepareUpdate;
                _ = Task.Run(async()=> await UpdateGameAsync());
            }
        }
    }

    /// <summary>
    /// 发起游戏数据导出监控会话并等待宿主就绪（游戏启动前调用）。
    /// 失败只 TipShow 提示，绝不阻断游戏启动。
    /// 成功后监控在后台持续运行，进度/结果经 Launcher 事件提示。
    /// </summary>
    private async Task StartGameDataExportSessionAsync()
    {
        var tipShow = AppContext.WindowManager.Shell.TipShow;
        try
        {
            var launcher = Instance.Host.Services.GetRequiredService<Services.GameDataExportLauncher>();

            // 订阅一次性提示（Launcher 是单例，避免重复订阅——用局部标志）
            if (!_gdeEventsSubscribed)
            {
                _gdeEventsSubscribed = true;
                var lastTip = DateTimeOffset.MinValue;
                launcher.ProgressChanged += (message, percent) =>
                {
                    _ = percent; // 百分比对轻提示无意义，只取消息
                    // 节流：同 10s 内只提示一次（监控心跳 2s 一条，全弹会刷屏）
                    var now = DateTimeOffset.Now;
                    if (now - lastTip < TimeSpan.FromSeconds(10))
                        return;
                    lastTip = now;
                    _ = tipShow.ShowMessageAsync(message, Symbol.Message);
                };
                launcher.SessionEnded += result =>
                {
                    _ = tipShow.ShowMessageAsync(
                        result.Success
                            ? string.Format(
                                LanguageService.GetString("Toolkit_GameDataExportDone"),
                                result.Inventory?.NormalItems.Count ?? 0,
                                result.Inventory?.Weapons.Count ?? 0,
                                result.Inventory?.Phantoms.Count ?? 0,
                                result.Inventory?.ValidTimeItems.Count ?? 0)
                            : result.Message,
                        result.Success ? Symbol.Accept : Symbol.Clear);
                };

                // 空转计时由 Launcher 按会话持有，不依赖游戏页面的 CTS。
            }

            var ready = await launcher.StartSessionForAutoLaunchAsync();
            if (ready)
            {
                await tipShow.ShowMessageAsync(
                    LanguageService.GetString("Toolkit_GameDataExportAutoStarted"),
                    Symbol.Message);
            }
            else
            {
                await tipShow.ShowMessageAsync(
                    LanguageService.GetString("Toolkit_GameDataExportAutoFailed"),
                    Symbol.Clear);
            }
        }
        catch (Exception ex)
        {
            // 静默降级：提示但不阻断游戏启动
            try
            {
                await tipShow.ShowMessageAsync(
                    LanguageService.GetString("Toolkit_GameDataExportAutoFailed") + $"\n{ex.Message}",
                    Symbol.Clear);
            }
            catch { }
        }
    }

    /// <summary>Launcher 事件是否已订阅（VM 实例可能多个，Launcher 单例只订一次）。</summary>
    private static bool _gdeEventsSubscribed;

    [RelayCommand]
    async Task StartDownloadProdGameResource()
    {
        var status = await this.GameContext.GetGameContextStatusAsync(this.CTS.Token);
        if (status == null)
            return;
        if(GameContext.ProdDownloadState== null)
        {
            this.PreDownloadIcon = "\uEBD3";
            await StartPreDownloadGame();
            return;
        }
        if (status.IsPause || GameContext.ProdDownloadState.IsPaused)
        {
            await this.GameContext.ResumeDownloadAsync();
            this.PreDownloadIcon = "\uE768";
        }
        else
        {
            await this.GameContext.PauseDownloadAsync();
            this.PreDownloadIcon = "\uE768";
        }
    }

    [RelayCommand]
    async Task ShowGameLocalTokenWindow()
    {
        if (!(GameContext.ContextName == nameof(WavesMainGameContextV2) || GameContext.ContextName == nameof(PunishMainGameContextV2)))
        {
            await WindowManager.Shell.DialogManager.ShowMessageDialog(new ShowDialogOption()
            {
                Context = LanguageService.GetStringByText("不支持的游戏类型")
            });
            return;
        }
        await WindowManager.Shell.DialogManager.ShowGameLocalTokenAsync(this.GameContext.ContextName);
    }
}
