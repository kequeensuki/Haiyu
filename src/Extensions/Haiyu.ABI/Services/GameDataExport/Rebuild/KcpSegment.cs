namespace Haiyu.ABI.Services.GameDataExport.Rebuild;

using System.Buffers.Binary;

/// <summary>
/// KCP 段头（24 字节，全部小端）。
///
/// <code>
/// 偏移 长度 字段
///  0    4   conv   会话 ID
///  4    1   cmd    81=PUSH 82=ACK 83=WASK 84=WINS
///  5    1   frg    分片号，0 表示最后一片
///  6    2   wnd    窗口
///  8    4   ts     时间戳
/// 12    4   sn     序列号
/// 16    4   una    累计确认号
/// 20    4   len    本段数据长度
/// </code>
/// </summary>
public readonly record struct KcpSegment(
    uint Conv,
    byte Cmd,
    byte Frg,
    ushort Wnd,
    uint Ts,
    uint Sn,
    uint Una,
    uint Len,
    byte[] Payload
)
{
    public const int HeaderLength = 24;
    public const byte CmdPush = 81;
    public const byte CmdAck = 82;
    public const byte CmdWask = 83;
    public const byte CmdWins = 84;

    /// <summary>CRC 校验开启时，段尾额外的 4 字节 CRC32。</summary>
    public const int CrcTailLength = 4;

    /// <summary>
    /// 解析一个 UDP 数据报里的<b>全部</b> KCP 段。
    ///
    /// <para>
    /// ★ 判定必须在 <c>uint</c> 域内比较长度，绝不能先转 <c>int</c>：
    ///   超过 <c>int.MaxValue</c> 的垃圾长度会溢出成负数，
    ///   后续按它分配缓冲区会直接崩（本项目在 PowerShell 版上踩过）。
    /// </para>
    ///
    /// <para>
    /// ★ 为什么必须循环切段（真实抓包实证 + native 语义对齐）：
    /// </para>
    /// <para>
    /// native 的 <c>ikcp_input</c>（IDA 逐行实证，见 OfflineClient
    /// <c>KcpClient.Input</c>）是 <c>while</c> 循环：<b>一个 UDP 数据报可以承载多个
    /// KCP 段</b>，逐段推进 <c>offset += 24 + len</c>，直到剩余不足 24 字节。
    /// </para>
    /// <para>
    /// 真实抓包（waves-udp.pcapng 帧 83）证实服务器确实会拼段：
    /// 一个 329 字节的数据报 = <c>ACK(sn=0)</c> + <c>PUSH(sn=0, len=277)</c> + CRC(4)，
    /// 其中那个 PUSH 正是 <c>112 ProtoKeyResponse</c> —— 每条连接的第一个数据帧。
    /// 只解析首段（ACK）会把 112 整个丢掉，且日志上完全看不出来。
    /// </para>
    /// <para>
    /// ★ CRC 只算一次，覆盖整个数据报（<c>[0, len-4)</c>）——所有段共享同一个尾校验。
    /// 这与 native 一致：<c>ikcp_input</c> 在循环外先做一次 CRC。
    /// </para>
    /// <para>
    /// ★ conv / cmd / len 校验失败时<b>停止解析</b>而非返回 false——
    /// native 遇到坏段会带错误码提前 return，但已解析的前序段仍然有效。
    /// 这里返回<b>已成功解析的段</b>，调用方不会因末尾坏段丢掉整个数据报。
    /// </para>
    /// </summary>
    /// <param name="datagram">完整 UDP 数据报（CRC 开启时含尾 4 字节）</param>
    /// <param name="crcEnabled">握手响应下发的 crcCheck 开关</param>
    /// <param name="segments">解析出的段（可能为空）</param>
    /// <returns>是否至少解析出一个段。</returns>
    public static bool TryParseMany(
        ReadOnlySpan<byte> datagram,
        bool crcEnabled,
        out List<KcpSegment> segments
    )
    {
        segments = [];

        var minimumLength = HeaderLength + (crcEnabled ? CrcTailLength : 0);
        if (datagram.Length < minimumLength)
            return false;

        // CRC 覆盖 [0, len-4)，整包只算一次（native: ikcp_input 循环外的 CRC 块）
        if (crcEnabled)
        {
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(
                datagram[(datagram.Length - CrcTailLength)..]
            );
            if (stored != Crc32.Compute(datagram[..^CrcTailLength]))
                return false;
        }

        // CRC 尾在整个数据报的末尾，各段共享；段边界从 offset 0 起逐段推进
        var end = datagram.Length - (crcEnabled ? CrcTailLength : 0);
        var offset = 0;

        while (offset + HeaderLength <= end)
        {
            var cmd = datagram[offset + 4];
            if (cmd is not (CmdPush or CmdAck or CmdWask or CmdWins))
                return segments.Count > 0;

            // ★ uint 域内比较：垃圾长度不能先转 int（见 TryParse 的说明）
            var len = BinaryPrimitives.ReadUInt32LittleEndian(datagram[(offset + 20)..]);
            var available = (uint)(end - offset - HeaderLength);
            if (len > available)
                return segments.Count > 0;

            var body = new byte[len];
            if (len > 0)
                datagram
                    .Slice(offset + HeaderLength, (int)len)
                    .CopyTo(body);

            segments.Add(
                new KcpSegment(
                    BinaryPrimitives.ReadUInt32LittleEndian(datagram[offset..]),
                    cmd,
                    datagram[offset + 5],
                    BinaryPrimitives.ReadUInt16LittleEndian(datagram[(offset + 6)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(datagram[(offset + 8)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(datagram[(offset + 12)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(datagram[(offset + 16)..]),
                    len,
                    body
                )
            );

            // native: v4 = v10 - v47; data += 24 + len —— 推进到下一段
            offset += HeaderLength + (int)len;
        }

        return segments.Count > 0;
    }
}
