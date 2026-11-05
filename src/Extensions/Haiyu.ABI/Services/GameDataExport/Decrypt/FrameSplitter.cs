using System.Buffers.Binary;

namespace Haiyu.ABI.Services.GameDataExport.Decrypt;

/// <summary>消息类型（取自帧首字节低 4 位）。</summary>
public enum GatewayMessageType : byte
{
    Request = 1,
    Response = 2,
    Exception = 3,
    Push = 4,
}

/// <summary>
/// 一个已切出的协议帧。
/// <see cref="Payload"/> 是<b>原始密文</b>，未经解密。
/// </summary>
public sealed record GatewayFrame(
    GatewayMessageType Type,
    byte RawType,
    uint SeqNo,
    ushort RpcId,
    ushort MsgId,
    uint Crc,
    byte[] Payload,
    uint OrigSize = 0
)
{
    /// <summary>压缩标志（原始类型字节的 0x10 位）。</summary>
    public bool IsCompressed => (RawType & 0x10) != 0;

    /// <summary>该 msgId 是否走明文（111 ProtoKeyRequest / 112 ProtoKeyResponse）。</summary>
    public bool IsPlaintext => MsgId is 111 or 112;
}

/// <summary>
/// 按 <c>[u24 len][type][seq][rpcId?][msgId?][crc][origSize?][payload]</c> 切帧。
///
/// ★ 长度前缀是 <b>3 字节</b>（u24），不是常见的 2 字节或 4 字节。
///   这是本项目最容易猜错的地方——用 2 字节试时只有 61.6% 能切对。
///
/// ★ 头部布局按 type 分支（照 OfflineClient <c>MessageCodec.TryParseHeader</c>）：
/// <code>
///   type 1/2 (Request/Response): rpcId(u16) + msgId(u16) + crc(u32) → 头 13 字节
///   type 3   (Exception)        : rpcId(u16) + crc(u32)             → 头 11 字节（无 msgId）
///   type 4   (Push)             : msgId(u16) + crc(u32)             → 头 11 字节（无 rpcId）
/// </code>
///
/// ★ 压缩帧（type 高 4 位含 0x10）在 CRC 之后额外有一个 <b>u32 origSize</b>，
///   必须跳过它，否则：
///   ① payload 前 4 字节混入 origSize → 这一帧的内容错位
///   ② 拿 payload 算 CRC 必然不匹配（实测 100 个压缩帧全部 CRC 失败）
/// </summary>
public static class FrameSplitter
{
    private const int LengthFieldLength = 3;
    private const int MinFrameBodyLength = 11;
    private const byte TypeMask = 0x0F;
    private const byte CompressedFlag = 0x10;
    private const int OrigSizeLength = 4;

    /// <summary>
    /// 解压后大小的合理上限（64 MB）。
    /// 超过这个值说明帧头被误判，或数据已损坏——宁可不返回，也不要分配巨量内存。
    /// </summary>
    private const uint MaxOrigSize = 64 * 1024 * 1024;

    /// <summary>
    /// 从流的 <paramref name="offset"/> 处切出一帧。
    /// 数据不足或格式非法返回 <c>false</c>。
    /// </summary>
    /// <summary>
    /// 从流的 <paramref name="offset"/> 处切出一帧。
    ///
    /// <para>
    /// <b>返回值语义</b>（调用方必须区分）：
    /// <list type="bullet">
    ///   <item><c>true</c>：切出一帧，<paramref name="offset"/> 前进。</item>
    ///   <item><c>false</c> + <paramref name="offset"/> 未变：数据不足，等待更多字节。</item>
    ///   <item><c>false</c> + <paramref name="offset"/> <b>已前进</b>：该帧格式非法，已跳过。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ★ 为什么必须「跳过」而不是「终止」：只要流里出现一个坏帧，
    ///   若直接 <c>return false</c> 且不推进 offset，调用方的 while 循环会立刻退出，
    ///   后面**整条流的帧全部丢失**（实测一个坏帧会导致 900+ 帧丢失）。
    ///   这里选择跳过坏帧继续找下一帧——单帧损坏不应摧毁整次导出。
    /// </para>
    /// </summary>
    public static bool TryTake(ReadOnlySpan<byte> stream, ref int offset, out GatewayFrame? frame)
    {
        frame = null;

        if (stream.Length - offset < LengthFieldLength)
            return false;

        // u24 小端
        var bodyLength =
            stream[offset] | (stream[offset + 1] << 8) | (stream[offset + 2] << 16);

        // 长度字段本身非法：无法确定下一帧起点，只能终止（无法安全跳过）
        if (bodyLength < MinFrameBodyLength)
            return false;

        var totalLength = LengthFieldLength + bodyLength;

        // 数据不足：等待更多字节，不推进 offset
        if (stream.Length - offset < totalLength)
            return false;

        // ── 到这里说明帧边界是确定的，后续任何校验失败都应「跳过这一帧」──
        if (!TryDecode(stream.Slice(offset + LengthFieldLength, bodyLength), out frame))
        {
            // 跳过坏帧：offset 前进，调用方继续找下一帧
            offset += totalLength;
            return false;
        }

        offset += totalLength;
        return true;
    }

    /// <summary>解析一帧的帧体（不含 u24 长度前缀）。失败返回 <c>false</c>。</summary>
    private static bool TryDecode(ReadOnlySpan<byte> body, out GatewayFrame? frame)
    {
        frame = null;

        var bodyLength = body.Length;
        var rawType = body[0];
        var baseType = (byte)(rawType & TypeMask);
        var isCompressed = (rawType & CompressedFlag) != 0;

        var headerLength = baseType switch
        {
            1 or 2 => 13,
            3 or 4 => 11,
            _ => 0,
        };

        if (headerLength == 0 || bodyLength < headerLength)
            return false;

        // ★ 压缩帧在 CRC 之后多一个 u32 origSize
        //   必须「跳过并读取」——只跳过不读取会丢掉解压所需的目标大小，
        //   而且无法校验这个字段是否被正确识别。
        uint origSize = 0;
        if (isCompressed)
        {
            headerLength += OrigSizeLength;
            if (bodyLength < headerLength)
                return false;

            var origSizeOffset = headerLength - OrigSizeLength;
            origSize = BinaryPrimitives.ReadUInt32LittleEndian(body[origSizeOffset..]);

            // 合理性校验：0 或超大值都说明帧头判错了
            if (origSize == 0 || origSize > MaxOrigSize)
                return false;
        }

        if (!TryReadHeader(baseType, body, out var rpcId, out var msgId, out var crc))
            return false;

        var seqNo = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        var payloadLength = bodyLength - headerLength;
        var payload = payloadLength > 0 ? body[headerLength..].ToArray() : [];

        frame = new GatewayFrame(
            (GatewayMessageType)baseType,
            rawType,
            seqNo,
            rpcId,
            msgId,
            crc,
            payload,
            origSize
        );

        return true;
    }

    private static bool TryReadHeader(
        byte baseType,
        ReadOnlySpan<byte> body,
        out ushort rpcId,
        out ushort msgId,
        out uint crc
    )
    {
        rpcId = 0;
        msgId = 0;

        var pos = 5; // type(1) + seq(4)

        switch (baseType)
        {
            case 1 or 2:
                rpcId = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
                msgId = BinaryPrimitives.ReadUInt16LittleEndian(body[(pos + 2)..]);
                pos += 4;
                break;
            case 3:
                rpcId = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
                pos += 2;
                break;
            case 4:
                msgId = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
                pos += 2;
                break;
            default:
                crc = 0;
                return false;
        }

        crc = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return true;
    }
}
