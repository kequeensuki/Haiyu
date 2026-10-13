namespace Waves.Core.Models.CloudGame;

/// <summary>
/// 云游戏登录会话
/// </summary>
public partial class CloudGameLoginSession:ObservableObject
{
    [ObservableProperty]
    public partial CloudGameLoginData OrginData { get; set; }

    [ObservableProperty]
    public partial PhoneTokenData PhoneToken { get; internal set; }

    [ObservableProperty]
    public partial AccessData AccessData { get; internal set; }

    [ObservableProperty]
    public partial EndLoginData EndLoginData { get; internal set; }
    public string TraceId { get; internal set; }

    public DateTime SaveTime { get; internal set; }

    /// <summary>
    /// 当前会话的客户端形态：安卓版凭据存在时为 Android，否则为官方网页版。
    /// 决定请求头（x-os / User-Agent / Cookie token）与启动参数（bizData / cmdLine）取哪一套。
    /// </summary>
    public CloudClientKind ClientKind =>
        OrginData?.HasAndroidCredentials == true
            ? CloudClientKind.Android
            : CloudClientKind.Web;

    public string GetId() => $"{OrginData.Username}";
}

/// <summary>
/// 云鸣潮会话的客户端形态
/// </summary>
public enum CloudClientKind
{
    /// <summary>
    /// 官方网页版（web-pc / x-os=web）
    /// </summary>
    Web = 0,

    /// <summary>
    /// 安卓版（platform=Android / x-os 由安卓值替换）
    /// </summary>
    Android = 1,
}

/// <summary>
/// 登录窗口快宅
/// </summary>
public partial class CloudGameLoginSnapshot
{
    public string DeviceNum { get; }

    public CloudGameLoginSnapshot(string deviceNum)
    {
        DeviceNum = deviceNum;
    }

    /// <summary>
    /// 创建登录快照
    /// </summary>
    /// <returns></returns>
    public static CloudGameLoginSnapshot Create()
    {
        return new(HardwareIdGenerator.GenerateDeviceId());
    }

    public static CloudGameLoginSnapshot Create(CloudGameLoginData? data)
    {
        return new CloudGameLoginSnapshot(data?.LoginDid ?? HardwareIdGenerator.GenerateDeviceId());
    }
}
