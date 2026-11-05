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
///   ① CRC 校验（已在切帧阶段完成）
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
public static class FrameCipher
{
    private const int BlockSize = 16;
    private const int SessionKeyLength = 32;
    private const int MaxPadding = 16;

    /// <summary>校验会话密钥长度（游戏侧 SetK 只接受恰好 32 字节）。</summary>
    public static bool IsValidSessionKey(byte[]? key) => key is { Length: SessionKeyLength };

    /// <summary>
    /// 解密并解压一帧的载荷，产出可直接交给 protobuf 的明文。
    ///
    /// <para>明文 msgId、无密钥、或长度非 16 对齐时跳过解密（视为未加密）。</para>
    /// <para>解压失败时返回解密后的中间结果（不抛异常，由调用方决定如何处理）。</para>
    /// </summary>
    public static byte[] Decrypt(GatewayFrame frame, byte[]? sessionKey)
    {
        var plain = DecryptRaw(frame, sessionKey);

        // ② 解压（仅压缩帧）
        if (!frame.IsCompressed || plain.Length == 0)
            return plain;

        return TryInflate(plain, frame.OrigSize, out var inflated) ? inflated : plain;
    }

    /// <summary>只做解密（不含解压）。</summary>
    public static byte[] DecryptRaw(GatewayFrame frame, byte[]? sessionKey)
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
            using var aes = Aes.Create();
            aes.Key = sessionKey!;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            plain = decryptor.TransformFinalBlock(payload, 0, payload.Length);
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
    /// </summary>
    public static bool TryInflate(byte[] compressed, uint expectedSize, out byte[] result)
    {
        result = [];

        if (compressed.Length == 0)
            return false;

        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            var data = output.ToArray();

            // native: `if (a2 != v9) { 日志; result = 0; }` —— 长度严格校验
            if (expectedSize > 0 && data.LongLength != expectedSize)
                return false;

            result = data;
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
