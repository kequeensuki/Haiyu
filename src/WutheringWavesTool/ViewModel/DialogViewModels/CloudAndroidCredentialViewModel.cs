using System;
using System.Collections.Generic;
using System.Text;
using Haiyu.Common.Contracts;
using Waves.Api.Models.CloudGame;
using Waves.Core.Contracts.CloudGame;

namespace Haiyu.ViewModel.DialogViewModels;

/// <summary>
/// 云鸣潮账号的安卓版凭据导入对话框 VM：
/// 将安卓版云鸣潮（mod 版 dump）的 token 与设备字段写入本地账号，
/// 使启动器会话伪装为安卓版客户端，避免互踢同账号的安卓版登录态。
/// </summary>
public sealed partial class CloudAndroidCredentialViewModel : DialogViewModelBase
{
    public CloudAndroidCredentialViewModel(
        DialogSession dialogSession,
        IWavesCloudGameService wavesCloudGameService
    )
        : base(dialogSession)
    {
        WavesCloudGameService = wavesCloudGameService;
    }

    public IWavesCloudGameService WavesCloudGameService { get; }

    /// <summary>
    /// 目标账号（已在 SetData 注入）。
    /// </summary>
    public CloudGameLoginData User { get; private set; }

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UniqueId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Did { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SdkToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClientVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TipMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCredential { get; set; }

    public void SetUser(CloudGameLoginData user)
    {
        User = user;
        Token = user.AndroidToken ?? string.Empty;
        UniqueId = user.AndroidUniqueId ?? string.Empty;
        Did = user.AndroidDid ?? string.Empty;
        SdkToken = user.AndroidSdkToken ?? string.Empty;
        DeviceModel = user.AndroidDeviceModel ?? string.Empty;
        ClientVersion = user.AndroidClientVersion ?? string.Empty;
        HasCredential = user.HasAndroidCredentials;
    }

    [RelayCommand]
    async Task Save()
    {
        if (User == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(Did))
        {
            TipMessage = LanguageService.GetStringByText("云游戏会话 token 与设备号不能为空");
            return;
        }

        User.AndroidToken = Token.Trim();
        User.AndroidUniqueId = string.IsNullOrWhiteSpace(UniqueId) ? null : UniqueId.Trim();
        User.AndroidDid = Did.Trim();
        User.AndroidSdkToken = string.IsNullOrWhiteSpace(SdkToken) ? null : SdkToken.Trim();
        User.AndroidDeviceModel = string.IsNullOrWhiteSpace(DeviceModel) ? null : DeviceModel.Trim();
        User.AndroidClientVersion =
            string.IsNullOrWhiteSpace(ClientVersion) ? null : ClientVersion.Trim();

        var saved = await WavesCloudGameService.ConfigManager.SaveUserAsync(User);
        if (!saved)
        {
            TipMessage = LanguageService.GetStringByText("保存失败，请检查账号文件是否被占用");
            return;
        }
        HasCredential = User.HasAndroidCredentials;
        SystemEventMessager.Publish(
            new()
            {
                Message = LanguageService.GetStringByText(
                    "安卓凭据已保存，下次启动会话将使用安卓版身份"
                ),
            }
        );
        await this.CloseAsync();
    }

    /// <summary>
    /// 清除安卓凭据，回到官方网页版会话。
    /// </summary>
    [RelayCommand]
    async Task Clear()
    {
        if (User == null)
        {
            return;
        }
        User.AndroidToken = null;
        User.AndroidUniqueId = null;
        User.AndroidDid = null;
        User.AndroidSdkToken = null;
        User.AndroidDeviceModel = null;
        User.AndroidClientVersion = null;

        var saved = await WavesCloudGameService.ConfigManager.SaveUserAsync(User);
        if (!saved)
        {
            TipMessage = LanguageService.GetStringByText("保存失败，请检查账号文件是否被占用");
            return;
        }
        Token = string.Empty;
        UniqueId = string.Empty;
        Did = string.Empty;
        SdkToken = string.Empty;
        DeviceModel = string.Empty;
        ClientVersion = string.Empty;
        HasCredential = false;
        SystemEventMessager.Publish(
            new() { Message = LanguageService.GetStringByText("已恢复为官方网页版会话") }
        );
    }

    /// <summary>
    /// 从 mod 版云鸣潮导出的 header dump 文本中解析字段（x-token / serverUser.uniqueId / deviceNum / User-Agent(webview) / Kr-Version / deviceModel）。
    /// </summary>
    [RelayCommand]
    void ParseDump()
    {
        var text = Token;
        if (string.IsNullOrWhiteSpace(text))
        {
            TipMessage = LanguageService.GetStringByText("请先在上方文本框粘贴导出的字段内容");
            return;
        }

        // 若粘贴的是整段 dump，则把解析结果回填到各字段，Token 框保留 x-token 值
        var parsedToken = MatchLine(text, "x-token:");
        var parsedUniqueId = MatchLine(text, "serverUser.uniqueId:");
        var parsedDid = MatchLine(text, "deviceNum(androidId|uuid):") ?? MatchLine(text, "body.deviceNum:");
        var parsedVersion = MatchLine(text, "Kr-Version:");
        var parsedModel = MatchLine(text, "body.deviceModel:");
        var ua = MatchLine(text, "User-Agent(webview):");

        if (parsedToken == null)
        {
            TipMessage = LanguageService.GetStringByText("未找到 x-token 字段，请检查粘贴内容");
            return;
        }

        Token = parsedToken;
        if (parsedUniqueId != null)
            UniqueId = parsedUniqueId;
        if (parsedDid != null)
            Did = parsedDid;
        if (parsedVersion != null)
            ClientVersion = parsedVersion;
        if (parsedModel != null)
            DeviceModel = parsedModel;
        if (ua != null && string.IsNullOrWhiteSpace(DeviceModel))
        {
            // Mozilla/5.0 (Linux; Android 17; sdk_gphone64_x86_64 Build/xxx; wv) ...
            var match = System.Text.RegularExpressions.Regex.Match(
                ua,
                @"Android\s+\d+;\s+([^;)]+?)\s+Build/",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (match.Success)
            {
                DeviceModel = match.Groups[1].Value.Trim();
            }
        }
        TipMessage = LanguageService.GetStringByText("解析完成，请补全缺失字段后保存");
    }

    private static string? MatchLine(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ', '\t');
            if (
                trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > key.Length
            )
            {
                var value = trimmed[key.Length..].Trim();
                if (value.Length > 0 && !value.StartsWith("<"))
                {
                    return value;
                }
                return null;
            }
        }
        return null;
    }
}
