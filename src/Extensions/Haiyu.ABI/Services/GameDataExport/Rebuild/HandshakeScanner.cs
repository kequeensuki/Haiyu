using System.Buffers.Binary;
using System.Text;

namespace Haiyu.ABI.Services.GameDataExport.Rebuild;

/// <summary>握手协商出的连接参数。</summary>
public sealed record KcpConnection(uint Conv, int Mtu, bool CrcEnabled, int ClientPort, int ServerPort)
{
    /// <summary>段大小，对齐生产 <c>GatewayClient.cs:197</c> 的 <c>SetKcpSegmentSize</c>。</summary>
    public int SegmentSize => (Mtu - 24) * 127;
}

/// <summary>
/// 从 UDP 帧里识别 KCP 握手，取 <c>conv</c> / <c>mtu</c> / <c>crcCheck</c>。
///
/// ★ 只是一个「扫描器」，不是状态机：拿到想要的就返回，没有就继续看下一帧。
///
/// ★ 两个真实抓包暴露的判定要点（照 OfflineClient <c>ReplayPcap</c> 实证）：
///   1. 不能只看首字节 0xED —— DNS 响应载荷首字节也可能是 0xED。
///      必须同时校验 <c>[1]==0x00 &amp;&amp; [2]==0x20</c>，且 32 字节 trace 全为可打印 ASCII。
///   2. <c>0xEE</c> 必须与 <c>0xED</c> 的端口**配对**（源==服务器端口 且 目的==客户端端口），
///      否则同一网关上的多条并发连接会串味，conv 对不上导致重组全错。
/// </summary>
public sealed class HandshakeScanner
{
    private const byte HandshakeRequestTag = 0xED;
    private const byte HandshakeResponseTag = 0xEE;
    private const int TraceLength = 32;
    private const int RequestHeaderLength = 3;
    private const int MinRequestLength = RequestHeaderLength + TraceLength;
    private const int MinResponseLength = 13;

    private int _clientPort = -1;
    private int _serverPort = -1;

    /// <summary>已识别出的连接参数；为 <c>null</c> 表示握手尚未完成。</summary>
    public KcpConnection? Connection { get; private set; }

    /// <summary>喂入一帧；返回 <c>true</c> 表示本次识别出了完整连接参数。</summary>
    public bool TryFeed(string srcIp, int srcPort, int dstPort, byte[] payload)
    {
        if (Connection is not null)
            return false;

        // ① 找握手请求：确定客户端/服务器端口
        if (_clientPort < 0)
        {
            if (IsHandshakeRequest(payload))
            {
                _clientPort = srcPort;
                _serverPort = dstPort;
            }

            return false;
        }

        // ② 找配对的握手响应：取 conv / mtu / crcCheck
        if (srcPort != _serverPort || dstPort != _clientPort)
            return false;

        if (payload.Length < MinResponseLength || payload[0] != HandshakeResponseTag)
            return false;

        var conv = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1));
        if (conv == 0)
            return false;

        var crcCheck = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(5));
        var mtu = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(9));

        Connection = new KcpConnection(
            conv,
            mtu > 0 ? mtu : 1000,
            crcCheck != 0,
            _clientPort,
            _serverPort
        );

        return true;
    }

    /// <summary>
    /// 从一帧 UDP 载荷里直接解析握手响应（0xEE）。
    ///
    /// <para>
    /// <b>为什么用 0xEE 而不是 0xED 建连接</b>：0xEE 载荷里自带
    /// <c>conv</c> / <c>crcCheck</c> / <c>mtu</c>，一次就能拿到全部参数，
    /// 无需 0xED→0xEE 配对。多连接并存时也能自然区分。
    /// </para>
    ///
    /// <para>
    /// ★ 必须校验 <c>mtu</c> 合理范围：随机 UDP 包首字节可能是 0xEE，
    ///   用 mtu ∈ [500, 9000] 过滤掉绝大多数误判。
    /// </para>
    /// </summary>
    public static bool TryParseHandshakeResponse(
        byte[] payload,
        int srcPort,
        int dstPort,
        out KcpConnection? connection
    )
    {
        connection = null;

        if (payload.Length < MinResponseLength || payload[0] != HandshakeResponseTag)
            return false;

        var conv = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1));
        if (conv == 0)
            return false;

        var crcCheck = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(5));
        var mtu = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(9));

        // mtu 必须合理，否则大概率不是握手帧
        if (mtu is < 500 or > 9000)
            return false;

        // 0xEE 是服务器→客户端，所以 src=服务器端口，dst=客户端端口
        connection = new KcpConnection(conv, mtu, crcCheck != 0, dstPort, srcPort);
        return true;
    }

    /// <summary>
    /// 握手请求：<c>[0]=0xED [1]=0x00 [2]=0x20</c> + 32 字节可打印 ASCII 的 trace。
    /// </summary>
    public static bool IsHandshakeRequest(byte[] payload)
    {
        if (payload.Length < MinRequestLength)
            return false;

        if (payload[0] != HandshakeRequestTag || payload[1] != 0x00 || payload[2] != 0x20)
            return false;

        for (var i = RequestHeaderLength; i < MinRequestLength; i++)
        {
            var b = payload[i];
            if (b < 0x20 || b > 0x7E)
                return false;
        }

        return true;
    }

    /// <summary>取握手请求里的 trace（仅用于诊断日志）。</summary>
    public static string ReadTrace(byte[] payload) =>
        Encoding.ASCII.GetString(payload, RequestHeaderLength, TraceLength);
}
