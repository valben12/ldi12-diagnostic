using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Active un privilège déjà détenu par le processus.
    /// </summary>
    /// <remarks>
    /// Un administrateur élevé détient le privilège de sauvegarde, mais Windows le laisse désactivé :
    /// il faut le demander. Rien n'est accordé ici qui ne l'était déjà.
    /// </remarks>
    internal static class PrivilegeNative
    {
        private const uint AdjustPrivileges = 0x0020;
        private const uint Query = 0x0008;
        private const uint Enabled = 0x00000002;
        private const int NotAllAssigned = 1300;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint Low;
            public int High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint Count;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
            uint length, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Vrai si le privilège est désormais actif pour tout le processus.</summary>
        internal static bool Enable(string privilege)
        {
            if (!OpenProcessToken(GetCurrentProcess(), AdjustPrivileges | Query, out var token)) return false;

            try
            {
                if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;

                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = Enabled };
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;

                // AdjustTokenPrivileges réussit même quand le jeton ne détient pas le privilège :
                // seule cette erreur le dit.
                return Marshal.GetLastWin32Error() != NotAllAssigned;
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
