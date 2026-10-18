using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace LDI12.Core.Network
{
    /// <summary>Un correspondant ou un port, et ce qui a transité par lui.</summary>
    public sealed class TrafficLine
    {
        public string Key { get; set; } = string.Empty;
        public string? Label { get; set; }
        public long Bytes { get; set; }
        public long Packets { get; set; }
    }

    /// <summary>Un paquet, en une ligne comme dans la liste de Wireshark.</summary>
    public sealed class PacketRecord
    {
        public long Number { get; set; }

        /// <summary>Secondes depuis le début de l'écoute.</summary>
        public double Time { get; set; }

        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;

        /// <summary>« HTTPS », « DNS », « TCP »… : le service quand il est connu, le transport sinon.</summary>
        public string Protocol { get; set; } = string.Empty;

        public int Length { get; set; }
        public string Info { get; set; } = string.Empty;

        /// <summary>Envoyé par ce PC ; sinon reçu.</summary>
        public bool Outgoing { get; set; }
    }

    /// <summary>
    /// Ce que l'écoute envoie à l'écran pendant qu'elle tourne : les paquets arrivés depuis le
    /// dernier envoi, et le résumé à jour. Au-delà d'un débit qu'un écran peut suivre, les paquets
    /// en trop ne sont pas listés (ils restent comptés, et écrits dans le fichier pour Wireshark).
    /// </summary>
    public sealed class TrafficLive
    {
        public List<PacketRecord> Packets { get; set; } = new List<PacketRecord>();
        public long Skipped { get; set; }
        public TrafficSummary Summary { get; set; } = new TrafficSummary();
    }

    /// <summary>Ce qu'une capture a vu passer, résumé comme les statistiques de Wireshark.</summary>
    public sealed class TrafficSummary
    {
        public TimeSpan Duration { get; set; }
        public long Packets { get; set; }
        public long Bytes { get; set; }
        public string LocalAddress { get; set; } = string.Empty;

        /// <summary>TCP, UDP, ICMP… par volume.</summary>
        public List<TrafficLine> Protocols { get; set; } = new List<TrafficLine>();

        /// <summary>Les adresses distantes qui ont le plus échangé avec ce PC.</summary>
        public List<TrafficLine> Hosts { get; set; } = new List<TrafficLine>();

        /// <summary>Les services (ports distants) les plus utilisés.</summary>
        public List<TrafficLine> Services { get; set; } = new List<TrafficLine>();

        /// <summary>Les noms demandés au DNS pendant la capture : ce que le PC cherche à joindre.</summary>
        public List<TrafficLine> DnsQueries { get; set; } = new List<TrafficLine>();

        /// <summary>Le fichier pcap écrit, à ouvrir dans Wireshark ; nul si aucun.</summary>
        public string? PcapPath { get; set; }

        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Lit des paquets IPv4 bruts (tels que Windows les livre à une prise en mode capture) et en
    /// tire les statistiques que donne Wireshark : protocoles, correspondants, services, DNS.
    /// </summary>
    public sealed class TrafficAnalyzer
    {
        private readonly uint _local;
        private readonly Dictionary<string, TrafficLine> _protocols = new Dictionary<string, TrafficLine>();
        private readonly Dictionary<string, TrafficLine> _hosts = new Dictionary<string, TrafficLine>();
        private readonly Dictionary<string, TrafficLine> _services = new Dictionary<string, TrafficLine>();
        private readonly Dictionary<string, TrafficLine> _dns = new Dictionary<string, TrafficLine>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>();
        private long _packets, _bytes;

        public TrafficAnalyzer(string localAddress)
        {
            LocalAddress = localAddress;
            _local = IPAddress.TryParse(localAddress, out var ip) && ip.GetAddressBytes().Length == 4 ? ToUInt(ip.GetAddressBytes(), 0) : 0;
        }

        public string LocalAddress { get; }

        public void Add(byte[] packet, int length) => Describe(packet, length, 0);

        /// <summary>
        /// Compte le paquet dans les statistiques et le décrit en une ligne ; nul pour ce qui n'est
        /// pas un paquet IPv4 lisible.
        /// </summary>
        public PacketRecord? Describe(byte[] packet, int length, double time)
        {
            if (length < 20 || (packet[0] >> 4) != 4) return null;
            var header = (packet[0] & 0x0F) * 4;
            if (header < 20 || header > length) return null;
            var total = Math.Min(length, (packet[2] << 8) | packet[3]);
            var protocol = packet[9];
            var source = ToUInt(packet, 12);
            var destination = ToUInt(packet, 16);

            _packets++;
            _bytes += total;

            var outgoing = source == _local;
            var remote = outgoing ? destination : source;
            var remoteText = ToText(remote);
            Count(_hosts, remoteText, total);

            int? remotePort = null;
            int? fromPort = null, toPort = null;
            string? info = null;
            string name;
            switch (protocol)
            {
                case 6:
                    name = "TCP";
                    if (header + 4 <= length)
                    {
                        fromPort = Port(packet, header);
                        toPort = Port(packet, header + 2);
                        remotePort = outgoing ? toPort : fromPort;
                    }
                    if (header + 14 <= length) info = Flags(packet[header + 13]);
                    break;
                case 17:
                    name = "UDP";
                    if (header + 8 <= length)
                    {
                        fromPort = Port(packet, header);
                        toPort = Port(packet, header + 2);
                        remotePort = outgoing ? toPort : fromPort;
                        if (toPort == 53 && outgoing) info = Dns(packet, header + 8, length, query: true);
                        else if (fromPort == 53 && !outgoing) info = Dns(packet, header + 8, length, query: false);
                    }
                    break;
                case 1:
                    name = "ICMP";
                    if (header + 1 <= length) info = packet[header] switch { 8 => "Ping (demande)", 0 => "Ping (réponse)", 3 => "Destination injoignable", 11 => "Durée de vie dépassée", _ => "Type " + packet[header] };
                    break;
                case 2: name = "IGMP"; break;
                default: name = "IP " + protocol; break;
            }
            Count(_protocols, name, total);
            if (remotePort.HasValue) Count(_services, name + " " + remotePort.Value, total);

            var service = remotePort.HasValue ? ServiceName(name + " " + remotePort.Value) : null;
            var ports = fromPort.HasValue ? fromPort + " → " + toPort : null;
            var sourceText = ToText(source);
            var destinationText = ToText(destination);
            return new PacketRecord
            {
                Number = _packets,
                Time = time,
                Source = _names.TryGetValue(sourceText, out var sourceName) ? sourceName : sourceText,
                Destination = _names.TryGetValue(destinationText, out var destinationName) ? destinationName : destinationText,
                Protocol = service ?? name,
                Length = total,
                Info = ports == null ? info ?? string.Empty : info == null ? ports : ports + "  " + info,
                Outgoing = outgoing,
            };
        }

        /// <summary>Les drapeaux TCP, comme Wireshark les écrit : [SYN], [ACK], [FIN, ACK]…</summary>
        private static string Flags(byte flags)
        {
            var names = new List<string>(4);
            if ((flags & 0x02) != 0) names.Add("SYN");
            if ((flags & 0x01) != 0) names.Add("FIN");
            if ((flags & 0x04) != 0) names.Add("RST");
            if ((flags & 0x08) != 0) names.Add("PSH");
            if ((flags & 0x10) != 0) names.Add("ACK");
            return names.Count == 0 ? string.Empty : "[" + string.Join(", ", names) + "]";
        }

        public TrafficSummary Summarize(TimeSpan duration, int top = 15)
        {
            List<TrafficLine> Top(Dictionary<string, TrafficLine> lines, Func<TrafficLine, long> by)
                => lines.Values.OrderByDescending(by).Take(top).ToList();

            foreach (var host in _hosts.Values)
                if (_names.TryGetValue(host.Key, out var named)) host.Label = named;
            foreach (var service in _services.Values) service.Label = ServiceName(service.Key);

            return new TrafficSummary
            {
                Duration = duration,
                Packets = _packets,
                Bytes = _bytes,
                LocalAddress = LocalAddress,
                Protocols = Top(_protocols, l => l.Bytes),
                Hosts = Top(_hosts, l => l.Bytes),
                Services = Top(_services, l => l.Bytes),
                DnsQueries = Top(_dns, l => l.Packets),
            };
        }

        /// <summary>
        /// Une requête DNS : le nom demandé. Une réponse : les adresses obtenues, pour nommer les
        /// correspondants (« 142.250.75.238 » devient « www.google.com »).
        /// </summary>
        private string? Dns(byte[] packet, int start, int length, bool query)
        {
            if (start + 12 > length) return null;
            var questions = (packet[start + 4] << 8) | packet[start + 5];
            var answers = (packet[start + 6] << 8) | packet[start + 7];
            if (questions < 1) return null;
            var at = start + 12;
            var name = ReadName(packet, start, ref at, length);
            if (name == null || at + 4 > length) return null;
            at += 4;
            if (query)
            {
                Count(_dns, name, 0);
                return "Requête " + name;
            }

            string? first = null;
            for (var index = 0; index < answers && at + 12 <= length; index++)
            {
                if (ReadName(packet, start, ref at, length) == null || at + 10 > length) break;
                var type = (packet[at] << 8) | packet[at + 1];
                var size = (packet[at + 8] << 8) | packet[at + 9];
                at += 10;
                if (at + size > length) break;
                if (type == 1 && size == 4)
                {
                    var address = ToText(ToUInt(packet, at));
                    _names[address] = name;
                    first ??= address;
                }
                at += size;
            }
            return "Réponse " + name + (first != null ? " → " + first : string.Empty);
        }

        private static string? ReadName(byte[] packet, int message, ref int at, int length)
        {
            var parts = new List<string>();
            var jumped = false;
            var cursor = at;
            for (var guard = 0; guard < 64 && cursor < length; guard++)
            {
                var size = packet[cursor];
                if (size == 0)
                {
                    if (!jumped) at = cursor + 1;
                    return parts.Count == 0 ? null : string.Join(".", parts);
                }
                if ((size & 0xC0) == 0xC0)
                {
                    if (cursor + 1 >= length) return null;
                    var pointer = message + (((size & 0x3F) << 8) | packet[cursor + 1]);
                    if (!jumped) at = cursor + 2;
                    jumped = true;
                    cursor = pointer;
                    continue;
                }
                if (cursor + 1 + size > length) return null;
                parts.Add(Encoding.ASCII.GetString(packet, cursor + 1, size));
                cursor += 1 + size;
            }
            return null;
        }

        private static void Count(Dictionary<string, TrafficLine> lines, string key, long bytes)
        {
            if (!lines.TryGetValue(key, out var line)) lines[key] = line = new TrafficLine { Key = key };
            line.Bytes += bytes;
            line.Packets++;
        }

        private static string? ServiceName(string key)
        {
            var space = key.LastIndexOf(' ');
            if (space < 0 || !int.TryParse(key.Substring(space + 1), out var port)) return null;
            return port switch
            {
                443 => "HTTPS",
                80 => "HTTP",
                53 => "DNS",
                123 => "Heure (NTP)",
                445 => "Partage de fichiers",
                3389 => "Bureau à distance",
                5353 => "mDNS",
                1900 => "SSDP (UPnP)",
                137 => "NetBIOS",
                138 => "NetBIOS",
                67 => "DHCP",
                68 => "DHCP",
                993 => "IMAP sécurisé",
                587 => "SMTP (envoi)",
                465 => "SMTP sécurisé",
                995 => "POP3 sécurisé",
                3478 => "Visio (STUN)",
                5228 => "Google Play",
                5222 => "Messagerie (XMPP)",
                8080 => "HTTP (8080)",
                _ => null,
            };
        }

        private static int Port(byte[] packet, int offset) => (packet[offset] << 8) | packet[offset + 1];

        private static uint ToUInt(byte[] bytes, int offset)
            => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

        private static string ToText(uint value)
            => (value >> 24) + "." + ((value >> 16) & 0xFF) + "." + ((value >> 8) & 0xFF) + "." + (value & 0xFF);
    }

    /// <summary>
    /// Écrit des paquets IP bruts au format pcap (en-tête de 24 octets, puis 16 octets par
    /// paquet), type de lien « IP brut » : Wireshark l'ouvre tel quel.
    /// </summary>
    public sealed class PcapWriter : IDisposable
    {
        private const int LinkTypeRaw = 101;
        private readonly BinaryWriter _writer;

        public PcapWriter(Stream stream)
        {
            _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
            _writer.Write(0xA1B2C3D4u); // nombre magique, microsecondes
            _writer.Write((ushort)2);
            _writer.Write((ushort)4);
            _writer.Write(0);           // fuseau
            _writer.Write(0u);          // précision
            _writer.Write(65535u);      // longueur maximale capturée
            _writer.Write((uint)LinkTypeRaw);
        }

        public void Write(DateTime utc, byte[] packet, int length)
        {
            var since = utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var seconds = (uint)Math.Max(0, Math.Floor(since.TotalSeconds));
            var micro = (uint)((since.Ticks % TimeSpan.TicksPerSecond) / 10);
            _writer.Write(seconds);
            _writer.Write(micro);
            _writer.Write((uint)length);
            _writer.Write((uint)length);
            _writer.Write(packet, 0, length);
        }

        public void Dispose() => _writer.Dispose();
    }
}
