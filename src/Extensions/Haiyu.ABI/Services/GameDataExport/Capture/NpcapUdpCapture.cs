using System.Buffers;
using System.Runtime.InteropServices;

namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>
/// 基于 Npcap 的实时 UDP 捕获。
///
/// BPF 固定为 <c>udp</c>——不过滤端口，因为游戏的 KCP 端口每次会话都可能不同
/// （实测见过 13108 / 13111），交给上层按 KCP 结构特征判定。
///
/// 每个实例只管理一张网卡，以非阻塞方式供多接口管理器公平轮询。
/// 捕获缓冲使用 Npcap 默认值，句柄读写与释放由同一捕获线程完成。
/// </summary>
internal sealed class NpcapUdpCapture(CaptureDevice device) : IUdpCapture
{
    private const string BpfFilter = "udp";
    private const int SnapLength = 65535;
    private const int ReadTimeoutMs = 100;


    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;

    /// <summary>实际选中的网卡描述（用于诊断输出）。</summary>
    public string DeviceDescription { get; private set; } = string.Empty;

    /// <summary>链路层类型（1 = Ethernet）。</summary>
    public int LinkType { get; private set; }

    /// <summary>内核层累计丢弃的包数（诊断用）。</summary>
    public int DroppedPackets
    {
        get
        {
            if (_handle == IntPtr.Zero)
                return 0;

            var stats = new int[3];
            return NpcapNative.pcap_stats(_handle, stats) == 0 ? stats[1] : 0;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != IntPtr.Zero)
            return;

        // wpcap.dll 安装在 C:\Windows\System32\Npcap，通常不在 PATH 里
        NpcapNative.EnsureSearchPath();

        _handle = OpenDevice(device.Name);
        DeviceDescription = device.Description;
        try
        {
            LinkType = NpcapNative.pcap_datalink(_handle);
            if (LinkType != 1)
                throw new NotSupportedException($"网卡 {device.Description} 的链路类型 {LinkType} 暂不支持。");
            ApplyFilter();
            var error = new byte[512];
            if (NpcapNative.pcap_setnonblock(_handle, 1, error) != 0)
                throw new IOException($"设置非阻塞捕获失败：{NpcapNative.DecodeError(error)}");
        }
        catch
        {
            CloseHandle();
            throw;
        }
    }

    /// <summary>
    /// 打开设备。
    ///
    /// ★ 只用 <c>pcap_open_live</c>，不用 <c>pcap_create</c>/<c>pcap_activate</c> 流程：
    ///   本机实测该流程在 <c>pcap_set_snaplen</c> 处直接 AV 崩溃（0xC0000005），
    ///   说明当前 Npcap 版本的 create 路径与这组 DllImport 签名不兼容。
    ///   <c>pcap_open_live</c> 稳定可用，缓冲大小交给 Npcap 默认值。
    /// </summary>
    private static IntPtr OpenDevice(string name)
    {
        // snaplen 取 65535 以容纳最大的 UDP 载荷；
        // 之前的丢包是抓包脚本处理太慢，不是缓冲大小问题。
        var errbuf = new byte[512];
        var handle = NpcapNative.pcap_open_live(
            name,
            SnapLength,
            promisc: 0,
            ReadTimeoutMs,
            errbuf
        );

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"打开网卡失败：{NpcapNative.DecodeError(errbuf)}"
            );
        }

        return handle;
    }


    private void ApplyFilter()
    {
        var program = default(NpcapNative.BpfProgram);
        var rc = NpcapNative.pcap_compile(_handle, ref program, BpfFilter, 1, 0);
        if (rc != 0)
        {
            var message = NpcapNative.GetLastError(_handle);
            NpcapNative.pcap_close(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"BPF 编译失败：{message}");
        }

        try
        {
            if (NpcapNative.pcap_setfilter(_handle, ref program) != 0)
                throw new IOException($"设置 UDP 过滤器失败：{NpcapNative.GetLastError(_handle)}");
        }
        finally
        {
            NpcapNative.pcap_freecode(ref program);
        }
    }

    public UdpFrame? TryRead()
    {
        if (_handle == IntPtr.Zero)
            return null;

        var rc = NpcapNative.pcap_next_ex(_handle, out var headerPtr, out var dataPtr);
        if (rc == 0)
            return null;
        if (rc < 0)
            throw new IOException($"网卡 {device.Description} 捕获失败：{NpcapNative.GetLastError(_handle)}");

        var header = Marshal.PtrToStructure<NpcapNative.PcapPkthdr>(headerPtr);
        if (header.Caplen == 0 || header.Caplen > SnapLength)
            return null;

        // ★ 内存纪律（v2 持续监控）：缓冲从 ArrayPool 租借，调用方用完必须归还。
        //   UdpFrame.RentedBuffer 标记租借来源；上层 PumpAsync 在单个数据报处理完
        //   （段已拷贝入流或丢弃）后立即归还——租借缓冲绝不跨数据报存活。
        var length = (int)header.Caplen;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(dataPtr, rented, 0, length);
            var frame = EthernetUdpParser.Parse(rented, length, LinkType);
            if (frame is not null)
                return frame with { InterfaceId = device.Id };
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
        ArrayPool<byte>.Shared.Return(rented);
        return null;
    }

    public void Stop()
    {
        CloseHandle();
    }

    private void CloseHandle()
    {
        if (_handle == IntPtr.Zero)
            return;

        NpcapNative.pcap_close(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CloseHandle();
    }

}
