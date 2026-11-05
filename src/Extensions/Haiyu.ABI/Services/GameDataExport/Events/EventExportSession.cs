using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Events;
/// <summary>单写入者的有界 JSONL 事件流。捕获线程只投递完整事件；磁盘失败/溢出明确停止，不静默丢弃。</summary>
internal sealed class EventExportSession
{
    internal sealed record Limits(int QueueBytes = 32 * 1024 * 1024, int QueueItems = 512, long ShardBytes = 128L * 1024 * 1024, long CaptureBytes = 1024L * 1024 * 1024, int OpenFiles = 16);
    private sealed record Envelope(byte[]? Bytes, int? Player, string Session, string Status, bool Bind = false);
    private sealed class Output(string path, FileStream stream)
    {
        internal string Path = path;
        internal FileStream Stream = stream;
        internal long Bytes;
    }

    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ProtocolCatalog _catalog = ProtocolCatalog.Default.Value;
    private readonly ProtocolJsonDecoder _decoder;
    private readonly Channel<Envelope> _queue;
    private readonly Limits _limits;
    private readonly string _root;
    private readonly Dictionary<string, Output> _outputs = new();
    private readonly Dictionary<string, int> _shards = new();
    private readonly List<string> _files = [];
    private readonly Dictionary<string, int> _bindings = new();
    private readonly Dictionary<string, int> _announced = new();
    private readonly Dictionary<string, long> _statuses = new();
    private readonly Dictionary<int, GameDataExportPlayerEventStats> _players = new();
    private readonly Dictionary<string, GameDataExportPlayerEventStats> _pending = new();
    private readonly Task _worker;
    private long _index, _received, _written, _decoded, _unknown, _failed, _unresolved, _dropped, _bytes, _queued;
    private string? _error;
    private bool _finished;
    internal string CaptureId { get; } = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
    internal string ManifestPath { get; }

    internal EventExportSession(string root, Limits? limits = null)
    {
        _root = root;
        _limits = limits ?? new();
        _decoder = new(_catalog);
        _queue = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(_limits.QueueItems) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        ManifestPath = Path.Combine(root, "captures", CaptureId, "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        WriteManifest(false);
        _worker = Task.Run(WriteAsync);
    }

    internal void ThrowIfFailed()
    {
        lock (_gate)
            if (_error != null)
                throw new IOException(_error);
    }

    internal bool Publish(GatewayFrame frame, byte[]? plain, int? player, string session, uint connection, string? status = null, bool connectionVerified = true)
    {
        ThrowIfFailed();
        lock (_gate) _received++;
        if ((plain ?? frame.Payload).Length > _limits.QueueBytes / 2)
            throw new IOException("单条消息超过事件导出的内存预算，已停止捕获。");
        var decoded = status == null && plain != null ? _decoder.Decode(frame.MsgId, plain) : null;
        var verified = decoded?.Status.StartsWith("decoded", StringComparison.Ordinal) == true;
        if (!connectionVerified && !verified)
        {
            player = null;
            session = "unverified-" + session + "-" + connection;
        }
        status ??= frame.Type == GatewayMessageType.Exception ? "transport_exception" : decoded?.Status ?? "key_unavailable";
        if (frame.Type == GatewayMessageType.Exception && decoded is not null)
            status = "transport_exception";
        using var buffer = new LimitedBuffer(_limits.QueueBytes);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("captureId", CaptureId);
            json.WriteNumber("eventIndex", ++_index);
            json.WriteString("receivedAt", DateTimeOffset.UtcNow);
            json.WriteString("sessionId", session);
            json.WriteNumber("connectionId", connection);
            if (player is { } id)
                json.WriteNumber("playerId", id);
            else
                json.WriteNull("playerId");
            json.WriteString("direction", "server_to_client");
            json.WriteString("frameType", frame.Type.ToString());
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
            if (decoded?.Json is { } payload)
                json.WriteRawValue(payload, skipInputValidation: true);
            else
                json.WriteNullValue();
            if (decoded?.Json == null || decoded.HasUnknownFields)
            {
                json.WriteString("rawEncoding", frame.Type == GatewayMessageType.Invalid ? "gateway_fragment" : plain == null ? "encrypted_or_unavailable" : "protobuf");
                json.WriteBase64String("rawPayload", plain ?? frame.Payload);
            }

            if (decoded?.Error is { } error)
                json.WriteString("error", error);
            json.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        Enqueue(new(bytes, player, session, status));
        return verified;
    }

    internal void Bind(string session, int player)
    {
        if (_announced.TryGetValue(session, out var previous) && previous == player)
            return;
        if (_announced.Count >= 4096)
            throw new IOException("本次捕获的登录会话数量达到上限。");
        Enqueue(new(null, player, session, "", true));
        _announced[session] = player;
    }

    internal void Checkpoint() => Enqueue(new(null, null, "", ""));
    private void Enqueue(Envelope item)
    {
        var size = item.Bytes?.Length ?? 0;
        lock (_gate)
        {
            if (_error != null)
                throw new IOException(_error);
            if (_queued + size > _limits.QueueBytes || !_queue.Writer.TryWrite(item))
            {
                _dropped += item.Bytes == null ? 0 : 1;
                _error = "事件导出队列已满，已停止捕获；请检查磁盘写入速度。";
                throw new IOException(_error);
            }

            _queued += size;
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                if (item.Bind)
                {
                    lock (_gate)
                    {
                        var id = item.Player!.Value;
                        _bindings[item.Session] = id;
                        if (_pending.Remove(item.Session, out var pending))
                        {
                            _unresolved -= pending.Written;
                            var old = _players.GetValueOrDefault(id) ?? new(id, 0, 0, 0, 0);
                            _players[id] = new(id, old.Written + pending.Written, old.Decoded + pending.Decoded, old.Unknown + pending.Unknown, old.Failed + pending.Failed);
                        }
                    }

                    continue;
                }

                if (item.Bytes == null)
                {
                    await FlushAsync();
                    WriteManifest(false);
                    continue;
                }

                try
                {
                    if (_bytes + item.Bytes.Length + 1 > _limits.CaptureBytes)
                        throw new IOException("事件导出达到本次捕获的磁盘容量上限。");
                    var key = item.Player?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unresolved";
                    var output = await GetOutputAsync(key, item.Bytes.Length + 1);
                    await output.Stream.WriteAsync(item.Bytes);
                    await output.Stream.WriteAsync(new byte[] { 10 });
                    output.Bytes += item.Bytes.Length + 1;
                    lock (_gate)
                    {
                        _bytes += item.Bytes.Length + 1;
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
                finally
                {
                    lock (_gate)
                        _queued -= item.Bytes.Length;
                }
            }

            await FlushAsync();
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            lock (_gate)
                _error ??= "事件文件写入失败：" + ex.Message;
        }
        finally
        {
            foreach (var output in _outputs.Values)
                try
                {
                    await output.Stream.DisposeAsync();
                }
                catch (IOException ex)
                {
                    lock (_gate)
                        _error ??= "事件文件关闭失败：" + ex.Message;
                }

            _outputs.Clear();
            while (_queue.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task<Output> GetOutputAsync(string key, int nextSize)
    {
        if (_outputs.TryGetValue(key, out var existing) && existing.Bytes + nextSize <= _limits.ShardBytes)
            return existing;
        if (existing != null)
        {
            await existing.Stream.DisposeAsync();
            _outputs.Remove(key);
        }

        if (_outputs.Count >= _limits.OpenFiles)
        {
            var first = _outputs.First();
            await first.Value.Stream.DisposeAsync();
            _outputs.Remove(first.Key);
        }

        var directory = key == "unresolved" ? Path.GetDirectoryName(ManifestPath)! : Path.Combine(_root, key, "captures", CaptureId);
        Directory.CreateDirectory(directory);
        var shard = _shards.GetValueOrDefault(key) + 1;
        _shards[key] = shard;
        var path = Path.Combine(directory, $"{(key == "unresolved" ? "unresolved" : "events")}-{shard:D6}.jsonl");
        var result = new Output(path, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
        if (_files.Count >= 4096)
        {
            await result.Stream.DisposeAsync();
            throw new IOException("事件分片数量达到上限。");
        }

        _outputs[key] = result;
        _files.Add(Path.GetRelativePath(_root, path).Replace('\\', '/'));
        return result;
    }

    private async Task FlushAsync()
    {
        foreach (var output in _outputs.Values)
            await output.Stream.FlushAsync();
    }

    internal GameDataExportEventStats Snapshot()
    {
        lock (_gate)
            return new(CaptureId, ManifestPath, _received, _written, _decoded, _unknown, _failed, _unresolved, _dropped, _bytes, _finished && _error == null, _error, _players.Values.OrderBy(p => p.PlayerId).ToArray());
    }

    internal async Task<GameDataExportEventStats> FinishAsync(string? error)
    {
        lock (_gate)
            _error ??= error;
        _queue.Writer.TryComplete();
        await _worker;
        lock (_gate)
        {
            _finished = true;
            _dropped = Math.Max(_dropped, _received - _written);
            if (_received != _written)
                _error ??= "事件队列未完全写入，捕获结果不完整。";
        }

        try
        {
            WriteManifest(true);
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
                _error ??= "捕获索引写入失败：" + ex.Message;
        }

        return Snapshot();
    }

    private void WriteManifest(bool ended)
    {
        var stats = Snapshot();
        var tmp = ManifestPath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("captureId", CaptureId);
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
            foreach (var file in _files)
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

        File.Move(tmp, ManifestPath, true);
    }
}
