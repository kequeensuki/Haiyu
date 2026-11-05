using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Haiyu.ABI.Services.GameDataExport.Security;

/// <summary>
/// 凭据管理器封装（Win32 CredRead/CredWrite/CredDelete）。
/// 优点：由 Windows 统一保管，可在「控制面板 → 凭据管理器」里查看/删除，用户可控。
/// </summary>
public static class CredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr cred);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    public static bool TryRead(string target, out byte[]? secret)
    {
        secret = null;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
            return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize <= 0)
                return false;
            secret = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, secret, 0, cred.CredentialBlobSize);
            return true;
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string target, string userName, byte[] secret)
    {
        var blob = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                CredentialBlobSize = secret.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "Haiyu local secret",
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite 失败");
        }
        finally
        {
            // 立刻抹掉非托管明文
            for (int i = 0; i < secret.Length; i++)
                Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static bool TryDelete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
}
