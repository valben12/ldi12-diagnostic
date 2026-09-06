using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Énumération des comptes locaux par l'API réseau de Windows.
    /// </summary>
    /// <remarks>
    /// Isolée des autres P/Invoke, car ces appels ont leur propre convention : le tampon rendu doit
    /// être libéré par <c>NetApiBufferFree</c>, faute de quoi chaque analyse laisse fuir de la
    /// mémoire non managée. Le regroupement rend l'oubli visible.
    /// </remarks>
    internal static class AccountNative
    {
        internal const int NERR_Success = 0;
        internal const int ERROR_MORE_DATA = 234;

        /// <summary>Niveau 1 : nom, privilège et indicateurs. Le plus compact qui porte les deux.</summary>
        internal const int UserInfoLevel1 = 1;

        /// <summary>Niveau 3 : le membre est rendu sous la forme « domaine\nom ».</summary>
        internal const int LocalGroupMembersLevel3 = 3;

        /// <summary>N'énumérer que les comptes normaux : ni comptes machine, ni comptes d'approbation.</summary>
        internal const int FILTER_NORMAL_ACCOUNT = 0x0002;

        internal const int UF_ACCOUNTDISABLE = 0x0002;
        internal const int UF_PASSWD_NOTREQD = 0x0020;
        internal const int UF_DONT_EXPIRE_PASSWD = 0x10000;
        internal const int UF_LOCKOUT = 0x0010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct USER_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string usri1_name;
            [MarshalAs(UnmanagedType.LPWStr)] internal string usri1_password;
            internal uint usri1_password_age;
            internal uint usri1_priv;
            [MarshalAs(UnmanagedType.LPWStr)] internal string usri1_home_dir;
            [MarshalAs(UnmanagedType.LPWStr)] internal string usri1_comment;
            internal uint usri1_flags;
            [MarshalAs(UnmanagedType.LPWStr)] internal string usri1_script_path;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LOCALGROUP_MEMBERS_INFO_3
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string lgrmi3_domainandname;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int NetUserEnum(
            string? serverName, int level, int filter, out IntPtr buffer, int preferredMaxLength,
            out int entriesRead, out int totalEntries, ref int resumeHandle);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int NetLocalGroupGetMembers(
            string? serverName, string groupName, int level, out IntPtr buffer, int preferredMaxLength,
            out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("netapi32.dll")]
        internal static extern int NetApiBufferFree(IntPtr buffer);
    }
}
