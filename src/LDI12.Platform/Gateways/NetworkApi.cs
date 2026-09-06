using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    public sealed class NetworkApi : INetworkApi
    {
        private const string Category = "Platform.Network";

        private readonly IScopedLogger _log;

        public NetworkApi(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public Measured<IReadOnlyList<WifiConnectionInfo>> ReadWifiConnections()
        {
            // Le service WLAN n'existe pas sur une machine sans matériel sans fil : l'absence est
            // un constat normal, pas une erreur.
            var wlanApi = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wlanapi.dll");
            if (!File.Exists(wlanApi))
            {
                return Measured.Missing<IReadOnlyList<WifiConnectionInfo>>(
                    "Cette machine ne dispose d'aucune interface sans fil.", DataSource.NativeApi);
            }

            var handle = IntPtr.Zero;
            var interfaceList = IntPtr.Zero;
            try
            {
                var open = WlanNative.WlanOpenHandle(
                    WlanNative.ClientVersionVistaOrLater, IntPtr.Zero, out _, out handle);
                if (open != WlanNative.ERROR_SUCCESS)
                {
                    return Measured.Missing<IReadOnlyList<WifiConnectionInfo>>(
                        DescribeOpenFailure(open), DataSource.NativeApi);
                }

                if (WlanNative.WlanEnumInterfaces(handle, IntPtr.Zero, out interfaceList) != WlanNative.ERROR_SUCCESS)
                {
                    return Measured.Missing<IReadOnlyList<WifiConnectionInfo>>(
                        "Les interfaces sans fil n'ont pas pu être énumérées.", DataSource.NativeApi);
                }

                var connections = ReadInterfaces(handle, interfaceList);
                return Measured.Ok((IReadOnlyList<WifiConnectionInfo>)connections, DataSource.NativeApi);
            }
            catch (Exception ex) when (
                ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                _log.Debug("wlanapi indisponible : " + ex.GetType().Name + ".");
                return Measured.Missing<IReadOnlyList<WifiConnectionInfo>>(
                    "L'interface Wi-Fi de Windows n'est pas disponible sur cette machine.", DataSource.NativeApi);
            }
            finally
            {
                if (interfaceList != IntPtr.Zero) WlanNative.WlanFreeMemory(interfaceList);
                if (handle != IntPtr.Zero) WlanNative.WlanCloseHandle(handle, IntPtr.Zero);
            }
        }

        public Measured<IReadOnlyList<IpRoute>> ReadRoutes()
        {
            try
            {
                return Measured.Ok(RouteTableNative.Read(), DataSource.NativeApi);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException || ex is DllNotFoundException ||
                ex is EntryPointNotFoundException || ex is System.ComponentModel.Win32Exception)
            {
                _log.Debug("Table de routage illisible : " + ex.Message);
                return Measured.Missing<IReadOnlyList<IpRoute>>(
                    "La table de routage n'a pas pu être lue.", DataSource.NativeApi);
            }
        }

        /// <summary>
        /// Traduction des échecs d'ouverture du service WLAN.
        /// </summary>
        /// <remarks>
        /// Un numéro d'erreur affiché tel quel n'apprend rien au technicien et encore moins au
        /// client. Le cas courant (de loin) est celui du poste fixe dont le service sans fil
        /// est arrêté parce qu'il n'a jamais servi.
        /// </remarks>
        internal static string DescribeOpenFailure(uint code) => code switch
        {
            WlanNative.ERROR_SERVICE_NOT_ACTIVE =>
                "Le service « Configuration automatique des réseaux sans fil » de Windows n'est pas " +
                "démarré. C'est l'état normal d'un poste fixe sans matériel Wi-Fi.",
            WlanNative.ERROR_ACCESS_DENIED =>
                "Windows a refusé l'accès au service sans fil.",
            _ =>
                "Le service sans fil de Windows n'a pas répondu.",
        };

        private List<WifiConnectionInfo> ReadInterfaces(IntPtr handle, IntPtr interfaceList)
        {
            var connections = new List<WifiConnectionInfo>();

            // WLAN_INTERFACE_INFO_LIST : dwNumberOfItems, dwIndex, puis le tableau d'entrées.
            var count = Marshal.ReadInt32(interfaceList);
            var entrySize = Marshal.SizeOf(typeof(WlanNative.WLAN_INTERFACE_INFO));
            var firstEntry = IntPtr.Add(interfaceList, 8);

            for (var i = 0; i < count; i++)
            {
                var info = (WlanNative.WLAN_INTERFACE_INFO)Marshal.PtrToStructure(
                    IntPtr.Add(firstEntry, i * entrySize), typeof(WlanNative.WLAN_INTERFACE_INFO))!;

                // Une carte déconnectée est rapportée elle aussi : sur un portable, « pourquoi le
                // Wi-Fi ne marche plus » se joue justement sur les interfaces qui ne sont
                // associées à rien, radio coupée, profil perdu, pilote en défaut.
                var connection = ReadInterface(handle, info);
                if (connection != null) connections.Add(connection);
            }

            return connections;
        }

        private WifiConnectionInfo? ReadInterface(IntPtr handle, WlanNative.WLAN_INTERFACE_INFO info)
        {
            var connected = info.isState == WlanNative.WLAN_INTERFACE_STATE.Connected;

            // NetworkInterface.Id vaut le GUID entre accolades : c'est la clé de rapprochement
            // avec l'inventaire des cartes réseau.
            var identity = new WifiConnectionInfo
            {
                InterfaceId = info.InterfaceGuid.ToString("B").ToUpperInvariant(),
                InterfaceDescription = info.strInterfaceDescription ?? string.Empty,
                State = DescribeState(info.isState),
                Connected = connected,
                RadioEnabled = ReadRadioState(handle, info.InterfaceGuid),
            };

            if (!connected) return identity;

            var attributes = ReadConnection(handle, info.InterfaceGuid);
            if (attributes == null) return identity;

            var association = attributes.Value.wlanAssociationAttributes;
            var channel = ReadChannel(handle, info.InterfaceGuid);
            var bssid = WlanNative.FormatMac(association.dot11Bssid);
            var accessPoints = ReadAccessPoints(handle, info.InterfaceGuid, out var limitation);

            // La fréquence exacte ne vient que de la liste des points d'accès : c'est la seule
            // source qui lève l'ambiguïté entre 2,4 et 6 GHz, où la numérotation des canaux est
            // la même.
            int? frequency = null;
            foreach (var point in accessPoints)
            {
                if (string.Equals(point.Bssid, bssid, StringComparison.OrdinalIgnoreCase))
                {
                    frequency = point.FrequencyKhz;
                    break;
                }
            }

            return new WifiConnectionInfo
            {
                InterfaceId = identity.InterfaceId,
                InterfaceDescription = identity.InterfaceDescription,
                State = identity.State,
                Connected = true,
                RadioEnabled = identity.RadioEnabled,
                Ssid = WlanNative.ReadSsid(association.dot11Ssid),
                Bssid = bssid,
                SignalPercent = (int)Math.Min(association.wlanSignalQuality, 100u),

                // Les débits sont exprimés en kbit/s.
                RxRateMbps = (int)(association.ulRxRate / 1000),
                TxRateMbps = (int)(association.ulTxRate / 1000),
                Security = attributes.Value.wlanSecurityAttributes.bSecurityEnabled
                    ? WlanNative.DescribeAuth(attributes.Value.wlanSecurityAttributes.dot11AuthAlgorithm)
                    : "Réseau ouvert (non chiffré)",
                PhyType = WlanNative.DescribePhy(association.dot11PhyType),
                Channel = channel,
                FrequencyKhz = frequency,
                RssiDbm = ReadRssi(handle, info.InterfaceGuid),
                AccessPoints = accessPoints,
                AccessPointsLimitation = limitation,
            };
        }

        internal static string DescribeState(WlanNative.WLAN_INTERFACE_STATE state) => state switch
        {
            WlanNative.WLAN_INTERFACE_STATE.NotReady => "Non prête",
            WlanNative.WLAN_INTERFACE_STATE.Connected => "Connectée",
            WlanNative.WLAN_INTERFACE_STATE.AdHocNetworkFormed => "Réseau direct formé",
            WlanNative.WLAN_INTERFACE_STATE.Disconnecting => "Déconnexion en cours",
            WlanNative.WLAN_INTERFACE_STATE.Disconnected => "Déconnectée",
            WlanNative.WLAN_INTERFACE_STATE.Associating => "Association en cours",
            WlanNative.WLAN_INTERFACE_STATE.Discovering => "Recherche d'un réseau",
            WlanNative.WLAN_INTERFACE_STATE.Authenticating => "Authentification en cours",
            _ => "État inconnu",
        };

        private WlanNative.WLAN_CONNECTION_ATTRIBUTES? ReadConnection(IntPtr handle, Guid interfaceGuid)
        {
            var data = Query(handle, interfaceGuid, WlanNative.OpCodeCurrentConnection, out _);
            if (data == IntPtr.Zero) return null;

            try
            {
                return (WlanNative.WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(
                    data, typeof(WlanNative.WLAN_CONNECTION_ATTRIBUTES))!;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException)
            {
                _log.Debug("Lecture de la connexion sans fil impossible : " + ex.GetType().Name + ".");
                return null;
            }
            finally
            {
                WlanNative.WlanFreeMemory(data);
            }
        }

        private int? ReadChannel(IntPtr handle, Guid interfaceGuid)
        {
            var data = Query(handle, interfaceGuid, WlanNative.OpCodeChannelNumber, out var size);
            if (data == IntPtr.Zero || size < sizeof(uint)) return null;

            try
            {
                var channel = Marshal.ReadInt32(data);
                return channel > 0 ? channel : (int?)null;
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }

        private int? ReadRssi(IntPtr handle, Guid interfaceGuid)
        {
            var data = Query(handle, interfaceGuid, WlanNative.OpCodeRssi, out var size);
            if (data == IntPtr.Zero || size < sizeof(int)) return null;

            try
            {
                var rssi = Marshal.ReadInt32(data);

                // Une puissance reçue est négative et vaut au mieux −20 dBm collé au point
                // d'accès. Zéro ou un positif signifie que la carte n'a rien renseigné.
                return rssi < 0 && rssi > -120 ? rssi : (int?)null;
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }

        /// <summary>
        /// Radio allumée ? Vrai seulement si aucun des deux commutateurs ne la coupe.
        /// </summary>
        private bool? ReadRadioState(IntPtr handle, Guid interfaceGuid)
        {
            var data = Query(handle, interfaceGuid, WlanNative.OpCodeRadioState, out var size);
            if (data == IntPtr.Zero || size < sizeof(uint)) return null;

            try
            {
                var phyCount = Marshal.ReadInt32(data);
                if (phyCount <= 0) return null;

                var entrySize = Marshal.SizeOf(typeof(WlanNative.WLAN_PHY_RADIO_STATE));
                var enabled = false;

                for (var i = 0; i < phyCount; i++)
                {
                    var offset = IntPtr.Add(data, 4 + i * entrySize);
                    var state = (WlanNative.WLAN_PHY_RADIO_STATE)Marshal.PtrToStructure(
                        offset, typeof(WlanNative.WLAN_PHY_RADIO_STATE))!;

                    // 1 : allumée, 2 : éteinte. Un seul interrupteur fermé suffit à couper.
                    if (state.dot11SoftwareRadioState == 2 || state.dot11HardwareRadioState == 2) return false;
                    if (state.dot11SoftwareRadioState == 1 && state.dot11HardwareRadioState == 1) enabled = true;
                }

                return enabled ? true : (bool?)null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException)
            {
                return null;
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }

        private IntPtr Query(IntPtr handle, Guid interfaceGuid, uint opCode, out uint size)
        {
            size = 0;
            try
            {
                var result = WlanNative.WlanQueryInterface(
                    handle, ref interfaceGuid, opCode, IntPtr.Zero, out size, out var data, IntPtr.Zero);

                if (result == WlanNative.ERROR_SUCCESS) return data;

                if (data != IntPtr.Zero) WlanNative.WlanFreeMemory(data);
                return IntPtr.Zero;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is ArgumentException)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Points d'accès déjà connus de la carte.
        /// </summary>
        /// <remarks>
        /// Aucun balayage n'est déclenché : <c>WlanGetNetworkBssList</c> rend ce que la carte a
        /// entendu, la liste étant rafraîchie par Windows de lui-même. Depuis Windows 10, cette
        /// liste est soumise à l'autorisation de localisation : les identifiants de points
        /// d'accès permettent de situer une machine sans GPS. Un refus est donc un état normal,
        /// dit comme tel, et le reste du relevé sans fil continue de fonctionner.
        /// </remarks>
        private IReadOnlyList<WifiAccessPointInfo> ReadAccessPoints(
            IntPtr handle, Guid interfaceGuid, out string? limitation)
        {
            limitation = null;
            var list = IntPtr.Zero;

            try
            {
                var result = WlanNative.WlanGetNetworkBssList(
                    handle, ref interfaceGuid, IntPtr.Zero, WlanNative.BssTypeInfrastructure,
                    false, IntPtr.Zero, out list);

                if (result != WlanNative.ERROR_SUCCESS)
                {
                    limitation = DescribeBssFailure(result);
                    return Array.Empty<WifiAccessPointInfo>();
                }

                if (list == IntPtr.Zero) return Array.Empty<WifiAccessPointInfo>();

                // WLAN_BSS_LIST : dwTotalSize, dwNumberOfItems, puis les entrées.
                var count = Marshal.ReadInt32(list, 4);
                var entrySize = Marshal.SizeOf(typeof(WlanNative.WLAN_BSS_ENTRY));
                var points = new List<WifiAccessPointInfo>();

                for (var i = 0; i < count; i++)
                {
                    var entry = (WlanNative.WLAN_BSS_ENTRY)Marshal.PtrToStructure(
                        IntPtr.Add(list, 8 + i * entrySize), typeof(WlanNative.WLAN_BSS_ENTRY))!;

                    points.Add(new WifiAccessPointInfo
                    {
                        Ssid = WlanNative.ReadSsid(entry.dot11Ssid),
                        Bssid = WlanNative.FormatMac(entry.dot11Bssid),
                        RssiDbm = entry.lRssi,
                        FrequencyKhz = (int)entry.ulChCenterFrequency,
                        PhyType = WlanNative.DescribePhy(entry.dot11BssPhyType),
                    });
                }

                return points;
            }
            catch (Exception ex) when (
                ex is EntryPointNotFoundException || ex is ArgumentException || ex is OutOfMemoryException)
            {
                _log.Debug("Liste des points d'accès illisible : " + ex.GetType().Name + ".");
                limitation = "La liste des réseaux voisins n'a pas pu être lue.";
                return Array.Empty<WifiAccessPointInfo>();
            }
            finally
            {
                if (list != IntPtr.Zero) WlanNative.WlanFreeMemory(list);
            }
        }

        internal static string DescribeBssFailure(uint code) => code switch
        {
            WlanNative.ERROR_ACCESS_DENIED =>
                "Windows réserve la liste des réseaux voisins aux applications autorisées à " +
                "accéder à la localisation : les points d'accès environnants permettent de situer " +
                "une machine. Le paramètre se trouve dans Confidentialité et sécurité, Localisation.",
            WlanNative.ERROR_NDIS_DOT11_POWER_STATE_INVALID =>
                "La radio de la carte est éteinte : elle n'entend aucun réseau.",
            WlanNative.ERROR_INVALID_STATE =>
                "La carte sans fil n'était pas dans un état permettant de lister les réseaux.",
            _ =>
                "La liste des réseaux voisins n'a pas été fournie par la carte.",
        };
    }
}
