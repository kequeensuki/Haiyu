namespace Haiyu.ABI.Services.GameDataExport.Capture;

/// <summary>
/// 所有句柄仅在调用 Start/TryRead/ConfirmInterface 的捕获线程上操作。
/// 系统事件回调只登记版本和时间；重新枚举不触碰游戏会话。
/// </summary>
internal sealed class MultiInterfaceUdpCapture : IAdaptiveUdpCapture
{
    private sealed record Opened(CaptureDevice Device, IUdpCapture Capture);
    private readonly Func<IReadOnlyList<CaptureDevice>> _enumerate;
    private readonly Func<CaptureDevice, IUdpCapture> _open;
    private readonly INetworkChangeSource _changes;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, Opened> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _diagnostics = new();
    private volatile bool _running;
    private bool _disposed;
    private bool _retry;
    private long _changedAt;
    private int _requested;
    private int _applied;
    private long _lastScan;
    private int _cursor;
    private string? _lockedId;

    public MultiInterfaceUdpCapture() : this(NpcapDeviceCatalog.Enumerate,
        device => new NpcapUdpCapture(device), new WindowsNetworkChanges()) { }

    internal MultiInterfaceUdpCapture(Func<IReadOnlyList<CaptureDevice>> enumerate,
        Func<CaptureDevice, IUdpCapture> open, INetworkChangeSource changes,
        TimeProvider? clock = null, TimeSpan? debounce = null)
    {
        _enumerate = enumerate;
        _open = open;
        _changes = changes;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
    }

    public int NetworkGeneration { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;
        _running = true;
        _changes.Changed += RequestScan;
        try
        {
            _changes.Start();
            Refresh();
            if (_opened.Count == 0)
                throw new InvalidOperationException("没有可捕获的已连接 IPv4 网卡。" + string.Join("；", _diagnostics));
        }
        catch { Stop(); throw; }
    }

    private void RequestScan()
    {
        if (!_running) return;
        Interlocked.Exchange(ref _changedAt, _clock.GetTimestamp());
        Interlocked.Increment(ref _requested);
    }

    public UdpFrame? TryRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_running) return null;
        var requested = Volatile.Read(ref _requested);
        var networkChange = requested != _applied &&
            _clock.GetElapsedTime(Interlocked.Read(ref _changedAt)) >= _debounce;
        if (networkChange || (_retry && _clock.GetElapsedTime(_lastScan) >= _debounce))
        {
            NetworkGeneration++;
            _lockedId = null;
            Refresh();
            _applied = requested;
        }
        // 每张网卡均为非阻塞读取；轮询起点轮换，活跃接口不能饿死其他接口。
        var entries = _opened.Values.ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var index = (_cursor + i) % entries.Length;
            var entry = entries[index];
            try
            {
                var frame = entry.Capture.TryRead();
                if (frame is not null)
                {
                    _cursor = (index + 1) % entries.Length;
                    return frame with { InterfaceId = entry.Device.Id };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _opened.Remove(entry.Device.Id);
                entry.Capture.Dispose();
                _lockedId = null;
                _retry = true;
                Diagnose($"网卡 {entry.Device.Description} 捕获中断，正在重新探测：{ex.Message}");
            }
        }
        _cursor = 0;
        return null;
    }

    public void ConfirmInterface(UdpFrame frame)
    {
        if (frame.InterfaceId is not { } id || !_opened.TryGetValue(id, out var selected) || _lockedId == id)
            return;
        _lockedId = id;
        foreach (var entry in _opened.Values.ToArray())
        {
            if (string.Equals(entry.Device.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            _opened.Remove(entry.Device.Id);
            entry.Capture.Dispose();
        }
        _retry = false;
        Diagnose($"已确认游戏流量，锁定网卡：{selected.Device.Description}");
    }

    private void Refresh()
    {
        _lastScan = _clock.GetTimestamp();
        _retry = false;
        IReadOnlyList<CaptureDevice> devices;
        try { devices = _enumerate(); }
        catch (Exception ex) when (ex is IOException or System.Net.NetworkInformation.NetworkInformationException)
        {
            _retry = true;
            Diagnose($"网卡重新枚举失败，保留现有捕获并重试：{ex.Message}");
            return;
        }
        // 先开新接口，再移除已不可用的旧接口，保留仍可用的原句柄。
        foreach (var device in devices)
        {
            if (_opened.ContainsKey(device.Id)) continue;
            IUdpCapture? capture = null;
            try
            {
                capture = _open(device);
                capture.Start();
                _opened.Add(device.Id, new(device, capture));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            {
                capture?.Dispose();
                _retry = true;
                Diagnose($"网卡 {device.Description} 暂不可捕获：{ex.Message}");
            }
        }
        var ids = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _opened.Values.ToArray())
        {
            if (ids.Contains(entry.Device.Id)) continue;
            _opened.Remove(entry.Device.Id);
            entry.Capture.Dispose();
        }
        if (_opened.Count == 0) _retry = true;
        Diagnose($"正在监听 {_opened.Count} 个 IPv4 接口，等待确认游戏流量。");
    }

    private void Diagnose(string message)
    {
        if (_diagnostics.Count >= 32) _diagnostics.Dequeue();
        _diagnostics.Enqueue(message);
    }
    public string? TakeDiagnostic() => _diagnostics.TryDequeue(out var message) ? message : null;

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _changes.Changed -= RequestScan;
        _changes.Dispose();
        foreach (var entry in _opened.Values) entry.Capture.Dispose();
        _opened.Clear();
        _lockedId = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
