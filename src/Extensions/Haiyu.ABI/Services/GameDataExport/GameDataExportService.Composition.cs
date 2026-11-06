using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Modules;

namespace Haiyu.ABI.Services.GameDataExport;

public sealed partial class GameDataExportService
{
    public GameDataExportService() : this(() => new MultiInterfaceUdpCapture(), LoadPrivateKey,
        BuiltInExportModules.Create(), GameDataExportPaths.OutputDirectory) { }

    // 旧回放测试注入点；生产写入统一由 JsonExportStore 执行。
    internal GameDataExportService(Func<IUdpCapture> capture, Action<SessionKeyLoader> loadKey,
        Func<GameDataExportResult, DateTimeOffset, string?> write, Func<EventExportSession>? events = null, string? outputRoot = null)
        : this(capture, loadKey, BuiltInExportModules.Create(write, events),
            outputRoot ?? Path.Combine(Path.GetTempPath(), "Haiyu.GameDataExport.Replay", Guid.NewGuid().ToString("N"))) { }
}
