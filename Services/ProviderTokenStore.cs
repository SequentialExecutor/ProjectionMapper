using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Optional per-provider secrets in the current user's Windows Credential Manager.</summary>
    public static class ProviderTokenStore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags, Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes, TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Write(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Delete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
        private static string Target(ArtworkProvider provider) => "ProjectionMapper.Artwork." + provider;
        public static void Save(ArtworkProvider provider, string token)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            if (bytes.Length == 0 || bytes.Length > 2560) throw new ArgumentException("The access token is empty or too long for Windows Credential Manager.");
            var pointer = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var credential = new Credential { Type = 1, TargetName = Target(provider), CredentialBlob = pointer, CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
                if (!Write(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the provider token securely.");
            }
            finally
            {
                for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
                Marshal.FreeCoTaskMem(pointer); CryptographicOperations.ZeroMemory(bytes);
            }
        }
        public static string? Load(ArtworkProvider provider)
        {
            if (!Read(Target(provider), 1, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the stored provider token.");
            }
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(pointer); }
        }
        public static void Forget(ArtworkProvider provider)
        {
            if (!Delete(Target(provider), 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the stored provider token.");
        }
    }
}
