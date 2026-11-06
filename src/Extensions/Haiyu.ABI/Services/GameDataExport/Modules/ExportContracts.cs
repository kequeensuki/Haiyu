using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

internal enum ExportSessionChangeKind
{
    LoginStarted,
    IdentityResolved,
    Logout,
    TransportResumed,
    ConnectionRetired,
}

internal sealed record ExportSessionChange(ExportSessionChangeKind Kind, string SessionId, string? PreviousSessionId,
    int? PlayerId, uint ConnectionId, uint? PreviousConnectionId = null, bool IsCurrent = true, bool Verified = true);

internal sealed record ExportStopContext(string? Error, bool IncompleteTransport);

internal sealed class ExportCaptureContext(JsonExportStore output, TimeProvider? clock = null, string? captureId = null)
{
    private readonly Dictionary<string, string> _legacyKeys = new(StringComparer.Ordinal);
    internal JsonExportStore Output { get; } = output;
    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal string CaptureId { get; } = captureId ?? DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    internal int CapturedFrames { get; set; }
    internal int DecodedFrames { get; set; }
    internal string IndexRelativePath => Path.Combine("captures", CaptureId, "capture-index.json");
    internal string IndexPath => Output.Resolve(IndexRelativePath);
    internal void RememberLegacyMetadata(GameSessionContext session)
    {
        if (session.SessionKey is not null)
        {
            if (_legacyKeys.Count >= 4096 && !_legacyKeys.ContainsKey(session.SessionId)) throw new IOException("登录会话数量达到上限。");
            _legacyKeys[session.SessionId] = Convert.ToHexString(session.SessionKey).ToLowerInvariant();
        }
    }
    internal string? LegacyKey(string session) => _legacyKeys.GetValueOrDefault(session);
}

internal interface IGameDataExportModule : IAsyncDisposable
{
    GameDataExportModuleDescriptor Descriptor { get; }
    IReadOnlySet<ushort>? MessageIds { get; }
    long ProgressRevision => 0;
    void Consume(MessageObservation message);
    void OnSessionChanged(ExportSessionChange change);
    void Checkpoint(bool stopping = false);
    void CheckpointMetadata() { }
    GameDataExportModuleResult GetProgress();
    ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop);
}

/// <summary>旧 ABI 的投影在模块适配边界实现，调度器不解释任何业务字段。</summary>
internal interface ILegacyExportProjection
{
    GameDataExportResult ProjectResult(GameDataExportResult result);
    GameDataExportProgress ProjectProgress(GameDataExportProgress progress);
}

internal sealed record ExportModuleRegistration(GameDataExportModuleDescriptor Descriptor,
    Func<ExportCaptureContext, IGameDataExportModule> Create);

internal sealed class ExportModuleRegistry(IEnumerable<ExportModuleRegistration> registrations)
{
    private readonly Dictionary<string, ExportModuleRegistration> _registrations = registrations.Select(Validate).ToDictionary(r => r.Descriptor.Id, StringComparer.Ordinal);
    private static ExportModuleRegistration Validate(ExportModuleRegistration registration)
    {
        var id = registration.Descriptor.Id;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("模块 ID 必须为 1–64 个 ASCII 字母、数字、连字符或下划线。");
        return registration;
    }
    internal GameDataExportModuleDescriptor[] Describe() => _registrations.Values.Select(r => r.Descriptor).ToArray();
    internal IGameDataExportModule[] Create(IReadOnlyList<string> ids, ExportCaptureContext context)
    {
        if (ids.Count == 0) throw new ArgumentException("请至少选择一个数据导出模块。");
        if (ids.Count > 64 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("导出模块数量超限或存在重复模块。");
        foreach (var id in ids) if (!_registrations.ContainsKey(id)) throw new ArgumentException($"未知导出模块：{id}");
        var created = new List<IGameDataExportModule>();
        try
        {
            foreach (var id in ids) created.Add(_registrations[id].Create(context));
            return created.ToArray();
        }
        catch
        {
            foreach (var module in created) module.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
