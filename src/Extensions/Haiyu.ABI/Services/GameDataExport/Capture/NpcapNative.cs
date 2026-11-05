using System.Runtime.InteropServices;
using System.Text;

namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>
/// Npcap 的 P/Invoke 绑定。
///
/// ★ 两个必须记住的坑（本项目实测踩过）：
///   1. Windows 上 <c>pcap_pkthdr.timeval</c> 的 tv_sec/tv_usec 是 <b>32 位</b>，
///      用 <c>long</c> 会让 caplen 字段错位，读出 2174613024 这种垃圾值。
///   2. <c>bpf_program</c> 必须整结构体 <c>ref</c> 传递；
///      用 <c>out IntPtr</c> 会直接 AV 崩溃（0xC0000005）。
/// </summary>
internal static class NpcapNative
{
    private const string Dll = "wpcap.dll";

    /// <summary>
    /// Npcap 的默认安装目录。
    /// ★ 该目录通常<b>不在 PATH</b> 中，直接 DllImport "wpcap.dll" 会抛
    ///   <c>DllNotFoundException</c>。首次使用时需把此目录加入 DLL 搜索路径。
    /// </summary>
    private const string NpcapDirectory = @"C:\Windows\System32\Npcap";

    private static int _searchPathPatched;

    /// <summary>
    /// 把 Npcap 目录加入进程的 DLL 搜索路径（只执行一次）。
    /// 用 <c>SetDllDirectory</c> 追加而不是替换，避免影响其他组件的 DLL 解析。
    /// </summary>
    internal static void EnsureSearchPath()
    {
        if (Interlocked.Exchange(ref _searchPathPatched, 1) != 0)
            return;

        try
        {
            if (Directory.Exists(NpcapDirectory))
                SetDllDirectory(NpcapDirectory);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非致命：若 wpcap.dll 已在 PATH 中，依然能正常加载
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BpfProgram
    {
        public uint BfLen;
        public IntPtr BfInsns;
    }

    /// <summary>Windows 下 timeval 为两个 32 位值，切勿用 long。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PcapPkthdr
    {
        public int TsSec;
        public int TsUsec;
        public uint Caplen;
        public uint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PcapIf
    {
        public IntPtr Next;
        public IntPtr Name;
        public IntPtr Description;
        public IntPtr Addresses;
        public uint Flags;
    }

    [DllImport(Dll, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_findalldevs(out IntPtr alldevs, byte[] errbuf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_freealldevs(IntPtr alldevs);

    [DllImport(Dll, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pcap_open_live(
        string device,
        int snaplen,
        int promisc,
        int timeoutMs,
        byte[] errbuf
    );

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_setnonblock(IntPtr handle, int nonblock, [Out] byte[] errbuf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_close(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pcap_geterr(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_compile(
        IntPtr handle,
        ref BpfProgram program,
        string filter,
        int optimize,
        uint netmask
    );

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_setfilter(IntPtr handle, ref BpfProgram program);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_freecode(ref BpfProgram program);

    /// <summary>返回 1=有帧，0=超时，-1=错误，-2=EOF。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_next_ex(
        IntPtr handle,
        out IntPtr header,
        out IntPtr data
    );

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_datalink(IntPtr handle);

    // ── 缓冲区调优相关（缺失时静默跳过，不影响主流程）──

    // ★ 不绑定 pcap_create / pcap_set_* / pcap_activate 这一组：
    //   本机 Npcap 上该流程在 pcap_set_snaplen 处 AV 崩溃（0xC0000005），
    //   签名与实际导出不兼容。统一走 pcap_open_live。

    /// <summary>已丢弃的包数（内核层统计）。参数为 32 位 int[3]。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_stats(IntPtr handle, int[] stats);

    internal static string GetLastError(IntPtr handle)
    {
        try
        {
            return Marshal.PtrToStringAnsi(pcap_geterr(handle)) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    internal static string DecodeError(byte[] errbuf)
    {
        var text = Encoding.ASCII.GetString(errbuf).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }
}
