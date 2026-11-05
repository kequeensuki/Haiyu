namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>一帧 UDP 载荷。<see cref="Payload"/> 已剥掉 Ethernet/IP/UDP 头。</summary>
public sealed record UdpFrame(
    string SrcIp,
    int SrcPort,
    string DstIp,
    int DstPort,
    byte[] Payload
);

/// <summary>
/// UDP 捕获源。
///
/// 首版只有 <see cref="NpcapUdpCapture"/>（实时抓包）；接口保留是为了将来能加
/// pcap 文件回放实现，便于离线调试而不用反复启动游戏。
/// </summary>
public interface IUdpCapture : IDisposable
{
    /// <summary>开始抓包。重复调用应幂等。</summary>
    void Start();

    /// <summary>停止抓包。</summary>
    void Stop();

    /// <summary>
    /// 尝试取一帧。没有数据返回 <c>null</c>（非阻塞）。
    /// </summary>
    UdpFrame? TryRead();
}
