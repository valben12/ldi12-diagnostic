using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// API WLAN native (wlanapi.dll), disponible depuis Windows Vista.
    /// </summary>
    /// <remarks>
    /// Toutes les structures sont marshalées explicitement plutôt que reconstruites à la main :
    /// les décalages diffèrent entre 32 et 64 bits, et l'application se compile en AnyCPU.
    /// </remarks>
    internal static class WlanNative
    {
        internal const uint ERROR_SUCCESS = 0;
        internal const uint ClientVersionVistaOrLater = 2;

        /// <summary>wlan_intf_opcode_current_connection</summary>
        internal const uint OpCodeCurrentConnection = 7;

        /// <summary>wlan_intf_opcode_radio_state, commutateur logiciel et matériel de la radio.</summary>
        internal const uint OpCodeRadioState = 4;

        /// <summary>wlan_intf_opcode_channel_number</summary>
        internal const uint OpCodeChannelNumber = 8;

        /// <summary>
        /// wlan_intf_opcode_rssi. La puissance reçue en dBm, là où <c>wlanSignalQuality</c> n'est
        /// qu'un pourcentage recalculé par Windows.
        /// </summary>
        /// <remarks>
        /// Les codes de la famille « msm » commencent à 0x10000100, d'où cette valeur qui n'a rien
        /// d'arbitraire : statistics vaut 0x10000101 et rssi le suit.
        /// </remarks>
        internal const uint OpCodeRssi = 0x10000102;

        /// <summary>dot11_BSS_type_infrastructure : les points d'accès, pas les liaisons directes.</summary>
        internal const uint BssTypeInfrastructure = 1;

        internal const uint ERROR_ACCESS_DENIED = 5;
        internal const uint ERROR_SERVICE_NOT_ACTIVE = 1062;
        internal const uint ERROR_NDIS_DOT11_POWER_STATE_INVALID = 0x80342000;
        internal const uint ERROR_INVALID_STATE = 5023;

        internal enum WLAN_INTERFACE_STATE
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11PhyType;
            public uint uDot11PhyIndex;

            /// <summary>Qualité du signal de 0 à 100, déjà normalisée par Windows.</summary>
            public uint wlanSignalQuality;

            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanOpenHandle(
            uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanEnumInterfaces(
            IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

        /// <summary>État des deux commutateurs d'une radio : 0 inconnu, 1 allumée, 2 éteinte.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_PHY_RADIO_STATE
        {
            public uint dwPhyIndex;
            public uint dot11SoftwareRadioState;
            public uint dot11HardwareRadioState;
        }

        /// <summary>
        /// Un point d'accès entendu par la carte.
        /// </summary>
        /// <remarks>
        /// Structure large et sensible à l'alignement : le champ <c>bInRegDomain</c> est un octet
        /// suivi d'un remplissage, et les horodatages 64 bits imposent un alignement sur 8. Elle
        /// est donc marshalée telle quelle plutôt que relue champ par champ, et un test vérifie
        /// que sa taille vaut bien 360 octets : c'est le seul garde-fou possible sans matériel
        /// sans fil sous la main.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_BSS_ENTRY
        {
            public DOT11_SSID dot11Ssid;
            public uint uPhyId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11BssType;
            public uint dot11BssPhyType;

            /// <summary>Puissance reçue en dBm, valeur négative.</summary>
            public int lRssi;

            public uint uLinkQuality;
            [MarshalAs(UnmanagedType.U1)] public bool bInRegDomain;
            public ushort usBeaconPeriod;
            public ulong ullTimestamp;
            public ulong ullHostTimestamp;
            public ushort usCapabilityInformation;

            /// <summary>Fréquence centrale du canal, en kHz.</summary>
            public uint ulChCenterFrequency;

            public uint uRateSetLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
            public ushort[] usRateSet;
            public uint ulIeOffset;
            public uint ulIeSize;
        }

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanGetNetworkBssList(
            IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, uint dot11BssType,
            [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanQueryInterface(
            IntPtr clientHandle, ref Guid interfaceGuid, uint opCode, IntPtr reserved,
            out uint dataSize, out IntPtr data, IntPtr valueType);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr memory);

        /// <summary>
        /// Le SSID est une suite d'octets, pas une chaîne : il peut contenir n'importe quoi.
        /// On l'interprète en UTF-8, ce qui couvre l'ASCII et les box en français.
        /// </summary>
        internal static string ReadSsid(DOT11_SSID ssid)
        {
            if (ssid.ucSSID == null || ssid.uSSIDLength == 0) return string.Empty;
            var length = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
            return System.Text.Encoding.UTF8.GetString(ssid.ucSSID, 0, length);
        }

        internal static string FormatMac(byte[]? address)
        {
            if (address == null || address.Length == 0) return string.Empty;
            var parts = new string[address.Length];
            for (var i = 0; i < address.Length; i++) parts[i] = address[i].ToString("X2");
            return string.Join(":", parts);
        }

        internal static string DescribeAuth(uint algorithm) => algorithm switch
        {
            1 => "Ouvert (non sécurisé)",
            2 => "Clé partagée",
            3 => "WPA",
            4 => "WPA-PSK",
            5 => "WPA (aucune)",
            6 => "WPA2",
            7 => "WPA2-PSK",
            8 => "WPA3",
            9 => "WPA3-SAE",
            10 => "OWE",
            11 => "WPA3-Entreprise",
            _ => "Authentification " + algorithm,
        };

        internal static string DescribePhy(uint phyType) => phyType switch
        {
            1 => "FHSS",
            2 => "DSSS",
            4 => "802.11b",
            5 => "802.11a (OFDM)",
            6 => "802.11g",
            7 => "802.11n",
            8 => "802.11ac",
            9 => "802.11ad",
            10 => "802.11ax",
            11 => "802.11be",
            _ => "Type physique " + phyType,
        };
    }
}
