using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using Microsoft.Win32;

namespace LDI12.Actions.Backup
{
    public enum PrinterKind
    {
        /// <summary>Une imprimante partagée par un autre poste ou un serveur : « \\SERVEUR\Accueil ».</summary>
        Connection = 0,

        /// <summary>Une imprimante réseau déclarée par son adresse IP.</summary>
        Network = 1,

        /// <summary>USB, WSD : Windows la retrouve seul au branchement, une fois son pilote là.</summary>
        Local = 2,
    }

    public sealed class PrinterEntry
    {
        public PrinterKind Kind { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Driver { get; init; } = string.Empty;

        public string Port { get; init; } = string.Empty;

        /// <summary>L'adresse de l'imprimante réseau : « 192.168.1.20 ».</summary>
        public string? Host { get; init; }

        public bool IsDefault { get; init; }
    }

    public sealed class NetworkDrive
    {
        /// <summary>« Z: ».</summary>
        public string Letter { get; init; } = string.Empty;

        /// <summary>« \\SERVEUR\Partage ».</summary>
        public string Path { get; init; } = string.Empty;

        public string? User { get; init; }
    }

    public sealed class ProductKey
    {
        /// <summary>« Windows, clé installée », « Windows, clé de la carte mère ».</summary>
        public string Label { get; init; } = string.Empty;

        public string Key { get; init; } = string.Empty;
    }

    /// <summary>Ce qu'on retrouve d'une machine en plus de ses fichiers.</summary>
    public sealed class MachineSettingsRecord
    {
        public IReadOnlyList<PrinterEntry> Printers { get; init; } = Array.Empty<PrinterEntry>();

        public IReadOnlyList<NetworkDrive> Drives { get; init; } = Array.Empty<NetworkDrive>();

        public IReadOnlyList<ProductKey> Keys { get; init; } = Array.Empty<ProductKey>();

        public bool IsEmpty => Printers.Count == 0 && Drives.Count == 0 && Keys.Count == 0;
    }

    /// <summary>
    /// Imprimantes, lecteurs réseau et clés de produit : ce qu'on oublie de noter avant une
    /// réinstallation, et qu'on cherche ensuite pendant une heure.
    /// </summary>
    /// <remarks>
    /// <b>Relevé dans la session du client.</b> Les lecteurs réseau et les imprimantes partagées
    /// sont propres à son compte. Tout est noté dans un fichier lisible de la sauvegarde, et repris
    /// dans la fiche de réinstallation.
    /// <para>
    /// À la restauration, les lecteurs réseau sont remis dans la session ; les imprimantes, qui
    /// demandent les droits administrateur pour leur port, sont réinstallées après les pilotes.
    /// Les imprimantes USB n'ont besoin que de leur pilote : Windows les retrouve au branchement.
    /// </para>
    /// </remarks>
    public static class MachineSettings
    {
        public const string FileName = "reglages-machine.txt";

        /// <summary>Ports des imprimantes virtuelles : PDF, XPS, OneNote, télécopie. Elles reviennent avec Windows.</summary>
        private static readonly HashSet<string> VirtualPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PORTPROMPT:", "nul:", "SHRFAX:", "XPSPort:", "FILE:", "Microsoft.Office.OneNote_16",
        };

        private const string KeyChars = "BCDFGHJKMPQRTVWXY2346789";

        // ------------------------------------------------------------------ relevé

        public static async Task<MachineSettingsRecord> ReadAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var printers = new List<PrinterEntry>();
            var keys = new List<ProductKey>();

            var installed = Decode(context.Registry.ReadBinary(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DigitalProductId", RegistryView.Registry64));
            if (installed != null) keys.Add(new ProductKey { Label = "Windows, clé installée", Key = installed });

            var run = await context.Processes.RunAsync(PowerShell(Script, TimeSpan.FromMinutes(1)), cancellationToken)
                .ConfigureAwait(false);

            if (run.Completed)
            {
                var parsed = Parse(run.StandardOutput);
                printers.AddRange(parsed.Printers);
                foreach (var key in parsed.Keys)
                    if (!keys.Exists(known => known.Key == key.Key)) keys.Add(key);
            }

            return new MachineSettingsRecord { Printers = printers, Drives = Drives(context.Registry), Keys = keys };
        }

        /// <summary>
        /// Le relevé des imprimantes et de la clé de la carte mère, une ligne par élément.
        /// </summary>
        /// <remarks>
        /// <c>Get-Printer</c> existe depuis Windows 8 ; plus ancien, le script ne rend que la clé.
        /// La sortie est en UTF-8 : un nom d'imprimante accentué passe intact.
        /// </remarks>
        internal const string Script =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
            "$d = ''\n" +
            "try { $d = (Get-CimInstance Win32_Printer -Filter 'Default=TRUE' -ErrorAction Stop).Name } catch { }\n" +
            "try {\n" +
            "  Get-Printer -ErrorAction Stop | ForEach-Object {\n" +
            "    $h = ''\n" +
            "    if ([string]$_.Type -eq 'Local') {\n" +
            "      $port = Get-PrinterPort -Name $_.PortName -ErrorAction SilentlyContinue\n" +
            "      if ($port -and $port.PrinterHostAddress) { $h = $port.PrinterHostAddress }\n" +
            "    }\n" +
            "    'P' + \"`t\" + [string]$_.Type + \"`t\" + $_.Name + \"`t\" + $_.DriverName + \"`t\" + $_.PortName + \"`t\" + $h + \"`t\" + [int]($_.Name -eq $d)\n" +
            "  }\n" +
            "} catch { }\n" +
            "try { $k = (Get-CimInstance SoftwareLicensingService -ErrorAction Stop).OA3xOriginalProductKey; if ($k) { 'K' + \"`t\" + $k } } catch { }\n";

        internal static MachineSettingsRecord Parse(string output)
        {
            var printers = new List<PrinterEntry>();
            var keys = new List<ProductKey>();

            foreach (var raw in output.Split('\n'))
            {
                var fields = raw.TrimEnd('\r').TrimStart('\uFEFF').Split('\t');
                if (fields[0] == "K" && fields.Length >= 2 && fields[1].Trim().Length > 0)
                {
                    keys.Add(new ProductKey { Label = "Windows, clé de la carte mère", Key = fields[1].Trim() });
                    continue;
                }

                if (fields[0] != "P" || fields.Length < 7) continue;

                var name = fields[2].Trim();
                var port = fields[4].Trim();
                if (name.Length == 0 || VirtualPorts.Contains(port) ||
                    name.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Fax", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var host = fields[5].Trim();
                printers.Add(new PrinterEntry
                {
                    Kind = string.Equals(fields[1].Trim(), "Connection", StringComparison.OrdinalIgnoreCase)
                        ? PrinterKind.Connection
                        : host.Length > 0 ? PrinterKind.Network : PrinterKind.Local,
                    Name = name,
                    Driver = fields[3].Trim(),
                    Port = port,
                    Host = host.Length > 0 ? host : null,
                    IsDefault = fields[6].Trim() == "1",
                });
            }

            return new MachineSettingsRecord { Printers = printers, Keys = keys };
        }

        /// <summary>Les lecteurs réseau du compte, tels qu'il les retrouve à chaque ouverture de session.</summary>
        internal static IReadOnlyList<NetworkDrive> Drives(IRegistryGateway registry)
        {
            var drives = new List<NetworkDrive>();
            foreach (var letter in registry.GetSubKeyNames(RegistryHive.CurrentUser, "Network"))
            {
                if (letter.Length != 1 || !char.IsLetter(letter[0])) continue;
                var path = registry.ReadString(RegistryHive.CurrentUser, @"Network\" + letter, "RemotePath");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var user = registry.ReadString(RegistryHive.CurrentUser, @"Network\" + letter, "UserName");
                drives.Add(new NetworkDrive
                {
                    Letter = char.ToUpperInvariant(letter[0]) + ":",
                    Path = path!.Trim(),
                    User = string.IsNullOrWhiteSpace(user) ? null : user!.Trim(),
                });
            }

            drives.Sort((a, b) => string.CompareOrdinal(a.Letter, b.Letter));
            return drives;
        }

        /// <summary>
        /// La clé de produit inscrite dans <c>DigitalProductId</c>.
        /// </summary>
        /// <remarks>
        /// L'algorithme connu depuis Windows XP, avec la variante de Windows 8 et suivants, où la
        /// lettre N marque une position. Une machine activée par licence numérique, sans clé saisie,
        /// rend une clé générique : elle est notée telle quelle, la licence étant liée au matériel.
        /// </remarks>
        public static string? Decode(byte[]? digitalProductId)
        {
            const int offset = 52;
            if (digitalProductId == null || digitalProductId.Length < offset + 15) return null;

            var id = (byte[])digitalProductId.Clone();
            var isWin8 = (id[66] / 6) & 1;
            id[66] = (byte)((id[66] & 0xF7) | ((isWin8 & 2) * 4));

            var key = new StringBuilder();
            var last = 0;
            for (var position = 24; position >= 0; position--)
            {
                var current = 0;
                for (var index = 14; index >= 0; index--)
                {
                    current = current * 256 + id[index + offset];
                    id[index + offset] = (byte)(current / 24);
                    current %= 24;
                    last = current;
                }

                key.Insert(0, KeyChars[current]);
            }

            var text = key.ToString();
            if (isWin8 == 1)
            {
                var rest = text.Substring(1);
                text = rest.Substring(0, last) + "N" + rest.Substring(last);
            }

            if (text.Trim('B').Length == 0) return null;

            var dashed = new StringBuilder();
            for (var index = 0; index < 25; index++)
            {
                if (index > 0 && index % 5 == 0) dashed.Append('-');
                dashed.Append(text[index]);
            }

            return dashed.ToString();
        }

        /// <summary>« 0A 1B … » ou « 0A1B… », tel que <c>reg query</c> rend un REG_BINARY.</summary>
        internal static byte[]? Hex(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var compact = text!.Replace(" ", string.Empty).Trim();
            if (compact.Length % 2 != 0) return null;

            var bytes = new byte[compact.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
                if (!byte.TryParse(compact.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
                    return null;
            return bytes;
        }

        // ------------------------------------------------------------------ fichier de la sauvegarde

        public static string Document(MachineSettingsRecord record)
        {
            var builder = new StringBuilder();
            builder.Append("# Réglages de la machine, relevés avec la sauvegarde. Une ligne par élément, champs séparés par des tabulations.\r\n");
            foreach (var printer in record.Printers)
                builder.Append("imprimante\t").Append(KindWord(printer.Kind)).Append('\t').Append(Flat(printer.Name)).Append('\t')
                    .Append(Flat(printer.Driver)).Append('\t').Append(Flat(printer.Port)).Append('\t').Append(Flat(printer.Host))
                    .Append('\t').Append(printer.IsDefault ? "par-defaut" : string.Empty).Append("\r\n");
            foreach (var drive in record.Drives)
                builder.Append("lecteur\t").Append(drive.Letter).Append('\t').Append(Flat(drive.Path)).Append('\t').Append(Flat(drive.User)).Append("\r\n");
            foreach (var key in record.Keys)
                builder.Append("licence\t").Append(Flat(key.Label)).Append('\t').Append(Flat(key.Key)).Append("\r\n");
            return builder.ToString();
        }

        public static MachineSettingsRecord Read(string? document)
        {
            var printers = new List<PrinterEntry>();
            var drives = new List<NetworkDrive>();
            var keys = new List<ProductKey>();
            if (document == null) return new MachineSettingsRecord();

            foreach (var raw in document.Split('\n'))
            {
                var fields = raw.TrimEnd('\r').TrimStart('\uFEFF').Split('\t');
                switch (fields[0])
                {
                    case "imprimante" when fields.Length >= 7 && fields[2].Length > 0:
                        printers.Add(new PrinterEntry
                        {
                            Kind = fields[1] == "partagee" ? PrinterKind.Connection : fields[1] == "reseau" ? PrinterKind.Network : PrinterKind.Local,
                            Name = fields[2],
                            Driver = fields[3],
                            Port = fields[4],
                            Host = fields[5].Length > 0 ? fields[5] : null,
                            IsDefault = fields[6] == "par-defaut",
                        });
                        break;

                    case "lecteur" when fields.Length >= 3 && fields[1].Length == 2 && fields[1][1] == ':' && char.IsLetter(fields[1][0]) &&
                                        fields[2].StartsWith(@"\\", StringComparison.Ordinal):
                        drives.Add(new NetworkDrive
                        {
                            Letter = fields[1].ToUpperInvariant(),
                            Path = fields[2],
                            User = fields.Length > 3 && fields[3].Length > 0 ? fields[3] : null,
                        });
                        break;

                    case "licence" when fields.Length >= 3:
                        keys.Add(new ProductKey { Label = fields[1], Key = fields[2] });
                        break;
                }
            }

            return new MachineSettingsRecord { Printers = printers, Drives = drives, Keys = keys };
        }

        public static string Describe(PrinterEntry printer)
            => printer.Name + (printer.Kind switch
            {
                PrinterKind.Connection => ", partagée",
                PrinterKind.Network => ", réseau " + printer.Host,
                _ => ", " + printer.Port,
            }) + (printer.IsDefault ? ", par défaut" : string.Empty);

        private static string KindWord(PrinterKind kind)
            => kind == PrinterKind.Connection ? "partagee" : kind == PrinterKind.Network ? "reseau" : "locale";

        private static string Flat(string? value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        /// <summary>Un script PowerShell dont la sortie est lue en UTF-8.</summary>
        internal static ProcessRequest PowerShell(string script, TimeSpan timeout)
        {
            var request = OpenFilesAction.PowerShell(script, timeout);
            return new ProcessRequest(request.FileName, request.Arguments) { Timeout = timeout, OutputEncoding = ConsoleOutputEncoding.Utf8 };
        }

        /// <summary>Une chaîne PowerShell entre apostrophes : seules les apostrophes se doublent.</summary>
        internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
