using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LDI12.Core.Execution;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les gestes du Gestionnaire de périphériques, par SetupAPI et Configuration Manager.
    /// </summary>
    /// <remarks>
    /// Ce sont les appels que font le Gestionnaire de périphériques et l'outil devcon de
    /// Microsoft : redémarrer un périphérique (DIF_PROPERTYCHANGE / DICS_PROPCHANGE), l'activer
    /// (DICS_ENABLE), le désinstaller (DiUninstallDevice), puis faire « Rechercher les
    /// modifications sur le matériel » (CM_Reenumerate_DevNode). Tous présents depuis Windows 7.
    /// <para>
    /// <b>32 et 64 bits.</b> Les structures de SetupAPI sont compactées à l'octet sur 32 bits et
    /// alignées sur 64 : leur taille est donc calculée par le marshaling, jamais écrite en dur.
    /// Un processus 32 bits sur un Windows 64 bits ne peut pas appeler l'installateur de classe
    /// (ERROR_IN_WOW64) : les deux processus du logiciel sont en AnyCPU sans préférence 32 bits,
    /// ce cas ne se présente donc pas, et il serait signalé tel quel s'il se présentait.
    /// </para>
    /// </remarks>
    internal static class DeviceNative
    {
        private const int CrSuccess = 0;
        private const uint CmLocateDevNodeNormal = 0;
        private const uint CmReenumerateSynchronous = 0x1;

        private const uint DifPropertyChange = 0x12;
        private const uint DicsEnable = 1;
        private const uint DicsPropChange = 3;
        private const uint DicsFlagGlobal = 1;
        private const uint DicsFlagConfigSpecific = 2;

        private const uint DiNeedRestart = 0x80;
        private const uint DiNeedReboot = 0x100;

        private const uint SpdrpDeviceDesc = 0x0;
        private const uint SpdrpClass = 0x7;
        private const uint SpdrpFriendlyName = 0xC;

        private const uint DnHasProblem = 0x400;

        private const int ErrorInWow64 = unchecked((int)0xE0000235);
        private const int ErrorAccessDenied = 5;

        internal static ControlResult Rescan()
        {
            var located = CM_Locate_DevNodeW(out var root, null, CmLocateDevNodeNormal);
            if (located != CrSuccess) return ControlResult.Fail("Arborescence des périphériques inaccessible (CR " + located + ").", located);

            var result = CM_Reenumerate_DevNode(root, CmReenumerateSynchronous);
            return result == CrSuccess
                ? ControlResult.Ok("Recherche de nouveau matériel terminée.")
                : ControlResult.Fail(result == 0x33
                    ? "Windows demande les privilèges administrateur pour rechercher le matériel."
                    : "Windows a refusé la recherche de matériel (CR " + result + ").", result);
        }

        internal static DeviceState? Find(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return null;
            if (CM_Locate_DevNodeW(out var node, instanceId, CmLocateDevNodeNormal) != CrSuccess) return null;

            var problem = 0;
            if (CM_Get_DevNode_Status(out var status, out var code, node, 0) == CrSuccess && (status & DnHasProblem) != 0)
                problem = (int)code;

            string? name = null, deviceClass = null;
            WithDevice(instanceId, (IntPtr set, ref SpDevinfoData data) =>
            {
                name = Property(set, ref data, SpdrpFriendlyName) ?? Property(set, ref data, SpdrpDeviceDesc);
                deviceClass = Property(set, ref data, SpdrpClass);
                return ControlResult.Ok(string.Empty);
            });

            return new DeviceState { InstanceId = instanceId, Name = name ?? instanceId, Class = deviceClass, ProblemCode = problem };
        }

        internal static ControlResult Restart(string instanceId)
            => WithDevice(instanceId, (IntPtr set, ref SpDevinfoData data) =>
                Change(set, ref data, DicsPropChange, DicsFlagConfigSpecific, "Périphérique redémarré."));

        internal static ControlResult Enable(string instanceId)
            => WithDevice(instanceId, (IntPtr set, ref SpDevinfoData data) =>
            {
                // Comme devcon : l'activation globale d'abord, puis celle du profil matériel courant,
                // qui seule compte sur certaines machines ; la seconde peut échouer sans gravité.
                var global = Change(set, ref data, DicsEnable, DicsFlagGlobal, "Périphérique activé.");
                var specific = Change(set, ref data, DicsEnable, DicsFlagConfigSpecific, "Périphérique activé.");
                return global.Succeeded ? global : specific;
            });

        internal static ControlResult Reinstall(string instanceId)
        {
            var removed = WithDevice(instanceId, (IntPtr set, ref SpDevinfoData data) =>
            {
                if (!DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out var reboot))
                    return Failure("La désinstallation du périphérique a été refusée");
                return ControlResult.Ok("Périphérique désinstallé.", reboot);
            });
            if (!removed.Succeeded) return removed;

            // Aussitôt redétecté : Windows lui réinstalle son pilote, comme au premier branchement.
            var rescan = Rescan();
            return rescan.Succeeded
                ? ControlResult.Ok("Périphérique désinstallé puis redétecté : Windows lui a réinstallé son pilote.", removed.RestartRequired)
                : ControlResult.Ok("Périphérique désinstallé ; il reviendra au prochain redémarrage. " + rescan.Message, true);
        }

        private delegate ControlResult DeviceAction(IntPtr set, ref SpDevinfoData data);

        private static ControlResult WithDevice(string instanceId, DeviceAction action)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return ControlResult.Fail("Périphérique non désigné.");

            var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return Failure("Liste des périphériques inaccessible");
            try
            {
                var data = new SpDevinfoData { Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData)) };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
                    return ControlResult.Fail("Ce périphérique n'existe pas, ou plus, sur ce PC.", Marshal.GetLastWin32Error());
                return action(set, ref data);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        private static ControlResult Change(IntPtr set, ref SpDevinfoData data, uint state, uint scope, string success)
        {
            var parameters = new SpPropchangeParams
            {
                Header = new SpClassinstallHeader { Size = (uint)Marshal.SizeOf(typeof(SpClassinstallHeader)), InstallFunction = DifPropertyChange },
                StateChange = state,
                Scope = scope,
                HwProfile = 0,
            };

            if (!SetupDiSetClassInstallParamsW(set, ref data, ref parameters, Marshal.SizeOf(typeof(SpPropchangeParams))))
                return Failure("Windows a refusé le changement d'état");
            if (!SetupDiCallClassInstaller(DifPropertyChange, set, ref data))
                return Failure("Windows a refusé le changement d'état");

            var install = new SpDevinstallParams { Size = (uint)Marshal.SizeOf(typeof(SpDevinstallParams)) };
            var restart = SetupDiGetDeviceInstallParamsW(set, ref data, ref install)
                          && (install.Flags & (DiNeedRestart | DiNeedReboot)) != 0;
            return ControlResult.Ok(success, restart);
        }

        private static ControlResult Failure(string what)
        {
            var error = Marshal.GetLastWin32Error();
            var reason = error switch
            {
                ErrorAccessDenied => "privilèges administrateur nécessaires",
                ErrorInWow64 => "processus 32 bits sur un Windows 64 bits",
                _ => new Win32Exception(error).Message,
            };
            return ControlResult.Fail(what + " : " + reason + ".", error);
        }

        private static string? Property(IntPtr set, ref SpDevinfoData data, uint property)
        {
            var buffer = new byte[1024];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size)) return null;
            var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
            return text.Length == 0 ? null : text;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpClassinstallHeader
        {
            public uint Size;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpPropchangeParams
        {
            public SpClassinstallHeader Header;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SpDevinstallParams
        {
            public uint Size;
            public uint Flags;
            public uint FlagsEx;
            public IntPtr HwndParent;
            public IntPtr InstallMsgHandler;
            public IntPtr InstallMsgHandlerContext;
            public IntPtr FileQueue;
            public UIntPtr ClassInstallReserved;
            public uint Reserved;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DriverPath;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, uint flags, ref SpDevinfoData data);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SpDevinfoData data, ref SpPropchangeParams parameters, int size);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SpDevinfoData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SpDevinfoData data, ref SpDevinstallParams parameters);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr set, ref SpDevinfoData data, uint property, out uint type, byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("newdev.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiUninstallDevice(IntPtr hwndParent, IntPtr set, ref SpDevinfoData data, uint flags,
            [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    }
}
