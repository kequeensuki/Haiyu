using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Haiyu.ABI.Services.GameDataExport.Security;

/// <summary>
/// DPAPI 薄封装（P/Invoke <c>crypt32!CryptProtectData</c> / <c>CryptUnprotectData</c>）。
///
/// <para>
/// ★ 为什么用 P/Invoke 而不是 <c>System.Security.Cryptography.ProtectedData</c> 包：
///   本项目全部工程 <c>PublishAot=true</c>，该 NuGet 包需逐一验证 AOT 兼容性；
///   P/Invoke 是纯声明、无反射，AOT 下零风险（密钥管理方案 §4.3 注明的备选路径）。
/// </para>
///
/// <para>
/// DPAPI 的密钥由 Windows 保管（绑定当前用户 + 当前机器），
/// 因此加密产物换用户/换机器都解不开——这正是我们需要的性质。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedDataAccess
{
    /// <summary>用当前用户的 DPAPI 主密钥加密。</summary>
    public static byte[] Protect(byte[] plain, byte[] entropy) =>
        ProtectedData(plain, entropy, protect: true);

    /// <summary>用当前用户的 DPAPI 主密钥解密；失败抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] Unprotect(byte[] cipher, byte[] entropy) =>
        ProtectedData(cipher, entropy, protect: false);

    private static byte[] ProtectedData(byte[] data, byte[] entropy, bool protect)
    {
        var plainBlob = ToBlob(data);
        var entropyBlob = ToBlob(entropy);
        var outputBlob = default(CryptoApiBlob);
        try
        {
            var success = protect
                ? CryptProtectData(ref plainBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob)
                : CryptUnprotectData(ref plainBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob);

            if (!success)
                throw new CryptographicException(
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);

            return FromBlob(outputBlob);
        }
        finally
        {
            FreeBlobIf(ref outputBlob);
            FreeBlobIf(ref plainBlob);
            FreeBlobIf(ref entropyBlob);
        }
    }

    private static CryptoApiBlob ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new CryptoApiBlob { Size = (uint)data.Length, Data = ptr };
    }

    private static byte[] FromBlob(CryptoApiBlob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Size == 0)
            return [];

        var result = new byte[blob.Size];
        Marshal.Copy(blob.Data, result, 0, (int)blob.Size);
        return result;
    }

    private static void FreeBlobIf(ref CryptoApiBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptoApiBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref CryptoApiBlob dataIn,
        string? description,
        ref CryptoApiBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        ref CryptoApiBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref CryptoApiBlob dataIn,
        IntPtr description,
        ref CryptoApiBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        ref CryptoApiBlob dataOut);

    // CRYPTPROTECT_UI_FORBIDDEN：无 UI 提示（服务进程无人交互）
    // —— 0 已是不弹 UI 的默认行为，此处保持 flags=0 与 DPAPI 包行为一致
}
