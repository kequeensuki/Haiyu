using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Haiyu.ABI.Services.GameDataExport.Capture;

internal sealed record CaptureDevice(string Id, string Name, string Description);
internal sealed record InterfaceAddress(string Id, bool IsUp, bool IsLoopback, IReadOnlyList<IPAddress> Addresses);

internal static class NpcapDeviceCatalog
{
    internal static IReadOnlyList<CaptureDevice> Enumerate()
    {
        NpcapNative.EnsureSearchPath();
        var error = new byte[512];
        if (NpcapNative.pcap_findalldevs(out var list, error) != 0)
            throw new IOException($"枚举 Npcap 网卡失败：{NpcapNative.DecodeError(error)}");
        var devices = new List<CaptureDevice>();
        try
        {
            for (var current = list; current != IntPtr.Zero;)
            {
                var device = Marshal.PtrToStructure<NpcapNative.PcapIf>(current);
                var name = Marshal.PtrToStringAnsi(device.Name) ?? string.Empty;
                devices.Add(new(name, name, Marshal.PtrToStringAnsi(device.Description) ?? name));
                current = device.Next;
            }
        }
        finally { NpcapNative.pcap_freealldevs(list); }
        var interfaces = new List<InterfaceAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
                continue;
            try
            {
                interfaces.Add(new(network.Id, true, network.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    network.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray()));
            }
            catch (NetworkInformationException) { /* 枚举期间接口消失，下次变更时重新枚举。 */ }
        }
        return Select(devices, interfaces);
    }

    internal static IReadOnlyList<CaptureDevice> Select(IEnumerable<CaptureDevice> devices, IEnumerable<InterfaceAddress> interfaces)
    {
        // Windows 的 NPF_{GUID} 与 NetworkInterface.Id 一一对应，不按厂商描述猜测。
        var ids = interfaces.Where(i => i.IsUp && !i.IsLoopback && i.Addresses.Any(IsUsableIpv4))
            .Select(i => Guid.TryParse(i.Id, out var id) ? "{" + id.ToString() + "}" : null)
            .OfType<string>().ToArray();
        return devices.Where(d => ids.Any(id => d.Name.EndsWith(id, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsUsableIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}

internal interface INetworkChangeSource : IDisposable
{
    event Action? Changed;
    void Start();
}

internal sealed class WindowsNetworkChanges : INetworkChangeSource
{
    public event Action? Changed;
    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnAddress;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
    }
    private void OnAddress(object? sender, EventArgs args) => Changed?.Invoke();
    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs args) => Changed?.Invoke();
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddress;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
    }
}
