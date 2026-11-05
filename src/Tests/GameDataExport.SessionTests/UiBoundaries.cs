global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using Haiyu.Common;
global using Haiyu.Services;
global using Microsoft.Extensions.DependencyInjection;

namespace Haiyu.Common.Contracts
{
    public interface IViewFactorys { }
    public sealed class WindowSession
    {
        public PickerContext Context { get; } = new();
    }
    public sealed class PickerContext { public TestPicker PickersService { get; } = new(); }
    public sealed record TestPickedFile(string Path);
    public sealed class TestPicker
    {
        public Func<IReadOnlyCollection<string>, Task<TestPickedFile?>> Pick { get; set; } = _ => Task.FromResult<TestPickedFile?>(null);
        public Task<TestPickedFile?> GetFileOpenPicker(IReadOnlyCollection<string> extensions) => Pick(extensions);
    }
}
namespace Haiyu.Common
{
    // 仅替代 WinUI 宿主边界；测试直接编译和执行生产 ViewModel。
    public class ViewModelBase : ObservableObject, IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        public bool IsDisposed { get; private set; }
        protected CancellationToken LifetimeToken => _lifetime.Token;
        protected virtual void OnDisposing() { }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            OnDisposing();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
namespace Haiyu.Services
{
    public static class Instance { public static TestHost Host { get; set; } = null!; }
    public sealed record TestHost(IServiceProvider Services);
    public sealed class ABIRuntimeService
    {
        public ABIRuntime.Runtime.PrivilegedRuntime? Runtime => null;
        public Task<bool> Initialize(string path) => Task.FromResult(false);
    }
    public static class LanguageService
    {
        public static string GetString(string key) => key switch
        {
            "Toolkit_GameDataExportSummaryDone" => "本次捕获已结束，共导出 {0} 个玩家。",
            "Toolkit_GameDataExportPlayerSummary" => "玩家 {0}（已更新 {1} 次）\n普通道具 {2}、武器 {3}、声骸 {4}、限时道具 {5}\n{6}",
            _ => key
        };
    }
    public static class WindowExtension
    {
        public const int SW_SHOWNORMAL = 1;
        public static void ShellExecute(IntPtr owner, string verb, string file, string args, string dir, int show)
            => throw new NotSupportedException("此测试不执行 Windows Shell。");
    }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private readonly SynchronizationContext _context;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private DispatcherQueue(SynchronizationContext context) => _context = context;
        public static DispatcherQueue? GetForCurrentThread() => SynchronizationContext.Current is { } context ? new(context) : null;
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _owner;
        public bool TryEnqueue(Action callback) { _context.Post(_ => callback(), null); return true; }
    }
}
