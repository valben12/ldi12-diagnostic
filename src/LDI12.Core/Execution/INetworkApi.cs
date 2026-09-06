using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Connexion sans fil active d'une interface Wi-Fi.</summary>
    public sealed class WifiConnectionInfo
    {
        /// <summary>Identifiant de l'interface, tel que rapporté par NetworkInterface.Id.</summary>
        public string InterfaceId { get; init; } = string.Empty;

        public string InterfaceDescription { get; init; } = string.Empty;
        public string Ssid { get; init; } = string.Empty;
        public string Bssid { get; init; } = string.Empty;

        /// <summary>Qualité du signal telle que rapportée par Windows, de 0 à 100.</summary>
        public int SignalPercent { get; init; }

        public int RxRateMbps { get; init; }
        public int TxRateMbps { get; init; }
        public string Security { get; init; } = string.Empty;
        public string PhyType { get; init; } = string.Empty;

        /// <summary>État de l'interface, traduit : « Connectée », « Déconnectée »…</summary>
        public string State { get; init; } = string.Empty;

        /// <summary>Vrai quand l'interface est associée à un réseau.</summary>
        public bool Connected { get; init; }

        /// <summary>Radio allumée, ou coupée par un interrupteur logiciel ou matériel.</summary>
        public bool? RadioEnabled { get; init; }

        /// <summary>Numéro de canal, tel que la carte le rapporte.</summary>
        public int? Channel { get; init; }

        /// <summary>Fréquence centrale en kHz, disponible seulement via la liste des points d'accès.</summary>
        public int? FrequencyKhz { get; init; }

        /// <summary>Puissance reçue en dBm.</summary>
        public int? RssiDbm { get; init; }

        /// <summary>Points d'accès entendus par cette carte, y compris celui de la liaison.</summary>
        public IReadOnlyList<WifiAccessPointInfo> AccessPoints { get; init; } = new List<WifiAccessPointInfo>();

        /// <summary>
        /// Motif pour lequel la liste des points d'accès n'a pas pu être lue, ou <c>null</c>.
        /// </summary>
        public string? AccessPointsLimitation { get; init; }
    }

    /// <summary>Un point d'accès entendu par une carte sans fil.</summary>
    public sealed class WifiAccessPointInfo
    {
        public string Ssid { get; init; } = string.Empty;
        public string Bssid { get; init; } = string.Empty;
        public int RssiDbm { get; init; }
        public int FrequencyKhz { get; init; }
        public string PhyType { get; init; } = string.Empty;
    }

    /// <summary>Qui a installé une route dans la table, tel que la pile IP le note.</summary>
    public enum IpRouteProtocol
    {
        Other,

        /// <summary>Route déduite de l'adresse d'une carte par la pile elle-même.</summary>
        Local,

        /// <summary>Route venue de la configuration du réseau : la passerelle du bail DHCP.</summary>
        Configured,

        /// <summary>Route posée par un administrateur ou un programme d'installation.</summary>
        Static,

        /// <summary>Route imposée par un routeur au moyen d'une redirection ICMP.</summary>
        Redirect,
    }

    /// <summary>Une ligne brute de la table de routage IPv4.</summary>
    public sealed class IpRoute
    {
        public string Destination { get; init; } = string.Empty;
        public string Mask { get; init; } = string.Empty;
        public string NextHop { get; init; } = string.Empty;
        public int InterfaceIndex { get; init; }
        public int Metric { get; init; }
        public IpRouteProtocol Protocol { get; init; }
    }

    /// <summary>
    /// Lectures réseau qui n'ont pas d'équivalent dans les classes du framework.
    /// </summary>
    /// <remarks>
    /// L'état d'une liaison Wi-Fi (SSID, qualité du signal, débit négocié) n'est exposé que par
    /// <c>wlanapi.dll</c>. L'alternative, <c>netsh wlan show interfaces</c>, produit une sortie
    /// traduite dont l'analyse casserait au premier Windows dans une autre langue.
    /// </remarks>
    public interface INetworkApi
    {
        Measured<IReadOnlyList<WifiConnectionInfo>> ReadWifiConnections();

        /// <summary>
        /// La table de routage IPv4, que rien dans le framework n'expose.
        /// </summary>
        /// <remarks>
        /// C'est elle qui dit où part réellement le trafic, et elle seule : une carte peut porter
        /// la bonne adresse et la bonne passerelle, et voir son trafic emporté par une route
        /// qu'un tunnel a laissée derrière lui.
        /// </remarks>
        Measured<IReadOnlyList<IpRoute>> ReadRoutes();
    }
}
