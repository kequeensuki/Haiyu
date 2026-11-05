using ABI.Models;
using Waves.Core.Services;
using ABIRuntime.Abstractions;
using ABIRuntime.Runtime;
using Haiyu.Plugin.Common.LegacyMessageBox;
using Microsoft.Extensions.Hosting;

namespace Haiyu.Services;

public sealed class ABIRuntimeService
{
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public PrivilegedRuntime? Runtime { get; private set; }

    /// <summary>
    /// 初始化后台ABI运行时
    /// </summary>
    public async Task<bool> Initialize(string baseDirectory)
    {
        await _initializeGate.WaitAsync();
        try
        {
            if (_initialized && Runtime is not null)
                return true;

            string corePath = Path.Combine(baseDirectory,"Haiyu.ABI.dll");
            if (!File.Exists(corePath))
            {
                throw new Exception(LanguageService.GetString("AbiRuntime_PackageMissing"));
            }
            try
            {
                if (Runtime == null || Runtime.RunFlage != 0)
                {
                    Runtime = new PrivilegedRuntime(corePath);
                }
                Progress<IPrivilegedProgress<ABISystemConfigProgress>> progress = new Progress<
                    IPrivilegedProgress<ABISystemConfigProgress>
                >(p =>
                {
                    if (p.Data is { } configProgress)
                    {
                        Debug.WriteLine($"Run{configProgress.IsRuning}");
                    }
                });
                var result = await this.Runtime.InvokeAsync(
                    ABIRuntime.Contract.ABISystemConfigContract,
                    new ABISystemConfigRequest() { BaseDirectory = baseDirectory },
                    progress,
                    default
                );
                _initialized = result.IsSuccess;
                return _initialized;
            }
            catch (Exception ex)
            {
                // 真实异常必须落日志——此弹窗曾吞掉根因（如 PawnIO 路径 bug 曾不可见）
                Instance.Host.Services.GetRequiredKeyedService<LoggerService>("AppLog")
                    .WriteError($"ABI 运行时初始化失败：{ex}");
                LegacyMessageBox.ShowError(
                    LanguageService.GetString("AbiRuntime_InitializeFailed")
                        + $"\n{ex.Message}",
                    LanguageService.GetString("Dialog_ShowError")
                );
                return false;
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task Close()
    {
        if (Runtime == null)
            return;
        await Runtime.DisposeAsync();
        Runtime = null;
        _initialized = false;
    }
}
