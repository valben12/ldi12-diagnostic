using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Network
{
    /// <summary>
    /// Cartes réseau, adressage IP et liaison sans fil.
    /// </summary>
    /// <remarks>
    /// Tout passe par les classes de <c>System.Net.NetworkInformation</c>, qui reposent sur
    /// <c>GetAdaptersAddresses</c> : instantané, non traduit, et identique de Windows 7 à
    /// Windows 11 : là où l'analyse de <c>ipconfig</c> dépendrait de la langue du système.
    /// Seul l'état de la liaison Wi-Fi impose un détour par l'API native.
    /// </remarks>
    public sealed class NetworkAdaptersProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.NetworkAdapters,
            DisplayName = "Cartes réseau",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromSeconds(0.8),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException ex)
            {
                context.Draft.SetNetworkAdapters(Array.Empty<NetworkAdapterInfo>());
                return Task.FromResult(ProbeOutcome.Failed(
                    "Les cartes réseau n'ont pas pu être énumérées : " + ex.Message, ex));
            }

            var wifi = context.Network.ReadWifiConnections();
            var wifiById = new Dictionary<string, WifiConnectionInfo>(StringComparer.OrdinalIgnoreCase);
            if (wifi.HasValue)
                foreach (var connection in wifi.Value) wifiById[connection.InterfaceId] = connection;

            var primaryId = FindPrimaryInterfaceId(interfaces);
            var adapters = new List<NetworkAdapterInfo>();
            var apipa = 0;
            var connected = 0;

            foreach (var adapter in interfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var kind = Classify(adapter);
                if (kind == NetworkAdapterKind.Loopback) continue;

                var properties = SafeProperties(adapter);
                var ipv4 = new List<string>();
                var ipv6 = new List<string>();
                var gateways = new List<string>();
                var dns = new List<string>();
                var hasApipa = false;

                if (properties != null)
                {
                    foreach (var address in properties.UnicastAddresses)
                    {
                        var text = address.Address.ToString();
                        if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            ipv4.Add(text);

                            // 169.254.x.x : la carte fonctionne mais n'a jamais obtenu de bail
                            // DHCP. C'est le symptôme direct du « je n'ai plus Internet ».
                            if (text.StartsWith("169.254.", StringComparison.Ordinal)) hasApipa = true;
                        }
                        else
                        {
                            ipv6.Add(text);
                        }
                    }

                    foreach (var gateway in properties.GatewayAddresses)
                        if (!IPAddress.IsLoopback(gateway.Address)) gateways.Add(gateway.Address.ToString());

                    foreach (var server in properties.DnsAddresses) dns.Add(server.ToString());
                }

                // Une adresse d'auto-configuration sur une carte débranchée ou virtuelle est
                // normale : ne la compter que sur une interface physique réellement active,
                // sinon chaque machine afficherait une fausse alerte réseau.
                var isPhysical = kind == NetworkAdapterKind.Ethernet || kind == NetworkAdapterKind.WiFi;
                if (hasApipa && isPhysical && adapter.OperationalStatus == OperationalStatus.Up) apipa++;
                if (adapter.OperationalStatus == OperationalStatus.Up && kind != NetworkAdapterKind.Virtual) connected++;


                wifiById.TryGetValue(adapter.Id, out var wifiInfo);

                adapters.Add(new NetworkAdapterInfo
                {
                    Name = adapter.Name,
                    Description = adapter.Description,
                    Kind = kind,
                    MacAddress = ReadMac(adapter),
                    Status = Measured.Ok(Describe(adapter.OperationalStatus), DataSource.NativeApi),
                    LinkSpeedBps = ReadSpeed(adapter),
                    DhcpEnabled = ReadDhcp(properties),
                    DhcpServer = ReadDhcpServer(properties),
                    IPv4Addresses = ipv4,
                    IPv6Addresses = ipv6,
                    Gateways = gateways,
                    DnsServers = dns,
                    HasApipaAddress = Measured.Ok(hasApipa, DataSource.NativeApi),
                    Counters = ReadCounters(adapter),
                    Wifi = kind == NetworkAdapterKind.WiFi ? BuildWifi(wifiInfo, wifi) : null,
                    IsPrimary = string.Equals(adapter.Id, primaryId, StringComparison.OrdinalIgnoreCase),
                    IsUp = adapter.OperationalStatus == OperationalStatus.Up,
                });
            }

            context.Draft.SetNetworkAdapters(adapters);

            if (adapters.Count == 0)
                return Task.FromResult(ProbeOutcome.Failed("Aucune carte réseau n'a été trouvée sur cette machine."));

            if (apipa > 0)
            {
                return Task.FromResult(ProbeOutcome.Ok(
                    adapters.Count + " carte(s) : " + apipa + " sans adresse valide (pas de réponse du serveur DHCP)."));
            }

            return Task.FromResult(ProbeOutcome.Ok(
                adapters.Count + " carte(s), " + connected + " connectée(s)."));
        }

        /// <summary>
        /// Interface portant la route par défaut : c'est celle qui compte pour le diagnostic,
        /// les autres n'étant souvent que des adaptateurs virtuels.
        /// </summary>
        private static string? FindPrimaryInterfaceId(NetworkInterface[] interfaces)
        {
            foreach (var adapter in interfaces)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = SafeProperties(adapter);
                if (properties == null) continue;

                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address != null && !IPAddress.Any.Equals(gateway.Address))
                        return adapter.Id;
                }
            }
            return null;
        }

        /// <summary>
        /// Points d'accès voisins retenus pour l'affichage, du plus fort au plus faible.
        /// </summary>
        /// <remarks>
        /// En immeuble, la carte en entend parfois soixante. Les compter tous a du sens ; les
        /// afficher tous n'en a aucun : le technicien regarde qui occupe son canal, pas la liste
        /// des voisins de son client.
        /// </remarks>
        internal const int RetainedNeighbours = 8;

        internal static WifiInfo BuildWifi(WifiConnectionInfo? connection, Measured<IReadOnlyList<WifiConnectionInfo>> source)
        {
            if (connection == null || !connection.Connected)
            {
                var reason = connection != null
                    ? "Cette interface sans fil n'est associée à aucun réseau."
                    : source.HasValue
                        ? "Cette interface sans fil n'est associée à aucun réseau."
                        : source.Reason ?? "L'état de la liaison sans fil n'a pas pu être lu.";

                return new WifiInfo
                {
                    Ssid = Measured.Missing<string>(reason),
                    Bssid = Measured.Missing<string>(reason),
                    Security = Measured.Missing<string>(reason),
                    Band = Measured.Missing<WifiBand>(reason),
                    RadioType = Measured.Missing<string>(reason),
                    Channel = Measured.Missing<int>(reason),
                    SignalPercent = Measured.Missing<int>(reason),
                    RssiDbm = Measured.Missing<int>(reason),
                    TxRateMbps = Measured.Missing<int>(reason),
                    RxRateMbps = Measured.Missing<int>(reason),

                    // L'état et la radio, eux, sont connus même sans association : c'est
                    // précisément ce qu'on vient chercher sur un portable qui ne se connecte plus.
                    State = connection != null
                        ? Measured.Ok(connection.State, DataSource.NativeApi)
                        : Measured.Missing<string>(reason),
                    RadioEnabled = connection?.RadioEnabled is bool radio
                        ? Measured.Ok(radio, DataSource.NativeApi)
                        : Measured.Missing<bool>("L'état de la radio n'est pas rapporté par cette carte."),
                };
            }

            return new WifiInfo
            {
                Ssid = Measured.Ok(connection.Ssid, DataSource.NativeApi),
                Bssid = Measured.Ok(connection.Bssid, DataSource.NativeApi),
                Security = Measured.Ok(connection.Security, DataSource.NativeApi),
                RadioType = Measured.Ok(connection.PhyType, DataSource.NativeApi),
                Band = ReadBand(connection),
                Channel = connection.Channel is int channel
                    ? Measured.Ok(channel, DataSource.NativeApi)
                    : Measured.Missing<int>("Le canal n'est pas rapporté par cette carte."),
                SignalPercent = Measured.Ok(connection.SignalPercent, DataSource.NativeApi),
                RssiDbm = connection.RssiDbm is int rssi
                    ? Measured.Ok(rssi, DataSource.NativeApi)
                    : Measured.Missing<int>("La puissance reçue n'est pas rapportée par cette carte."),
                TxRateMbps = Measured.Ok(connection.TxRateMbps, DataSource.NativeApi),
                RxRateMbps = Measured.Ok(connection.RxRateMbps, DataSource.NativeApi),
                State = Measured.Ok(connection.State, DataSource.NativeApi),
                RadioEnabled = connection.RadioEnabled is bool enabled
                    ? Measured.Ok(enabled, DataSource.NativeApi)
                    : Measured.Missing<bool>("L'état de la radio n'est pas rapporté par cette carte."),
                Neighbourhood = BuildNeighbourhood(connection),
            };
        }

        /// <summary>
        /// Bande de la liaison, mesurée si la fréquence est connue, déduite sinon.
        /// </summary>
        /// <remarks>
        /// La déduction par le numéro de canal reste marquée <i>partielle</i> : la bande 6 GHz
        /// reprend la numérotation à 1, donc un canal 6 peut désigner deux fréquences très
        /// différentes. Afficher « 2,4 GHz » sans réserve ferait conclure à tort qu'un portable
        /// Wi-Fi 6E est resté sur la bande encombrée.
        /// </remarks>
        internal static Measured<WifiBand> ReadBand(WifiConnectionInfo connection)
        {
            if (connection.FrequencyKhz is int frequency)
            {
                var measured = WifiChannels.BandFromFrequency(frequency);
                if (measured != WifiBand.Unknown) return Measured.Ok(measured, DataSource.NativeApi);
            }

            if (connection.Channel is not int channel)
                return Measured.Missing<WifiBand>("Ni la fréquence ni le canal ne sont rapportés par cette carte.");

            var sixCapable = WifiChannels.SingleStreamRateMbps(connection.PhyType) >= 600;
            var deduced = WifiChannels.BandFromChannel(channel, sixCapable);

            if (deduced == WifiBand.Unknown)
            {
                return Measured.Missing<WifiBand>(
                    "Le canal " + channel + " porte le même numéro en 2,4 GHz et en 6 GHz : la bande " +
                    "ne peut pas être déduite sans la fréquence, que Windows ne donne qu'avec la " +
                    "liste des réseaux voisins.");
            }

            return Measured.Partial(deduced, DataSource.NativeApi,
                "Déduite du numéro de canal, la fréquence exacte n'étant pas disponible.");
        }

        internal static WifiNeighbourhood? BuildNeighbourhood(WifiConnectionInfo connection)
        {
            if (connection.AccessPoints.Count == 0)
            {
                if (connection.AccessPointsLimitation == null) return null;

                var reason = connection.AccessPointsLimitation;
                return new WifiNeighbourhood
                {
                    Total = Measured.Missing<int>(reason),
                    SameChannel = Measured.Missing<int>(reason),
                    OverlappingChannel = Measured.Missing<int>(reason),
                };
            }

            var band = WifiBand.Unknown;
            var channel = connection.Channel ?? 0;
            if (connection.FrequencyKhz is int frequency) band = WifiChannels.BandFromFrequency(frequency);

            var neighbours = new List<WifiNeighbour>();
            var same = 0;
            var overlapping = 0;

            foreach (var point in connection.AccessPoints)
            {
                // Le point d'accès de la liaison n'est pas son propre voisin.
                if (string.Equals(point.Bssid, connection.Bssid, StringComparison.OrdinalIgnoreCase)) continue;

                var pointBand = WifiChannels.BandFromFrequency(point.FrequencyKhz);
                var pointChannel = WifiChannels.ChannelFromFrequency(point.FrequencyKhz);

                neighbours.Add(new WifiNeighbour
                {
                    Ssid = point.Ssid,
                    Bssid = point.Bssid,
                    Channel = pointChannel,
                    Band = pointBand,
                    RssiDbm = point.RssiDbm,
                });

                // Un point d'accès d'une autre bande ne gêne pas : il n'émet pas dans les mêmes
                // fréquences. Sans bande connue des deux côtés, on ne compte rien.
                if (band == WifiBand.Unknown || pointBand != band || channel <= 0) continue;

                if (pointChannel == channel) same++;
                else if (WifiChannels.Overlaps(band, channel, pointChannel)) overlapping++;
            }

            neighbours.Sort((left, right) => right.RssiDbm.CompareTo(left.RssiDbm));
            if (neighbours.Count > RetainedNeighbours) neighbours.RemoveRange(
                RetainedNeighbours, neighbours.Count - RetainedNeighbours);

            var countable = band != WifiBand.Unknown && channel > 0;
            var uncountable = "Le canal ou la bande de la liaison n'est pas connu : les voisins ne " +
                "peuvent pas être rapportés à elle.";

            return new WifiNeighbourhood
            {
                Total = Measured.Ok(connection.AccessPoints.Count, DataSource.NativeApi),
                SameChannel = countable
                    ? Measured.Ok(same, DataSource.NativeApi)
                    : Measured.Missing<int>(uncountable),
                OverlappingChannel = countable
                    ? Measured.Ok(overlapping, DataSource.NativeApi)
                    : Measured.Missing<int>(uncountable),
                Strongest = neighbours,
            };
        }

        private static IPInterfaceProperties? SafeProperties(NetworkInterface adapter)
        {
            // Une carte en cours de retrait (clé USB Wi-Fi débranchée pendant l'analyse) lève ici.
            try { return adapter.GetIPProperties(); }
            catch (NetworkInformationException) { return null; }
            catch (PlatformNotSupportedException) { return null; }
        }

        private static Measured<string> ReadMac(NetworkInterface adapter)
        {
            var address = adapter.GetPhysicalAddress();
            var bytes = address?.GetAddressBytes();
            if (bytes == null || bytes.Length == 0)
                return Measured.Missing<string>("Cette interface n'a pas d'adresse matérielle.");

            var parts = new string[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) parts[i] = bytes[i].ToString("X2");
            return Measured.Ok(string.Join(":", parts), DataSource.NativeApi);
        }

        private static Measured<long> ReadSpeed(NetworkInterface adapter)
        {
            try
            {
                return adapter.Speed > 0
                    ? Measured.Ok(adapter.Speed, DataSource.NativeApi)
                    : Measured.Missing<long>("Le débit de liaison n'est pas rapporté par ce pilote.");
            }
            catch (Exception ex) when (ex is NetworkInformationException || ex is PlatformNotSupportedException)
            {
                return Measured.Missing<long>("Le débit de liaison n'est pas accessible.");
            }
        }

        private static Measured<bool> ReadDhcp(IPInterfaceProperties? properties)
        {
            if (properties == null) return Measured.Missing<bool>("Les propriétés IP n'ont pas pu être lues.");

            try
            {
                var v4 = properties.GetIPv4Properties();
                return v4 == null
                    ? Measured.Missing<bool>("Cette interface n'a pas de configuration IPv4.")
                    : Measured.Ok(v4.IsDhcpEnabled, DataSource.NativeApi);
            }
            catch (NetworkInformationException)
            {
                return Measured.Missing<bool>("La configuration IPv4 de cette interface est inaccessible.");
            }
        }

        private static Measured<string> ReadDhcpServer(IPInterfaceProperties? properties)
        {
            if (properties == null) return Measured.Missing<string>("Les propriétés IP n'ont pas pu être lues.");

            foreach (var server in properties.DhcpServerAddresses)
                return Measured.Ok(server.ToString(), DataSource.NativeApi);

            return Measured.Missing<string>("Aucun serveur DHCP n'a répondu sur cette interface.");
        }

        /// <summary>
        /// Compteurs de trames et d'erreurs de la liaison.
        /// </summary>
        /// <remarks>
        /// La seule fenêtre du logiciel sur la couche physique. Un câble abîmé, une prise oxydée
        /// ou un port de commutateur fatigué ne se voient nulle part ailleurs : la connexion
        /// fonctionne, les tests passent, et la machine rame parce que chaque trame perdue est
        /// retransmise.
        /// <para>
        /// Les compteurs sont cumulés depuis le démarrage : bruts, ils ne veulent rien dire. Le
        /// taux pour un million de trames est calculé ici, une fois, pour que ni les règles ni
        /// l'affichage n'aient à le refaire chacun à sa façon.
        /// </para>
        /// </remarks>
        private static LinkCounters ReadCounters(NetworkInterface adapter)
        {
            IPInterfaceStatistics statistics;
            try
            {
                statistics = adapter.GetIPStatistics();
            }
            catch (Exception ex) when (ex is NetworkInformationException || ex is PlatformNotSupportedException)
            {
                return LinkCounters.None;
            }

            var received = statistics.UnicastPacketsReceived + statistics.NonUnicastPacketsReceived;
            var sent = statistics.UnicastPacketsSent + statistics.NonUnicastPacketsSent;

            // Le taux se calcule par direction, et c'est la pire des deux qui est retenue.
            // Mélanger les deux sens dilue le défaut : quatre-vingts trames abîmées en réception
            // sur cinq cent mille reçues font cent cinquante par million, et seulement
            // soixante-quinze si on les noie dans le million de trames des deux sens réunis.
            // Un câble abîmé n'abîme qu'un sens à la fois.
            var inbound = Rate(received, statistics.IncomingPacketsWithErrors);
            var outbound = Rate(sent, statistics.OutgoingPacketsWithErrors);

            // Une carte qui n'a rien vu passer n'a pas un taux d'erreur nul : elle n'a pas de
            // taux du tout. Annoncer « 0 » sur une carte inactive ferait passer pour saine une
            // liaison dont on ne sait rien.
            var worst = inbound.HasValue || outbound.HasValue
                ? Measured.Ok(Math.Max(inbound ?? 0, outbound ?? 0), DataSource.NativeApi)
                : Measured.Missing<double>("Aucune trame n'a circulé sur cette interface depuis le démarrage.");

            return new LinkCounters
            {
                BytesReceived = Measured.Ok(statistics.BytesReceived, DataSource.NativeApi),
                BytesSent = Measured.Ok(statistics.BytesSent, DataSource.NativeApi),
                PacketsReceived = Measured.Ok(
                    statistics.UnicastPacketsReceived + statistics.NonUnicastPacketsReceived, DataSource.NativeApi),
                PacketsSent = Measured.Ok(
                    statistics.UnicastPacketsSent + statistics.NonUnicastPacketsSent, DataSource.NativeApi),
                ErrorsReceived = Measured.Ok(statistics.IncomingPacketsWithErrors, DataSource.NativeApi),
                ErrorsSent = Measured.Ok(statistics.OutgoingPacketsWithErrors, DataSource.NativeApi),
                DiscardsReceived = Measured.Ok(statistics.IncomingPacketsDiscarded, DataSource.NativeApi),
                DiscardsSent = Measured.Ok(statistics.OutgoingPacketsDiscarded, DataSource.NativeApi),
                ErrorsPerMillion = worst,
            };
        }

        /// <summary>Erreurs pour un million de trames dans un sens, ou rien si ce sens est resté muet.</summary>
        private static double? Rate(long packets, long errors)
        {
            var total = packets + errors;
            return total > 0 ? Math.Round(1_000_000d * errors / total, 1) : (double?)null;
        }

        /// <summary>Marques des clients VPN les plus répandus, telles qu'elles apparaissent dans la description.</summary>
        /// <remarks>
        /// Une liste de noms commerciaux vieillit, et celle-ci vieillira. Le choix est assumé :
        /// une carte VPN non reconnue retombe dans son type d'origine et reste affichée, alors
        /// qu'une reconnaissance par heuristique se tromperait dans les deux sens sur des cartes
        /// virtuelles parfaitement ordinaires.
        /// </remarks>
        private static readonly string[] VpnMarkers =
        {
            "NordLynx", "NordVPN", "WireGuard", "OpenVPN", "TAP-Windows", "AnyConnect", "FortiClient",
            "Fortinet", "Check Point", "GlobalProtect", "Pulse Secure", "SonicWall", "Mullvad",
            "ProtonVPN", "ExpressVPN", "Surfshark", "Private Internet Access", "Hamachi", "ZeroTier",
            "Tailscale", "Sophos", "WatchGuard", "Barracuda",
        };

        private static NetworkAdapterKind Classify(NetworkInterface adapter)
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) return NetworkAdapterKind.Loopback;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return NetworkAdapterKind.Tunnel;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return NetworkAdapterKind.WiFi;

            var description = adapter.Description ?? string.Empty;

            // Avant le test « virtuel » : un adaptateur TAP est la carte d'un client VPN, pas une
            // carte de machine virtuelle, et les deux ne se lisent pas de la même façon.
            foreach (var marker in VpnMarkers)
                if (description.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (adapter.Name ?? string.Empty).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return NetworkAdapterKind.Vpn;

            if (description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                description.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                description.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                description.IndexOf("TAP-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NetworkAdapterKind.Virtual;
            }

            if (description.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkAdapterKind.Bluetooth;

            return adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet => NetworkAdapterKind.Ethernet,
                NetworkInterfaceType.GigabitEthernet => NetworkAdapterKind.Ethernet,
                NetworkInterfaceType.FastEthernetT => NetworkAdapterKind.Ethernet,
                NetworkInterfaceType.FastEthernetFx => NetworkAdapterKind.Ethernet,
                NetworkInterfaceType.Wman => NetworkAdapterKind.Mobile,
                NetworkInterfaceType.Wwanpp => NetworkAdapterKind.Mobile,
                NetworkInterfaceType.Wwanpp2 => NetworkAdapterKind.Mobile,
                _ => NetworkAdapterKind.Unknown,
            };
        }

        private static string Describe(OperationalStatus status) => status switch
        {
            OperationalStatus.Up => "Connectée",
            OperationalStatus.Down => "Déconnectée",
            OperationalStatus.Testing => "En test",
            OperationalStatus.Dormant => "En veille",
            OperationalStatus.NotPresent => "Absente",
            OperationalStatus.LowerLayerDown => "Câble débranché ou support absent",
            _ => "État inconnu",
        };
    }
}
