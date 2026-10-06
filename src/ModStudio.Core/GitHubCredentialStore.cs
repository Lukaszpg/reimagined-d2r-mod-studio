using System.Runtime.InteropServices;
using System.Text;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>Windows Credential Manager storage compatible with the Charsi token approach: generic credential, UTF-8 blob, no JSON copy.</summary>
public static class GitHubCredentialStore
{
    private const string Target = "ReimaginedD2RModStudio/GitHubToken";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static bool Available => OperatingSystem.IsWindows();

    public static string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredReadW(Target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new IOException($"Windows Credential Manager read failed ({error}).");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public static void Save(string token)
    {
        Require(OperatingSystem.IsWindows(), "Secure GitHub token storage requires Windows Credential Manager.");
        token = token.Trim();
        Require(token.Length > 0, "Paste a GitHub token first.");
        var bytes = Encoding.UTF8.GetBytes(token);
        IntPtr target = IntPtr.Zero, user = IntPtr.Zero, blob = IntPtr.Zero;
        try
        {
            target = Marshal.StringToCoTaskMemUni(Target);
            user = Marshal.StringToCoTaskMemUni("GitHub PAT");
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user
            };
            if (!CredWriteW(ref credential, 0)) throw new IOException($"Windows Credential Manager write failed ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
            if (user != IntPtr.Zero) Marshal.FreeCoTaskMem(user);
            if (target != IntPtr.Zero) Marshal.FreeCoTaskMem(target);
        }
        var roundTrip = Read();
        Require(roundTrip != null && Encoding.UTF8.GetBytes(roundTrip).SequenceEqual(bytes), "GitHub token did not round-trip through Windows Credential Manager.");
    }

    public static void Delete()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (CredDeleteW(Target, CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new IOException($"Windows Credential Manager delete failed ({error}).");
    }

    public static string Describe(string token) => $"length {token.Length} · SHA-256 {Hash(token)[..12]}";

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FileTime LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
