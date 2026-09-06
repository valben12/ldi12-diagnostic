using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Qui a posé une route dans la table.</summary>
    /// <remarks>
    /// La distinction qui compte pour un dépannage : une route posée par Windows ou par le bail
    /// DHCP se refait toute seule au prochain démarrage, une route posée à la main survit à tout
    /// et n'est visible nulle part dans les fenêtres de Windows. C'est celle-là qui explique
    /// qu'un poste ne joigne plus un serveur que tous ses voisins joignent.
    /// </remarks>
    public enum RouteOrigin
    {
        Unknown,

        /// <summary>Route que Windows pose lui-même à partir de l'adresse d'une carte.</summary>
        System,

        /// <summary>Route venue de la configuration du réseau, la passerelle du bail DHCP.</summary>
        Configured,

        /// <summary>Route ajoutée à la main, ou par un programme d'installation.</summary>
        Manual,

        /// <summary>Route imposée par un routeur au moyen d'une redirection ICMP.</summary>
        Redirect,
    }

    /// <summary>Une ligne de la table de routage, telle que la pile IP la garde.</summary>
    public sealed class RouteEntry
    {
        public string Destination { get; init; } = string.Empty;

        public string Mask { get; init; } = string.Empty;

        /// <summary>Longueur du préfixe, déduite du masque : « /24 » pour 255.255.255.0.</summary>
        public int PrefixLength { get; init; }

        /// <summary>Prochain routeur, ou « 0.0.0.0 » quand la destination est directement branchée.</summary>
        public string NextHop { get; init; } = string.Empty;

        public int InterfaceIndex { get; init; }

        /// <summary>Nom de la carte, quand l'index a pu être rapproché d'une carte relevée.</summary>
        public string? InterfaceName { get; init; }

        public int Metric { get; init; }

        public RouteOrigin Origin { get; init; }

        /// <summary>La route de sortie : celle qui emporte tout ce qui n'est pas local.</summary>
        public bool IsDefault => PrefixLength == 0 && Destination == "0.0.0.0";

        public string Prefix => Destination + "/" + PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>La table de routage IPv4, et ce qu'on en tire sans mesure de plus.</summary>
    /// <remarks>
    /// IPv4 seulement, et c'est délibéré : la table IPv6 se lit par une autre fonction dont les
    /// structures portent des unions d'adresses, et aucune des pannes que ce logiciel cherche à
    /// expliquer ne s'y joue aujourd'hui. Mieux vaut une mesure dont on connaît la portée qu'une
    /// mesure large et fausse par endroits.
    /// </remarks>
    public sealed class RoutingTable
    {
        public IReadOnlyList<RouteEntry> Routes { get; init; } = Array.Empty<RouteEntry>();

        public Measured<int> Count { get; init; } = Measured.NotCollected<int>();

        /// <summary>Les routes de sortie. Plus d'une, et le trafic part au hasard des métriques.</summary>
        public IReadOnlyList<RouteEntry> DefaultRoutes
        {
            get
            {
                var found = new List<RouteEntry>();
                foreach (var route in Routes) if (route.IsDefault) found.Add(route);
                return found;
            }
        }

        /// <summary>Les routes que quelqu'un a posées, et que rien ne retirera tout seul.</summary>
        public IReadOnlyList<RouteEntry> Manual
        {
            get
            {
                var found = new List<RouteEntry>();
                foreach (var route in Routes) if (route.Origin == RouteOrigin.Manual) found.Add(route);
                return found;
            }
        }
    }

    /// <summary>Ce que fait une ligne du fichier hosts.</summary>
    public enum HostsTarget
    {
        /// <summary>Renvoie vers le néant : le nom devient injoignable. C'est un blocage.</summary>
        Blocked,

        /// <summary>Renvoie vers la machine elle-même, blocage également, ou service local.</summary>
        Loopback,

        /// <summary>Renvoie ailleurs : le nom est détourné vers une autre machine.</summary>
        Redirected,
    }

    /// <summary>Une redirection posée dans le fichier hosts.</summary>
    public sealed class HostsEntry
    {
        public int Line { get; init; }

        public string Address { get; init; } = string.Empty;

        public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

        public HostsTarget Target { get; init; }

        /// <summary>La ligne telle qu'elle est écrite, pour la montrer avant de la retirer.</summary>
        public string Raw { get; init; } = string.Empty;
    }

    /// <summary>
    /// Le fichier hosts : la seule chose qui passe avant les serveurs DNS.
    /// </summary>
    /// <remarks>
    /// <b>Le premier endroit à regarder quand un seul site ne répond pas.</b> Une ligne dans ce
    /// fichier l'emporte sur toute la configuration réseau, ne se voit dans aucune fenêtre de
    /// Windows, et survit à une réinitialisation de la pile réseau comme à un changement de box.
    /// Les logiciels piratés y renvoient les serveurs de leur éditeur ; les publiciels y renvoient
    /// les mises à jour de Windows et les sites des antivirus.
    /// </remarks>
    public sealed class HostsFile
    {
        public string? Path { get; init; }

        /// <summary>Nombre de lignes actives, les commentaires ne comptent pas.</summary>
        public Measured<int> ActiveCount { get; init; } = Measured.NotCollected<int>();

        public IReadOnlyList<HostsEntry> Entries { get; init; } = Array.Empty<HostsEntry>();

        /// <summary>Taille du fichier en lignes, commentaires compris.</summary>
        public int LineCount { get; init; }

        /// <summary>
        /// Domaines dont le détournement casse une fonction que le client attribuera à autre chose.
        /// </summary>
        /// <remarks>
        /// La liste ne prétend pas être exhaustive et ne cherche pas de menace : elle nomme les
        /// domaines dont un blocage produit un symptôme trompeur (« Windows ne se met plus à
        /// jour », « l'antivirus ne se met plus à jour ») que personne ne relie spontanément à un
        /// fichier texte de quatre lignes.
        /// </remarks>
        public static bool IsConsequential(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var lower = name.ToLowerInvariant();

            foreach (var marker in Consequential)
                if (lower == marker || lower.EndsWith("." + marker, StringComparison.Ordinal)) return true;

            return false;
        }

        private static readonly string[] Consequential =
        {
            "microsoft.com", "windowsupdate.com", "windowsupdate.microsoft.com", "update.microsoft.com",
            "msftncsi.com", "msftconnecttest.com", "live.com", "office.com",
            "avast.com", "avg.com", "bitdefender.com", "eset.com", "kaspersky.com", "mcafee.com",
            "norton.com", "symantec.com", "malwarebytes.com", "sophos.com", "trendmicro.com",
            "adobe.com", "autodesk.com",
        };
    }

    /// <summary>Un fournisseur du catalogue Winsock, une bibliothèque insérée dans la pile réseau.</summary>
    public sealed class WinsockProvider
    {
        public int Index { get; init; }

        public string LibraryPath { get; init; } = string.Empty;

        /// <summary>Bibliothèque livrée avec Windows.</summary>
        public bool FromWindows { get; init; }

        /// <summary>Nom du fichier seul, pour l'afficher sans le chemin.</summary>
        public string FileName
        {
            get
            {
                var slash = LibraryPath.LastIndexOf('\\');
                return slash >= 0 && slash + 1 < LibraryPath.Length ? LibraryPath.Substring(slash + 1) : LibraryPath;
            }
        }
    }

    /// <summary>
    /// Le catalogue Winsock : la file d'attente par laquelle passe tout le trafic de la machine.
    /// </summary>
    /// <remarks>
    /// <b>La panne où plus rien ne marche et où tout est correct.</b> Un programme peut s'insérer
    /// dans cette file pour voir passer le trafic, filtrage parental, ancien pare-feu,
    /// accélérateur douteux. S'il est désinstallé sans retirer son entrée, la pile appelle une
    /// bibliothèque absente et le réseau s'arrête net : adresse correcte, passerelle joignable au
    /// ping, et aucun navigateur qui fonctionne. C'est exactement ce que
    /// <c>netsh winsock reset</c> répare, et personne ne sait pourquoi.
    /// </remarks>
    public sealed class WinsockCatalog
    {
        public IReadOnlyList<WinsockProvider> Providers { get; init; } = Array.Empty<WinsockProvider>();

        public Measured<int> Count { get; init; } = Measured.NotCollected<int>();

        /// <summary>Les fournisseurs qui ne viennent pas de Windows.</summary>
        public IReadOnlyList<WinsockProvider> Foreign
        {
            get
            {
                var found = new List<WinsockProvider>();
                foreach (var provider in Providers) if (!provider.FromWindows) found.Add(provider);
                return found;
            }
        }
    }

    /// <summary>Ce qu'est un serveur DNS pour la machine qui l'utilise.</summary>
    public enum DnsServerNature
    {
        Unknown,

        /// <summary>La box ou le routeur : le cas ordinaire chez un particulier.</summary>
        Gateway,

        /// <summary>La machine elle-même, un filtre local, ou un contrôleur de domaine.</summary>
        LocalMachine,

        /// <summary>Une autre machine du même réseau : serveur d'entreprise, filtre maison.</summary>
        LocalNetwork,

        /// <summary>Un résolveur public identifié : Cloudflare, Google, Quad9, FDN…</summary>
        KnownPublic,
    }

    /// <summary>Un serveur DNS configuré, et ce qu'il est.</summary>
    public sealed class DnsServerUse
    {
        public string Address { get; init; } = string.Empty;

        public string? InterfaceName { get; init; }

        public DnsServerNature Nature { get; init; }

        /// <summary>Nom du service public, quand il est reconnu.</summary>
        public string? Operator { get; init; }
    }

    /// <summary>Résolveurs publics assez répandus pour être reconnus plutôt que signalés.</summary>
    /// <remarks>
    /// Sans cette liste, la règle sur les serveurs DNS se déclencherait sur la moitié des
    /// machines de l'atelier : mettre 1.1.1.1 ou 8.8.8.8 est le premier geste de tout technicien
    /// devant une box dont le résolveur rame. Ce n'est pas un défaut, et un logiciel qui le
    /// signale apprend au technicien à ne plus le lire.
    /// </remarks>
    public static class PublicResolvers
    {
        private static readonly Dictionary<string, string> Known =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "1.1.1.1", "Cloudflare" }, { "1.0.0.1", "Cloudflare" },
                { "1.1.1.2", "Cloudflare (filtré)" }, { "1.1.1.3", "Cloudflare (familles)" },
                { "8.8.8.8", "Google" }, { "8.8.4.4", "Google" },
                { "9.9.9.9", "Quad9" }, { "149.112.112.112", "Quad9" },
                { "208.67.222.222", "OpenDNS" }, { "208.67.220.220", "OpenDNS" },
                { "94.140.14.14", "AdGuard" }, { "94.140.15.15", "AdGuard" },
                { "80.67.169.12", "FDN" }, { "80.67.169.40", "FDN" },
                { "89.234.141.66", "ARN" },
                { "185.228.168.9", "CleanBrowsing" }, { "185.228.169.9", "CleanBrowsing" },
                { "76.76.2.0", "Control D" }, { "76.76.10.0", "Control D" },
                { "45.90.28.0", "NextDNS" }, { "45.90.30.0", "NextDNS" },
            };

        /// <summary>Nom de l'opérateur du résolveur, ou <c>null</c> s'il n'est pas reconnu.</summary>
        public static string? Operator(string address)
            => address != null && Known.TryGetValue(address, out var name) ? name : null;
    }

    /// <summary>
    /// L'état du partage de fichiers, du côté des protocoles plutôt que des services.
    /// </summary>
    /// <remarks>
    /// Les deux mesures répondent à la même plainte vue de ses deux bouts : « je ne vois plus le
    /// NAS » quand SMB1 a été retiré d'un poste que le NAS était seul à savoir servir, et « ce
    /// poste accepte encore SMB1 » quand personne ne l'a jamais retiré.
    /// </remarks>
    public sealed class SharingState
    {
        /// <summary>Le pilote du partage de première génération est installé sur la machine.</summary>
        public Measured<bool> Smb1Installed { get; init; } = Measured.NotCollected<bool>();

        /// <summary>Il est installé <b>et</b> son démarrage n'est pas désactivé.</summary>
        public Measured<bool> Smb1Enabled { get; init; } = Measured.NotCollected<bool>();

        /// <summary>Interfaces sur lesquelles NetBIOS a été explicitement coupé.</summary>
        public Measured<int> NetbiosDisabled { get; init; } = Measured.NotCollected<int>();

        /// <summary>Interfaces pour lesquelles un réglage NetBIOS a été relevé.</summary>
        public Measured<int> NetbiosInterfaces { get; init; } = Measured.NotCollected<int>();
    }

    /// <summary>
    /// Ce qui décide du chemin qu'un paquet prend, par-dessus la carte qui l'émet.
    /// </summary>
    /// <remarks>
    /// Quatre lectures qu'aucune fenêtre de Windows ne montre, et qui expliquent les pannes que
    /// le reste du logiciel ne peut que constater : la table de routage dit où part le trafic, le
    /// fichier hosts passe avant tous les serveurs DNS, le catalogue Winsock filtre tout ce qui
    /// sort, et les serveurs DNS décident de ce que « google.fr » veut dire sur cette machine.
    /// </remarks>
    public sealed class NetworkPathsInfo
    {
        public RoutingTable Routing { get; init; } = new RoutingTable();

        public HostsFile Hosts { get; init; } = new HostsFile();

        public WinsockCatalog Winsock { get; init; } = new WinsockCatalog();

        public IReadOnlyList<DnsServerUse> DnsServers { get; init; } = Array.Empty<DnsServerUse>();

        public SharingState Sharing { get; init; } = new SharingState();
    }

    /// <summary>
    /// Les services par lesquels une machine voit ses voisins, et se fait voir d'eux.
    /// </summary>
    /// <remarks>
    /// <b>Aucune mesure nouvelle : une projection.</b> Ces services sont déjà relevés parmi les
    /// deux cents autres, où personne ne va les chercher. Ce qui manquait n'était pas leur état
    /// mais leur rapprochement : « je ne vois plus le NAS ni l'imprimante réseau » est une
    /// plainte, pas un diagnostic, et sa réponse tient dans quatre lignes de cette liste.
    /// </remarks>
    public static class DiscoveryServices
    {
        /// <summary>Services de découverte, du plus visible au plus discret.</summary>
        public static readonly IReadOnlyList<string> Names =
            new[] { "FDResPub", "fdPHost", "SSDPSRV", "upnphost" };

        /// <summary>Ce que chacun rend possible, dit du point de vue de ce qu'on perd sans lui.</summary>
        public static string Describe(string name)
        {
            switch (name)
            {
                case "FDResPub": return "publie cette machine auprès des autres";
                case "fdPHost": return "trouve les autres machines et les imprimantes";
                case "SSDPSRV": return "trouve les box, les NAS et les téléviseurs";
                case "upnphost": return "rend les services de cette machine visibles des autres";
                default: return "participe à la découverte du voisinage";
            }
        }

        /// <summary>Services de découverte à l'arrêt, parmi ceux qui existent sur la machine.</summary>
        public static IReadOnlyList<ServiceInfo> Stopped(IReadOnlyList<ServiceInfo> services)
        {
            var stopped = new List<ServiceInfo>();
            if (services == null) return stopped;

            foreach (var service in services)
            {
                var known = false;
                foreach (var name in Names)
                    if (string.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase)) known = true;

                if (!known) continue;
                if (string.Equals(service.State, "Running", StringComparison.OrdinalIgnoreCase)) continue;

                stopped.Add(service);
            }

            return stopped;
        }
    }
}
