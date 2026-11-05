using System.Security.Cryptography;
using System.Text;
using Haiyu.Services.Contracts;
using Waves.Core.Security;

namespace Haiyu.Services;

/// <summary>PEM 文件校验与凭据持久化；不依赖窗口、文件选择器或导出会话。</summary>
public sealed class GameDataExportKeyService : IGameDataExportKeyService
{
    public const string CredentialTarget = "Haiyu/Secret/waves.kcp";
    private const int MaximumFileSize = 64 * 1024;
    private const int MaximumCredentialSize = 2560;
    private readonly Action<string, string, byte[]> _write;

    public GameDataExportKeyService() : this(CredentialStore.Write) { }

    internal GameDataExportKeyService(Action<string, string, byte[]> write) => _write = write;

    public Task ImportAsync(string filePath, CancellationToken token = default) =>
        Task.Run(() => ImportCoreAsync(filePath, token), token);

    private async Task ImportCoreAsync(string filePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".pem", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 PEM 格式的 RSA 私钥文件。");

        var bytes = new byte[MaximumFileSize + 1];
        char[]? text = null;
        byte[]? privateKey = null;
        try
        {
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await file.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumFileSize)
                throw new ArgumentException("PEM 文件过大，请选择不超过 64 KiB 的 RSA 私钥文件。");

            text = Encoding.UTF8.GetChars(bytes, 0, length);
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(text);
                // 导出私钥同时拒绝仅含公钥的 PEM；DER 比 PEM 紧凑，可容纳常见 4096 位密钥。
                privateKey = rsa.ExportPkcs8PrivateKey();
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new ArgumentException("文件不是有效的未加密 RSA 私钥，请使用 PKCS#1 或 PKCS#8 PEM 文件。");
            }
            if (privateKey.Length > MaximumCredentialSize)
                throw new ArgumentException("RSA 私钥超过 Windows 普通凭据的 2560 字节上限，无法保存。");

            token.ThrowIfCancellationRequested();
            _write(CredentialTarget, Environment.UserName, privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (text is not null) Array.Clear(text);
            if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
