using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>D'où vient un port série, ce qui change tout au dépannage.</summary>
    public enum SerialPortKind
    {
        Unknown = 0,

        /// <summary>Port physique de la carte mère. Devenu rare, et le plus fiable qui soit.</summary>
        Native = 1,

        /// <summary>Convertisseur USB vers série. La quasi-totalité de ce qu'on branche aujourd'hui.</summary>
        UsbAdapter = 2,

        Bluetooth = 3,

        /// <summary>Port créé par un logiciel : émulateur, machine virtuelle, pilote de terminal.</summary>
        Virtual = 4,
    }

    /// <summary>Un port série vu par Windows.</summary>
    public sealed class SerialPortInfo
    {
        /// <summary>Nom complet tel que Windows l'affiche : « USB Serial Port (COM3) ».</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>« COM3 », quand Windows a pu attribuer un numéro.</summary>
        public string? PortName { get; init; }

        public string? Manufacturer { get; init; }

        public SerialPortKind Kind { get; init; }

        /// <summary>Code du Gestionnaire de périphériques. 0 = le port fonctionne.</summary>
        public int ProblemCode { get; init; }

        public string? ProblemLabel { get; init; }

        public bool Usable => ProblemCode == 0 && PortName != null;
    }

    /// <summary>
    /// Les ports série de la machine, et les numéros que Windows garde en réserve.
    /// </summary>
    /// <remarks>
    /// <b>Une projection : rien n'est mesuré ici que la sonde des périphériques n'ait déjà
    /// relevé.</b> Les ports série sont des périphériques PnP comme les autres ; ce qui manquait
    /// n'était pas leur existence mais leur lecture : personne ne va chercher un port série dans
    /// une liste de deux cents périphériques.
    /// <para>
    /// La réserve de numéros, elle, vient du registre. Windows y note chaque numéro attribué et
    /// ne le rend jamais : un convertisseur USB rebranché sur un autre port prend le numéro
    /// suivant, et après une dizaine de manipulations l'appareil est sur COM13, que le logiciel
    /// du client, souvent, n'accepte pas.
    /// </para>
    /// </remarks>
    public sealed class SerialPortsInfo
    {
        /// <summary>Numéros de port déjà attribués et jamais rendus, présents ou non.</summary>
        public IReadOnlyList<int> Reserved { get; init; } = Array.Empty<int>();

        public Measured<int> ReservedCount { get; init; }
    }

    /// <summary>Assemble la vue des ports série à partir de ce qui a déjà été relevé.</summary>
    public static class SerialPorts
    {
        /// <summary>Classe de périphériques que Windows attribue aux ports série.</summary>
        private const string PortsClass = "Ports";

        /// <summary>
        /// Identifiants des puces de conversion USB-série les plus répandues.
        /// </summary>
        /// <remarks>
        /// Servent à reconnaître un convertisseur <b>dont le pilote manque</b> : sans pilote,
        /// Windows ne lui donne ni classe ni nom utilisable, et il se perd dans la liste des
        /// périphériques inconnus. Or c'est précisément le cas où le client dit « mon appareil
        /// n'est pas reconnu ».
        /// </remarks>
        private static readonly (string Vendor, string Name)[] KnownAdapters =
        {
            ("VID_0403", "FTDI"),
            ("VID_067B", "Prolific"),
            ("VID_10C4", "Silicon Labs CP210x"),
            ("VID_1A86", "WCH CH340"),
            ("VID_2341", "Arduino"),
            ("VID_16C0", "Teensy"),
        };

        public static IReadOnlyList<SerialPortInfo> Build(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var ports = new List<SerialPortInfo>();

            foreach (var device in snapshot.Windows.Devices)
            {
                var isPort = string.Equals(device.DeviceClass, PortsClass, StringComparison.OrdinalIgnoreCase);
                var adapter = MatchAdapter(device.HardwareId);

                // Un convertisseur sans pilote n'a pas de classe : il n'est ni dans les ports ni
                // nulle part. Le retenir sur son identifiant matériel est le seul moyen de le
                // montrer, et c'est le cas qui amène le client à l'atelier.
                if (!isPort && adapter == null) continue;

                ports.Add(new SerialPortInfo
                {
                    Name = device.Name,
                    PortName = ExtractPort(device.Name),
                    Manufacturer = string.IsNullOrWhiteSpace(device.Manufacturer) ? adapter : device.Manufacturer,
                    Kind = Classify(device, adapter != null),
                    ProblemCode = device.ProblemCode,
                    ProblemLabel = device.ProblemLabel,
                });
            }

            return ports;
        }

        /// <summary>
        /// Numéros réservés qu'aucun port présent n'utilise.
        /// </summary>
        /// <remarks>
        /// C'est le chiffre qui explique la dérive : dix numéros réservés pour un seul appareil
        /// branché signifie que le prochain branchement donnera COM11.
        /// </remarks>
        public static IReadOnlyList<int> Orphaned(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var used = new HashSet<int>();
            foreach (var port in Build(snapshot))
            {
                var number = Number(port.PortName);
                if (number > 0) used.Add(number);
            }

            var orphaned = new List<int>();
            foreach (var reserved in snapshot.Windows.SerialPorts.Reserved)
                if (!used.Contains(reserved)) orphaned.Add(reserved);

            return orphaned;
        }

        /// <summary>« COM3 » extrait de « USB Serial Port (COM3) ».</summary>
        private static string? ExtractPort(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var open = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return null;

            var close = name.IndexOf(')', open);
            if (close < 0) return null;

            return name.Substring(open + 1, close - open - 1);
        }

        public static int Number(string? portName)
        {
            if (string.IsNullOrEmpty(portName)) return 0;
            if (!portName!.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return 0;

            return int.TryParse(portName.Substring(3), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        private static string? MatchAdapter(string? hardwareId)
        {
            if (string.IsNullOrWhiteSpace(hardwareId)) return null;

            foreach (var (vendor, name) in KnownAdapters)
                if (hardwareId!.IndexOf(vendor, StringComparison.OrdinalIgnoreCase) >= 0) return name;

            return null;
        }

        private static SerialPortKind Classify(DeviceInfo device, bool knownAdapter)
        {
            var id = device.HardwareId ?? string.Empty;
            var name = device.Name ?? string.Empty;

            if (knownAdapter || id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("FTDIBUS", StringComparison.OrdinalIgnoreCase))
                return SerialPortKind.UsbAdapter;

            if (id.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
                return SerialPortKind.Bluetooth;

            // ACPI\PNP0501 est le port série d'une carte mère. Tout le reste qui n'est ni USB ni
            // Bluetooth vient d'un logiciel : émulateur, machine virtuelle, pilote de terminal.
            if (id.IndexOf("PNP0501", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
                return SerialPortKind.Native;

            return id.Length == 0 ? SerialPortKind.Unknown : SerialPortKind.Virtual;
        }
    }
}
