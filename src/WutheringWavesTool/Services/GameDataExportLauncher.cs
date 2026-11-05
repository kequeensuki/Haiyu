using ABI.Models;
using ABIRuntime;
using ABIRuntime.Abstractions;

namespace Haiyu.Services;

/// <summary>
/// 游戏数据导出会话管理器（v2 报告 §3.1）。
///
/// <para>
/// 持有当前监控会话的完整生命周期：开始（幂等）/ 停止 / 空转超时探测（
/// 30 分钟无进度 <b>且</b> 游戏进程不存在 → 自动收尾）/ 进度与结果事件。
/// UI（导出窗口 VM、游戏启动挂钩）都经由本类与会话交互——
/// 会话归属应用层，窗口关闭不终止会话。
/// </para>
///
/// <para>
/// ★ 线程模型：会话主循环（InvokeAsync 的长调用）在宿主进程里跑，
///   本类的 StartAsync 只负责发起与订阅；事件回调在线程池触发，
///   UI 订阅方自行调度回 UI 线程（WinUI 的 Progress&lt;T&gt; 已自动调度）。
/// </para>
/// </summary>
public sealed class GameDataExportLauncher : IDisposable
{
    /// <summary>空转判定：连续无进度消息的时长阈值。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>空转探测周期。</summary>
    private static readonly TimeSpan IdleProbeInterval = TimeSpan.FromSeconds(60);

    /// <summary>停止等待会话收尾的超时（超时强取消）。</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCts;
    private Task<GameDataExportResult>? _sessionTask;
    private DateTimeOffset _lastProgressTick;

    /// <summary>会话是否进行中。</summary>
    public bool IsSessionActive
    {
        get
        {
            lock (_gate)
                return _sessionTask is { IsCompleted: false };
        }
    }

    /// <summary>进度消息（监控态高频心跳按 2s 节流自宿主侧）。</summary>
    public event Action<string, int>? ProgressChanged;

    /// <summary>会话结束（正常收尾 / 失败 / 取消）。参数为最终结果。</summary>
    public event Action<GameDataExportResult>? SessionEnded;

    /// <summary>
    /// 启动监控会话（幂等：已有活跃会话直接返回 false 不重复发起）。
    /// </summary>
    /// <returns>true = 新会话已发起；false = 已有会话进行中。</returns>
    public bool TryStart(out Task<GameDataExportResult> sessionTask)
    {
        lock (_gate)
        {
            if (_sessionTask is { IsCompleted: false })
            {
                sessionTask = _sessionTask;
                return false;
            }

            _sessionCts = new CancellationTokenSource();
            _lastProgressTick = DateTimeOffset.Now;
            var cts = _sessionCts;
            var runtime = Instance.Host.Services.GetRequiredService<ABIRuntimeService>();

            sessionTask = Task.Run(async () =>
            {
                // ABI 就绪（幂等；失败抛异常转失败结果）
                var initialized = await runtime.Initialize(
                    Waves.Settings.AppSettings.ABIRuntimeSavePath);
                if (!initialized || runtime.Runtime is null)
                {
                    return Fail(
                        LanguageService.GetString("Toolkit_GameDataExportAbiMissing"));
                }

                var progress = new Progress<IPrivilegedProgress<GameDataExportProgress>>(p =>
                {
                    _lastProgressTick = DateTimeOffset.Now;
                    ProgressChanged?.Invoke(p.Message, p.Percentage);
                });

                var result = await runtime.Runtime.InvokeAsync(
                    ABIRuntime.Contract.GameDataExportContract,
                    new GameDataExportRequest(GameDataExportScope.Inventory),
                    progress,
                    cts.Token);

                return result.Data ?? Fail(result.Message);
            }, CancellationToken.None);

            _sessionTask = sessionTask;
            _ = sessionTask.ContinueWith(
                t =>
                {
                    var final = t.Status == TaskStatus.Faulted
                        ? Fail(t.Exception?.GetBaseException().Message ?? "未知错误")
                        : t.Result;
                    SessionEnded?.Invoke(final);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 自动启动场景入口：发起会话并等待宿主进入「等待握手中/监控就绪」态
    /// （进度消息出现即视为宿主就绪，超时 <see cref="HandshakeReadyTimeout"/> 放行不阻断游戏）。
    /// 返回 false = 发起失败（ABI 未就绪等），调用方静默降级只提示不阻断。
    /// </summary>
    public async Task<bool> StartSessionForAutoLaunchAsync()
    {
        if (!TryStart(out var session))
            return true; // 已有会话在跑：视为就绪

        try
        {
            await session.WaitAsync(HandshakeReadyTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return true; // 超时放行：会话仍在后台初始化，不阻断游戏启动
        }
        catch (Exception)
        {
            return false; // 会话快速失败（ABI 未就绪等）
        }
    }

    private static readonly TimeSpan HandshakeReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 启动空转探测循环（自动启动场景调用一次；手动窗口场景不需要——
    /// 用户在场可以自己停止）。
    ///
    /// 规则（需求方决策）：连续 30 分钟无任何进度消息 <b>且</b>
    /// 游戏进程不存在 → 取消会话（宿主收尾落盘返回最新快照）。
    /// 游戏进程在 → 计时持续刷新（监控心跳会不断推送进度）。
    /// </summary>
    public async Task RunIdleWatcherAsync(CancellationToken watcherCt)
    {
        try
        {
            using var timer = new PeriodicTimer(IdleProbeInterval);
            while (await timer.WaitForNextTickAsync(watcherCt))
            {
                Task<GameDataExportResult>? session;
                DateTimeOffset lastTick;
                lock (_gate)
                {
                    session = _sessionTask;
                    lastTick = _lastProgressTick;
                }

                if (session is null || session.IsCompleted)
                    continue;

                var idleFor = DateTimeOffset.Now - lastTick;
                if (idleFor < IdleTimeout)
                    continue;

                if (IsGameRunning())
                    continue; // 游戏还在：不收尾，继续监控

                // 空转 + 游戏不在 → 收尾
                _ = StopAsync();
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 停止会话（宿主收尾落盘返回最新快照）。
    /// 等待 <see cref="StopGrace"/>，超时强取消。
    /// </summary>
    public async Task<GameDataExportResult?> StopAsync()
    {
        Task<GameDataExportResult>? session;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            session = _sessionTask;
            cts = _sessionCts;
        }

        if (session is null || session.IsCompleted)
            return session?.Status == TaskStatus.RanToCompletion ? session.Result : null;

        // 先请求优雅停止（宿主「收到停止信号 → 解析已捕获数据 → 返回」）
        cts?.Cancel();

        try
        {
            await session.WaitAsync(StopGrace);
            return session.Status == TaskStatus.RanToCompletion ? session.Result : null;
        }
        catch (TimeoutException)
        {
            // 宽限超时：WaitAsync 抛的是 TaskCanceledException 包装，此处强杀已由
            // cts.Cancel 传递，直接返回 null（会话 Ended 事件会另行通知）
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Haiyu 退出路径：停止会话并释放资源（App.xaml.cs 调用）。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;
        }
    }

    private static GameDataExportResult Fail(string message) =>
        new(false, message, null, null, 0, 0, null, 0, 0);

    /// <summary>
    /// 游戏进程是否在运行（空转收尾的 AND 条件之一）。
    /// 鸣潮本体进程名：Client-Win64-Shipping（国服/国际服一致）。
    /// </summary>
    private static bool IsGameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("Client-Win64-Shipping")
                .Length > 0;
        }
        catch
        {
            return false; // 探测失败按「不在」处理：宁可多收尾也不悬挂
        }
    }
}
