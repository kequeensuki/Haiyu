namespace Haiyu.ABI.Services.GameDataExport.Storage;

/// <summary>可供所有模块使用的追加、分片与句柄限制；不解释消息、玩家或业务统计。</summary>
internal sealed class JsonLinesArtifactWriter(JsonExportStore store, Func<string, int, string> path,
    long shardBytes = 128L * 1024 * 1024, int openFiles = 16, int maxFiles = 4096) : IAsyncDisposable
{
    private sealed record Output(string Path, FileStream Stream) { internal long Bytes; }
    private readonly Dictionary<string, Output> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _shards = new(StringComparer.Ordinal);
    private readonly List<string> _files = [];
    private readonly object _filesGate = new();
    internal IReadOnlyList<string> Files { get { lock (_filesGate) return _files.ToArray(); } }

    internal async ValueTask<string?> AppendAsync(string partition, ReadOnlyMemory<byte> bytes)
    {
        if (!_open.TryGetValue(partition, out var output) || output.Bytes + bytes.Length > shardBytes)
        {
            if (output is not null) { await output.Stream.DisposeAsync(); _open.Remove(partition); }
            if (_open.Count >= openFiles)
            {
                var first = _open.First(); await first.Value.Stream.DisposeAsync(); _open.Remove(first.Key);
            }
            if (_files.Count >= maxFiles) throw new IOException("JSONL 分片数量达到上限。");
            var shard = _shards.GetValueOrDefault(partition) + 1;
            var relative = path(partition, shard);
            var full = store.Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            output = new(full, new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
            _open[partition] = output; _shards[partition] = shard; lock (_filesGate) _files.Add(relative.Replace('\\', '/'));
        }
        await output.Stream.WriteAsync(bytes).ConfigureAwait(false);
        output.Bytes += bytes.Length;
        return output.Path;
    }
    internal async Task FlushAsync() { foreach (var output in _open.Values) await output.Stream.FlushAsync(); }
    public async ValueTask DisposeAsync()
    {
        foreach (var output in _open.Values) await output.Stream.DisposeAsync();
        _open.Clear();
    }
}
