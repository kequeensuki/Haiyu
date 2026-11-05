using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.Common.Contracts;
using Haiyu.ViewModel.ToolkitsViewModel;

var tests = new (string, Func<Task>)[]
{
    ("捕获就绪信号及提前失败", ReadyAsync),
    ("取消返回结果、通知一次、会话可重启", StopAsync),
    ("等待提示不刷新空转时钟，每次会话均有 watcher", IdleAsync),
    ("游戏运行时保留会话，超时停止不开放重叠会话", LifetimeAsync),
    ("打开即接入自动会话，关闭观察窗口可回收且后台继续", WindowAsync),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
}
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static GameDataExportResult Result(bool success = true) => new(success, success ? "已停止并收尾" : "初始化失败", null, null, 0, 0, null);
static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(5, timeout.Token);
}
static GameDataExportLauncher Create(Func<IProgress<IPrivilegedProgress<GameDataExportProgress>>, CancellationToken, Task<GameDataExportResult>> run,
    Func<bool>? game = null, TimeSpan? idle = null) => new(run, game ?? (() => false),
        idleTimeout: idle ?? TimeSpan.FromSeconds(2), probeInterval: TimeSpan.FromMilliseconds(10),
        readyTimeout: TimeSpan.FromMilliseconds(500), stopGrace: TimeSpan.FromMilliseconds(60));
static void Send(IProgress<IPrivilegedProgress<GameDataExportProgress>> p, bool ready = false, bool data = false) =>
    p.Report(new PrivilegedProgress<GameDataExportProgress>(PrivilegedStage.Executing, 0, "业务进度",
        new(GameDataExportStage.Capturing, 5, "正在等待游戏连接", ready, data)));

static async Task ReadyAsync()
{
    await using (var failed = Create((_, _) => Task.FromResult(Result(false))))
        Check(!await failed.StartSessionForAutoLaunchAsync(), "失败被误报为就绪");
    IProgress<IPrivilegedProgress<GameDataExportProgress>>? progress = null;
    await using var launcher = Create(async (p, ct) =>
    {
        progress = p;
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return Result();
    });
    var ready = launcher.StartSessionForAutoLaunchAsync();
    await WaitUntil(() => progress is not null);
    Send(progress!); // 外层进度及等待提示不等于捕获就绪。
    Check(!ready.IsCompleted, "收到任意进度就放行");
    Send(progress!, ready: true);
    Check(await ready, "捕获就绪后仍等到整场会话结束");
    await using var slow = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Result(); });
    Check(!await slow.StartSessionForAutoLaunchAsync(), "初始化超时被误报成功");
}
static async Task StopAsync()
{
    var ended = 0;
    await using var launcher = Create(async (_, ct) =>
    {
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return Result();
    });
    launcher.SessionEnded += _ => Interlocked.Increment(ref ended);
    for (var i = 0; i < 2; i++)
    {
        Check(launcher.TryStart(out var first), "无法重新开始");
        Check(!launcher.TryStart(out var reused) && ReferenceEquals(first, reused), "出现重叠会话");
        Check((await launcher.StopAsync())?.Success == true, "停止丢失最终结果");
        Check(!launcher.IsSessionActive, "停止后仍为活跃");
    }
    Check(ended == 2, "结束通知丢失或重复");
    await using var canceled = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Result(); });
    var canceledEnded = 0;
    canceled.SessionEnded += _ => canceledEnded++;
    canceled.TryStart(out _);
    await canceled.StopAsync();
    Check(canceledEnded == 1 && canceled.LastResult is { Success: false }, "取消任务没有通知终态");
}
static async Task IdleAsync()
{
    var ended = 0;
    await using var launcher = Create(async (p, ct) =>
    {
        try { while (true) { Send(p); await Task.Delay(5, ct); } }
        catch (OperationCanceledException) { return Result(); }
    }, idle: TimeSpan.FromMilliseconds(90));
    launcher.SessionEnded += _ => Interlocked.Increment(ref ended);
    for (var i = 1; i <= 2; i++)
    {
        launcher.TryStart(out var task);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(ended == i, "后续会话没有空转监控");
    }
    // 成功解析的数据会刷新计时，普通等待消息不会。
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dataSent = 0;
    await using var active = Create(async (p, ct) =>
    {
        try
        {
            while (!release.Task.IsCompleted) { Send(p, data: true); dataSent++; await Task.Delay(5, ct); }
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { return Result(); }
        return Result();
    }, idle: TimeSpan.FromMilliseconds(90));
    active.TryStart(out var activeTask);
    await WaitUntil(() => dataSent >= 25);
    Check(!activeTask.IsCompleted, "新背包数据未刷新时钟");
    release.SetResult();
    await activeTask.WaitAsync(TimeSpan.FromSeconds(2));
}
static async Task LifetimeAsync()
{
    var gameRunning = true;
    await using var launcher = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Result(); },
        () => gameRunning, TimeSpan.FromMilliseconds(50));
    launcher.TryStart(out var task);
    await Task.Delay(120);
    Check(!task.IsCompleted, "游戏运行期间被空转关闭");
    gameRunning = false;
    await task.WaitAsync(TimeSpan.FromSeconds(2));
    var done = new TaskCompletionSource<GameDataExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var stuck = Create((_, _) => done.Task);
    stuck.TryStart(out var pending);
    Check((await stuck.StopAsync()) is { Success: false }, "停止超时没有反馈");
    Check(!stuck.TryStart(out var reused) && ReferenceEquals(pending, reused), "旧服务未退出即启动第二个");
    done.SetResult(Result());
    await pending;
}
static Task WindowAsync()
{
    // 控制 Progress<T> 的 UI 调度；本测试不启动 WinUI/XAML。
    var previous = SynchronizationContext.Current;
    var ui = new PumpContext();
    SynchronizationContext.SetSynchronizationContext(ui);
    try
    {
        var done = new TaskCompletionSource<GameDataExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var launcher = Create((p, _) => { Send(p, ready: true); return done.Task; });
        var services = new ServiceCollection().AddSingleton(launcher).BuildServiceProvider();
        Instance.Host = new TestHost(services);
        launcher.TryStart(out var task);
        SpinWait.SpinUntil(() => launcher.LastProgress is not null, TimeSpan.FromSeconds(2));
        using var vm = new GameDataExportViewModel(new ViewFactory());
        Check(vm.IsExporting && vm.ProgressValue == 5, "自动会话打开窗口后仍显示未开始");
        var weak = ClosedWindow(ui);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive, "关闭的窗口 VM 被会话长期保留");
        Check(launcher.IsSessionActive, "关闭观察窗口终止了实际会话");
        done.SetResult(Result());
        Check(task.Wait(TimeSpan.FromSeconds(2)), "会话未结束");
        ui.Drain();
        Check(!vm.IsExporting && vm.HasResult, "未接收终态");
        services.Dispose();
    }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
    return Task.CompletedTask;
}
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference ClosedWindow(PumpContext ui)
{
    var vm = new GameDataExportViewModel(new ViewFactory());
    vm.StartExportCommand.Execute(null);
    vm.Dispose();
    ui.Drain();
    return new WeakReference(vm);
}
sealed class ViewFactory : IViewFactorys { }
sealed class PumpContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public void Drain() { while (_queue.TryDequeue(out var entry)) entry.Item1(entry.Item2); }
}
