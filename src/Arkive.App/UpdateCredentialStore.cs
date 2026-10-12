using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Arkive.App;

internal static class UpdateCredentialStore
{
    private const string Target = "Arkive:GitHub:AN-SOTP/Arkive";

    internal static string? Read()
    {
        if (!CredRead(Target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    internal static void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a valid GitHub access token.", nameof(token));
        var bytes = Encoding.Unicode.GetBytes(token);
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, TargetName = Target, BlobSize = (uint)bytes.Length,
                Blob = pointer, Persist = 2, UserName = "GitHub",
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static void Delete()
    {
        if (!CredDelete(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
