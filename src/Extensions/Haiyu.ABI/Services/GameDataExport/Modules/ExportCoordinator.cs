using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Session;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>串行调度启用模块；只汇总模块状态，不解释业务数据或完成条件。</summary>
internal sealed class ExportCoordinator : IGameMessageObserver, IAsyncDisposable
{
    private readonly ExportCaptureContext _context;
    private readonly IGameDataExportModule[] _modules;
    private string? _fatal;
    private readonly Dictionary<string, long> _progressRevisions = new(StringComparer.Ordinal);
    internal long MessageCount { get; private set; }
    internal IReadOnlyList<IGameDataExportModule> Modules => _modules;
    internal ExportCoordinator(ExportCaptureContext context, ExportModuleRegistry registry, IReadOnlyList<string> selected)
    {
        _context = context;
        _modules = registry.Create(selected, context);
    }
    public void Observe(MessageObservation message)
    {
        MessageCount++;
        message = message with { CaptureId = _context.CaptureId, EventIndex = MessageCount, ReceivedAt = message.ReceivedAt ?? _context.Clock.GetUtcNow() };
        foreach (var module in _modules)
        {
            if (module.MessageIds is { } ids && !ids.Contains(message.Frame.MsgId)) continue;
            module.Consume(message);
        }
    }
    public void Bind(string sessionId, int playerId) => SessionChanged(new(ExportSessionChangeKind.IdentityResolved,
        sessionId, sessionId, playerId, 0, IsCurrent: false));
    public void SessionChanged(ExportSessionChange change)
    {
        foreach (var module in _modules) module.OnSessionChanged(change);
    }
    internal bool TakeProgressChanged()
    {
        var changed = false;
        foreach (var module in _modules)
        {
            var revision = module.ProgressRevision;
            if (_progressRevisions.GetValueOrDefault(module.Descriptor.Id) == revision) continue;
            _progressRevisions[module.Descriptor.Id] = revision;
            changed = true;
        }
        return changed;
    }
    internal void Checkpoint()
    {
        _context.Output.DrainReceipts();
        _context.Output.ThrowIfFailed();
        foreach (var module in _modules) module.Checkpoint();

    }
    internal GameDataExportModuleResult[] Progress()
    {
        _context.Output.DrainReceipts();
        return _modules.Select(m => m.GetProgress()).ToArray();
    }
    internal void CheckpointMetadata()
    {
        foreach (var module in _modules) module.CheckpointMetadata();
        QueueIndex();
    }
    internal void QueueIndex() => _context.Output.QueueMetadata(_context.IndexRelativePath, EncodeIndex(false));
    internal byte[] EncodeIndex(bool ended, GameDataExportModuleResult[]? results = null)
    {
        var modules = results ?? Progress();
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("formatVersion", 1);
            json.WriteString("captureId", _context.CaptureId);
            json.WriteString("startedAt", _context.StartedAt);
            json.WriteString("state", ended ? (_fatal is null && modules.All(m => m.State == GameDataExportModuleState.Completed) ? "completed" : "incomplete") : "running");
            if (ended) json.WriteString("endedAt", DateTimeOffset.UtcNow);
            json.WriteString("error", _fatal);
            json.WriteString("endReason", ended ? (_fatal is null ? "caller_stop" : "error") : null);
            json.WritePropertyName("modules");
            JsonSerializer.Serialize(json, modules, ABIJsonContext.Default.GameDataExportModuleResultArray);
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }
    internal async Task<GameDataExportModuleResult[]> FinishAsync(ExportStopContext stop)
    {
        _fatal ??= stop.Error;
        var results = new List<GameDataExportModuleResult>();
        foreach (var module in _modules)
        {
            try { results.Add(await module.CompleteAsync(stop).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { results.Add(module.GetProgress() with { State = GameDataExportModuleState.Failed, Error = ex.Message }); }
        }
        _fatal ??= _context.Output.Failure;
        try
        {
            await _context.Output.FlushAsync().ConfigureAwait(false);
            await _context.Output.MetadataAsync(_context.IndexRelativePath, EncodeIndex(true, results.ToArray())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _fatal ??= ex.Message; }
        return results.ToArray();
    }
    internal GameDataExportResult LegacyResult(GameDataExportModuleResult[] results)
    {
        var success = _fatal is null && results.Length > 0 && results.All(r => r.State == GameDataExportModuleState.Completed);
        var errors = results.Where(r => r.State != GameDataExportModuleState.Completed)
            .Select(r => r.DisplayName + "：" + (r.Error ?? r.State.ToString()));
        var result = new GameDataExportResult(success, success ? "数据导出已结束。" : string.Join("；", errors.Prepend(_fatal).OfType<string>()),
            null, null, _context.CapturedFrames, _context.DecodedFrames, null,
            Modules: results, CaptureIndexPath: _context.IndexPath);
        foreach (var projection in _modules.OfType<ILegacyExportProjection>()) result = projection.ProjectResult(result);
        return result;
    }
    internal GameDataExportProgress LegacyProgress(bool ready = false)
    {
        var results = Progress();
        var progress = new GameDataExportProgress(GameDataExportStage.Monitoring, ready ? 5 : 100,
            "数据导出监控中。", CaptureReady: ready, HasDataUpdate: MessageCount > 0,
            Modules: results, CaptureIndexPath: _context.IndexPath);
        foreach (var projection in _modules.OfType<ILegacyExportProjection>()) progress = projection.ProjectProgress(progress);
        return progress;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var module in _modules) await module.DisposeAsync().ConfigureAwait(false);
        await _context.Output.DisposeAsync().ConfigureAwait(false);
    }
}
