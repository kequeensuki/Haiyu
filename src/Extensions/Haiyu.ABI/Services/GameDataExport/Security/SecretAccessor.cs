using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Haiyu.ABI.Services.GameDataExport.Security;

/// <summary>
/// 运行时密钥访问器：把「密钥从哪来」和「密钥怎么用」解耦。
///
/// 设计约束：
///   1. 密钥不得出现在源码 / git 历史 / 构建产物里
///   2. 密钥不得以明文形态长期落盘
///   3. 解析出的明文只在内存中存在，Dispose 时抹除
///   4. 多来源回退，任一路径可用即可工作
///
/// 优先级：环境变量（开发/CI）→ 凭据管理器 → DPAPI 加密文件 → 报错引导
/// 注意：刻意【不】支持「内嵌到程序」——那不是保密，只是混淆。
///
/// 依赖 GlobalUsings.cs 中的 System / System.Security.Cryptography / System.Text / System.IO。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretAccessor : IDisposable{
    private readonly string _secretId;
    private readonly string _dpapiPath;
    private readonly byte[] _entropy;

    private bool _resolved;
    private byte[]? _raw;
    private string? _source;

    /// <param name="secretId">逻辑名，用于环境变量名、凭据 target、日志（不含密钥内容）</param>
    /// <param name="dpapiPath">DPAPI 加密文件路径（可选）</param>
    public SecretAccessor(string secretId, string? dpapiPath = null)
    {
        if (string.IsNullOrWhiteSpace(secretId))
            throw new ArgumentException("密钥逻辑名不能为空", nameof(secretId));

        _secretId = secretId;
        _dpapiPath = dpapiPath ?? string.Empty;
        // 熵值是「应用作用域」，本身不保密；只防止别的程序拿同一份 DPAPI blob 直接解
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"Haiyu.Secret.{secretId}.v1"));
    }

    /// <summary>凭据管理器中使用的 target 名。</summary>
    public string CredentialTarget => $"Haiyu/Secret/{_secretId}";

    /// <summary>环境变量名，如 <c>HAIYU_SECRET_WAVES_KCP</c>。</summary>
    public string EnvironmentVariable =>
        $"HAIYU_SECRET_{_secretId.Replace('.', '_').ToUpperInvariant()}";

    /// <summary>当前密钥来源，便于 UI/日志提示（不含密钥内容）。</summary>
    public string? Source => _source;

    /// <summary>是否已成功解析到密钥。</summary>
    public bool IsAvailable => _resolved && _raw is { Length: > 0 };

    /// <summary>
    /// 解析密钥，按优先级依次尝试，第一个成功的胜出。
    /// 线程安全；重复调用只解析一次。失败时不抛异常，返回 false 并给出可操作提示。
    /// </summary>
    public bool TryResolve(out string? error)
    {
        error = null;
        if (_resolved)
            return IsAvailable;

        // ① 环境变量：开发机 / CI
        var envValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            _raw = Encoding.UTF8.GetBytes(envValue);
            _source = $"环境变量 {EnvironmentVariable}";
            _resolved = true;
            return true;
        }

        // ② Windows 凭据管理器
        if (CredentialStore.TryRead(CredentialTarget, out var cred) && cred is { Length: > 0 })
        {
            _raw = cred;
            _source = "Windows 凭据管理器";
            _resolved = true;
            return true;
        }

        // ③ DPAPI 加密文件
        if (!string.IsNullOrEmpty(_dpapiPath) && File.Exists(_dpapiPath))
        {
            try
            {
                var blob = File.ReadAllBytes(_dpapiPath);
                _raw = ProtectedDataAccess.Unprotect(blob, _entropy);
                _source = $"DPAPI 文件 {_dpapiPath}";
                _resolved = true;
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                // 换用户 / 换机器 / 文件损坏都会到这里，不视为致命错误
                error = $"DPAPI 解密失败（{ex.GetType().Name}）：该文件只能由原 Windows 用户在原机器上解密。";
            }
        }

        // ④ 全部失败
        _resolved = true;
        error ??= $"未找到密钥。可通过环境变量 {EnvironmentVariable}、凭据管理器（{CredentialTarget}）"
                + (_dpapiPath.Length > 0 ? $"，或导入到 {_dpapiPath}" : string.Empty) + " 提供。";
        return false;
    }

    /// <summary>取出 PEM 文本（调用方负责尽快用完；不要写日志）。</summary>
    public string? GetPem() => IsAvailable ? Encoding.UTF8.GetString(_raw!) : null;

    /// <summary>解析 RSA 私钥，返回值由调用方 Dispose。</summary>
    public RSA? CreateRsa()
    {
        if (!IsAvailable)
            return null;

        var rsa = RSA.Create();
        try
        {
            // 优先 PEM；不是 PEM 就当 PKCS#8 DER 试一次
            var text = Encoding.UTF8.GetString(_raw!);
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
                rsa.ImportFromPem(text);
            else
                rsa.ImportPkcs8PrivateKey(_raw!, out _);
            return rsa;
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        // 尽力缩短明文在内存中的驻留窗口
        if (_raw is { Length: > 0 })
            CryptographicOperations.ZeroMemory(_raw);
        _raw = null;
        _source = null;
    }
}
