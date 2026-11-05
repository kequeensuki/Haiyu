using System.Security.Cryptography;
using Haiyu.ABI.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Decrypt;

/// <summary>
/// 从 <c>112 ProtoKeyResponse</c> 里解出 32 字节会话密钥。
///
/// 流程（照 OfflineClient <c>CryptoLayer.DecryptSessionKeyBlob</c>）：
/// <code>
///   112 载荷（明文）→ ProtoKeyResponse.Key（256B）→ RSA PKCS#1 v1.5 解密 → 32B 会话密钥
/// </code>
///
/// ★ 112 本身是明文（<c>PlaintextMsgIds = {111,112}</c>），所以不需要密钥就能解出它，
///   再用它当钥匙去解其他帧。
///
/// ★ 私钥不内置：由调用方通过 <see cref="LoadPrivateKeyPem"/> 注入
///   （来源见 doc/密钥管理方案.md：凭据管理器 / DPAPI 文件 / 环境变量）。
/// </summary>
public sealed class SessionKeyLoader : IDisposable
{
    private const int ExpectedBlobLength = 256;
    private const int ProtoKeyResponseMsgId = 112;

    private RSA? _privateKey;
    private byte[]? _lastKeyBlob;
    internal int Generation { get; private set; }

    /// <summary>是否已载入私钥。</summary>
    public bool HasPrivateKey => _privateKey is not null;

    /// <summary>解出的会话密钥；未解出时为 <c>null</c>。</summary>
    public byte[]? SessionKey { get; private set; }

    /// <summary>载入 PEM 格式的 RSA 私钥。</summary>
    public void LoadPrivateKeyPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }

        _privateKey?.Dispose();
        _privateKey = rsa;
    }

    /// <summary>共享私钥来源，但每条连接拥有独立的会话密钥和生命周期。</summary>
    internal SessionKeyLoader CreateSession()
    {
        if (_privateKey is null)
            throw new InvalidOperationException("尚未载入私钥。");
        var session = new SessionKeyLoader();
        session.LoadPrivateKeyPem(_privateKey.ExportPkcs8PrivateKeyPem());
        return session;
    }

    /// <summary>
    /// 若该帧是 112，尝试解出会话密钥。
    /// 返回 <c>true</c> 表示本次成功解出（或此前已解出）。
    /// </summary>
    public bool TryResolve(GatewayFrame frame)
    {
        if (frame.MsgId != ProtoKeyResponseMsgId)
            return false;

        if (_privateKey is null)
            return false;

        if (frame.Payload.Length < 2)
            return false;

        var blob = ExtractKeyBlob(frame.Payload);
        if (blob is null || blob.Length != ExpectedBlobLength)
            return false;

        // 相同 112 重传不能重置正在收集的快照。
        if (SessionKey is not null && _lastKeyBlob is not null && blob.AsSpan().SequenceEqual(_lastKeyBlob))
            return true;

        byte[]? key;
        try
        {
            // PKCS#1 v1.5：填充会被自动剥离，得到原始 32 字节
            key = _privateKey.Decrypt(blob, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (!FrameCipher.IsValidSessionKey(key))
            return false;

        if (SessionKey is not null)
            CryptographicOperations.ZeroMemory(SessionKey);
        SessionKey = key;
        _lastKeyBlob = blob;
        Generation++;
        return true;
    }

    /// <summary>
    /// 从 ProtoKeyResponse 载荷里取 <c>key</c> 字段（字段号 3）。
    /// 用生成的 protobuf 类型解析，不手写解析器。
    /// </summary>
    private static byte[]? ExtractKeyBlob(byte[] payload)
    {
        try
        {
            var message = ProtoKeyResponse.Parser.ParseFrom(payload);
            var blob = message.Key.ToByteArray();
            return blob.Length > 0 ? blob : null;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _privateKey?.Dispose();
        _privateKey = null;

        if (SessionKey is { Length: > 0 })
            CryptographicOperations.ZeroMemory(SessionKey);

        SessionKey = null;
        _lastKeyBlob = null;
    }
}
