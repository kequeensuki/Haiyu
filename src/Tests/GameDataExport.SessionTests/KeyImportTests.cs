using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Haiyu.Common.Contracts;
using Haiyu.Services.Contracts;
using Haiyu.ViewModel.ToolkitsViewModel;
using CoreSecretAccessor = Waves.Core.Security.SecretAccessor;
using AbiSecretAccessor = Haiyu.ABI.Services.GameDataExport.Security.SecretAccessor;

internal sealed class FakeKeyService : IGameDataExportKeyService
{
    internal int Calls;
    internal Func<string, CancellationToken, Task> Import = (_, _) => Task.CompletedTask;
    public Task ImportAsync(string path, CancellationToken token = default)
    {
        Calls++;
        return Import(path, token);
    }
}

internal static class KeyImportTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    internal static async Task BusinessAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "haiyu-key-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "private.pem");
        var writes = 0;
        byte[]? saved = null;
        byte[]? borrowed = null;
        var service = new GameDataExportKeyService((target, user, secret) =>
        {
            Check(target == "Haiyu/Secret/waves.kcp", "导入和导出使用不同凭据目标");
            Check(user == Environment.UserName, "未按当前用户保存");
            Check(secret.Length <= 2560, "超过普通凭据上限");
            writes++;
            borrowed = secret;
            if (saved is not null) CryptographicOperations.ZeroMemory(saved);
            saved = secret.ToArray();
        });
        try
        {
            foreach (var size in new[] { 2048, 4096 })
            {
                using var original = RSA.Create(size);
                foreach (var pem in new[] { original.ExportRSAPrivateKeyPem(), original.ExportPkcs8PrivateKeyPem() })
                {
                    await File.WriteAllTextAsync(file, pem);
                    await service.ImportAsync(file);
                    Check(borrowed!.All(b => b == 0), "保存后没有清除私钥临时缓冲");
                    using var restored = RSA.Create();
                    restored.ImportPkcs8PrivateKey(saved!, out var count);
                    Check(count == saved!.Length, "凭据不是完整 PKCS#8 私钥");
                    Check(original.ExportSubjectPublicKeyInfo().SequenceEqual(restored.ExportSubjectPublicKeyInfo()), "保存改变了密钥");
                    using var consumer = AbiSecretAccessor.ParseRsa(saved);
                    Check(consumer is not null && original.ExportSubjectPublicKeyInfo().SequenceEqual(consumer.ExportSubjectPublicKeyInfo()),
                        "导出端实际解析器无法读取导入后的凭据");
                }
            }
            Check(writes == 4, "有效私钥未保存");
            using var rsa = RSA.Create(2048);
            using var ec = ECDsa.Create();
            foreach (var invalid in new[]
            {
                "", "invalid PEM", rsa.ExportSubjectPublicKeyInfoPem(), ec.ExportPkcs8PrivateKeyPem(),
                rsa.ExportEncryptedPkcs8PrivateKeyPem("test password", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10)),
                new string('x', 65537)
            })
            {
                await File.WriteAllTextAsync(file, invalid);
                try { await service.ImportAsync(file); throw new Exception("非法文件被接受"); }
                catch (ArgumentException) { }
                Check(writes == 4, "校验失败覆盖了已有凭据");
            }
            await File.WriteAllTextAsync(file, rsa.ExportPkcs8PrivateKeyPem());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await service.ImportAsync(file, canceled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { }
            Check(writes == 4, "取消仍写入凭据");
            byte[]? failedBuffer = null;
            var failed = new GameDataExportKeyService((_, _, bytes) => { failedBuffer = bytes; throw new Win32Exception(5); });
            try { await failed.ImportAsync(file); throw new Exception("保存失败未传播"); }
            catch (Win32Exception) { }
            Check(failedBuffer!.All(b => b == 0), "保存失败没有清理私钥缓冲");

            // 旧 PEM 来源仍可读，无效 PEM 的 ArgumentException 也应安全返回 null。
            var id = "test." + Guid.NewGuid().ToString("N");
            var env = "HAIYU_SECRET_" + id.Replace('.', '_').ToUpperInvariant();
            try
            {
                foreach (var pem in new[] { rsa.ExportRSAPrivateKeyPem(), rsa.ExportPkcs8PrivateKeyPem(), "-----BEGIN PRIVATE KEY-----\ninvalid\n-----END PRIVATE KEY-----" })
                {
                    Environment.SetEnvironmentVariable(env, pem);
                    using var core = new CoreSecretAccessor(id);
                    using var abi = new AbiSecretAccessor(id);
                    Check(core.TryResolve(out _) && abi.TryResolve(out _), "旧来源读取失败");
                    using var coreRsa = core.CreateRsa();
                    using var abiRsa = abi.CreateRsa();
                    Check((coreRsa is null) == pem.Contains("invalid") && (abiRsa is null) == pem.Contains("invalid"), "PEM 读取兼容性异常");
                }
            }
            finally { Environment.SetEnvironmentVariable(env, null); }

            if (OperatingSystem.IsWindows())
            {
                // 使用独立临时 target 验证真正的跨组件读写，不碰用户现有密钥。
                var target = "Haiyu/Secret/" + id;
                try
                {
                    Waves.Core.Security.CredentialStore.Write(target, Environment.UserName, saved!);
                    using var reader = new AbiSecretAccessor(id);
                    Check(reader.TryResolve(out _), "ABI 无法读取 UI 写入的普通凭据");
                    using var restored = reader.CreateRsa();
                    Check(restored?.KeySize == 4096, "ABI 无法解析导入的 DER 私钥");
                }
                finally { Waves.Core.Security.CredentialStore.TryDelete(target); }
            }
            else Console.WriteLine("SKIP Windows 凭据管理器原生读写（当前不是 Windows）");
        }
        finally
        {
            if (saved is not null) CryptographicOperations.ZeroMemory(saved);
            Directory.Delete(folder, true);
        }
    }

    internal static Task UiAsync()
    {
        var previous = SynchronizationContext.Current;
        var ui = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            void Finish(Task task)
            {
                var timer = Stopwatch.StartNew();
                while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(3)) { ui.Drain(); Thread.Sleep(1); }
                Check(task.IsCompleted, "UI 命令没有结束");
                task.GetAwaiter().GetResult();
                ui.Drain();
            }
            using var launcher = new GameDataExportLauncher(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new(false, "停止", null, null, 0, 0, null);
            }, () => false);
            using var services = new ServiceCollection().AddSingleton(launcher).BuildServiceProvider();
            Instance.Host = new TestHost(services);
            var session = new WindowSession();
            var keys = new FakeKeyService();
            using var vm = new GameDataExportViewModel(new ViewFactory(), session, keys, new FakeCatalog(), FakeSelections.Create());
            Finish(vm.LoadModulesCommand.ExecuteAsync(null));
            Finish(vm.ImportKeyCommand.ExecuteAsync(null)); // 用户取消文件选择。
            Check(keys.Calls == 0 && vm.KeyImportMessage == string.Empty, "取消选择仍调用业务服务");
            session.Context.PickersService.Pick = extensions =>
            {
                Check(extensions.SequenceEqual(new[] { ".pem" }), "文件选择器过滤器不正确");
                return Task.FromResult<TestPickedFile?>(new("selected.pem"));
            };
            Finish(vm.ImportKeyCommand.ExecuteAsync(null));
            Check(keys.Calls == 1 && vm.KeyImportMessage.Contains("KeySaved"), "成功状态未显示");
            keys.Import = (_, _) => throw new ArgumentException("invalid private key");
            Finish(vm.ImportKeyCommand.ExecuteAsync(null));
            Check(keys.Calls == 2 && vm.KeyImportMessage.Contains("KeyFailed"), "失败状态未显示");
            var picker = new TaskCompletionSource<TestPickedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Context.PickersService.Pick = _ => picker.Task;
            var pending = vm.ImportKeyCommand.ExecuteAsync(null);
            Check(!vm.StartExportCommand.CanExecute(null), "导入期间仍可从窗口启动导出");
            launcher.TryStart(out _); // 模拟选择文件期间自动启动捕获。
            Check(!vm.ImportKeyCommand.CanExecute(null), "捕获期间仍允许导入");
            picker.SetResult(new("selected.pem"));
            Finish(pending);
            Check(keys.Calls == 2 && vm.KeyImportMessage.Contains("KeyBusy"), "捕获启动后仍调用导入服务");
            Finish(launcher.StopAsync());
            Check(vm.ImportKeyCommand.CanExecute(null) && vm.StartExportCommand.CanExecute(null), "停止后按钮没有恢复");

            picker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = vm.ImportKeyCommand.ExecuteAsync(null);
            vm.Dispose();
            picker.SetResult(new("selected.pem"));
            Finish(pending);
            Check(keys.Calls == 2, "窗口关闭后仍导入私钥");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        return Task.CompletedTask;
    }
}
