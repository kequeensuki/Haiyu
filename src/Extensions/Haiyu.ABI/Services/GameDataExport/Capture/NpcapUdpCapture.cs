using System.Runtime.InteropServices;

namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>
/// 基于 Npcap 的实时 UDP 捕获。
///
/// BPF 固定为 <c>udp</c>——不过滤端口，因为游戏的 KCP 端口每次会话都可能不同
/// （实测见过 13108 / 13111），交给上层按 KCP 结构特征判定。
///
/// ★ 缓冲区调优（关键）：默认 1MB 缓冲在突发时会导致**内核丢包**，
///   而 KCP 段丢失会直接导致无法拼出完整帧（实测丢过一次 41%，
///   112 会话密钥帧就在丢失的段里）。这里显式配置大缓冲 + 实时模式。
/// </summary>
public sealed class NpcapUdpCapture : IUdpCapture
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

        var device = SelectDevice();
        _handle = OpenDevice(device.Name);
        DeviceDescription = device.Description;
        LinkType = NpcapNative.pcap_datalink(_handle);
        ApplyFilter();
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
            NpcapNative.pcap_setfilter(_handle, ref program);
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
        if (rc <= 0)
            return null;

        var header = Marshal.PtrToStructure<NpcapNative.PcapPkthdr>(headerPtr);
        if (header.Caplen == 0 || header.Caplen > SnapLength)
            return null;

        var buffer = new byte[header.Caplen];
        Marshal.Copy(dataPtr, buffer, 0, (int)header.Caplen);

        return EthernetUdpParser.Parse(buffer, LinkType);
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

    private static DeviceInfo SelectDevice()
    {
        var errbuf = new byte[512];
        if (NpcapNative.pcap_findalldevs(out var listPtr, errbuf) != 0)
        {
            throw new InvalidOperationException(
                $"枚举网卡失败：{NpcapNative.DecodeError(errbuf)}"
            );
        }

        try
        {
            var candidates = new List<DeviceInfo>();
            var current = listPtr;

            while (current != IntPtr.Zero)
            {
                var device = Marshal.PtrToStructure<NpcapNative.PcapIf>(current);
                candidates.Add(
                    new DeviceInfo(
                        Marshal.PtrToStringAnsi(device.Name) ?? string.Empty,
                        Marshal.PtrToStringAnsi(device.Description) ?? string.Empty
                    )
                );
                current = device.Next;
            }

            // 优先真实网卡：排除 WAN Miniport 与 Loopback
            var selected =
                candidates.FirstOrDefault(d =>
                    d.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
                    || d.Description.Contains("以太网", StringComparison.OrdinalIgnoreCase)
                    || d.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                    || d.Description.Contains("WLAN", StringComparison.OrdinalIgnoreCase)
                )
                ?? candidates.FirstOrDefault(d =>
                    !d.Description.Contains("Miniport", StringComparison.OrdinalIgnoreCase)
                    && !d.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                )
                ?? candidates.FirstOrDefault();

            return selected
                ?? throw new InvalidOperationException("未找到可用网卡（是否已安装 Npcap？）");
        }
        finally
        {
            NpcapNative.pcap_freealldevs(listPtr);
        }
    }

    private sealed record DeviceInfo(string Name, string Description);
}
