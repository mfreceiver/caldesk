using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CalDesk.Core.Services;

namespace CalDesk.App.Services;

/// <summary><see cref="ICredentialStore"/> backed by the Windows Credential Manager
/// (CRED_TYPE_GENERIC, persisted to local machine). Passwords never touch SQLite or disk files.</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref CREDENTIAL cred, uint flags);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credential);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint reservedFlag);

    public Task<string?> GetPasswordAsync(string target, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(Read(target));
    }

    public Task SetPasswordAsync(string target, string password, CancellationToken ct = default)
    {
        Write(target, password);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string target, CancellationToken ct = default)
    {
        CredDeleteW(target, CRED_TYPE_GENERIC, 0);
        return Task.CompletedTask;
    }

    private static string? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            return Marshal.GetLastWin32Error() == ERROR_NOT_FOUND ? null : throw new Win32Exception();
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    private static void Write(string target, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password ?? string.Empty);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = Marshal.StringToCoTaskMemUni(target),
            UserName = Marshal.StringToCoTaskMemUni("CalDesk"),
            CredentialBlobSize = (uint)blob.Length,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
        };

        Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);

        try
        {
            if (!CredWriteW(ref cred, 0))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(cred.TargetName);
            Marshal.FreeCoTaskMem(cred.UserName);
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }
}
