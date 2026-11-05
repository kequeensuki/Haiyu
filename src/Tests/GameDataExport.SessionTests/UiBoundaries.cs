global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using Haiyu.Common;
global using Haiyu.Services;
global using Microsoft.Extensions.DependencyInjection;

namespace Haiyu.Common.Contracts { public interface IViewFactorys { } }
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
    public static class LanguageService { public static string GetString(string key) => key; }
    public static class WindowExtension
    {
        public const int SW_SHOWNORMAL = 1;
        public static void ShellExecute(IntPtr owner, string verb, string file, string args, string dir, int show)
            => throw new NotSupportedException("此测试不执行 Windows Shell。");
    }
}
