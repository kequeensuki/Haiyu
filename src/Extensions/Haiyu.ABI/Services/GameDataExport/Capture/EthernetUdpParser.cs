using System.Buffers.Binary;

namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>
/// 从链路层帧里剥出 UDP 载荷。
///
/// ★ 所有长度切片的检查都不能省：抓包缓冲区里可能有截断帧，
///   越界读会直接抛异常打断整个捕获循环（本项目在 PowerShell 版上踩过）。
/// </summary>
internal static class EthernetUdpParser
{
    private const int EtherHeaderLength = 14;
    private const int VlanTagLength = 4;
    private const int Ipv4HeaderMinLength = 20;
    private const int UdpHeaderLength = 8;
    private const ushort EtherTypeIpv4 = 0x0800;
    private const ushort EtherTypeVlan = 0x8100;
    private const ushort EtherTypeQinQ = 0x88A8;
    private const byte ProtocolUdp = 17;

    /// <summary>解析失败（非 IPv4/UDP、或帧被截断）返回 <c>null</c>。</summary>
    /// <remarks>
    /// ★ 零拷贝（v2 持续监控）：返回的 <see cref="UdpFrame"/> 其 Payload 是
    /// <paramref name="frameBuffer"/> 的切片，不分配新内存；缓冲所有权随帧移交。
    /// </remarks>
    public static UdpFrame? Parse(byte[] frameBuffer, int frameLength, int linkType)
    {
        // 1 == DLT_EN10MB。其他链路层（如 Loopback）首部不同，首版不支持。
        if (linkType != 1)
            return null;

        if (frameLength < EtherHeaderLength)
            return null;

        var frame = frameBuffer.AsSpan(0, frameLength);
        var offset = EtherHeaderLength;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);

        // 处理 802.1Q / QinQ 标签
        while (etherType is EtherTypeVlan or EtherTypeQinQ)
        {
            if (frame.Length < offset + VlanTagLength)
                return null;

            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
            offset += VlanTagLength;
        }

        if (etherType != EtherTypeIpv4)
            return null;

        return ParseIpv4(frameBuffer, frame, offset);
    }

    private static UdpFrame? ParseIpv4(byte[] frameBuffer, ReadOnlySpan<byte> frame, int ipOffset)
    {
        if (frame.Length < ipOffset + Ipv4HeaderMinLength)
            return null;

        var versionAndIhl = frame[ipOffset];
        if ((versionAndIhl >> 4) != 4)
            return null;

        var ipHeaderLength = (versionAndIhl & 0x0F) * 4;
        if (ipHeaderLength < Ipv4HeaderMinLength)
            return null;

        if (frame[ipOffset + 9] != ProtocolUdp)
            return null;

        var udpOffset = ipOffset + ipHeaderLength;
        if (frame.Length < udpOffset + UdpHeaderLength)
            return null;

        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame[(udpOffset + 4)..]);
        var payloadLength = udpLength - UdpHeaderLength;

        if (payloadLength < 0 || udpOffset + UdpHeaderLength + payloadLength > frame.Length)
            return null;

        // ★ 零拷贝：切片直接指向租借缓冲的 UDP 载荷区（不再 new byte[] 拷贝）
        var payload = frameBuffer.AsMemory(udpOffset + UdpHeaderLength, payloadLength);

        return new UdpFrame(
            FormatIp(frame, ipOffset + 12),
            BinaryPrimitives.ReadUInt16BigEndian(frame[udpOffset..]),
            FormatIp(frame, ipOffset + 16),
            BinaryPrimitives.ReadUInt16BigEndian(frame[(udpOffset + 2)..]),
            payload,
            frameBuffer
        );
    }

    private static string FormatIp(ReadOnlySpan<byte> frame, int offset) =>
        $"{frame[offset]}.{frame[offset + 1]}.{frame[offset + 2]}.{frame[offset + 3]}";
}
