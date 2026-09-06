using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    /// L'environnement réseau : ce qui commande le comportement, par-dessus ce qui est branché.
    /// </summary>
    /// <remarks>
    /// <b>Ce relevé explique les pannes que les tests de connectivité ne voient pas.</b> Une
    /// machine peut répondre au ping, résoudre les noms et joindre Internet, et n'ouvrir aucune
    /// page parce qu'un proxy pointe dans le vide, ne montrer aucun partage parce que le réseau
    /// est classé public, ou refuser toute ouverture de session parce que son suffixe DNS a
    /// disparu. Ce sont les trois plaintes les plus fréquentes en entreprise, et aucune des trois
    /// ne se voyait ici.
    /// <para>
    /// Quatre lectures, aucune n'exigeant les privilèges administrateur : le service de
    /// localisation réseau par COM, les deux configurations de proxy par le registre, et
    /// l'appartenance au domaine par le registre et l'environnement.
    /// </para>
    /// </remarks>
    public sealed class NetworkEnvironmentProbe : IDiagnosticProbe
    {
        /// <summary>Proxy de l'utilisateur : celui que suivent les navigateurs.</summary>
        private const string UserInternetSettings =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        /// <summary>
        /// Réglages de connexion de la session : c'est là, et non un niveau plus haut, que vit le
        /// bloc binaire portant la détection automatique.
        /// </summary>
        private const string UserConnections =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";

        /// <summary>Proxy de la machine : celui que suivent les services, Windows Update en tête.</summary>
        private const string WinHttpSettings =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";

        private const string TcpipParameters = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.NetworkEnvironment,
            DisplayName = "Environnement réseau",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var locations = ReadLocations();
            var user = ReadUserProxy(context);
            var machine = ReadMachineProxy(context);
            var domain = ReadDomain(context);

            context.Draft.SetNetworkEnvironment(new NetworkEnvironment
            {
                Locations = locations,
                UserProxy = user,
                MachineProxy = machine,
                Domain = domain,
            });

            var parts = new List<string>();

            foreach (var location in locations)
                parts.Add(location.Name + " : " + Describe(location.Category) + ", " + Describe(location.Reach));

            if (user.Configured) parts.Add("proxy utilisateur configuré");
            if (machine.Configured) parts.Add("proxy machine configuré");

            if (domain.Joined.Or(false))
                parts.Add("machine jointe au domaine " + domain.Name.Or("inconnu"));

            return Task.FromResult(parts.Count == 0
                ? ProbeOutcome.Partial("Aucun réseau actif n'a été reconnu par Windows.")
                : ProbeOutcome.Ok(string.Join(" · ", parts)));
        }

        // ================================================================= réseaux connectés

        /// <summary>
        /// Les réseaux auxquels Windows se considère connecté, avec leur classement et leur portée.
        /// </summary>
        /// <remarks>
        /// Par le service de localisation réseau, en COM, et non par le registre. Le registre
        /// garde la liste de <b>tous</b> les réseaux jamais rencontrés (plusieurs dizaines sur
        /// un portable) sans dire lequel est actif. Or c'est exactement la question posée.
        /// <para>
        /// La portée est la valeur qui manquait le plus : c'est elle qui est derrière le globe
        /// barré de la zone de notification. Windows sait qu'il n'a pas Internet ; jusqu'ici le
        /// logiciel ne le lui demandait pas.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<NetworkLocation> ReadLocations()
        {
            var found = new List<NetworkLocation>();

            object? manager = null;
            try
            {
                var type = Type.GetTypeFromCLSID(NetworkListManagerClsid);
                if (type == null) return found;

                manager = Activator.CreateInstance(type);
                if (manager is not INetworkListManager list) return found;

                // NLM_ENUM_NETWORK_CONNECTED = 1 : les réseaux connectés, et eux seuls.
                if (list.GetNetworks(1) is not System.Collections.IEnumerable networks) return found;

                foreach (var item in networks)
                {
                    if (item is not INetwork network) continue;

                    try
                    {
                        found.Add(new NetworkLocation
                        {
                            Name = network.GetName() ?? "Réseau sans nom",
                            Category = Translate(network.GetCategory()),
                            Reach = Translate(network.GetConnectivity()),

                            // NLM_DOMAIN_TYPE : 0 hors domaine, 1 réseau de domaine, 2 authentifié.
                            Managed = network.GetDomainType() != 0,
                        });
                    }
                    catch (COMException)
                    {
                        // Un réseau peut disparaître entre l'énumération et la lecture : on
                        // débranche un câble pendant l'analyse. Les autres restent lisibles.
                    }
                    finally
                    {
                        Release(item);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException ||
                                       ex is NotSupportedException || ex is MemberAccessException ||
                                       ex is TypeLoadException)
            {
                // Le service de localisation peut être arrêté, ou l'objet COM absent d'une
                // installation abîmée. Le relevé est alors vide, et c'est ce que dit la fiche.
            }
            finally
            {
                Release(manager);
            }

            return found;
        }

        private static void Release(object? instance)
        {
            try
            {
                if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
            }
            catch (Exception)
            {
                // Rien à faire d'utile : l'objet sera libéré à la fin du processus.
            }
        }

        private static NetworkCategory Translate(int category) => category switch
        {
            0 => NetworkCategory.Public,
            1 => NetworkCategory.Private,
            2 => NetworkCategory.DomainAuthenticated,
            _ => NetworkCategory.Unknown,
        };

        /// <summary>
        /// Traduit les drapeaux de connectivité de NLM.
        /// </summary>
        /// <remarks>
        /// Les bits utiles, tels que NLM_CONNECTIVITY les définit : 0x40 et 0x400 disent
        /// « Internet » en IPv4 et en IPv6 ; 0x10, 0x20, 0x100 et 0x200 disent « sous-réseau » ou
        /// « réseau local ». Un réseau qui a Internet porte aussi les bits du local : l'ordre des
        /// tests n'est donc pas indifférent. Zéro signifie déconnecté, et les bits 0x01 et 0x02
        /// (« aucun trafic ») ne sont pas une portée.
        /// </remarks>
        private static NetworkReach Translate(uint connectivity)
        {
            const uint internet = 0x40 | 0x400;
            const uint local = 0x10 | 0x20 | 0x100 | 0x200;

            if ((connectivity & internet) != 0) return NetworkReach.Internet;
            if ((connectivity & local) != 0) return NetworkReach.LocalOnly;
            return connectivity == 0 ? NetworkReach.None : NetworkReach.Unknown;
        }

        // ================================================================= proxy

        private static ProxySettings ReadUserProxy(ProbeContext context)
        {
            var registry = context.Registry;

            var enabled = registry.ReadInt32(RegistryHive.CurrentUser, UserInternetSettings, "ProxyEnable");
            var server = registry.ReadString(RegistryHive.CurrentUser, UserInternetSettings, "ProxyServer");
            var bypass = registry.ReadString(RegistryHive.CurrentUser, UserInternetSettings, "ProxyOverride");
            var autoConfig = registry.ReadString(RegistryHive.CurrentUser, UserInternetSettings, "AutoConfigURL");
            var detect = registry.ReadBinary(RegistryHive.CurrentUser, UserConnections, "DefaultConnectionSettings");

            return new ProxySettings
            {
                Enabled = enabled.HasValue
                    ? Measured.Ok(enabled.Value != 0, DataSource.Registry)
                    : Measured.Ok(false, DataSource.Registry),
                Server = Text(server, "Aucun serveur mandataire n'est déclaré pour cette session."),
                Bypass = Text(bypass, "Aucune exception n'est déclarée."),
                AutoConfigUrl = Text(autoConfig, "Aucun script de configuration automatique n'est déclaré."),
                AutoDetect = detect != null && detect.Length > 8
                    ? Measured.Ok((detect[8] & 0x08) != 0, DataSource.Registry)
                    : Measured.Missing<bool>("La détection automatique n'a pas pu être lue."),
            };
        }

        /// <summary>
        /// Le proxy de la machine, celui que suit Windows Update.
        /// </summary>
        /// <remarks>
        /// Il n'existe que sous forme d'un bloc binaire, sans équivalent en clair. Sa structure
        /// est stable depuis Windows Vista : quatre octets de version, quatre de compteur, quatre
        /// de drapeaux, puis la chaîne du serveur et celle des exceptions, chacune précédée de sa
        /// longueur. C'est la seule lecture binaire de tout le logiciel, et elle est ici parce
        /// qu'aucune autre porte ne mène à cette valeur.
        /// </remarks>
        private static ProxySettings ReadMachineProxy(ProbeContext context)
        {
            var raw = context.Registry.ReadBinary(
                RegistryHive.LocalMachine, WinHttpSettings, "WinHttpSettings");

            if (raw == null || raw.Length < 12)
                return new ProxySettings
                {
                    Enabled = Measured.Ok(false, DataSource.Registry),
                    Server = Measured.Missing<string>("Aucun proxy n'est déclaré pour la machine."),
                    Bypass = Measured.Missing<string>("Aucune exception n'est déclarée."),
                    AutoConfigUrl = Measured.Missing<string>("Aucun script de configuration automatique n'est déclaré."),
                    AutoDetect = Measured.Ok(false, DataSource.Registry),
                };

            var flags = BitConverter.ToInt32(raw, 8);
            var offset = 12;

            var server = ReadLengthPrefixed(raw, ref offset);
            var bypass = ReadLengthPrefixed(raw, ref offset);

            return new ProxySettings
            {
                // 0x02 : un serveur mandataire est imposé. 0x01 signifie « accès direct ».
                Enabled = Measured.Ok((flags & 0x02) != 0, DataSource.Registry),
                Server = Text(server, "Aucun proxy n'est déclaré pour la machine."),
                Bypass = Text(bypass, "Aucune exception n'est déclarée."),
                AutoConfigUrl = Measured.Missing<string>(
                    "WinHTTP ne conserve pas d'adresse de script à cet emplacement."),
                AutoDetect = Measured.Ok((flags & 0x04) != 0, DataSource.Registry),
            };
        }

        /// <summary>Chaîne précédée de sa longueur sur quatre octets, telle que WinHTTP les écrit.</summary>
        private static string? ReadLengthPrefixed(byte[] raw, ref int offset)
        {
            if (offset + 4 > raw.Length) return null;

            var length = BitConverter.ToInt32(raw, offset);
            offset += 4;

            if (length <= 0 || offset + length > raw.Length) return null;

            var text = Encoding.ASCII.GetString(raw, offset, length);
            offset += length;
            return text;
        }

        // ================================================================= domaine

        private static DomainMembership ReadDomain(ProbeContext context)
        {
            var joined = context.Platform.Profile.IsDomainJoined;

            string? name = null;
            try
            {
                var properties = IPGlobalProperties.GetIPGlobalProperties();
                name = properties.DomainName;
                if (string.IsNullOrWhiteSpace(name)) name = null;
            }
            catch (Exception ex) when (ex is NetworkInformationException || ex is PlatformNotSupportedException)
            {
                // Le nom reste inconnu ; l'appartenance, elle, vient du profil de plateforme.
            }

            var suffix = context.Registry.ReadString(RegistryHive.LocalMachine, TcpipParameters, "Domain");
            if (string.IsNullOrWhiteSpace(suffix))
                suffix = context.Registry.ReadString(RegistryHive.LocalMachine, TcpipParameters, "NV Domain");

            var search = context.Registry.ReadString(RegistryHive.LocalMachine, TcpipParameters, "SearchList");

            return new DomainMembership
            {
                Joined = Measured.Ok(joined, DataSource.NativeApi),
                Name = Text(name, joined
                    ? "Le nom du domaine n'a pas pu être lu."
                    : "Cette machine n'appartient à aucun domaine."),
                PrimaryDnsSuffix = Text(suffix, joined
                    ? "Aucun suffixe DNS principal n'est configuré."
                    : "Aucun suffixe DNS principal, normal hors domaine."),
                SearchSuffixes = Split(search),
                LogonServer = Text(LogonServer(), joined
                    ? "Le serveur d'ouverture de session n'est pas renseigné pour cette session."
                    : "Session ouverte localement, sans contrôleur de domaine."),
            };
        }

        /// <summary>
        /// Contrôleur ayant validé la session, lu dans l'environnement.
        /// </summary>
        /// <remarks>
        /// Windows y écrit <c>\\NOM</c> pour une session de domaine et le nom de la machine
        /// elle-même pour une session locale. Le second cas n'est pas une panne : c'est ce qu'on
        /// lit sur tout poste hors domaine, et le motif d'absence le dit plutôt que d'afficher le
        /// nom de la machine comme s'il s'agissait d'un serveur.
        /// </remarks>
        private static string? LogonServer()
        {
            try
            {
                var value = Environment.GetEnvironmentVariable("LOGONSERVER");
                if (string.IsNullOrWhiteSpace(value)) return null;

                var trimmed = value!.TrimStart('\\');
                return string.Equals(trimmed, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : trimmed;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ================================================================= aides

        private static Measured<string> Text(string? value, string reason)
            => string.IsNullOrWhiteSpace(value)
                ? Measured.Missing<string>(reason)
                : Measured.Ok(value!.Trim(), DataSource.Registry);

        private static IReadOnlyList<string> Split(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

            var parts = value!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) kept.Add(trimmed);
            }
            return kept;
        }

        private static string Describe(NetworkCategory category) => category switch
        {
            NetworkCategory.Public => "public",
            NetworkCategory.Private => "privé",
            NetworkCategory.DomainAuthenticated => "domaine",
            _ => "classement inconnu",
        };

        private static string Describe(NetworkReach reach) => reach switch
        {
            NetworkReach.Internet => "Internet",
            NetworkReach.LocalOnly => "réseau local seulement",
            NetworkReach.None => "aucune connectivité",
            _ => "portée inconnue",
        };

        // ================================================================= COM

        private static readonly Guid NetworkListManagerClsid =
            new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        /// <summary>
        /// <c>INetworkListManager</c>, dans l'ordre exact de sa table de méthodes.
        /// </summary>
        /// <remarks>
        /// <b>L'ordre est le contrat, pas les noms.</b> Le CLR appelle la n-ième entrée de la
        /// table virtuelle : une méthode oubliée ou déplacée n'échoue pas à la compilation, elle
        /// appelle autre chose à l'exécution. Les méthodes inutilisées sont donc déclarées quand
        /// même, à leur place, avec leur signature réelle.
        /// </remarks>
        [ComImport]
        [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkListManager
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetNetworks(int flags);

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetNetwork(Guid networkId);

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetNetworkConnections();

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetNetworkConnection(Guid connectionId);

            [return: MarshalAs(UnmanagedType.VariantBool)]
            bool GetIsConnectedToInternet();

            [return: MarshalAs(UnmanagedType.VariantBool)]
            bool GetIsConnected();

            uint GetConnectivity();
        }

        /// <summary>Un réseau, dans l'ordre exact de sa table de méthodes.</summary>
        [ComImport]
        [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            string GetName();

            void SetName([MarshalAs(UnmanagedType.BStr)] string name);

            [return: MarshalAs(UnmanagedType.BStr)]
            string GetDescription();

            void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

            Guid GetNetworkId();

            uint GetDomainType();

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetNetworkConnections();

            void GetTimeCreatedAndConnected(
                out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);

            [return: MarshalAs(UnmanagedType.VariantBool)]
            bool GetIsConnectedToInternet();

            [return: MarshalAs(UnmanagedType.VariantBool)]
            bool GetIsConnected();

            uint GetConnectivity();

            int GetCategory();

            void SetCategory(int category);
        }
    }
}
