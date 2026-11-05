using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ABI.Models;
using ABIRuntime.Abstractions;
using Haiyu.Common.Contracts;
using Haiyu.ViewModel.ToolkitsViewModel;

var tests = new (string, Func<Task>)[]
{
    ("PEM 导入校验、持久化与读取兼容", KeyImportTests.BusinessAsync),
    ("导入入口：取消、成功、失败及会话互斥", KeyImportTests.UiAsync),
    ("捕获就绪信号及提前失败", ReadyAsync),
    ("游戏退出事件与外部进程退出停止捕获", GameExitAsync),
    ("取消返回结果、通知一次、会话可重启", StopAsync),
    ("无密钥时等待提示不续期，每次会话均有 watcher", IdleAsync),
    ("取得密钥后持续监听，停止超时不开放重叠会话", LifetimeAsync),
    ("密钥期限从游戏启动开始且不被普通流量延长", KeyDeadlineAsync),
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
        keyWaitTimeout: idle ?? TimeSpan.FromSeconds(2), probeInterval: TimeSpan.FromMilliseconds(10),
        readyTimeout: TimeSpan.FromMilliseconds(500), stopGrace: TimeSpan.FromMilliseconds(60));
static void Send(IProgress<IPrivilegedProgress<GameDataExportProgress>> p, bool ready = false, bool data = false, bool key = false) =>
    p.Report(new PrivilegedProgress<GameDataExportProgress>(PrivilegedStage.Executing, 0, "业务进度",
        new(GameDataExportStage.Capturing, 5, "正在等待游戏连接", ready, data, SessionKeyAcquired: key)));

static async Task GameExitAsync()
{
    var running = false;
    await using var launcher = Create(async (_, ct) =>
    {
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return Result();
    }, () => running, TimeSpan.FromSeconds(30));
    launcher.TryStart(out var session);
    await Task.Delay(35);
    Check(launcher.IsSessionActive, "开始抓包时游戏尚未启动，不应立即停止");
    running = true;
    await Task.Delay(35);
    running = false;
    await session.WaitAsync(TimeSpan.FromSeconds(1));
    Check(!launcher.IsSessionActive, "游戏进程退出后仍等待 30 分钟");

    await using var events = new Waves.Core.Services.GameEventPublisher();
    await launcher.ObserveGameExitAsync(events);
    await launcher.ObserveGameExitAsync(events); // 重复绑定应幂等。
    launcher.TryStart(out var second);
    events.Publish(new Waves.Core.Models.GameContextOutputArgs { Type = Waves.Core.Models.Enums.GameContextActionType.None });
    await Task.Delay(25);
    Check(launcher.IsSessionActive, "普通状态变化被当作游戏退出");
    await using var currentEvents = new Waves.Core.Services.GameEventPublisher();
    await launcher.ObserveGameExitAsync(currentEvents);
    events.Publish(new Waves.Core.Models.GameContextOutputArgs { Type = Waves.Core.Models.Enums.GameContextActionType.GameExit });
    await Task.Delay(25);
    Check(launcher.IsSessionActive, "旧游戏上下文的事件停止了当前会话");
    currentEvents.Publish(new Waves.Core.Models.GameContextOutputArgs { Type = Waves.Core.Models.Enums.GameContextActionType.GameExit });
    await second.WaitAsync(TimeSpan.FromSeconds(1));
    Check(!launcher.IsSessionActive && launcher.LastResult?.Success == true, "已有 GameExit 事件没有触发收尾");
}

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
    }, game: () => true, idle: TimeSpan.FromMilliseconds(90));
    launcher.SessionEnded += _ => Interlocked.Increment(ref ended);
    for (var i = 1; i <= 2; i++)
    {
        launcher.TryStart(out var task);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(ended == i, "后续会话没有空转监控");
    }
    // 取得密钥后即使长期没有背包更新，也不再应用初次密钥期限。
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dataSent = 0;
    var dataGameRunning = true;
    await using var active = Create(async (p, ct) =>
    {
        try
        {
            Send(p, key: true);
            while (!release.Task.IsCompleted) { Send(p, data: true); dataSent++; await Task.Delay(5, ct); }
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { return Result(); }
        return Result();
    }, game: () => dataGameRunning, idle: TimeSpan.FromMilliseconds(90));
    active.TryStart(out var activeTask);
    await WaitUntil(() => dataSent >= 25);
    Check(!activeTask.IsCompleted, "已取得密钥却仍被初次密钥期限取消");
    release.SetResult();
    dataGameRunning = false;
    await activeTask.WaitAsync(TimeSpan.FromSeconds(2));
}
static async Task LifetimeAsync()
{
    var gameRunning = true;
    await using var launcher = Create(async (p, ct) => { Send(p, key: true); await Task.Delay(Timeout.Infinite, ct); return Result(); },
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
static async Task KeyDeadlineAsync()
{
    var clock = new ExportTestClock();
    var running = false;
    IProgress<IPrivilegedProgress<GameDataExportProgress>>? progress = null;
    await using var launcher = new GameDataExportLauncher(async (p, ct) =>
    {
        progress = p;
        await Task.Delay(Timeout.Infinite, ct);
        return Result();
    }, () => running, clock);
    launcher.TryStart(out var task);
    await WaitUntil(() => progress is not null);
    clock.Advance(TimeSpan.FromMinutes(40));
    await Task.Delay(30);
    Check(!task.IsCompleted, "游戏尚未启动就开始消耗密钥等待时间");
    running = true;
    clock.Advance(TimeSpan.FromSeconds(2));
    await Task.Delay(30);
    clock.Advance(TimeSpan.FromMinutes(29));
    await Task.Delay(30);
    Check(!task.IsCompleted, "从启动前计时或未满 30 分钟就停止");
    Send(progress!, ready: true, data: true); // 模拟普通业务进度；不能替代密钥信号。
    clock.Advance(TimeSpan.FromMinutes(1));
    var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!result.Success && result.Message.Contains("会话密钥超时"), "进程存活/普通进度延长了密钥期限，或未报告超时原因");
    progress = null;
    launcher.TryStart(out var keyedTask);
    await WaitUntil(() => progress is not null);
    Send(progress!, key: true);
    clock.Advance(TimeSpan.FromHours(1));
    await Task.Delay(30);
    Check(!keyedTask.IsCompleted, "取得密钥后仍在执行初次密钥超时");
    running = false;
    clock.Advance(TimeSpan.FromSeconds(2));
    await keyedTask.WaitAsync(TimeSpan.FromSeconds(2));
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
        IProgress<IPrivilegedProgress<GameDataExportProgress>>? live = null;
        using var launcher = Create((p, _) => { live = p; Send(p, ready: true); return done.Task; });
        var services = new ServiceCollection().AddSingleton(launcher).BuildServiceProvider();
        Instance.Host = new TestHost(services);
        launcher.TryStart(out var task);
        SpinWait.SpinUntil(() => launcher.LastProgress is not null, TimeSpan.FromSeconds(2));
        using var vm = new GameDataExportViewModel(new ViewFactory(), new WindowSession(), new FakeKeyService());
        Check(vm.IsExporting && vm.ProgressValue == 5, "自动会话打开窗口后仍显示未开始");
        void Report(GameDataExportProgress data)
        {
            Task.Run(() => live!.Report(new PrivilegedProgress<GameDataExportProgress>(PrivilegedStage.Executing, data.Percentage, "业务进度", data))).GetAwaiter().GetResult();
            ui.Drain();
        }
        Report(new(GameDataExportStage.Monitoring, 100, "导出完成：old.json", PlayerId: 123, OutputJsonPath: "123/old.json", UpdateCount: 1));
        Check(vm.ProgressMessage.Contains("123/old.json") && vm.IsExporting && !vm.HasResult, "首次快照被当作会话结束");
        Report(new(GameDataExportStage.Capturing, 20, "等待新的玩家身份"));
        Check(vm.ProgressMessage == "等待新的玩家身份" && launcher.IsSessionActive, "回登录界面导致终止或旧状态滞留");
        Report(new(GameDataExportStage.Monitoring, 100, "导出完成：old.json", PlayerId: 456, OutputJsonPath: "456/new.json", UpdateCount: 1));
        Check(vm.ProgressMessage.Contains("456/new.json") && vm.ProgressMessage.Contains("1 次") && !vm.ProgressMessage.Contains("old.json"), "切号后 UI 没有使用当前玩家的路径和次数");
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
    var vm = new GameDataExportViewModel(new ViewFactory(), new WindowSession(), new FakeKeyService());
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
