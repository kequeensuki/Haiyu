using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Session;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

internal sealed class ServerMessagesExportModule : IGameDataExportModule, ILegacyExportProjection
{
    internal static readonly GameDataExportModuleDescriptor Definition = new("server-messages", "Toolkit_GameDataExportAllMessages", "服务端消息事件", false, false);
    private readonly EventExportSession _events;
    private readonly GameDataExportModuleDescriptor _descriptor;
    private readonly IReadOnlySet<ushort>? _messageIds;
    private readonly bool _legacyProjection;
    private readonly ExportCaptureContext _context;
    private GameDataExportModuleState? _ended;
    internal ServerMessagesExportModule(ExportCaptureContext context, EventExportSession? events = null,
        GameDataExportModuleDescriptor? descriptor = null, IReadOnlySet<ushort>? messageIds = null)
    {
        _context = context; _descriptor = descriptor ?? Definition; _messageIds = messageIds;
        _legacyProjection = descriptor is null;
        _events = events ?? new(context.Output, context.CaptureId, moduleId: _descriptor.Id,
            partition: descriptor is null ? null : Path.Combine("modules", Uri.EscapeDataString(descriptor.Id)), startedAt: context.StartedAt);
    }
    public GameDataExportModuleDescriptor Descriptor => _descriptor;
    public IReadOnlySet<ushort>? MessageIds => _messageIds;
    public void Consume(MessageObservation message) => _events.Observe(message);
    public void OnSessionChanged(ExportSessionChange change)
    {
        if (change.Kind == ExportSessionChangeKind.IdentityResolved && change.PlayerId is > 0 && change.Verified)
            _events.Bind(change.SessionId, change.PlayerId.Value);
    }
    public void Checkpoint(bool stopping = false) { _events.ThrowIfFailed(); }
    public void CheckpointMetadata() => _events.Checkpoint();
    public GameDataExportModuleResult GetProgress()
    {
        var stats = _events.Snapshot();
        return new(_descriptor.Id, _descriptor.DisplayName, _ended ?? (stats.Error is null ? GameDataExportModuleState.Running : GameDataExportModuleState.Failed),
            stats.Error, new GameDataExportCounter[]
            {
                new("received", stats.Received), new("written", stats.Written), new("decoded", stats.Decoded),
                new("unknown", stats.Unknown), new("failed", stats.Failed), new("unresolved", stats.Unresolved), new("dropped", stats.Dropped)
            }.Concat(stats.Players.SelectMany(player => new GameDataExportCounter[]
            {
                new("written", player.Written, player.PlayerId), new("decoded", player.Decoded, player.PlayerId),
                new("unknown", player.Unknown, player.PlayerId), new("failed", player.Failed, player.PlayerId)
            })).ToArray(), [],
            _events.Files.Select(file => new GameDataExportArtifact(_descriptor.Id, null, "", _context.Output.Resolve(file), "jsonl", stats.Written, "EventOrder", [])).Append(
                new(_descriptor.Id, null, "", stats.ManifestPath, "manifest", stats.Written, "EventOrder", [])).ToArray());
    }
    public async ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop)
    {
        var stats = await _events.FinishAsync(stop.Error ?? (stop.IncompleteTransport ? "停止时存在未完成的 KCP 重组数据。" : null)).ConfigureAwait(false);
        _ended = stats.Error is not null ? GameDataExportModuleState.Incomplete : stats.Written == 0 ? GameDataExportModuleState.NoData : GameDataExportModuleState.Completed;
        return GetProgress();
    }
    public GameDataExportResult ProjectResult(GameDataExportResult result) => _legacyProjection ? result with { EventExport = _events.Snapshot() } : result;
    public GameDataExportProgress ProjectProgress(GameDataExportProgress progress) => _legacyProjection ? progress with { EventExport = _events.Snapshot() } : progress;
    public ValueTask DisposeAsync() => _events.DisposeAsync();
}
