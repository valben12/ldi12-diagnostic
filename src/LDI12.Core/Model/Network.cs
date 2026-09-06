using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public enum NetworkAdapterKind
    {
        Unknown = 0,
        Ethernet = 1,
        WiFi = 2,
        Loopback = 3,
        Virtual = 4,
        Tunnel = 5,
        Bluetooth = 6,
        Mobile = 7,

        /// <summary>
        /// Interface d'un client VPN.
        /// </summary>
        /// <remarks>
        /// Windows ne la distingue pas : elle se présente en « tunnel », en « Ethernet » ou en
        /// type inconnu selon le client installé. La reconnaître change pourtant la lecture de
        /// tout le reste : une passerelle par défaut portée par un VPN, des serveurs DNS
        /// inattendus et une latence multipliée par trois ne sont pas des pannes quand on sait
        /// qu'un tunnel est monté.
        /// </remarks>
        Vpn = 8,
    }

    /// <summary>Bande de fréquences d'une liaison sans fil.</summary>
    public enum WifiBand
    {
        Unknown = 0,
        Band24 = 1,
        Band5 = 2,
        Band6 = 3,
    }

    /// <summary>Un point d'accès entendu par la carte, autre que celui auquel elle est associée.</summary>
    public sealed class WifiNeighbour
    {
        public string Ssid { get; init; } = string.Empty;
        public string Bssid { get; init; } = string.Empty;
        public int Channel { get; init; }
        public WifiBand Band { get; init; }

        /// <summary>Puissance reçue en dBm : négative, et d'autant plus proche de zéro qu'elle est forte.</summary>
        public int RssiDbm { get; init; }
    }

    /// <summary>
    /// Ce que la carte entend autour d'elle.
    /// </summary>
    /// <remarks>
    /// La question que le client pose sans la formuler ainsi : « pourquoi ça rame le soir ? ».
    /// En immeuble, une vingtaine de box se partagent trois canaux réellement indépendants en
    /// 2,4 GHz ; le signal reste excellent et le débit s'effondre quand même. Aucun autre relevé
    /// de ce logiciel n'explique ce cas.
    ///
    /// Le relevé est <b>passif</b> : la liste des points d'accès déjà connus de la carte est
    /// lue telle quelle, sans déclencher de balayage : un balayage interromprait brièvement la
    /// liaison de la machine qu'on est venu réparer.
    /// </remarks>
    public sealed class WifiNeighbourhood
    {
        /// <summary>Points d'accès entendus, celui de la liaison compris.</summary>
        public Measured<int> Total { get; init; }

        /// <summary>Autres points d'accès sur le canal exact de la liaison.</summary>
        public Measured<int> SameChannel { get; init; }

        /// <summary>Autres points d'accès dont le canal recouvre celui de la liaison.</summary>
        public Measured<int> OverlappingChannel { get; init; }

        /// <summary>Les plus forts d'entre eux, pour que le technicien voie de quoi il s'agit.</summary>
        public IReadOnlyList<WifiNeighbour> Strongest { get; init; } = Array.Empty<WifiNeighbour>();
    }

    public sealed class WifiInfo
    {
        public Measured<string> Ssid { get; init; }
        public Measured<string> Bssid { get; init; }
        public Measured<string> Security { get; init; }

        /// <summary>Bande de fréquences : 2,4 GHz, 5 GHz ou 6 GHz.</summary>
        public Measured<WifiBand> Band { get; init; }

        /// <summary>Norme de la liaison : 802.11n, 802.11ac, 802.11ax…</summary>
        public Measured<string> RadioType { get; init; }

        public Measured<int> Channel { get; init; }

        /// <summary>Qualité du signal telle que rapportée par Windows (0 à 100).</summary>
        public Measured<int> SignalPercent { get; init; }

        /// <summary>
        /// Puissance reçue en dBm, telle que la carte la mesure.
        /// </summary>
        /// <remarks>
        /// Le pourcentage de Windows est un recalcul de cette valeur, et il écrase les nuances :
        /// −67 dBm (correct pour de la vidéo) et −75 dBm (limite) tombent souvent dans la même
        /// tranche de pourcentage. C'est en dBm que se lisent les repères du métier.
        /// </remarks>
        public Measured<int> RssiDbm { get; init; }

        public Measured<int> TxRateMbps { get; init; }
        public Measured<int> RxRateMbps { get; init; }

        /// <summary>État de la liaison : associée, déconnectée, en cours d'association…</summary>
        public Measured<string> State { get; init; }

        /// <summary>
        /// Radio allumée. Fausse quand un interrupteur physique ou le mode avion la coupe,
        /// première cause d'un « le Wi-Fi a disparu » sur un portable.
        /// </summary>
        public Measured<bool> RadioEnabled { get; init; }

        public WifiNeighbourhood? Neighbourhood { get; init; }
    }

    public sealed class NetworkAdapterInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public NetworkAdapterKind Kind { get; init; }
        public Measured<string> MacAddress { get; init; }
        public Measured<string> Status { get; init; }
        public Measured<long> LinkSpeedBps { get; init; }
        public Measured<bool> DhcpEnabled { get; init; }
        public Measured<string> DhcpServer { get; init; }

        public IReadOnlyList<string> IPv4Addresses { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> IPv6Addresses { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Adresse d'auto-configuration 169.254.x.x : la carte fonctionne mais n'a jamais obtenu
        /// de bail DHCP. Symptôme direct d'un « je n'ai plus Internet ».
        /// </summary>
        public Measured<bool> HasApipaAddress { get; init; }

        /// <summary>Compteurs de trames et d'erreurs de la liaison, cumulés depuis le démarrage.</summary>
        public LinkCounters Counters { get; init; } = LinkCounters.None;

        /// <summary>Renseigné uniquement pour les interfaces sans fil.</summary>
        public WifiInfo? Wifi { get; init; }

        /// <summary>Interface portant la route par défaut : celle qui compte pour le diagnostic.</summary>
        public bool IsPrimary { get; init; }

        /// <summary>
        /// Liaison établie.
        /// </summary>
        /// <remarks>
        /// À côté de <see cref="Status"/>, qui porte un libellé français destiné à l'écran. Une
        /// règle qui déciderait en comparant ce libellé à « Connectée » dépendrait de la langue
        /// du logiciel : ce booléen existe pour qu'aucune ne le fasse.
        /// </remarks>
        public bool IsUp { get; init; }
    }

    public sealed class PingResult
    {
        public string Target { get; init; } = string.Empty;
        public string? Label { get; init; }
        public Measured<int> Sent { get; init; }
        public Measured<int> Received { get; init; }
        public Measured<double> LossPercent { get; init; }
        public Measured<double> MinMs { get; init; }
        public Measured<double> AverageMs { get; init; }
        public Measured<double> MaxMs { get; init; }
        public Measured<bool> Reachable { get; init; }
    }

    public sealed class DnsResolutionResult
    {
        public string Host { get; init; } = string.Empty;
        public Measured<bool> Resolved { get; init; }
        public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();
        public Measured<long> DurationMs { get; init; }
    }

    public sealed class HttpCheckResult
    {
        public string Url { get; init; } = string.Empty;
        public Measured<int> StatusCode { get; init; }
        public Measured<long> DurationMs { get; init; }
        public Measured<bool> Succeeded { get; init; }
    }

    /// <summary>
    /// Tests actifs. Ce sont les <b>seules</b> sorties réseau du logiciel, et elles sont listées
    /// à l'écran avant exécution : un technicien doit pouvoir dire à son client exactement ce que
    /// l'outil contacte.
    /// </summary>
    /// <summary>Une étape du chemin réseau, telle qu'un paquet à durée de vie limitée la révèle.</summary>
    public sealed class RouteHop
    {
        /// <summary>Rang de l'étape, de 1 jusqu'à la cible.</summary>
        public int Distance { get; init; }

        public Measured<string> Address { get; init; }

        public Measured<double> RoundTripMs { get; init; }

        /// <summary>
        /// L'étape n'a pas répondu.
        /// </summary>
        /// <remarks>
        /// Fréquent et rarement significatif : beaucoup d'équipements sont configurés pour ne pas
        /// répondre aux paquets expirés. Une étape muette au milieu d'un chemin qui aboutit n'est
        /// pas une panne, et le dire évite de faire changer une box qui fonctionne.
        /// </remarks>
        public bool Silent { get; init; }

        /// <summary>Étape appartenant au réseau local, première frontière visible du diagnostic.</summary>
        public bool IsLocal { get; init; }
    }

    /// <summary>
    /// Qualité du chemin réseau : pertes, régularité, longueur du trajet, taille de paquet.
    /// </summary>
    /// <remarks>
    /// Trois mesures que le test de connectivité simple ne donne pas, et qui expliquent les
    /// pannes les plus difficiles à faire admettre : une ligne qui « marche » mais perd un
    /// paquet sur dix, une latence en dents de scie, ou une MTU réduite par un tunnel : ce
    /// dernier cas donnant des pages web qui se chargent à moitié sans aucune erreur visible.
    /// </remarks>
    public sealed class PathQuality
    {
        public string Target { get; init; } = string.Empty;

        public Measured<int> Sent { get; init; }
        public Measured<int> Received { get; init; }
        public Measured<double> LossPercent { get; init; }
        public Measured<double> AverageMs { get; init; }

        /// <summary>Écart entre les temps de réponse successifs : la gigue.</summary>
        public Measured<double> JitterMs { get; init; }

        public Measured<double> MinMs { get; init; }
        public Measured<double> MaxMs { get; init; }
    }

    public sealed class NetworkTests
    {
        public PingResult? Gateway { get; init; }
        public PingResult? Internet { get; init; }
        public IReadOnlyList<DnsResolutionResult> DnsResolutions { get; init; } = Array.Empty<DnsResolutionResult>();
        public IReadOnlyList<HttpCheckResult> HttpChecks { get; init; } = Array.Empty<HttpCheckResult>();

        /// <summary>Chemin jusqu'à Internet, étape par étape. Vide en analyse rapide.</summary>
        public IReadOnlyList<RouteHop> Route { get; init; } = Array.Empty<RouteHop>();

        public PathQuality? Quality { get; init; }

        /// <summary>Plus grand paquet transmis sans fragmentation, en octets.</summary>
        public Measured<int> PathMtu { get; init; }
    }

    public sealed class NetworkSnapshot
    {
        public IReadOnlyList<NetworkAdapterInfo> Adapters { get; init; } = Array.Empty<NetworkAdapterInfo>();
        public NetworkTests Tests { get; init; } = new NetworkTests();

        /// <summary>Ce qui commande le comportement du réseau, par-dessus ce qui est branché.</summary>
        public NetworkEnvironment Environment { get; init; } = new NetworkEnvironment();

        /// <summary>Ce que la machine écoute, et par quel programme.</summary>
        public ListeningPortsInfo Listening { get; init; } = new ListeningPortsInfo();

        /// <summary>Ce qui décide du chemin d'un paquet, par-dessus la carte qui l'émet.</summary>
        public NetworkPathsInfo Paths { get; init; } = new NetworkPathsInfo();

        /// <summary>
        /// Cartes portant une passerelle par défaut.
        /// </summary>
        /// <remarks>
        /// Deux passerelles actives en même temps est l'une des pannes d'entreprise les plus
        /// coûteuses à trouver : Windows choisit par métrique, le choix change au redémarrage, et
        /// la moitié des postes sortent par la mauvaise porte un jour sur deux. Le compte se
        /// déduit des cartes déjà relevées : aucune mesure de plus.
        /// </remarks>
        public int DefaultGatewayCount
        {
            get
            {
                var count = 0;
                foreach (var adapter in Adapters)
                {
                    if (adapter.Kind == NetworkAdapterKind.Loopback) continue;
                    if (!adapter.IsUp) continue;
                    if (adapter.Gateways.Count > 0) count++;
                }
                return count;
            }
        }
    }
}
