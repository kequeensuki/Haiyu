namespace Waves.Core.Models.CloudGame
{
    public class BrowserSessionLaunchOptions
    {

        public string BootstrapUrl { get; init; } = string.Empty;

        public string AccessToken { get; init; } = string.Empty;

        public string RefreshToken { get; init; } = string.Empty;

        public string CloudApiToken { get; init; } = string.Empty;

        public string CookieDomain { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init; } =
            new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> Cookies { get; init; } =
            new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> StorageItems { get; init; } =
            new Dictionary<string, string>();

        public IReadOnlyList<string> HeaderHostPatterns { get; init; } = new List<string>();

        public StreamQualityOptions Quality { get; init; }

        public int StreamDpi { get; init; } = 120;

        public CloudGameStreamSession StreamOptions { get; internal set; }

        public string DispatchMessage { get; set; }

        /// <summary>
        /// 桥页 OnCloudGameLoginPreLaunch 回传给游戏侧的 Platform 字段
        /// （网页版 Windows；安卓凭据会话 Android，避免云端识别为 Web 端）。
        /// </summary>
        public string ClientPlatform { get; init; } = "Windows";

        /// <summary>
        /// 桥页 OnCloudGameLoginPreLaunch 回传给游戏侧的 Device 字段（navigator.userAgent 的替换值；
        /// 安卓凭据会话使用安卓 WebView UA）。
        /// </summary>
        public string ClientDevice { get; init; } = string.Empty;

        public bool IsComplete { get; internal set; }

        internal BrowserSessionLaunchOptions Clone()
        {
            return new BrowserSessionLaunchOptions()
            {
                AccessToken = this.AccessToken,
                Quality = this.Quality,
                AdditionalHeaders = this.AdditionalHeaders,
                BootstrapUrl = this.BootstrapUrl,
                CookieDomain = this.CookieDomain,
                Cookies = this.Cookies,
                HeaderHostPatterns = this.HeaderHostPatterns,
                RefreshToken = this.RefreshToken,
                CloudApiToken = this.CloudApiToken,
                StorageItems = this.StorageItems,
                StreamDpi = this.StreamDpi,
                StreamOptions = this.StreamOptions,
                ClientPlatform = this.ClientPlatform,
                ClientDevice = this.ClientDevice,
            };
        }
    }
}

public sealed record StreamQualityOptions(
    int BitRate,
    int BitRateMin,
    int Fps,
    int Width,
    int Height,
    int CodecType,
    string StreamStrategy,
    bool EnableImageEnhancement,
    int DPI,
    CloudQualityType Type = CloudQualityType.Clarity
)
{
    public const string SmoothPreset = "smooth";

    public const string ClearPreset = "clear";

    public string ResolutionKey => $"{Width}x{Height}";

    public int BitRateMax => BitRate;

    public CloudQualityType OfficialPreset => Type;

    
}
