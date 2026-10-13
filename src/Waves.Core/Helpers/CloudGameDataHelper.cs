namespace Waves.Core.Common;

/// <summary>
/// 云游戏构建参数
/// </summary>
public static class CloudGameDataHelper
{
    public const string WelinkTenantKey = "1853717215719854081";
    public const string WelinkGameId = "1853717365355843585600007";
    public const string MainUrl = "https://mc.kurogames.com/cloud/index.html";
    public static void BuildLauncheOption(CloudGameLoginSession login) { }

    /// <summary>
    /// 当前会话的 API User-Agent：安卓凭据存在时用安卓 OkHttp UA，否则用官方网页 Electron UA。
    /// </summary>
    private static string GetApiUserAgent(CloudGameLoginSession login) =>
        login.ClientKind == CloudClientKind.Android
            ? AndroidCloudGameMethod.UserAgent
            : CloudGameMethod.OfficialWelinkOsVersion;

    /// <summary>
    /// 当前会话的 x-os 头：安卓主业务 API 实测不带 x-os（仅 x-token / x-b3-traceid），返回 null 表示不添加。
    /// </summary>
    private static string? GetXOs(CloudGameLoginSession login) =>
        login.ClientKind == CloudClientKind.Android ? null : "web";

    /// <summary>
    /// 当前会话的会话 token（x-token / Cookie token）：
    /// 安卓会话直接使用安卓版 Login/Login 产出的 token，不再使用网页版 token。
    /// </summary>
    private static string GetSessionToken(CloudGameLoginSession login) =>
        login.ClientKind == CloudClientKind.Android
            ? login.OrginData.AndroidToken!
            : login.EndLoginData.Token;

    /// <summary>
    /// 桥页回传给游戏侧的 SDK token（OnCloudGameLogin 的 Token 字段）：
    /// 安卓会话使用安卓版 SDK 的 access_token。
    /// </summary>
    private static string GetSdkToken(CloudGameLoginSession login) =>
        login.ClientKind == CloudClientKind.Android
            ? login.OrginData.AndroidSdkToken ?? string.Empty
            : login.AccessData.AccessToken;

    /// <summary>
    /// 当前会话的设备标识（traceId / 桥页 useMcCloudGameDid）。
    /// </summary>
    private static string GetDeviceId(CloudGameLoginSession login) =>
        login.ClientKind == CloudClientKind.Android
            ? login.OrginData.AndroidDid ?? string.Empty
            : login.OrginData.LoginDid ?? string.Empty;

    public static Dictionary<string, string> BuildWebStorageItems(CloudGameLoginSession login)
    {
        var isAndroid = login.ClientKind == CloudClientKind.Android;
        var sessionToken = GetSessionToken(login);
        var sdkToken = GetSdkToken(login);
        var deviceId = GetDeviceId(login);
        var uniqueId = isAndroid
            ? login.OrginData.AndroidUniqueId ?? login.EndLoginData?.UniqueId
            : login.EndLoginData?.UniqueId;
        var phone = login.PhoneToken?.Phone ?? login.OrginData.Phone;
        var sdkOpenid = login.PhoneToken?.Id ?? login.OrginData.Id;

        var sdkLoginInfo = new CloudSdkLoginInfo
        {
            Cuid = login.OrginData.Cuid,
            Token = sdkToken,
            Username = login.OrginData.Username,
            Phone = phone,
            SdkOpenid = sdkOpenid,
        };

        var appLoginInfo = new CloudAppLoginInfo
        {
            Token = sessionToken,
            UniqueId = uniqueId,
            WalletData = login.EndLoginData?.WalletData,
        };

        var appStore = new CloudGameAppStore
        {
            SdkLoginInfo = sdkLoginInfo,
            AppLoginInfo = appLoginInfo,
        };

        return new Dictionary<string, string>()
        {
            { "wl_cloud_game_userId", uniqueId ?? string.Empty },
            { $"useMcCloudGameUserSession@Official#{login.OrginData.Id}", "1-" },
            { "sdkuserid", login.OrginData.Sdkuserid },
            { "username", login.OrginData.Username },
            { "cuid", login.OrginData.Cuid },
            { "useMcCloudGameDid", deviceId },
            { "welink_cloud_game_uuid", Guid.NewGuid().ToString().ToUpperInvariant() },
            {
                "sdkLoginInfo",
                JsonSerializer.Serialize(sdkLoginInfo, CloudGameContext.Default.CloudSdkLoginInfo)
            },
            { "token", sessionToken },
            { $"McCloudSessionId", Guid.NewGuid().ToString() },
            {
                "useMcCloudGameAppStore",
                JsonSerializer.Serialize(appStore, CloudGameContext.Default.CloudGameAppStore)
            },
            { $"__KrSDK_UUID__", Guid.NewGuid().ToString("N") },
            { $"show_user_name", $"1" },
            { "code", login.OrginData.Code },
            { "autoToken", login.OrginData.AutoToken },
            { "phoneToken", login.OrginData.PhoneToken },
            { "refreshToken", login.PhoneToken?.PhoneToken },
            { "accessToken", sdkToken },
            { "access_token", sdkToken },
            { "refresh_token", login.PhoneToken?.PhoneToken },
            {
                "__KrSDK_SYS_CONF__",
                """
{"thirdLogin":{"wechat":{"enabled":0},"qq":{"enabled":0},"taptap":{"enabled":0},"phone":{"enabled":1},"phoneQk":{"enabled":0},"tourist":{"enabled":0},"accLogin":{"enabled":0},"accReg":{"enabled":0},"apple":{"enabled":0},"qrLogin":{"enabled":0}},"heartFreq":5,"heartEnable":1,"uagr":"https://wutheringwaves.kurogame.com/p/agreement_public.html","pagr":"https://wutheringwaves.kurogame.com/p/personal_privacy.html","childArgUrl":"https://wutheringwaves.kurogame.com/p/child_privacy.html","webViewUrlWhiteList":["www.kurogame.com"],"clientUrl":{"accCenterUrl":"https://pro-cdn-sdk.kurogame.com/pro/sdk_web/index.html#/account_center","cancelUrl":"https://pro-cdn-sdk.kurogame.com/pro/sdk_personal/account-cancellation.html","sobot":"https://web-static.kurogame.com/shared/zhichi-cs/index.html","kefuServ":"https://user-zone-api.kurogame.com/notice/","kefu":"https://web-static.kurogame.com/shared/cs/index.html","pcGeetestUrl":"https://pro-cdn-sdk.kurogame.com/pro/sdk_personal/geetest.html","sobotRedDotUrl":"https://user-zone-api.kurogame.com/notice/sobot/client/red-dot"},"pwdLife":"60d","clientSwitch":{"disableWebviewVideoSupportedForWin":false,"sobot":true,"marketTencent":true,"marketToutiao":true,"ksCps":true,"useNewClientFocus":false,"krData":true,"clientFocus":true,"uaOsVersionForWin":true,"td":true,"bytedance":false,"webAccelerated":true,"trackingioPayUpload":true,"kefu":true,"appleASA":true,"didGt":false,"trackingioPayMoneyUpload":true},"toSwitch":true,"kefuInterval":300,"kefuSessionExpireHour":1,"accessTokenCheckFreq":10,"didSmSwitch":false,"didTxSwitch":false,"geetestSwitch":true,"cancelSwitch":false}
"""
            },
            { "__KrSDK__", BuildKrSdkJson(login) },
            { "G152", BuildPkgData(login) },
        };
    }

    private static string BuildKrSdkJson(CloudGameLoginSession session)
    {
        var isAndroid = session.ClientKind == CloudClientKind.Android;
        KRSDK sdk = new KRSDK()
        {
            Lang = "zh-Hans",
            GameId = "G152",
            PkgId = isAndroid ? AndroidCloudGameMethod.ProductId : "A1493",
            Pkg = isAndroid ? AndroidCloudGameMethod.Pkg : "com.kurogame.mingchao",
            ChannelId = isAndroid ? AndroidCloudGameMethod.ChannelId : "211",
            ClientId = "vvkewnskrxxwfo0yi61cy24l",
            ClientSecret = "g9ej0i1jf3y68wchb0ncm266",
            ViewType = string.Empty,
            RoleData = new(),
            LoginConfig = new LoginConfig()
            {
                Age = session.PhoneToken?.Age ?? 0,
                AutoToken = session.PhoneToken?.AutoToken,
                AutoTokenStatus = session.PhoneToken?.AutoTokenStatus ?? false,
                BindDevStat = 0,
                Code = session.PhoneToken?.Code,
                Cuid = session.PhoneToken?.Cuid,
                FirstLgn = session.PhoneToken?.FirstLgn ?? 0,
                Id = session.PhoneToken?.Id ?? 0,
                IdStat = session.PhoneToken?.IdStat ?? 0,
                LoginType = session.PhoneToken?.LoginType ?? 0,
                Phone = session.PhoneToken?.Phone,
                PhoneCheck = session.PhoneToken?.PhoneCheck ?? 0,
                PhoneToken = session.PhoneToken?.PhoneToken,
                SdkOpenid = session.PhoneToken?.Id ?? 0,
                Sdkuserid = session.PhoneToken?.Sdkuserid,
                ShowPaw = session.PhoneToken?.ShowPaw ?? false,
                Token = GetSdkToken(session),
                Username = session.PhoneToken?.Username,
            },
        };
        return JsonSerializer.Serialize(sdk, CloudGameContext.Default.KRSDK);
    }

    public static string BuildPkgData(CloudGameLoginSession session)
    {
        PKGData data = new PKGData()
        {
            Age = 22,
            AutoToken = session.PhoneToken?.AutoToken,
            AutoTokenStatus = session.PhoneToken?.AutoTokenStatus ?? false,
            BindDevStat = session.PhoneToken?.BindDevStat ?? 0,
            Code = session.PhoneToken?.Code,
            Cuid = session.PhoneToken?.Cuid,
            FirstLgn = session.PhoneToken?.FirstLgn ?? 0,
            Id = session.PhoneToken?.Id ?? 0,
            IdStat = session.PhoneToken?.IdStat ?? 0,
            LoginType = session.PhoneToken?.LoginType ?? 0,
            Phone = session.PhoneToken?.Phone,
            PhoneCheck = session.PhoneToken?.PhoneCheck ?? 0,
            PhoneToken = session.PhoneToken?.PhoneToken,
            SdkOpenid = session.PhoneToken?.Id ?? 0,
            Sdkuserid = session.PhoneToken?.Sdkuserid,
            ShowPaw = session.PhoneToken?.ShowPaw ?? false,
            Token = GetSdkToken(session),
            Username = session.PhoneToken?.Username,
        };
        return JsonSerializer.Serialize(data, CloudGameContext.Default.PKGData);
    }

    public static Dictionary<string, string> BuildCookieItems(CloudGameLoginSession login)
    {
        var autoToken = login.OrginData.AutoToken;
        if (string.IsNullOrWhiteSpace(autoToken))
        {
            autoToken = login.PhoneToken?.AutoToken;
        }

        var phoneToken = login.OrginData.PhoneToken;
        if (string.IsNullOrWhiteSpace(phoneToken))
        {
            phoneToken = login.PhoneToken?.PhoneToken;
        }

        return new Dictionary<string, string>()
        {
            { "token", GetSessionToken(login) },
            { "autoToken", autoToken },
            { "phoneToken", phoneToken },
            { "username", login.OrginData.Username },
            { "sdkuserid", login.OrginData.Sdkuserid },
            { "cuid", login.OrginData.Cuid },
            { "code", login.OrginData.Code },
        };
    }

    public static BrowserSessionLaunchOptions BuildLaunchOption(
        CloudGameLoginSession session,
        StreamQualityOptions qualityOptions
    )
    {
        var isAndroid = session.ClientKind == CloudClientKind.Android;
        return new BrowserSessionLaunchOptions
        {
            BootstrapUrl = "https://mc.kurogames.com/cloud/index.html",
            AccessToken = GetSdkToken(session),
            RefreshToken = session.PhoneToken?.PhoneToken,
            CloudApiToken = GetSessionToken(session),
            CookieDomain = ".kurogames.com",
            AdditionalHeaders = new Dictionary<string, string>(),
            Cookies = BuildCookieItems(session),
            StorageItems = BuildWebStorageItems(session),
            HeaderHostPatterns =
            [
                "mc.kurogames.com",
                "cloud-game-sh.aki-game.com",
                "usercenter.kurogames.com",
                "*.aki-game.com",
                "*.kurogames.com",
            ],
            Quality = qualityOptions,
            StreamDpi = qualityOptions.DPI,
            ClientPlatform = isAndroid
                ? AndroidCloudGameMethod.Platform
                : "Windows",
            ClientDevice = isAndroid ? GetAndroidWebViewUserAgent(session) : string.Empty,
        };
    }

    /// <summary>
    /// 安卓凭据会话下，桥页回传给游戏侧的 Device 字段（安卓 WebView UA 形态）。
    /// </summary>
    private static string GetAndroidWebViewUserAgent(CloudGameLoginSession session)
    {
        var model =
            session.OrginData.AndroidDeviceModel ?? AndroidCloudGameMethod.DefaultDeviceModel;
        var version = session.OrginData.AndroidClientVersion ?? "17";
        return $"Mozilla/5.0 (Linux; Android {version}; {model} Build/TP1A.220624.014; wv) "
            + "AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/145.0.0.0 Mobile Safari/537.36";
    }

    /// <summary>
    /// 通过DPI缩放转换物理像素
    /// </summary>
    /// <param name="quality"></param>
    /// <param name="clampToWindow"></param>
    /// <returns></returns>
    public static StreamQualityOptions ScaleQualityToPhysical(
        StreamQualityOptions quality,
        bool clampToWindow
    )
    {
        var dpiScale = Math.Max(1.0, quality.DPI / 96.0);
        var isSmooth = quality.Type ==  Models.Enums.CloudQualityType.Smooth;
        var qualityScale = isSmooth ? 2.0 / 3.0 : 1.0;
        var maxWidth = Math.Max(640, (int)Math.Round(quality.Width * qualityScale));
        var maxHeight = Math.Max(360, (int)Math.Round(quality.Height * qualityScale));
        var physicalWidth = ClampEven(
            (int)Math.Round(quality.Width * dpiScale * qualityScale),
            640,
            maxWidth
        );
        var physicalHeight = ClampEven(
            (int)Math.Round(quality.Height * dpiScale * qualityScale),
            360,
            maxHeight
        );
        const double targetBitRatePerPixel = 0.010;
        var targetBitRate = (int)(physicalWidth * physicalHeight * targetBitRatePerPixel);
        targetBitRate = Math.Clamp(
            targetBitRate,
            CloudGameMethod.MinBitRate,
            CloudGameMethod.DefaultBitRate
        );
        return new StreamQualityOptions(
            targetBitRate,
            quality.BitRateMin,
            quality.Fps,
            physicalWidth,
            physicalHeight,
            quality.CodecType,
            quality.StreamStrategy,
            quality.EnableImageEnhancement,
            quality.DPI,
            quality.Type
        );
    }

    public static HttpClient CreateWebCloudClient(CloudGameLoginSession session)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            UseCookies = false,
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://cloud-game-sh.aki-game.com/"),
        };

        // 安卓主业务 API 实测只带 x-token + x-b3-traceid（OkHttp UA），无 Kr-Ver/Referer/Origin/x-os/Cookie
        if (session.ClientKind == CloudClientKind.Android)
        {
            client.DefaultRequestHeaders.Add("x-token", GetSessionToken(session));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(GetApiUserAgent(session));
        }
        else
        {
            client.DefaultRequestHeaders.Add("Kr-Ver", "1.9.0");
            client.DefaultRequestHeaders.Add("x-token", GetSessionToken(session));
            client.DefaultRequestHeaders.Referrer = new Uri(
                "https://mc.kurogames.com/cloud/index.html"
            );
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Code/1.120.0 Chrome/142.0.7444.265 Electron/39.8.8 Safari/537.36"
            );
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Origin",
                "https://mc.kurogames.com"
            );
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Cookie",
                string.Join(
                    ";",
                    CloudGameDataHelper
                        .BuildCookieItems(session)
                        .Select(pair => $"{pair.Key}={pair.Value}")
                )
            );
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-os", "web");
        }
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.Add("x-b3-traceid",session.TraceId);
        return client;
    }

    /// <summary>
    /// 构建 CommonStartGame 的 bizData：按会话形态切换 os/osVer/osCategory/clientVer。
    /// </summary>
    public static CloudBizData CreateCloudBizData(
        CloudGameLoginSession session,
        IEnumerable<BizCloudNode> nodes
    )
    {
        if (session.ClientKind == CloudClientKind.Android)
        {
            var deviceModel =
                session.OrginData.AndroidDeviceModel ?? AndroidCloudGameMethod.DefaultDeviceModel;
            var clientVer =
                session.OrginData.AndroidClientVersion
                ?? AndroidCloudGameMethod.DefaultAppVersion;
            return new CloudBizData(deviceModel, clientVer, nodes)
            {
                Os = AndroidCloudGameMethod.Os,
                OsCategory = AndroidCloudGameMethod.OsCategory,
            };
        }
        return new CloudBizData(
            "Code/1.120.0 Chrome/142.0.7444.265 Electron/39.8.8 Safari/537.36",
            CloudGameMethod.WelinkClientVersion,
            nodes
        );
    }

    /// <summary>
    /// 构建 Welink 启动参数：安卓会话使用安卓版 cmdLine 与 wlResourceData.version。
    /// </summary>
    public static WelinkStartParameters CreateWebLinkParameters(
        CloudGameLoginSession session,
        int dpi,
        int maxWidth,
        int maxHeight,
        int bitRate,
        int fps,
        int codeType,
        string bizData,
        IEnumerable<CloudGameNode> nodes,
        CloudGameNode node
    )
    {
        var resolution = GetPreferredResolution(dpi, maxWidth, maxHeight);
        var version = "v1.0";
        string cmdLine;
        if (session.ClientKind == CloudClientKind.Android)
        {
            var deviceModel =
                session.OrginData.AndroidDeviceModel ?? AndroidCloudGameMethod.DefaultDeviceModel;
            version =
                session.OrginData.AndroidClientVersion
                ?? AndroidCloudGameMethod.DefaultAppVersion;
            cmdLine = string.Format(
                AndroidCloudGameMethod.CmdLineTemplate,
                fps,
                dpi,
                resolution,
                deviceModel
            );
        }
        else
        {
            cmdLine =
                $"-CloudGamePlatform=Windows -fps={fps} -Dpi={dpi} -DeviceScreenResolution={resolution} -Device=Windows -SkipSplash -IsWeb=1";
        }

        return new WelinkStartParameters(
            WelinkTenantKey,
            WelinkGameId,
            resolution,
            bitRate,
            fps,
            codeType,
            version,
            cmdLine,
            bizData,
            nodes,
            node
        );
    }

    private static string GetPreferredResolution(int dpi, int maxWidth, int maxHeight)
    {
        var screenWidth = ClampEven(
            maxWidth,
            CloudGameMethod.MinStreamWidth,
            CloudGameMethod.MaxStreamWidth
        );
        var screenHeight = ClampEven(
            maxHeight,
            CloudGameMethod.MinStreamHeight,
            CloudGameMethod.MaxStreamHeight
        );

        return $"{screenWidth}x{screenHeight}";
    }

    private static int ClampEven(int value, int minValue, int maxValue)
    {
        var clamped = Math.Max(minValue, Math.Min(maxValue, value));
        return clamped % 2 == 0 ? clamped : Math.Max(minValue, clamped - 1);
    }
}
