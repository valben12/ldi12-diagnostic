using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace LDI12.Core.Network
{
    /// <summary>Ce qu'un appareil du réseau semble être, d'après ce qu'il expose.</summary>
    public enum LanDeviceKind
    {
        Unknown,
        ThisComputer,
        Router,
        Computer,
        Printer,
        Storage,
        Phone,
        MediaPlayer,
        Camera,
        WebDevice,
    }

    /// <summary>Un appareil trouvé sur le réseau local.</summary>
    public sealed class LanDevice
    {
        public string Address { get; init; } = string.Empty;

        /// <summary>L'adresse matérielle, « 3C:52:82:1A:2B:3C », quand l'appareil est sur le même segment.</summary>
        public string? Mac { get; set; }

        public string? HostName { get; set; }

        /// <summary>Le nom Windows (NetBIOS) : « BUREAU-MARIE ».</summary>
        public string? NetBiosName { get; set; }

        public string? Workgroup { get; set; }

        /// <summary>Le fabricant de la carte réseau, d'après la base IEEE quand elle est disponible.</summary>
        public string? Vendor { get; set; }

        public int? RoundTripMs { get; set; }

        public List<int> OpenPorts { get; } = new List<int>();

        /// <summary>Les dossiers partagés visibles.</summary>
        public List<string> Shares { get; } = new List<string>();

        public bool IsGateway { get; set; }

        public bool IsSelf { get; set; }

        public LanDeviceKind Kind { get; set; }

        /// <summary>Adresse MAC aléatoire (bit « administrée localement ») : un téléphone ou une tablette récents.</summary>
        public bool HasRandomMac => Mac != null && Mac.Length >= 2 &&
                                    int.TryParse(Mac.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && (first & 0x02) != 0;

        public string Name => NetBiosName ?? HostName ?? Address;

        /// <summary>Tri naturel des adresses : 192.168.1.2 avant 192.168.1.10.</summary>
        public long SortKey
        {
            get
            {
                if (!IPAddress.TryParse(Address, out var ip)) return long.MaxValue;
                var bytes = ip.GetAddressBytes();
                return bytes.Length == 4 ? ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3] : long.MaxValue;
            }
        }
    }

    /// <summary>Ce qu'un appareil est, d'après ses ports, son nom et son fabricant.</summary>
    public static class LanClassifier
    {
        /// <summary>Les ports essayés sur chaque appareil : ceux qui disent ce qu'il est et ce qu'on peut en faire.</summary>
        public static readonly int[] Ports = { 80, 443, 445, 139, 3389, 22, 21, 9100, 631, 515, 5000, 5001, 8080, 62078, 8009, 554, 5900 };

        public static LanDeviceKind Classify(LanDevice device)
        {
            if (device.IsSelf) return LanDeviceKind.ThisComputer;
            if (device.IsGateway) return LanDeviceKind.Router;
            var ports = new HashSet<int>(device.OpenPorts);
            var vendor = device.Vendor ?? string.Empty;
            bool Vendor(params string[] names) => names.Any(n => vendor.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

            if (ports.Contains(9100) || ports.Contains(631) || ports.Contains(515) ||
                Vendor("Brother", "Canon", "Epson", "Seiko Epson", "Lexmark", "Kyocera", "Xerox", "Ricoh", "Konica")) return LanDeviceKind.Printer;
            if (ports.Contains(5000) || ports.Contains(5001) || Vendor("Synology", "QNAP", "Western Digital", "Buffalo")) return LanDeviceKind.Storage;
            if (ports.Contains(62078)) return LanDeviceKind.Phone;
            if (ports.Contains(8009) || Vendor("Sonos", "Roku")) return LanDeviceKind.MediaPlayer;
            if (ports.Contains(554) || Vendor("Hikvision", "Dahua", "Axis")) return LanDeviceKind.Camera;
            if (ports.Contains(3389) || ports.Contains(445) || ports.Contains(139) || device.NetBiosName != null) return LanDeviceKind.Computer;
            if (device.HasRandomMac) return LanDeviceKind.Phone;
            if (ports.Contains(80) || ports.Contains(443) || ports.Contains(8080)) return LanDeviceKind.WebDevice;
            return LanDeviceKind.Unknown;
        }

        public static string Describe(LanDeviceKind kind) => kind switch
        {
            LanDeviceKind.ThisComputer => "Ce PC",
            LanDeviceKind.Router => "Box ou routeur",
            LanDeviceKind.Computer => "Ordinateur",
            LanDeviceKind.Printer => "Imprimante",
            LanDeviceKind.Storage => "Stockage réseau (NAS)",
            LanDeviceKind.Phone => "Téléphone ou tablette",
            LanDeviceKind.MediaPlayer => "Télévision, enceinte, Chromecast",
            LanDeviceKind.Camera => "Caméra",
            LanDeviceKind.WebDevice => "Appareil avec interface web",
            _ => "Appareil",
        };

        public static string PortName(int port) => port switch
        {
            80 => "web",
            443 => "web sécurisé",
            445 => "partage de fichiers",
            139 => "NetBIOS",
            3389 => "bureau à distance",
            22 => "SSH",
            21 => "FTP",
            9100 => "impression directe",
            631 => "IPP",
            515 => "LPD",
            5000 => "Synology",
            5001 => "Synology sécurisé",
            8080 => "web (8080)",
            62078 => "Apple",
            8009 => "Cast",
            554 => "flux vidéo",
            5900 => "VNC",
            _ => port.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Les adresses à interroger sur un réseau : toutes celles de la plage, sans l'adresse du
        /// réseau ni celle de diffusion. Au-delà de 1 024, seules les 1 024 autour de la machine.
        /// </summary>
        public static IReadOnlyList<IPAddress> Range(IPAddress address, int prefixLength)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return Array.Empty<IPAddress>();
            prefixLength = Math.Max(22, Math.Min(30, prefixLength));
            var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            var network = value & mask;
            var broadcast = network | ~mask;

            var list = new List<IPAddress>();
            for (var host = network + 1; host < broadcast; host++)
                list.Add(new IPAddress(new[] { (byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host }));
            return list;
        }
    }

    /// <summary>
    /// La base des fabricants de cartes réseau publiée par l'IEEE (oui.csv) : les trois premiers
    /// octets d'une adresse MAC désignent le fabricant.
    /// </summary>
    public sealed class OuiDatabase
    {
        private readonly Dictionary<string, string> _vendors;

        private OuiDatabase(Dictionary<string, string> vendors) => _vendors = vendors;

        public int Count => _vendors.Count;

        /// <summary>« Registry,Assignment,Organization Name,Organization Address » : une ligne par préfixe.</summary>
        public static OuiDatabase Parse(TextReader reader)
        {
            var vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var fields = SplitCsv(line);
                if (fields.Count < 3 || fields[1].Length != 6 || fields[1].Equals("Assignment", StringComparison.OrdinalIgnoreCase)) continue;
                var name = fields[2].Trim();
                if (name.Length > 0) vendors[fields[1].ToUpperInvariant()] = name;
            }
            return new OuiDatabase(vendors);
        }

        public string? Find(string? mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            var hex = new string(mac!.Where(Uri.IsHexDigit).ToArray());
            return hex.Length >= 6 && _vendors.TryGetValue(hex.Substring(0, 6), out var vendor) ? vendor : null;
        }

        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var quoted = false;
            for (var index = 0; index < line.Length; index++)
            {
                var c = line[index];
                if (c == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }
    }

    /// <summary>Une connexion ouverte par un programme de ce PC.</summary>
    public sealed class ConnectionEntry
    {
        public string Protocol { get; init; } = "TCP";
        public string LocalAddress { get; init; } = string.Empty;
        public int LocalPort { get; init; }
        public string RemoteAddress { get; init; } = string.Empty;
        public int RemotePort { get; init; }

        /// <summary>« établie », « en écoute »… ; vide pour UDP.</summary>
        public string State { get; init; } = string.Empty;

        public int ProcessId { get; init; }
    }
}
