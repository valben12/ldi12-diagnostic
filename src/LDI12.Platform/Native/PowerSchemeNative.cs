using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>Le mode d'alimentation, par l'API de gestion de l'alimentation (Vista et suivants).</summary>
    internal static class PowerSchemeNative
    {
        internal static Guid? Active()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid))!; }
            finally { LocalFree(pointer); }
        }

        /// <summary>Le mode existe sur ce PC : beaucoup de portables récents n'en gardent qu'un.</summary>
        internal static bool Exists(Guid scheme)
        {
            uint size = 0;
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) == 0 && size > 0;
        }

        internal static int Activate(Guid scheme) => (int)PowerSetActiveScheme(IntPtr.Zero, ref scheme);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(
            IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
