using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Le type de démarrage d'un service, changé par le gestionnaire de services comme le fait
    /// la console Services : écrire la valeur dans le registre ne serait lu qu'au prochain
    /// démarrage, et le gestionnaire continuerait d'ici là sur l'ancienne.
    /// </summary>
    internal static class ServiceNative
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceNoChange = 0xFFFFFFFF;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfigW(
            IntPtr service, uint type, uint start, uint errorControl, string? binaryPath, string? loadOrderGroup,
            IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>0 si le changement est fait, sinon le code d'erreur de Windows.</summary>
        public static int ChangeStart(string name, int start)
        {
            var manager = OpenSCManagerW(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try
            {
                var service = OpenServiceW(manager, name, ServiceChangeConfig);
                if (service == IntPtr.Zero) return Marshal.GetLastWin32Error();
                try
                {
                    return ChangeServiceConfigW(service, ServiceNoChange, (uint)start, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null)
                        ? 0
                        : Marshal.GetLastWin32Error();
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }
    }
}
