using ABI.Models;
using ABIRuntime.Abstractions;

namespace Haiyu.Services;

/// <summary>应用级导出会话。页面只观察状态，计时器与取消资源由每次会话持有。</summary>
public sealed partial class GameDataExportLauncher : IDisposable, IAsyncDisposable
{
    private sealed class Session
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<GameDataExportResult> Task = null!;
        internal bool Active = true;
        internal GameDataExportScope Scope;
        internal long? KeyWaitStartedAt;
        internal bool SessionKeyAcquired;
        internal string? StopReason;
        internal bool SawGameProcess;
    }

    private readonly object _gate = new();
    private readonly Func<GameDataExportScope, IProgress<IPrivilegedProgress<GameDataExportProgress>>, CancellationToken, Task<GameDataExportResult>> _invoke;
    private readonly Func<bool> _isGameRunning;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _keyWaitTimeout;
    private readonly TimeSpan _probeInterval;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _stopGrace;
    private Session? _session;
    private bool _disposed;
    private IDisposable? _gameExitSubscription;
    private (string Message, int Percentage)? _lastProgress;
    private GameDataExportResult? _lastResult;
    private GameDataExportProgress? _lastBusinessProgress;

    internal GameDataExportLauncher(
        Func<IProgress<IPrivilegedProgress<GameDataExportProgress>>, CancellationToken, Task<GameDataExportResult>> invoke,
        Func<bool> isGameRunning, TimeProvider? clock = null,
        TimeSpan? keyWaitTimeout = null, TimeSpan? probeInterval = null,
        TimeSpan? readyTimeout = null, TimeSpan? stopGrace = null)
        : this((_, progress, token) => invoke(progress, token), isGameRunning, clock,
            keyWaitTimeout, probeInterval, readyTimeout, stopGrace) { }

    internal GameDataExportLauncher(
        Func<GameDataExportScope, IProgress<IPrivilegedProgress<GameDataExportProgress>>, CancellationToken, Task<GameDataExportResult>> invoke,
        Func<bool> isGameRunning, TimeProvider? clock = null,
        TimeSpan? keyWaitTimeout = null, TimeSpan? probeInterval = null,
        TimeSpan? readyTimeout = null, TimeSpan? stopGrace = null)
    {
        _invoke = invoke;
        _isGameRunning = isGameRunning;
        _clock = clock ?? TimeProvider.System;
        _keyWaitTimeout = keyWaitTimeout ?? TimeSpan.FromMinutes(30);
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(2);
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(15);
        _stopGrace = stopGrace ?? TimeSpan.FromSeconds(10);
    }

    public (string Message, int Percentage)? LastProgress { get { lock (_gate) return _lastProgress; } }
    public GameDataExportProgress? LastBusinessProgress { get { lock (_gate) return _lastBusinessProgress; } }
    public GameDataExportResult? LastResult { get { lock (_gate) return _lastResult; } }
    public GameDataExportScope CurrentScope { get { lock (_gate) return _session?.Scope ?? GameDataExportScope.Inventory; } }
    public bool IsSessionActive { get { lock (_gate) return _session is { Active: true }; } }
    public event Action<string, int>? ProgressChanged;
    public event Action<GameDataExportResult>? SessionEnded;
    public event Action? StateChanged;

    public bool TryStart(out Task<GameDataExportResult> sessionTask, GameDataExportScope scope = GameDataExportScope.Inventory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { Active: true } existing)
            {
                sessionTask = existing.Task;
                return false;
            }
            var running = _isGameRunning();
            var session = new Session
            {
                KeyWaitStartedAt = running ? _clock.GetTimestamp() : null,
                SawGameProcess = running,
                Scope = scope
            };
            _session = session;
            _lastProgress = null;
            _lastResult = null;
            _lastBusinessProgress = null;
            sessionTask = session.Task = Task.Run(() => RunSessionAsync(session));
        }
        StateChanged?.Invoke();
        return true;
    }

    private async Task<GameDataExportResult> RunSessionAsync(Session session)
    {
        using var watcherStop = new CancellationTokenSource();
        var watcher = WatchSessionAsync(session, watcherStop.Token);
        GameDataExportResult result;
        try
        {
            // 同步接收传输回调，避免 Progress<T> 的线程池排队让就绪信号晚于结果。
            var progress = new InlineProgress(p => OnProgress(session, p));
            result = await _invoke(session.Scope, progress, session.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Fail("游戏数据导出监控已停止。");
        }
        catch (Exception ex)
        {
            result = Fail(ex.Message);
        }
        finally
        {
            watcherStop.Cancel();
            await watcher.ConfigureAwait(false);
        }
        lock (_gate)
        {
            if (session.StopReason is { } reason)
                result = result with { Success = false, Message = reason };
            session.Active = false;
            session.Ready.TrySetResult(false);
            if (ReferenceEquals(_session, session))
                _lastResult = result;
            session.Stop.Dispose();
        }
        SessionEnded?.Invoke(result);
        StateChanged?.Invoke();
        return result;
    }

    private void OnProgress(Session session, IPrivilegedProgress<GameDataExportProgress> progress)
    {
        var message = progress.Data?.Message ?? progress.Message;
        var percentage = progress.Data?.Percentage ?? progress.Percentage;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || !session.Active)
                return;
            _lastProgress = (message, percentage);
            if (progress.Data is not null) _lastBusinessProgress = progress.Data;
            if (progress.Data?.CaptureReady == true && !session.Stop.IsCancellationRequested)
                session.Ready.TrySetResult(true);
            // 此信号只在服务成功解析密钥后发送；普通流量与 UI 进度都不续期。
            if (progress.Data?.SessionKeyAcquired == true)
                session.SessionKeyAcquired = true;
        }
        ProgressChanged?.Invoke(message, percentage);
    }

    /// <summary>只等待网卡启动成功；初始化失败或超时返回 false，调用方仍可继续启动游戏。</summary>
    public async Task<bool> StartSessionForAutoLaunchAsync()
    {
        TryStart(out _);
        Session session;
        lock (_gate) session = _session!;
        try
        {
            return await session.Ready.Task.WaitAsync(_readyTimeout).ConfigureAwait(false)
                && IsSessionActive;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task WatchSessionAsync(Session session, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_probeInterval, _clock);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (!session.Active)
                        return;
                    if (_isGameRunning())
                    {
                        session.SawGameProcess = true;
                        session.KeyWaitStartedAt ??= _clock.GetTimestamp();
                    }
                    else if (session.SawGameProcess)
                    {
                        // 与手动停止使用同一业务取消通道；本 watcher 无需等待自己结束。
                        session.Stop.Cancel();
                        return;
                    }
                    if (!session.SessionKeyAcquired && session.KeyWaitStartedAt is { } started
                        && _clock.GetElapsedTime(started) >= _keyWaitTimeout)
                    {
                        session.StopReason = "等待游戏会话密钥超时，已停止流量捕获。";
                        session.Stop.Cancel();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>复用游戏启动器的退出通知。回登录界面仍有进程，不触发停止。</summary>
    public void NotifyGameExited()
    {
        lock (_gate)
        {
            if (_session is not { Active: true } session) return;
            _lastProgress = ("游戏已退出，正在停止抓包并保存数据。", 100);
            _lastBusinessProgress = null;
            session.Stop.Cancel();
        }
        ProgressChanged?.Invoke("游戏已退出，正在停止抓包并保存数据。", 100);
    }

    public async Task<GameDataExportResult?> StopAsync()
    {
        Task<GameDataExportResult>? task;
        lock (_gate)
        {
            task = _session?.Task;
            if (_session is { Active: true } session)
                session.Stop.Cancel();
        }
        if (task is null)
            return null;
        try
        {
            return await task.WaitAsync(_stopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 不伪造已结束状态，也不开放第二个会话；旧调用结束后才可重新开始。
            return Fail("导出停止超时，正在等待后台释放资源。");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            subscription = _gameExitSubscription;
            _gameExitSubscription = null;
            if (_session is { Active: true } session)
                session.Stop.Cancel();
        }
        subscription?.Dispose();
    }

    private static GameDataExportResult Fail(string message) => new(false, message, null, null, 0, 0, null);

    private sealed class InlineProgress(Action<IPrivilegedProgress<GameDataExportProgress>> report)
        : IProgress<IPrivilegedProgress<GameDataExportProgress>>
    {
        public void Report(IPrivilegedProgress<GameDataExportProgress> value) => report(value);
    }
}
