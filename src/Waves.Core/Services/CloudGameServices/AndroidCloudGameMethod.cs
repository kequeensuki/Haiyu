namespace Waves.Core.Services.CloudGameServices;

/// <summary>
/// 安卓版云鸣潮（com.kuro.cloudgame）的客户端特征常量。
/// 用于把启动器的云鸣潮会话伪装成安卓版，避免服务端识别为 Web 端后互踢同账号的安卓版登录态。
/// 字段来源：安卓客户端 3.3.0 逆向 + mod 版实机 header dump（2026-10 实测）。
/// </summary>
public static class AndroidCloudGameMethod
{
    /// <summary>
    /// Login/Login 的 platform：安卓版客户端值（Android）。
    /// </summary>
    public const string Platform = "Android";

    /// <summary>
    /// 云鸣潮安卓版 appVersion / gameVersion（kr_sdk_config 与公共参数实测均为 3.3.0）。
    /// </summary>
    public const string DefaultAppVersion = "3.3.0";

    /// <summary>
    /// CommonStartGame wlResourceData.version：安卓版用 app 版本号（网页版为 v1.0）。
    /// </summary>
    public const string ResourceVersion = DefaultAppVersion;

    /// <summary>
    /// bizData.os：安卓版取 ANDROID。
    /// </summary>
    public const string Os = "ANDROID";

    /// <summary>
    /// bizData.osCategory：安卓版取 Android。
    /// </summary>
    public const string OsCategory = "Android";

    /// <summary>
    /// API User-Agent：安卓客户端 OkHttp 形态（dump 中 User-Agent(api) = okhttp/&lt;bundled&gt;）。
    /// </summary>
    public const string UserAgent = "okhttp/4.12.0";

    /// <summary>
    /// SDK 登录表单 os / platform / market 字段：安卓版取 AND（实测 dump：os=AND、platform=AND、market=AND）。
    /// </summary>
    public const string SdkOs = "AND";

    /// <summary>
    /// SDK 登录表单 channelOp：安卓云鸣潮专属渠道操作字（实测 dump）。
    /// </summary>
    public const string SdkChannelOp = "androidcloud";

    /// <summary>
    /// 安卓云鸣潮包名（实测 dump：pkg=com.kuro.cloudgame）。
    /// </summary>
    public const string Pkg = "com.kuro.cloudgame";

    /// <summary>
    /// 安卓云鸣潮 channelId（kr_sdk_config.json，实测 dump：channelId=18）。
    /// </summary>
    public const string ChannelId = "18";

    /// <summary>
    /// 安卓云鸣潮 pkgId / productId（kr_sdk_config.json，实测 dump：productId=A1474）。
    /// </summary>
    public const string ProductId = "A1474";

    /// <summary>
    /// SDK 版本（实测 dump：sdkVersion=2.16.0，与网页版一致）。
    /// </summary>
    public const string SdkVersion = "2.16.0";

    /// <summary>
    /// cmdLine 模板（与 CDN 配置的模板一致，安卓端代入值：
    /// -CloudGamePlatform=Android -fps=&lt;fps&gt; -Dpi=&lt;dpi&gt; -DeviceScreenResolution=&lt;WxH&gt; -Device=&lt;model&gt; -SkipSplash -IsWeb=1）。
    /// </summary>
    public const string CmdLineTemplate =
        "-CloudGamePlatform=Android -fps={0} -Dpi={1} -DeviceScreenResolution={2} -Device={3} -SkipSplash -IsWeb=1";

    /// <summary>
    /// 安卓设备型号缺省值（bizData.osVer 与 cmdLine 的 Device 段共用；实测 dump：deviceModel=sdk_gphone64_x86_64）。
    /// </summary>
    public const string DefaultDeviceModel = "M2012K11AC";
}
