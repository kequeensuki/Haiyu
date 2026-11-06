using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace Haiyu.ABI.Services.GameDataExport.Events;

/// <summary>事件格式和玩家归属投影；队列、容量和文件句柄由共享输出层负责。</summary>
internal sealed class EventExportSession : IGameMessageObserver, IAsyncDisposable
{
    internal sealed record Limits(int QueueBytes = 32 * 1024 * 1024, int QueueItems = 512, long ShardBytes = 128L * 1024 * 1024, long CaptureBytes = 1024L * 1024 * 1024, int OpenFiles = 16);
    private sealed record Envelope(byte[] Bytes, int? Player, string Session, string Status);
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt;
    private readonly ProtocolCatalog _catalog = ProtocolCatalog.Default.Value;
    private readonly JsonExportStore _output;
    private readonly string _moduleId;
    private readonly string _manifestRelative;
    private readonly JsonLinesArtifactWriter _lines;
    private readonly bool _ownsStore;
    private readonly Limits _limits;
    private readonly Dictionary<string, int> _bindings = new();
    private readonly Dictionary<string, long> _statuses = new();
    private readonly Dictionary<int, GameDataExportPlayerEventStats> _players = new();
    private readonly Dictionary<string, GameDataExportPlayerEventStats> _pending = new();
    private long _index, _received, _written, _decoded, _unknown, _failed, _unresolved, _dropped, _bytes;
    private string? _error;
    private bool _finished;
    internal string CaptureId { get; }
    internal string ManifestPath { get; }
    internal IReadOnlyList<string> Files => _lines.Files;

    internal EventExportSession(string root, Limits? limits = null)
        : this(new JsonExportStore(root, new((limits ?? new()).QueueBytes, (limits ?? new()).QueueItems, (limits ?? new()).CaptureBytes)),
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"), limits, true) { }

    internal EventExportSession(JsonExportStore output, string captureId, Limits? limits = null, bool ownsStore = false, string moduleId = "server-messages", string? partition = null, DateTimeOffset? startedAt = null)
    {
        _startedAt = startedAt ?? DateTimeOffset.UtcNow;
        _output = output; _moduleId = moduleId; CaptureId = captureId; _ownsStore = ownsStore; _limits = limits ?? new();
        _manifestRelative = Path.Combine("captures", captureId, partition ?? "", "manifest.json");
        ManifestPath = output.Resolve(_manifestRelative);
        _lines = new(output, (key, shard) => key == "unresolved"
            ? Path.Combine("captures", captureId, partition ?? "", $"unresolved-{shard:D6}.jsonl")
            : Path.Combine(key, "captures", captureId, partition ?? "", $"events-{shard:D6}.jsonl"), _limits.ShardBytes, _limits.OpenFiles);
        _output.QueueMetadata(_manifestRelative, EncodeManifest(false));
    }
    internal void ThrowIfFailed()
    {
        _output.DrainReceipts();
        _output.ThrowIfFailed();
        if (_error is not null) throw new IOException(_error);
    }
    public void Observe(MessageObservation message) => Publish(message.Frame, message.Plain, message.PlayerId,
        message.SessionId, message.ConnectionId, message.Status, message.Protocol, message.EventIndex, message.ReceivedAt);
    private void Publish(GatewayFrame frame, byte[]? plain, int? player, string session, uint connection,
        string? status, ProtocolJsonDecoder.Result? decoded, long eventIndex = 0, DateTimeOffset? receivedAt = null)
    {
        ThrowIfFailed();
        lock (_gate) _received++;
        if ((plain ?? frame.Payload).Length > _limits.QueueBytes / 2)
            throw new IOException("单条消息超过事件导出的内存预算，已停止捕获。");
        var encoded = decoded?.Json;
        status ??= frame.Type == GatewayMessageType.Exception ? "transport_exception" : decoded?.Status ?? "key_unavailable";
        if (frame.Type == GatewayMessageType.Exception && decoded is not null)
            status = "transport_exception";
        using var buffer = new LimitedBuffer(_limits.QueueBytes);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("captureId", CaptureId);
            json.WriteNumber("eventIndex", ++_index);
            if (eventIndex > 0) json.WriteNumber("captureEventIndex", eventIndex);
            json.WriteString("receivedAt", receivedAt ?? DateTimeOffset.UtcNow);
            json.WriteString("sessionId", session);
            json.WriteNumber("connectionId", connection);
            if (player is { } id)
                json.WriteNumber("playerId", id);
            else
                json.WriteNull("playerId");
            json.WriteString("direction", "server_to_client");
            json.WriteString("frameType", frame.Type.ToString());
            if (frame.Type == GatewayMessageType.Exception)
                json.WriteNumber("errorCode", frame.Crc);
            if (frame.Type is GatewayMessageType.Exception or GatewayMessageType.Invalid)
                json.WriteNull("msgId");
            else
                json.WriteNumber("msgId", frame.MsgId);
            json.WriteString("messageType", decoded?.MessageType);
            if (frame.Type == GatewayMessageType.Invalid) json.WriteNull("seqNo");
            else json.WriteNumber("seqNo", frame.SeqNo);
            if (frame.Type is GatewayMessageType.Invalid or GatewayMessageType.Push) json.WriteNull("rpcId");
            else json.WriteNumber("rpcId", frame.RpcId);
            json.WriteString("schemaVersion", _catalog.Version);
            json.WriteString("decodeStatus", status);
            json.WriteBoolean("hasUnknownFields", decoded?.HasUnknownFields ?? false);
            json.WritePropertyName("payload");
            if (encoded is { } payload)
                json.WriteRawValue(payload, skipInputValidation: true);
            else
                json.WriteNullValue();
            if (encoded == null || decoded.HasUnknownFields)
            {
                json.WriteString("rawEncoding", frame.Type == GatewayMessageType.Invalid ? "gateway_fragment"
                    : frame.Type == GatewayMessageType.Exception ? "transport_control"
                    : plain == null ? "encrypted_or_unavailable" : "protobuf");
                json.WriteBase64String("rawPayload", plain ?? frame.Payload);
            }

            if (decoded?.Error is { } error)
                json.WriteString("error", error);
            json.WriteEndObject();
        }

        var bytes = new byte[buffer.Length + 1];
        buffer.ToArray().CopyTo(bytes, 0);
        bytes[^1] = 10;
        try
        {
            _output.Enqueue(_moduleId, "event-stream/" + CaptureId + "/" + Uri.EscapeDataString(_moduleId), CaptureId, _index, bytes,
                async data => await _lines.AppendAsync(player?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unresolved", data).ConfigureAwait(false),
                receipt => Written(new(bytes, player, session, status), receipt), true);
        }
        catch (IOException ex) { lock (_gate) { _error ??= ex.Message; _dropped++; } throw; }
    }

    public void Bind(string session, int player)
    {
        _output.DrainReceipts();
        if (_bindings.TryGetValue(session, out var oldBinding))
        { if (oldBinding != player) throw new InvalidDataException("事件会话归属冲突。"); return; }
        if (_bindings.Count >= 4096) throw new IOException("本次捕获的登录会话数量达到上限。");
        _bindings.Add(session, player);
        if (_pending.Remove(session, out var pending))
        {
            _unresolved -= pending.Written;
            var old = _players.GetValueOrDefault(player) ?? new(player, 0, 0, 0, 0);
            _players[player] = new(player, old.Written + pending.Written, old.Decoded + pending.Decoded,
                old.Unknown + pending.Unknown, old.Failed + pending.Failed);
        }
    }
    internal void Checkpoint()
    {
        _output.DrainReceipts();
        _output.Enqueue(_moduleId, "flush/" + CaptureId + "/" + Uri.EscapeDataString(_moduleId), CaptureId, 0, [], async _ =>
        { await _lines.FlushAsync().ConfigureAwait(false); return ManifestPath; }, _ => { }, true);
        _output.QueueMetadata(_manifestRelative, EncodeManifest(false));
    }
    private void Written(Envelope item, OutputReceipt receipt)
    {
        if (receipt.Error is not null) { _error ??= receipt.Error; return; }
        lock (_gate)
        {
            _bytes += item.Bytes.Length;
            _written++;
            var good = item.Status.StartsWith("decoded", StringComparison.Ordinal);
            var unknown = item.Status == "unknown_message";
            if (good)
                _decoded++;
            else if (unknown)
                _unknown++;
            else
                _failed++;
            _statuses[item.Status] = _statuses.GetValueOrDefault(item.Status) + 1;
            var owner = item.Player ?? (_bindings.TryGetValue(item.Session, out var bound) ? bound : (int? )null);
            if (owner is { } id)
            {
                var p = _players.GetValueOrDefault(id) ?? new(id, 0, 0, 0, 0);
                _players[id] = p with
                {
                    Written = p.Written + 1,
                    Decoded = p.Decoded + (good ? 1 : 0),
                    Unknown = p.Unknown + (unknown ? 1 : 0),
                    Failed = p.Failed + (!good && !unknown ? 1 : 0)
                };
            }
            else
            {
                if (_pending.Count >= 4096)
                    throw new IOException("待归属会话数量达到上限。");
                _unresolved++;
                var p = _pending.GetValueOrDefault(item.Session) ?? new(0, 0, 0, 0, 0);
                _pending[item.Session] = p with
                {
                    Written = p.Written + 1,
                    Decoded = p.Decoded + (good ? 1 : 0),
                    Unknown = p.Unknown + (unknown ? 1 : 0),
                    Failed = p.Failed + (!good && !unknown ? 1 : 0)
                };
            }

        }
    }
    internal GameDataExportEventStats Snapshot()
    {
        _output.DrainReceipts();
        lock (_gate) return new(CaptureId, ManifestPath, _received, _written, _decoded, _unknown, _failed, _unresolved,
            _dropped, _bytes, _finished && _error is null, _error, _players.Values.OrderBy(p => p.PlayerId).ToArray());
    }
    internal async Task<GameDataExportEventStats> FinishAsync(string? error)
    {
        if (_finished) return Snapshot();
        _error ??= error;
        await _output.FlushAsync().ConfigureAwait(false);
        try { await _lines.DisposeAsync().ConfigureAwait(false); }
        catch (IOException ex) { _error ??= ex.Message; }
        _finished = true;
        _dropped = Math.Max(_dropped, _received - _written);
        if (_received != _written) _error ??= "事件队列未完全写入，捕获结果不完整。";
        try { await _output.MetadataAsync(_manifestRelative, EncodeManifest(true)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error ??= ex.Message; }
        return Snapshot();
    }
    private byte[] EncodeManifest(bool ended)
    {
        var stats = Snapshot();
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("captureId", CaptureId);
            json.WriteString("moduleId", _moduleId);
            json.WriteString("schemaVersion", _catalog.Version);
            json.WriteString("sourceCommit", _catalog.SourceCommit);
            json.WriteString("state", ended ? (stats.Complete ? "completed" : "incomplete") : "running");
            json.WriteString("startedAt", _startedAt);
            if (ended) json.WriteString("endedAt", DateTimeOffset.UtcNow); else json.WriteNull("endedAt");
            json.WriteString("endReason", ended ? (stats.Error is null ? "caller_stop" : "error") : null);
            json.WriteStartObject("limits");
            json.WriteNumber("queueBytes", _limits.QueueBytes); json.WriteNumber("queueItems", _limits.QueueItems);
            json.WriteNumber("shardBytes", _limits.ShardBytes); json.WriteNumber("captureBytes", _limits.CaptureBytes);
            json.WriteNumber("openFiles", _limits.OpenFiles); json.WriteEndObject();
            json.WriteString("error", stats.Error);
            json.WriteNumber("received", stats.Received);
            json.WriteNumber("written", stats.Written);
            json.WriteNumber("decoded", stats.Decoded);
            json.WriteNumber("unknown", stats.Unknown);
            json.WriteNumber("failed", stats.Failed);
            json.WriteNumber("unresolved", stats.Unresolved);
            json.WriteNumber("dropped", stats.Dropped);
            json.WriteNumber("bytesWritten", stats.BytesWritten);
            json.WriteStartObject("sessionBindings");
            foreach (var(session, player)in _bindings)
                json.WriteNumber(session, player);
            json.WriteEndObject();
            json.WriteStartObject("statuses");
            foreach (var(status, count)in _statuses)
                json.WriteNumber(status, count);
            json.WriteEndObject();
            json.WriteStartArray("files");
            foreach (var file in _lines.Files)
                json.WriteStringValue(file);
            json.WriteEndArray();
            json.WriteStartArray("players");
            foreach (var p in stats.Players)
            {
                json.WriteStartObject();
                json.WriteNumber("playerId", p.PlayerId);
                json.WriteNumber("written", p.Written);
                json.WriteNumber("decoded", p.Decoded);
                json.WriteNumber("unknown", p.Unknown);
                json.WriteNumber("failed", p.Failed);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();

        }
        return buffer.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        if (!_finished) await FinishAsync("捕获未正常结束。").ConfigureAwait(false);
        if (_ownsStore) await _output.DisposeAsync().ConfigureAwait(false);
    }
}
