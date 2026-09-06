using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les sorties graphiques actives et leur mode, par l'API d'affichage de Windows.
    /// </summary>
    /// <remarks>
    /// <c>EnumDisplayDevices</c> énumère les sorties, <c>EnumDisplaySettings</c> donne le mode de
    /// chacune. Les deux existent depuis Windows 95 et n'exigent aucun privilège : c'est le
    /// chemin le plus court et le plus sûr vers une information que WMI ne rend pas par sortie.
    /// </remarks>
    public sealed class DisplayNative : IDisplayApi
    {
        private const string Category = "Platform.Display";

        /// <summary>La sortie est rattachée au bureau : un écran y est branché et allumé.</summary>
        private const int AttachedToDesktop = 0x00000001;

        private const int PrimaryDevice = 0x00000004;

        /// <summary>Demande le chemin d'interface plutôt que le nom convivial : il porte l'identifiant.</summary>
        private const int GetDeviceInterfaceName = 0x00000001;

        private const int CurrentSettings = -1;

        private readonly IScopedLogger _log;

        public DisplayNative(ILdiLogger logger)
        {
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public Measured<IReadOnlyList<AttachedDisplay>> ReadAttached()
        {
            var found = new List<AttachedDisplay>();

            try
            {
                for (uint index = 0; ; index++)
                {
                    var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                    if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;

                    if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;

                    var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                    if (!EnumDisplaySettings(adapter.DeviceName, CurrentSettings, ref mode)) continue;

                    found.Add(new AttachedDisplay
                    {
                        Output = adapter.DeviceName,
                        MonitorId = ReadMonitorId(adapter.DeviceName),
                        IsPrimary = (adapter.StateFlags & PrimaryDevice) != 0,
                        Width = mode.PelsWidth,
                        Height = mode.PelsHeight,
                        RefreshHz = mode.DisplayFrequency,
                        MaxRefreshHz = MaxRefresh(adapter.DeviceName, mode.PelsWidth, mode.PelsHeight),
                    });
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException ||
                                       ex is SEHException)
            {
                _log.Warn("L'énumération des sorties graphiques a échoué.", ex);
                return Measured.Missing<IReadOnlyList<AttachedDisplay>>(
                    "Les sorties graphiques n'ont pas pu être énumérées.");
            }

            return found.Count == 0
                ? Measured.Missing<IReadOnlyList<AttachedDisplay>>(
                    "Aucune sortie graphique n'est rattachée au bureau.")
                : Measured.Ok<IReadOnlyList<AttachedDisplay>>(found, DataSource.NativeApi);
        }

        /// <summary>
        /// Identifiant de la dalle branchée sur une sortie, ramené à la forme du registre.
        /// </summary>
        /// <remarks>
        /// Windows le rend sous la forme d'un chemin d'interface :
        /// <c>\\?\DISPLAY#ACR052C#5&amp;22dd8a13&amp;0&amp;UID4352#{guid}</c> : là où le registre
        /// range la même dalle sous <c>DISPLAY\ACR052C\5&amp;22dd8a13&amp;0&amp;UID4352</c>. Les
        /// deux moitiés utiles sont les deux segments du milieu ; tout le reste est de la
        /// plomberie qui varie d'une version de Windows à l'autre.
        /// </remarks>
        private static string ReadMonitorId(string output)
        {
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(output, 0, ref monitor, GetDeviceInterfaceName)) return string.Empty;

            var id = monitor.DeviceID ?? string.Empty;
            var parts = id.Split('#');
            return parts.Length >= 3 ? parts[1] + "\\" + parts[2] : string.Empty;
        }

        /// <summary>
        /// Fréquence la plus élevée que le pilote propose à cette définition.
        /// </summary>
        /// <remarks>
        /// À définition constante, volontairement : comparer la fréquence courante au maximum
        /// absolu de la carte ferait sonner l'alerte sur tout écran qui n'est pas au plus petit
        /// mode possible. La question utile est « ce même affichage pourrait-il être plus
        /// fluide », pas « existe-t-il un mode plus rapide ailleurs ».
        /// </remarks>
        private static int MaxRefresh(string output, int width, int height)
        {
            var best = 0;

            for (var index = 0; ; index++)
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettings(output, index, ref mode)) break;

                if (mode.PelsWidth != width || mode.PelsHeight != height) continue;
                if (mode.DisplayFrequency > best) best = mode.DisplayFrequency;
            }

            return best;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(
            string? device, uint index, ref DisplayDevice info, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        /// <summary>
        /// <c>DEVMODE</c>, dont seuls les champs d'affichage nous intéressent.
        /// </summary>
        /// <remarks>
        /// La structure est déclarée en entier : sa taille est passée à l'API, et une structure
        /// tronquée ferait écrire Windows au-delà de ce qui est alloué.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TrueTypeOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }
    }
}
