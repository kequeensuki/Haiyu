using System;
using System.Collections.Generic;
using System.Text;
using Haiyu.Common.Contracts;
using Waves.Core.Contracts.CloudGame;
using Waves.Core.Services;

namespace Haiyu.ViewModel.DialogViewModels;

public sealed partial class WavesCloudUserViewModel : DialogViewModelBase
{
    public WavesCloudUserViewModel(
        DialogSession dialogSession,
        IWavesCloudGameService wavesCloudGameService,
        [FromKeyedServices(nameof(KuroCloudGameContext))] IKuroCloudGameContext cloudGameContext
    )
        : base(dialogSession)
    {
        WavesCloudGameService = wavesCloudGameService;
        CloudGameContext = cloudGameContext;
        RegisterMessager();
    }

    private void RegisterMessager()
    {
        this.Messenger.Register<DeleteCloudUserMessager>(this, DeleteCloudUserMethod);
    }

    private async void DeleteCloudUserMethod(object recipient, DeleteCloudUserMessager message)
    {
        await this.WavesCloudGameService.DeleteUserAsync(message.id);
        await Loaded();
    }

    public IWavesCloudGameService WavesCloudGameService { get; }
    public IKuroCloudGameContext CloudGameContext { get; }

    [ObservableProperty]
    public partial ObservableCollection<CloudGameLoginDataWrapper> CloudUsers { get; set; }

    [RelayCommand]
    async Task Loaded()
    {
        var users = await this.WavesCloudGameService.ConfigManager.GetUsersAsync();
        var temp = users.Select(x => new CloudGameLoginDataWrapper(x)).ToObservableCollection();
        var currentLogin = await this.WavesCloudGameService.GetCurrentUserSession();
        if (currentLogin != null)
        {
            foreach (var item in temp)
            {
                if (currentLogin.GetId() == item.Id)
                {
                    item.IsSelect = true;
                }
            }
        }
        this.CloudUsers = temp;
    }

    [RelayCommand]
    public async Task ApplyCloudUser()
    {
        var selectUser = this.CloudUsers.FirstOrDefault(x => x.IsSelect);
        if (selectUser == null)
            return;
        var result = await this.WavesCloudGameService.SetCurrentUserSession(selectUser.Id);
        if (result)
        {
            await AppSettings.SetSelectCloudUserIDAsync(selectUser.Id);
        }
        await this.CloseAsync();
    }

    /// <summary>
    /// 打开所选账号的安卓凭据导入对话框；凭据变更后刷新账号列表与云游戏页。
    /// </summary>
    [RelayCommand]
    public async Task ShowAndroidCredential()
    {
        var selectUser = this.CloudUsers?.FirstOrDefault(x => x.IsSelect);
        if (selectUser == null)
        {
            SystemEventMessager.Publish(
                new() { Message = LanguageService.GetStringByText("请先选择一个账号") }
            );
            return;
        }
        var user = await WavesCloudGameService.ConfigManager.GetUserAsync(selectUser.Id);
        if (user == null)
        {
            return;
        }
        await this.AppContext.WindowManager.Shell.DialogManager.ShowCloudAndroidCredentialDialogAsync(user);
        await Loaded();
        // 凭据变更可能影响当前会话的客户端形态，通知云游戏页刷新会话
        WeakReferenceMessenger.Default.Send(new RefreshGamePageMessager(true));
    }
}
