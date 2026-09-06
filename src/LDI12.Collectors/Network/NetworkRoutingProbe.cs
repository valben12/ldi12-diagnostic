using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Network
{
    /// <summary>
    /// Ce qui décide du chemin d'un paquet, par-dessus la carte qui l'émet.
    /// </summary>
    /// <remarks>
    /// <b>Quatre lectures qu'aucune fenêtre de Windows ne montre.</b> Le reste du logiciel sait
    /// dire qu'une machine a une adresse, une passerelle joignable et un DNS qui répond, et
    /// s'arrête là quand malgré tout un site ne s'ouvre pas. Les quatre mesures rassemblées ici
    /// sont celles qui expliquent ce « malgré tout » : une route posée à la main qui détourne un
    /// sous-réseau entier, une ligne du fichier hosts qui l'emporte sur tous les serveurs DNS,
    /// une bibliothèque restée dans le catalogue Winsock après une désinstallation, un serveur
    /// DNS qui n'est ni la box ni un résolveur connu.
    /// <para>
    /// Aucune ne demande de privilèges, et aucune ne sort sur le réseau.
    /// </para>
    /// </remarks>
    public sealed class NetworkRoutingProbe : IDiagnosticProbe
    {
        private const string PersistentRoutes =
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes";

        private const string WinsockCatalogKey =
            @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9";

        private const string NetbtInterfaces =
            @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";

        private const string Smb1Driver = @"SYSTEM\CurrentControlSet\Services\mrxsmb10";

        private const string LanmanServerParameters =
            @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters";

        /// <summary>NetbiosOptions vaut 2 quand quelqu'un a explicitement coupé NetBIOS.</summary>
        private const int NetbiosDisabledValue = 2;

        /// <summary>Démarrage désactivé, au sens du registre des services.</summary>
        private const int ServiceStartDisabled = 4;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.NetworkRouting,
            DisplayName = "Chemins et résolution de noms",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromMilliseconds(400),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var interfaces = ReadInterfaces();
            var persistent = ReadPersistentRoutes(context);
            var routing = ReadRouting(context, interfaces, persistent);
            var hosts = ReadHosts();
            var winsock = ReadWinsock(context);
            var dns = ReadDnsServers(interfaces);
            var sharing = ReadSharing(context);

            context.Draft.SetNetworkPaths(new NetworkPathsInfo
            {
                Routing = routing,
                Hosts = hosts,
                Winsock = winsock,
                DnsServers = dns,
                Sharing = sharing,
            });

            return Task.FromResult(Conclude(routing, hosts, winsock));
        }

        /// <summary>
        /// Conclusion en une phrase, du point de vue de ce qui sort de l'ordinaire.
        /// </summary>
        /// <remarks>
        /// Sur une machine saine, les trois compteurs sont à zéro et la phrase le dit sans
        /// détour : c'est une information utile, parce qu'elle écarte trois causes d'un coup.
        /// </remarks>
        private static ProbeOutcome Conclude(RoutingTable routing, HostsFile hosts, WinsockCatalog winsock)
        {
            if (!routing.Count.HasValue)
                return ProbeOutcome.Partial(routing.Count.Reason ?? "La table de routage n'a pas pu être lue.");

            var notes = new List<string>
            {
                routing.Count.Value + " route(s), " + routing.DefaultRoutes.Count + " de sortie",
            };

            if (hosts.ActiveCount.HasValue)
                notes.Add(hosts.ActiveCount.Value == 0
                    ? "fichier hosts sans redirection"
                    : hosts.ActiveCount.Value + " redirection(s) dans le fichier hosts");

            if (winsock.Count.HasValue)
                notes.Add(winsock.Foreign.Count == 0
                    ? "catalogue Winsock d'origine"
                    : winsock.Foreign.Count + " fournisseur(s) Winsock étranger(s)");

            return ProbeOutcome.Ok(string.Join(", ", notes));
        }

        // ============================================================ routes

        /// <summary>
        /// La table de routage, rapprochée des cartes et des routes rendues permanentes.
        /// </summary>
        /// <remarks>
        /// Le protocole rendu par la pile ne suffit pas à distinguer une route posée à la main :
        /// une route ajoutée par <c>route add</c> et la passerelle venue du bail DHCP portent
        /// toutes deux le même marqueur. Ce que le registre des routes permanentes contient, en
        /// revanche, n'y est jamais arrivé tout seul : c'est cette liste qui tranche.
        /// </remarks>
        private static RoutingTable ReadRouting(
            ProbeContext context, IReadOnlyDictionary<int, string> interfaces, HashSet<string> persistent)
        {
            var raw = context.Network.ReadRoutes();
            if (!raw.HasValue)
            {
                return new RoutingTable
                {
                    Count = Measured.Missing<int>(
                        raw.Reason ?? "La table de routage n'a pas pu être lue.", DataSource.NativeApi),
                };
            }

            var routes = new List<RouteEntry>();

            foreach (var route in raw.Value)
            {
                var origin = route.Protocol switch
                {
                    IpRouteProtocol.Local => RouteOrigin.System,
                    IpRouteProtocol.Configured => RouteOrigin.Configured,
                    IpRouteProtocol.Static => RouteOrigin.Manual,
                    IpRouteProtocol.Redirect => RouteOrigin.Redirect,
                    _ => RouteOrigin.Unknown,
                };

                if (persistent.Contains(route.Destination + "," + route.Mask + "," + route.NextHop))
                    origin = RouteOrigin.Manual;

                routes.Add(new RouteEntry
                {
                    Destination = route.Destination,
                    Mask = route.Mask,
                    PrefixLength = PrefixOf(route.Mask),
                    NextHop = route.NextHop,
                    InterfaceIndex = route.InterfaceIndex,
                    InterfaceName = interfaces.TryGetValue(route.InterfaceIndex, out var name) ? name : null,
                    Metric = route.Metric,
                    Origin = origin,
                });
            }

            return new RoutingTable
            {
                Routes = routes,
                Count = Measured.Ok(routes.Count, DataSource.NativeApi),
            };
        }

        /// <summary>Longueur de préfixe déduite du masque, sans supposer qu'il soit contigu.</summary>
        internal static int PrefixOf(string mask)
        {
            if (string.IsNullOrEmpty(mask)) return 0;

            var parts = mask.Split('.');
            if (parts.Length != 4) return 0;

            var bits = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return 0;
                for (var bit = 7; bit >= 0; bit--) if ((value & (1 << bit)) != 0) bits++;
            }

            return bits;
        }

        /// <summary>
        /// Les routes que quelqu'un a rendues permanentes.
        /// </summary>
        /// <remarks>
        /// Les valeurs portent le nom « destination,masque,passerelle,métrique » : la clé se lit
        /// donc dans les noms de valeurs, pas dans leur contenu.
        /// </remarks>
        private static HashSet<string> ReadPersistentRoutes(ProbeContext context)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in context.Registry.GetValueNames(RegistryHive.LocalMachine, PersistentRoutes))
            {
                var parts = name.Split(',');
                if (parts.Length < 3) continue;
                found.Add(parts[0] + "," + parts[1] + "," + parts[2]);
            }

            return found;
        }

        /// <summary>Index d'interface vers nom de carte, pour nommer une route autrement que par un numéro.</summary>
        private static Dictionary<int, string> ReadInterfaces()
        {
            var names = new Dictionary<int, string>();

            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var properties = adapter.GetIPProperties();
                        var ipv4 = properties.GetIPv4Properties();
                        if (ipv4 != null && !names.ContainsKey(ipv4.Index)) names[ipv4.Index] = adapter.Name;
                    }
                    catch (NetworkInformationException)
                    {
                        // Une carte désactivée ne rend pas ses propriétés IPv4 : la route restera
                        // désignée par son index, ce qui suffit à la retrouver.
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Aucun nom ne sera rattaché : les routes restent lisibles.
            }

            return names;
        }

        // ============================================================ fichier hosts

        /// <summary>
        /// Le fichier hosts, lu ligne à ligne pour pouvoir montrer chacune avant de la retirer.
        /// </summary>
        /// <remarks>
        /// Le numéro de ligne et le texte d'origine sont conservés : une opération qui propose
        /// d'effacer des lignes doit pouvoir les montrer telles qu'elles sont écrites, et pas
        /// telles qu'on les a comprises.
        /// </remarks>
        private static HostsFile ReadHosts()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

            if (!File.Exists(path))
            {
                return new HostsFile
                {
                    Path = path,
                    ActiveCount = Measured.Missing<int>(
                        "Le fichier hosts est absent de cette machine.", DataSource.FileSystem),
                };
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new HostsFile
                {
                    Path = path,
                    ActiveCount = Measured.Missing<int>(
                        "Le fichier hosts n'a pas pu être lu : " + ex.Message, DataSource.FileSystem),
                };
            }

            var entries = new List<HostsEntry>();

            for (var index = 0; index < lines.Length; index++)
            {
                var raw = lines[index];
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                var comment = trimmed.IndexOf('#');
                if (comment >= 0) trimmed = trimmed.Substring(0, comment).Trim();
                if (trimmed.Length == 0) continue;

                var fields = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) continue;

                var names = new List<string>();
                for (var field = 1; field < fields.Length; field++) names.Add(fields[field]);

                entries.Add(new HostsEntry
                {
                    Line = index + 1,
                    Address = fields[0],
                    Names = names,
                    Target = TargetOf(fields[0]),
                    Raw = raw,
                });
            }

            return new HostsFile
            {
                Path = path,
                LineCount = lines.Length,
                Entries = entries,
                ActiveCount = Measured.Ok(entries.Count, DataSource.FileSystem),
            };
        }

        private static HostsTarget TargetOf(string address)
        {
            if (address == "0.0.0.0" || address == "::") return HostsTarget.Blocked;
            if (address.StartsWith("127.", StringComparison.Ordinal) || address == "::1") return HostsTarget.Loopback;
            return HostsTarget.Redirected;
        }

        // ============================================================ catalogue Winsock

        /// <summary>
        /// Le catalogue Winsock, dont chaque entrée nomme une bibliothèque en tête de sa structure.
        /// </summary>
        /// <remarks>
        /// <b>Le chemin est écrit en ANSI, pas en UTF-16.</b> Lu comme du texte large, il rend des
        /// idéogrammes : c'est ce qu'a produit la première mesure sur une machine dont le
        /// catalogue était parfaitement sain. La structure commence par 260 octets d'un octet par
        /// caractère, suivis de la description du protocole dont ce logiciel n'a pas l'usage.
        /// </remarks>
        private static WinsockCatalog ReadWinsock(ProbeContext context)
        {
            var entries = context.Registry.GetSubKeyNames(
                RegistryHive.LocalMachine, WinsockCatalogKey + @"\Catalog_Entries");

            if (entries.Count == 0)
            {
                return new WinsockCatalog
                {
                    Count = Measured.Missing<int>(
                        "Le catalogue Winsock n'a pas pu être lu.", DataSource.Registry),
                };
            }

            var providers = new List<WinsockProvider>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var packed = context.Registry.ReadBinary(
                    RegistryHive.LocalMachine, WinsockCatalogKey + @"\Catalog_Entries\" + entry,
                    "PackedCatalogItem");

                if (packed == null || packed.Length < 4) continue;

                var library = AnsiString(packed);
                if (library.Length == 0) continue;

                // Les quatorze entrées d'un Windows d'origine désignent toutes la même
                // bibliothèque : les répéter n'apprendrait rien.
                if (!seen.Add(library)) continue;

                int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);

                providers.Add(new WinsockProvider
                {
                    Index = index,
                    LibraryPath = library,
                    FromWindows = IsWindowsLibrary(library),
                });
            }

            var count = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, WinsockCatalogKey, "Num_Catalog_Entries");

            return new WinsockCatalog
            {
                Providers = providers,
                Count = count.HasValue
                    ? Measured.Ok(count.Value, DataSource.Registry)
                    : Measured.Ok(entries.Count, DataSource.Registry),
            };
        }

        /// <summary>Chaîne d'octets terminée par zéro, au plus 260 caractères.</summary>
        private static string AnsiString(byte[] packed)
        {
            var length = 0;
            var limit = Math.Min(260, packed.Length);
            while (length < limit && packed[length] != 0) length++;

            return length == 0 ? string.Empty : Encoding.Default.GetString(packed, 0, length);
        }

        /// <summary>
        /// La bibliothèque est livrée avec Windows.
        /// </summary>
        /// <remarks>
        /// Le test porte sur l'emplacement et non sur le nom : une bibliothèque déposée ailleurs
        /// que dans le dossier système n'a pas été livrée avec Windows, quel que soit son nom. Le
        /// chemin est écrit avec la variable d'environnement telle que la pile la garde.
        /// </remarks>
        private static bool IsWindowsLibrary(string library)
        {
            var lower = library.ToLowerInvariant().Replace('/', '\\');

            return lower.StartsWith(@"%systemroot%\system32\", StringComparison.Ordinal)
                   || lower.StartsWith(@"%systemroot%\syswow64\", StringComparison.Ordinal)
                   || lower.StartsWith(@"c:\windows\system32\", StringComparison.Ordinal)
                   || lower.StartsWith(@"c:\windows\syswow64\", StringComparison.Ordinal);
        }

        // ============================================================ serveurs DNS

        /// <summary>
        /// Les serveurs DNS configurés, et ce qu'ils sont pour cette machine.
        /// </summary>
        /// <remarks>
        /// La nature compte plus que l'adresse. Un résolveur public répandu est le premier geste
        /// de tout technicien devant une box qui rame ; un serveur du même réseau est un filtre
        /// maison ou un serveur d'entreprise. Reste le cas qui mérite d'être dit : une adresse
        /// publique que personne ne connaît, mise là par autre chose que l'utilisateur.
        /// </remarks>
        private static IReadOnlyList<DnsServerUse> ReadDnsServers(IReadOnlyDictionary<int, string> interfaces)
        {
            var servers = new List<DnsServerUse>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;

                    IPInterfaceProperties properties;
                    try { properties = adapter.GetIPProperties(); }
                    catch (NetworkInformationException) { continue; }

                    foreach (var address in properties.DnsAddresses)
                    {
                        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                        var text = address.ToString();
                        if (!seen.Add(text)) continue;

                        servers.Add(new DnsServerUse
                        {
                            Address = text,
                            InterfaceName = adapter.Name,
                            Nature = Classify(text, properties),
                            Operator = PublicResolvers.Operator(text),
                        });
                    }
                }
            }
            catch (NetworkInformationException)
            {
                return servers;
            }

            return servers;
        }

        private static DnsServerNature Classify(string address, IPInterfaceProperties properties)
        {
            if (address.StartsWith("127.", StringComparison.Ordinal)) return DnsServerNature.LocalMachine;

            foreach (var gateway in properties.GatewayAddresses)
                if (gateway.Address != null && gateway.Address.ToString() == address) return DnsServerNature.Gateway;

            if (PublicResolvers.Operator(address) != null) return DnsServerNature.KnownPublic;

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (unicast.IPv4Mask == null) continue;
                if (unicast.Address.ToString() == address) return DnsServerNature.LocalMachine;
                if (SameSubnet(address, unicast.Address.ToString(), unicast.IPv4Mask.ToString()))
                    return DnsServerNature.LocalNetwork;
            }

            return DnsServerNature.Unknown;
        }

        /// <summary>Deux adresses tombent dans le même sous-réseau.</summary>
        internal static bool SameSubnet(string left, string right, string mask)
        {
            var a = Octets(left);
            var b = Octets(right);
            var m = Octets(mask);
            if (a == null || b == null || m == null) return false;

            for (var index = 0; index < 4; index++)
                if ((a[index] & m[index]) != (b[index] & m[index])) return false;

            return true;
        }

        private static int[]? Octets(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            var parts = address.Split('.');
            if (parts.Length != 4) return null;

            var octets = new int[4];
            for (var index = 0; index < 4; index++)
            {
                if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out octets[index]))
                    return null;
            }

            return octets;
        }

        // ============================================================ partage

        /// <summary>
        /// L'état du partage de première génération et de NetBIOS.
        /// </summary>
        /// <remarks>
        /// <b>Mesuré par le registre, et pas par DISM.</b> La commande qui interroge les
        /// fonctionnalités facultatives de Windows exige une élévation : vérifié sur la machine de
        /// développement, où elle a répondu « l'opération demandée nécessite une élévation ». Le
        /// service du pilote, lui, se lit depuis une session ordinaire : absent, le protocole
        /// n'est pas installé ; présent avec un démarrage désactivé, il est installé mais éteint.
        /// </remarks>
        private static SharingState ReadSharing(ProbeContext context)
        {
            var installed = context.Registry.KeyExists(RegistryHive.LocalMachine, Smb1Driver);

            Measured<bool> enabled;
            if (!installed)
            {
                enabled = Measured.Ok(false, DataSource.Registry);
            }
            else
            {
                var start = context.Registry.ReadInt32(RegistryHive.LocalMachine, Smb1Driver, "Start");
                var switchedOff = context.Registry.ReadInt32(
                    RegistryHive.LocalMachine, LanmanServerParameters, "SMB1") == 0;

                enabled = start.HasValue
                    ? Measured.Ok(start.Value != ServiceStartDisabled && !switchedOff, DataSource.Registry)
                    : Measured.Missing<bool>(
                        "Le démarrage du pilote de partage n'a pas pu être lu.", DataSource.Registry);
            }

            var interfaces = context.Registry.GetSubKeyNames(RegistryHive.LocalMachine, NetbtInterfaces);
            var disabled = 0;
            var counted = 0;

            foreach (var name in interfaces)
            {
                var options = context.Registry.ReadInt32(
                    RegistryHive.LocalMachine, NetbtInterfaces + "\\" + name, "NetbiosOptions");

                if (!options.HasValue) continue;

                counted++;
                if (options.Value == NetbiosDisabledValue) disabled++;
            }

            return new SharingState
            {
                Smb1Installed = Measured.Ok(installed, DataSource.Registry),
                Smb1Enabled = enabled,
                NetbiosDisabled = counted == 0
                    ? Measured.Missing<int>("Aucun réglage NetBIOS n'a été relevé.", DataSource.Registry)
                    : Measured.Ok(disabled, DataSource.Registry),
                NetbiosInterfaces = Measured.Ok(counted, DataSource.Registry),
            };
        }
    }
}
