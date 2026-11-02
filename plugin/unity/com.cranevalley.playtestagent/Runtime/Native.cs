// What Unity's .NET profile has no managed API for: a file's owner. FileSecurity isn't available to a package here.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Autopilot.Playtest
{
    public static class Native
    {
        static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>Whether this user owns <paramref name="path"/>, itself and not through a link.</summary>
        public static bool OwnedByCurrentUser(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                // Off Windows the folder is in the user's home, where no other user can create it.
                return !Windows || OwnerIsTokenUser(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // -- Windows ---------------------------------------------------------------------------------------------

        const int FileObject = 1;
        const uint OwnerSecurityInformation = 1;
        const uint TokenQuery = 8;
        const int TokenUser = 1;

        static bool OwnerIsTokenUser(string path)
        {
            if (GetNamedSecurityInfoW(path, FileObject, OwnerSecurityInformation, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor) != 0)
                return false;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
                try
                {
                    GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var size);
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        // TOKEN_USER starts with the SID's pointer.
                        return GetTokenInformation(token, TokenUser, buffer, size, out _) && EqualSid(owner, Marshal.ReadIntPtr(buffer));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetNamedSecurityInfoW(string name, int type, uint info, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int size, out int needed);

        [DllImport("advapi32.dll")]
        static extern bool EqualSid(IntPtr a, IntPtr b);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);
    }
}
