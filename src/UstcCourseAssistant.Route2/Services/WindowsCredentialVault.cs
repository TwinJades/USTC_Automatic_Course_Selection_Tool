using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;

namespace UstcCourseAssistant.Route2.Services;

public sealed record StoredCredential(string UserName, string Password);

public interface ICredentialVault
{
    void Save(string userName, SecureString password);
    StoredCredential? Read();
    bool Exists();
    void Delete();
}

public sealed class WindowsCredentialVault : ICredentialVault
{
    private const string TargetName = "USTC Course Assistant Route2";
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    public void Save(string userName, SecureString password)
    {
        var normalizedUserName = (userName ?? string.Empty).Trim();
        if (normalizedUserName.Length == 0)
        {
            throw new ArgumentException("账号不能为空。", nameof(userName));
        }

        if (password.Length == 0)
        {
            throw new ArgumentException("密码不能为空。", nameof(password));
        }

        var blobSize = checked(password.Length * sizeof(char));
        if (blobSize > MaximumBlobBytes)
        {
            throw new ArgumentException("密码长度超过 Windows 凭据管理器允许的范围。", nameof(password));
        }

        var secretPointer = Marshal.SecureStringToCoTaskMemUnicode(password);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = TargetName,
                CredentialBlobSize = blobSize,
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = normalizedUserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows 凭据管理器。");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(secretPointer);
        }
    }

    public StoredCredential? Read()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "无法读取 Windows 凭据管理器。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return null;
            }

            var password = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                credential.CredentialBlobSize / sizeof(char)) ?? string.Empty;
            return new StoredCredential(credential.UserName ?? string.Empty, password);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public bool Exists()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }

            throw new Win32Exception(error, "无法读取 Windows 凭据管理器状态。");
        }

        CredFree(credentialPointer);
        return true;
    }

    public void Delete()
    {
        if (CredDelete(TargetName, GenericCredential, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "无法删除 Windows 凭据管理器中的密码。");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credentialPointer);
}

internal sealed class InMemoryCredentialVault : ICredentialVault
{
    private StoredCredential? _credential;

    public void Save(string userName, SecureString password)
    {
        _credential = new StoredCredential(userName.Trim(), new NetworkCredential(string.Empty, password).Password);
    }

    public StoredCredential? Read() => _credential;

    public bool Exists() => _credential is not null;

    public void Delete() => _credential = null;
}
