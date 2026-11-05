using System.IO.Compression;
using System.Security.Cryptography;

namespace Haiyu.ABI.Services.GameDataExport.Decrypt;

/// <summary>
/// 帧载荷解密 + 解压层。
///
/// <para>
/// <b>处理顺序</b>（照 OfflineClient <c>ReplayPcap.ParseFrame</c> /
/// <c>GatewayClient.HandleFrame</c>，经 IDA 实证）：
/// <code>
///   ① 业务帧 CRC 校验
///   ② 解密：AES-256-ECB（无 IV） → 去 PKCS#7 → XOR(seqNo)
///   ③ 解压：仅压缩帧（type &amp; 0x10），zlib 格式，长度必须等于 origSize
/// </code>
/// </para>
///
/// <para>
/// XOR 规则：<c>payload[seqNo % len] ^= key[seqNo % 32]</c>（只翻转一个字节）。
/// </para>
///
/// <para>
/// ★ 明文 msgId（111/112）不解密也不解压，直接返回原字节。
/// </para>
///
/// <para>
/// ★ <b>为什么必须解压</b>：背包等大数据帧全部是压缩帧
/// （实测 <c>22445</c> 密文 2016B / <c>origSize=4034</c>），
/// 不解压直接喂给 protobuf 必然解析失败。
/// </para>
/// </summary>
public sealed class FrameCipher : IDisposable
{
    private const int BlockSize = 16;
    private const int SessionKeyLength = 32;
    private const int MaxPadding = 16;

    /// <summary>解压后大小的合理上限（与 FrameSplitter 的帧头校验一致：64 MB）。</summary>
    private const uint MaxOrigSize = 64 * 1024 * 1024;

    private readonly object _gate = new();
    private ICryptoTransform? _transform;
    private byte[]? _key;
    private bool _disposed;

    public static bool IsValidSessionKey(byte[]? key) => key is { Length: SessionKeyLength };

    private ICryptoTransform GetDecryptor(byte[] sessionKey)
    {
        if (_transform is { CanReuseTransform: true } && _key is not null && _key.AsSpan().SequenceEqual(sessionKey))
            return _transform;
        ClearTransform();
        using var aes = Aes.Create();
        aes.Key = sessionKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        _transform = aes.CreateDecryptor();
        _key = sessionKey.ToArray();
        return _transform;
    }

    private void ClearTransform()
    {
        _transform?.Dispose();
        _transform = null;
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearTransform();
        }
    }

    /// <summary>
    /// 解密并解压一帧的载荷，产出可直接交给 protobuf 的明文。
    ///
    /// <para>校验或解码失败时抛出 InvalidDataException；持续捕获使用 TryDecode 取得结构化状态。</para>
    /// </summary>
    public static byte[] Decrypt(GatewayFrame frame, byte[]? sessionKey)
    {
        if (!TryDecrypt(frame, sessionKey, out var plain, out var status))
            throw new InvalidDataException($"业务帧解码失败：{status}");
        return plain!;
    }

    /// <summary>一次性解码入口；持续捕获复用会话自己的实例。</summary>
    internal static bool TryDecrypt(GatewayFrame frame, byte[]? key, out byte[]? plain, out string? status)
    {
        using var cipher = new FrameCipher();
        return cipher.TryDecode(frame, key, out plain, out status);
    }

    internal bool TryDecode(GatewayFrame frame, byte[]? key, out byte[]? plain, out string? status)
    {
        plain = null;
        status = null;
        if (!FrameIntegrity.IsValid(frame))
        {
            status = "crc_failed";
            return false;
        }
        if (frame.Type == GatewayMessageType.Exception)
        {
            plain = frame.Payload;
            return true;
        }
        if (!frame.IsPlaintext && !IsValidSessionKey(key))
        {
            status = "key_unavailable";
            return false;
        }
        var decrypted = DecryptPayload(frame, key);
        if (!frame.IsPlaintext && frame.Payload.Length > 0 && frame.Payload.Length % BlockSize == 0
            && ReferenceEquals(decrypted, frame.Payload))
        {
            status = "decrypt_failed";
            return false;
        }
        if (frame.IsCompressed && !TryInflate(decrypted, frame.OrigSize, out decrypted))
        {
            status = "decompress_failed";
            return false;
        }
        plain = decrypted;
        return true;
    }

    /// <summary>只做解密（不含解压）。</summary>
    public static byte[] DecryptRaw(GatewayFrame frame, byte[]? sessionKey)
    {
        using var cipher = new FrameCipher();
        return cipher.DecryptPayload(frame, sessionKey);
    }

    private byte[] DecryptPayload(GatewayFrame frame, byte[]? sessionKey)
    {
        var payload = frame.Payload;

        if (payload.Length == 0)
            return payload;

        if (frame.IsPlaintext)
            return payload;

        if (!IsValidSessionKey(sessionKey))
            return payload;

        // 非 16 字节对齐说明不是 AES 密文（AES 分组要求）
        if (payload.Length % BlockSize != 0)
            return payload;

        byte[] plain;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    plain = GetDecryptor(sessionKey!).TransformFinalBlock(payload, 0, payload.Length);
                }
                catch (CryptographicException)
                {
                    ClearTransform();
                    throw;
                }
            }
        }
        catch (CryptographicException)
        {
            return payload;
        }

        plain = RemovePadding(plain);
        return XorBySequence(plain, frame.SeqNo, sessionKey!);
    }

    /// <summary>
    /// zlib 解压，长度必须严格等于 <paramref name="expectedSize"/>。
    ///
    /// ★ 对应 native <c>sub_142E8EA10</c>：<c>inflateInit_("1.2.5", 88)</c>
    ///   → <c>inflate(stream, Z_FINISH)</c> → 长度比对，不符即失败。
    ///
    /// ★ 内存（v2 P2-A7，消除双拷贝）：origSize 已知时直接按其分配输出缓冲、
    ///   <see cref="ZLibStream.Read"/> 直填——旧实现经 MemoryStream 中转再
    ///   <c>ToArray()</c> 是两次拷贝；新实现一次分配一次填充，调用方签名不变。
    ///   无 origSize 时回退旧路径（频率为零：压缩帧必带 origSize）。
    /// </summary>
    public static bool TryInflate(byte[] compressed, uint expectedSize, out byte[] result)
    {
        result = [];

        if (compressed.Length == 0)
            return false;

        // 快路径：压缩帧必带合理的 origSize → 精确分配直填
        if (expectedSize > 0 && expectedSize <= MaxOrigSize)
        {
            var output = new byte[expectedSize];
            try
            {
                int written;
                using (var input = new MemoryStream(compressed))
                using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
                {
                    written = 0;
                    while (written < output.Length)
                    {
                        var read = zlib.Read(output, written, output.Length - written);
                        if (read == 0)
                            break;
                        written += read;
                    }
                    if (zlib.ReadByte() != -1)
                        return false;
                }

                // native: `if (a2 != v9) { 日志; result = 0; }` —— 长度严格校验
                if (written != expectedSize)
                    return false;

                result = output;
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        // 回退路径：无 origSize / 异常尺寸（实际不发生，防御保留）
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);

            if (output.Length > int.MaxValue || (expectedSize > 0 && output.Length != expectedSize))
                return false;

            result = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
    /// <summary>
    /// 去 PKCS#7 填充。填充非法时按无填充处理（不抛异常）。
    /// </summary>
    private static byte[] RemovePadding(byte[] data)
    {
        if (data.Length == 0)
            return data;

        var pad = data[^1];
        if (pad is 0 or > MaxPadding || pad > data.Length)
            return data;

        for (var i = data.Length - pad; i < data.Length; i++)
        {
            if (data[i] != pad)
                return data;
        }

        return data[..^pad];
    }

    /// <summary><c>payload[seq % len] ^= key[seq % keyLen]</c></summary>
    private static byte[] XorBySequence(byte[] data, uint seqNo, byte[] key)
    {
        if (data.Length == 0 || key.Length == 0)
            return data;

        var index = (int)(seqNo % (uint)data.Length);
        data[index] ^= key[seqNo % (uint)key.Length];
        return data;
    }
}
