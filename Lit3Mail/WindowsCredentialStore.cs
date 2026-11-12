using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lit3Mail;

internal sealed record SavedSignIn(
    string ConnectionKey, string IncomingPassword, string OutgoingPassword)
{
    public override string ToString() => "Lit3Mail sign-in";
}

internal static class WindowsCredentialStore
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;
    private const int MaxBlobSize = 2560;

    private static string Target(Guid id) => $"Lit3Mail:Account:{id:D}";

    public static SavedSignIn? Read(Guid id)
    {
        if (!CredRead(Target(id), Generic, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize is 0 or > MaxBlobSize)
                throw new JsonException("Invalid saved sign-in size.");
            var bytes = new byte[credential.BlobSize];
            try
            {
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                var signIn = JsonSerializer.Deserialize<SavedSignIn>(bytes);
                if (signIn is null || string.IsNullOrEmpty(signIn.IncomingPassword) || string.IsNullOrEmpty(signIn.OutgoingPassword))
                    throw new JsonException("The saved sign-in has no mail server passwords.");
                return signIn;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(Guid id, SavedSignIn signIn)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(signIn);
        try
        {
            if (bytes.Length > MaxBlobSize)
                throw new InvalidOperationException("The sign-in exceeds the Windows Credential Manager size limit.");

            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var credential = new Credential
                {
                    Type = Generic,
                    TargetName = Target(id),
                    BlobSize = (uint)bytes.Length,
                    Blob = handle.AddrOfPinnedObject(),
                    Persist = LocalMachine,
                    UserName = "Lit3Mail"
                };
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static void Delete(Guid id)
    {
        if (CredDelete(Target(id), Generic, 0))
            return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound)
            throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}

