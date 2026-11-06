using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Haiyu.ABI.Services.GameDataExport.Storage;

internal sealed record OutputReceipt(long Order, string ModuleId, string Target, string Owner, long Revision,
    string? Path, int Bytes, string? Error, bool Superseded = false, bool Fatal = false);

/// <summary>一次捕获共享的受限 I/O 调度器；回执由调用方串行取回，不在后台修改业务状态。</summary>
internal sealed class JsonExportStore : IAsyncDisposable
{
    internal sealed record Limits(int QueueBytes = 32 * 1024 * 1024, int QueueItems = 512,
        long CaptureBytes = 1024L * 1024 * 1024);
    private sealed record Work(long Order, string Module, string Target, string Owner, long Revision, byte[] Bytes,
        Func<ReadOnlyMemory<byte>, ValueTask<string?>> Write, Action<OutputReceipt> Receipt, bool Fatal,
        TaskCompletionSource? Barrier = null, bool Metadata = false, bool FatalExceptions = false);
    private readonly object _gate = new();
    private readonly Channel<Work> _queue;
    private readonly ConcurrentQueue<Action> _receipts = new();
    private static readonly StringComparer TargetComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, string> _owners = new(TargetComparer);
    private readonly Dictionary<string, string> _targetModules = new(TargetComparer);
    private readonly Task _worker;
    private readonly Limits _limits;
    private long _queuedBytes, _metadataBytes, _writtenBytes, _order;
    private string? _failure;
    private bool _closed;
    internal long ReceiptVersion { get; private set; }
    internal string Root { get; }
    internal string? Failure { get { lock (_gate) return _failure; } }

    internal JsonExportStore(string root, Limits? limits = null)
    {
        Root = Path.GetFullPath(root);
        _limits = limits ?? new();
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(_limits.QueueItems)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WriteAsync);
    }

    internal string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("输出路径必须相对导出根目录。");
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("输出路径越界。");
        return path;
    }

    internal void Claim(string target, string owner)
    {
        target = Path.GetRelativePath(Root, Resolve(target));
        lock (_gate)
        {
            if (_owners.Count >= 8192 && !_owners.ContainsKey(target)) throw new IOException("输出目标数量达到上限。");
            _owners[target] = owner;
        }
    }

    internal bool IsOwner(string target, string owner)
    {
        target = Path.GetRelativePath(Root, Resolve(target));
        lock (_gate) return !_owners.TryGetValue(target, out var active) || active == owner;
    }

    internal void Snapshot(string module, string relative, string owner, long revision, byte[] json,
        Action<OutputReceipt> receipt, Func<string?>? legacyWrite = null)
    {
        var path = Resolve(relative);
        Enqueue(module, relative, owner, revision, json, async bytes =>
        {
            if (legacyWrite is not null) return legacyWrite();
            return await WriteAtomicAsync(path, bytes, commit: (temp, target) =>
            {
                lock (_gate)
                {
                    if (!IsOwner(relative, owner)) return false;
                    File.Move(temp, target, true);
                    return true;
                }
            }).ConfigureAwait(false);
        }, receipt, false, fatalExceptions: legacyWrite is not null);
    }

    internal void Enqueue(string module, string target, string owner, long revision, byte[] json,
        Func<ReadOnlyMemory<byte>, ValueTask<string?>> write, Action<OutputReceipt> receipt, bool fatal, bool fatalExceptions = false)
    {
        target = Path.GetRelativePath(Root, Resolve(target));
        lock (_gate)
        {
            Reserve(target, module);
            if (_closed || _failure is not null) throw new IOException(_failure ?? "输出已经结束。");
            if (_queuedBytes + json.Length > _limits.QueueBytes)
                throw Fail("导出共享输出队列超过内存上限。");
            var work = new Work(++_order, module, target, owner, revision, json, write, receipt, fatal, FatalExceptions: fatalExceptions);
            if (!_queue.Writer.TryWrite(work)) throw Fail("导出共享输出队列已满。");
            _queuedBytes += json.Length;
        }
    }

    private void Reserve(string target, string module)
    {
        if (_targetModules.TryGetValue(target, out var previous) && previous != module)
            throw new IOException($"多个导出模块争用同一目标：{target}");
        if (_targetModules.Count >= 8192 && !_targetModules.ContainsKey(target)) throw new IOException("输出目标数量达到上限。");
        _targetModules[target] = module;
    }

    internal void QueueMetadata(string relative, byte[] bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("捕获元数据超过容量上限。");
        lock (_gate)
        {
            if (_closed) throw new IOException("输出已经结束。");
            Reserve(Path.GetRelativePath(Root, Resolve(relative)), "metadata");
            var work = new Work(++_order, "metadata", relative, "", 0, bytes,
                data => WriteAtomicAsync(Resolve(relative), data), _ => { }, true, Metadata: true);
            if (_metadataBytes + bytes.Length > 4 * 1024 * 1024 || !_queue.Writer.TryWrite(work))
                throw Fail("捕获元数据输出队列已满。");
            _metadataBytes += bytes.Length;
        }
    }

    private IOException Fail(string message) { _failure ??= message; return new IOException(message); }
    internal void DrainReceipts() { while (_receipts.TryDequeue(out var action)) { action(); ReceiptVersion++; } }
    internal void ThrowIfFailed() { if (Failure is { } error) throw new IOException(error); }

    internal async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(0, "", "", "", 0, [], _ => ValueTask.FromResult<string?>(null), _ => { }, false, barrier);
        await _queue.Writer.WriteAsync(work).ConfigureAwait(false);
        await barrier.Task.ConfigureAwait(false);
        DrainReceipts();
    }

    private async Task WriteAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Barrier is { } barrier) { barrier.TrySetResult(); continue; }
            OutputReceipt result;
            try
            {
                if (!work.Metadata && Failure is { } failure) throw new IOException(failure);
                if (!IsOwner(work.Target, work.Owner))
                    result = new(work.Order, work.Module, work.Target, work.Owner, work.Revision, null, 0, null, true);
                else
                {
                    if (!work.Metadata && _writtenBytes + work.Bytes.Length > _limits.CaptureBytes)
                        throw new IOException("导出达到本次捕获的磁盘容量上限。");
                    var path = await work.Write(work.Bytes).ConfigureAwait(false);
                    var superseded = path is null && !IsOwner(work.Target, work.Owner);
                    result = new(work.Order, work.Module, work.Target, work.Owner, work.Revision, path,
                        path is null ? 0 : work.Bytes.Length, path is null && !superseded ? "JSON 写入失败，保留上次文件，稍后重试。" : null, superseded);
                    if (path is not null && !work.Metadata) _writtenBytes += work.Bytes.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result = new(work.Order, work.Module, work.Target, work.Owner, work.Revision, null, 0, ex.Message, Fatal: work.FatalExceptions);
            }
            lock (_gate)
            {
                if (work.Metadata) _metadataBytes -= work.Bytes.Length;
                else _queuedBytes -= work.Bytes.Length;
                if ((work.Fatal || result.Fatal) && result.Error is not null) _failure ??= result.Error;
            }
            var captured = result;
            _receipts.Enqueue(() => work.Receipt(captured));
        }
    }

    internal async Task<string?> MetadataAsync(string relative, byte[] bytes) =>
        await WriteAtomicAsync(Resolve(relative), bytes).ConfigureAwait(false);

    internal static byte[] Encode<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);

    internal static async ValueTask<string?> WriteAtomicAsync(string path, ReadOnlyMemory<byte> json, Func<bool>? owns = null, Func<string, string, bool>? commit = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, json.ToArray()).ConfigureAwait(false);
            if (owns is not null && !owns()) return null;
            // 所有目标的文件操作由单写入者执行；所有权会在提交前再次确认。
            if (commit is null) File.Move(temp, path, true);
            else if (!commit(temp, path)) return null;
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_closed) return; _closed = true; _queue.Writer.TryComplete(); }
        await _worker.ConfigureAwait(false);
        DrainReceipts();
    }
}
