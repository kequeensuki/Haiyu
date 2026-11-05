namespace Haiyu.ABI.Services.GameDataExport.Capture;

using System.Buffers;

/// <summary>
/// 一帧 UDP 载荷。<see cref="Payload"/> 已剥掉 Ethernet/IP/UDP 头。
///
/// ★ 内存纪律（v2 持续监控）：<see cref="Payload"/> 是
/// <see cref="RentedBuffer"/> 的切片（ArrayPool 租借），<b>不拥有内存</b>——
/// 消费方必须在处理完本数据报后调用 <see cref="ReturnBuffer"/> 归还；
/// 需要跨数据报长存的数据（入流段、0xEC 裸帧）必须先 <c>ToArray()</c> 拷贝。
/// </summary>
public sealed record UdpFrame(
    string SrcIp,
    int SrcPort,
    string DstIp,
    int DstPort,
    Memory<byte> Payload,
    byte[]? RentedBuffer
)
{
    /// <summary>归还租借缓冲（非租借构造的帧为空操作）。</summary>
    public void ReturnBuffer()
    {
        if (RentedBuffer is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}

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
    ///
    /// ★ 返回的 <see cref="UdpFrame"/> 携带 ArrayPool 租借缓冲，
    ///   调用方处理完必须 <see cref="UdpFrame.ReturnBuffer"/>。
    /// </summary>
    UdpFrame? TryRead();
}
