using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Waves.Api.Models.CloudGame;

public class CloudGameLoginData
{
    [JsonPropertyName("username")]
    public string Username { get; set; }

    [JsonPropertyName("sdkuserid")]
    public string Sdkuserid { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("loginType")]
    public int LoginType { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("idStat")]
    public int IdStat { get; set; }

    [JsonPropertyName("age")]
    public int Age { get; set; }

    [JsonPropertyName("cuid")]
    public string Cuid { get; set; }

    [JsonPropertyName("showPaw")]
    public bool ShowPaw { get; set; }

    [JsonPropertyName("bindDevStat")]
    public int BindDevStat { get; set; }

    [JsonPropertyName("autoToken")]
    public string AutoToken { get; set; }

    [JsonPropertyName("autoTokenStatus")]
    public bool AutoTokenStatus { get; set; }

    [JsonPropertyName("firstLgn")]
    public int FirstLgn { get; set; }

    [JsonPropertyName("phoneCheck")]
    public int PhoneCheck { get; set; }

    [JsonPropertyName("phone")]
    public string Phone { get; set; }

    [JsonPropertyName("phoneToken")]
    public string PhoneToken { get; set; }

    [JsonPropertyName("loginDid")]
    public string? LoginDid { get; set; }

    /// <summary>
    /// 安卓版云鸣潮的会话 token（Login/Login 返回的 EndLogin token）。
    /// 设置后启动器会话将伪装成安卓版云鸣潮，避免互踢安卓端登录态。
    /// </summary>
    [JsonPropertyName("androidToken")]
    public string? AndroidToken { get; set; }

    /// <summary>
    /// 安卓版云鸣潮的会话唯一 ID（uniqueId，如 Official#xxx）。
    /// </summary>
    [JsonPropertyName("androidUniqueId")]
    public string? AndroidUniqueId { get; set; }

    /// <summary>
    /// 安卓设备的 deviceNum（androidId 形态，16 位 hex）。
    /// </summary>
    [JsonPropertyName("androidDid")]
    public string? AndroidDid { get; set; }

    /// <summary>
    /// 安卓版 SDK 的 access_token（桥页 OnCloudGameLogin 回传给游戏侧使用）。
    /// </summary>
    [JsonPropertyName("androidSdkToken")]
    public string? AndroidSdkToken { get; set; }

    /// <summary>
    /// 安卓版云鸣潮客户端版本号（如 3.3.0）。
    /// </summary>
    [JsonPropertyName("androidClientVersion")]
    public string? AndroidClientVersion { get; set; }

    /// <summary>
    /// 安卓设备型号（Build.MODEL，如 M2012K11AC）。
    /// </summary>
    [JsonPropertyName("androidDeviceModel")]
    public string? AndroidDeviceModel { get; set; }

    /// <summary>
    /// 是否已配置安卓版凭据（决定会话构建方式与请求特征）。
    /// </summary>
    [JsonIgnore]
    public bool HasAndroidCredentials =>
        !string.IsNullOrWhiteSpace(AndroidToken)
        && !string.IsNullOrWhiteSpace(AndroidDid);

    public string GetId() => $"{Username}";
}

