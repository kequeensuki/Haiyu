using MemoryPack;

namespace ABI.Models;

/// <summary>显式保留 ABI 的 MemoryPack Formatter，供 NativeAOT 两端初始化。</summary>
public static class ABIMemoryPack
{
    private static int _registered;

    public static MemoryPackSerializerOptions Options { get; } = new()
    {
        StringEncoding = StringEncoding.Utf8,
    };

    public static void EnsureFormatters()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        MemoryPackFormatterProvider.Register<CleanMemoryRequest>();
        MemoryPackFormatterProvider.Register<CleanMemoryProgress>();
        MemoryPackFormatterProvider.Register<CMonitorRequest>();
        MemoryPackFormatterProvider.Register<CMonitorProgress>();
        MemoryPackFormatterProvider.Register<FpsMonitorRequest>();
        MemoryPackFormatterProvider.Register<FpsMonitorProgress>();
        MemoryPackFormatterProvider.Register<XboxSessionRequest>();
        MemoryPackFormatterProvider.Register<XboxSessionReportProcess>();
        MemoryPackFormatterProvider.Register<XboxSession>();
        MemoryPackFormatterProvider.Register<RunResult>();
        MemoryPackFormatterProvider.Register<MonitorRecord>();
        MemoryPackFormatterProvider.Register<HardwareInfo>();
        MemoryPackFormatterProvider.Register<CPUData>();
        MemoryPackFormatterProvider.Register<GPUData>();
        MemoryPackFormatterProvider.Register<MemoryData>();
        MemoryPackFormatterProvider.Register<VirtualMemoryData>();
        MemoryPackFormatterProvider.Register<NetworkData>();
        MemoryPackFormatterProvider.Register<FPSData>();
        MemoryPackFormatterProvider.Register<ABISystemConfigRequest>();
        MemoryPackFormatterProvider.Register<ABISystemConfigProgress>();
        MemoryPackFormatterProvider.Register<PipeMessage>();
        MemoryPackFormatterProvider.Register<OpenRequestMessage>();

        // 游戏数据导出（管道传输体，AOT 下必须显式注册全部 MemoryPackable 类型）
        MemoryPackFormatterProvider.Register<GameDataExportRequest>();
        MemoryPackFormatterProvider.Register<GameDataExportProgress>();
        MemoryPackFormatterProvider.Register<GameDataExportResult>();
        MemoryPackFormatterProvider.Register<GameInventoryData>();
        MemoryPackFormatterProvider.Register<GameNormalItem>();
        MemoryPackFormatterProvider.Register<GameWeaponItem>();
        MemoryPackFormatterProvider.Register<GamePhantomItem>();
        MemoryPackFormatterProvider.Register<GamePhantomProp>();
        MemoryPackFormatterProvider.Register<GameValidTimeItem>();
    }
}
