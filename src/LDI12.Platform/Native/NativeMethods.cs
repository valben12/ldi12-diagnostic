using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// P/Invoke regroupés par famille d'API.
    /// </summary>
    /// <remarks>
    /// Règle du projet : toute API absente d'une version de Windows cible est appelée par
    /// pointeur de fonction obtenu via <see cref="GetProcAddress"/>, jamais par un
    /// <c>DllImport</c> direct. Un <c>DllImport</c> vers une fonction inexistante ne lève
    /// qu'à l'appel, mais on veut transformer l'absence en état de disponibilité, pas en
    /// exception à rattraper dans chaque module.
    /// </remarks>
    internal static class NativeMethods
    {
        // ---------- Version réelle du système ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        internal const byte VER_NT_WORKSTATION = 0x01;

        /// <summary>
        /// Seule source fiable de la version de Windows : contrairement à GetVersionEx et à
        /// Environment.OSVersion, RtlGetVersion n'est jamais soumis à la couche de compatibilité
        /// applicative. Renvoie 0 (STATUS_SUCCESS) en cas de succès.
        /// </summary>
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RtlGetVersion(ref OSVERSIONINFOEXW versionInformation);

        // ---------- Architecture ----------

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        internal const ushort PROCESSOR_ARCHITECTURE_INTEL = 0;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM = 5;
        internal const ushort PROCESSOR_ARCHITECTURE_IA64 = 6;
        internal const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM64 = 12;

        [DllImport("kernel32.dll")]
        internal static extern void GetNativeSystemInfo(ref SYSTEM_INFO systemInfo);

        internal const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
        internal const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
        internal const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01C4;
        internal const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
        internal const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate bool IsWow64Process2Delegate(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        /// <summary>
        /// IsWow64Process2 n'existe qu'à partir de Windows 10 1511. C'est le seul moyen de
        /// distinguer un vrai ARM64 d'un x64 : sur les versions antérieures, ARM64 n'existe pas,
        /// donc GetNativeSystemInfo suffit.
        /// </summary>
        internal static bool TryGetWow64Machines(out ushort processMachine, out ushort nativeMachine)
        {
            processMachine = IMAGE_FILE_MACHINE_UNKNOWN;
            nativeMachine = IMAGE_FILE_MACHINE_UNKNOWN;

            var address = TryGetProcAddress("kernel32.dll", "IsWow64Process2");
            if (address == IntPtr.Zero) return false;

            try
            {
                var call = (IsWow64Process2Delegate)Marshal.GetDelegateForFunctionPointer(
                    address, typeof(IsWow64Process2Delegate));
                return call(GetCurrentProcess(), out processMachine, out nativeMachine);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is MarshalDirectiveException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        // ---------- Sondage d'API ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string procName);

        /// <summary>
        /// Cherche une fonction dans un module <b>déjà chargé</b>. Ne charge volontairement
        /// aucune bibliothèque : sonder une capacité ne doit pas avoir d'effet de bord.
        /// </summary>
        internal static IntPtr TryGetProcAddress(string moduleName, string procName)
        {
            var module = GetModuleHandleW(moduleName);
            return module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, procName);
        }

        internal static bool ApiExists(string moduleName, string procName)
            => TryGetProcAddress(moduleName, procName) != IntPtr.Zero;

        // ---------- Élévation ----------

        internal const int TokenElevationType = 18;
        internal const uint TOKEN_QUERY = 0x0008;

        internal enum TOKEN_ELEVATION_TYPE
        {
            Default = 1,   // UAC désactivé, ou compte standard
            Full = 2,      // processus élevé
            Limited = 3,   // administrateur non élevé (jeton filtré)
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
            int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        // ---------- Mesures système ----------

        /// <summary>Alimentation secteur : 0 hors secteur, 1 sur secteur, 255 indéterminé.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            internal byte ACLineStatus;
            internal byte BatteryFlag;
            internal byte BatteryLifePercent;
            internal byte SystemStatusFlag;
            internal uint BatteryLifeTime;
            internal uint BatteryFullLifeTime;
        }

        internal const byte AC_LINE_OFFLINE = 0;
        internal const byte AC_LINE_ONLINE = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll")]
        internal static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFirmwareEnvironmentVariableW(
            string name, string guid, byte[] buffer, uint size);

        // ---------- Appartenance à un domaine ----------

        internal const int NetSetupDomainName = 3;

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int NetGetJoinInformation(
            string? server, out IntPtr nameBuffer, out int bufferType);

        [DllImport("netapi32.dll")]
        internal static extern int NetApiBufferFree(IntPtr buffer);

        // ---------- Corbeille ----------

        /// <summary>
        /// Windows ne donne de la corbeille que son décompte et son volume ; les noms des
        /// éléments ne s'obtiennent que par le shell. La prévisualisation dit donc ce qu'elle
        /// sait, et dit aussi ce qu'elle ne sait pas.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct SHQUERYRBINFO
        {
            internal int cbSize;
            internal long i64Size;
            internal long i64NumItems;
        }

        internal const int SHERB_NOCONFIRMATION = 0x00000001;
        internal const int SHERB_NOPROGRESSUI = 0x00000002;
        internal const int SHERB_NOSOUND = 0x00000004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHEmptyRecycleBin(IntPtr owner, string? rootPath, int flags);
    }
}
