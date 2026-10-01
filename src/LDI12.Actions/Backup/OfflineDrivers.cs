using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Ce qu'un fichier .inf dit de lui-même.</summary>
    public sealed class InfSummary
    {
        public string? DeviceClass { get; init; }

        public string? Provider { get; init; }

        public string? Version { get; init; }

        /// <summary>Le premier périphérique qu'il déclare : « NVIDIA GeForce RTX 3060 ».</summary>
        public string? Device { get; init; }

        /// <summary>Les bus des matériels qu'il équipe, d'après leurs identifiants : « USB », « PCI », « HDAUDIO ».</summary>
        public IReadOnlyCollection<string> Buses { get; init; } = Array.Empty<string>();

        /// <summary>Périphérique ou propre à ce PC.</summary>
        public DriverKind Kind => DriverKinds.Classify(DeviceClass, Buses);

        /// <summary>Ce que le technicien lit sur la case.</summary>
        public string Label(string fallback)
            => Device ?? (Provider != null ? Provider + (DeviceClass != null ? " · " + DeviceClass : string.Empty) : fallback);
    }

    /// <summary>
    /// Les pilotes tiers d'un Windows qui ne tourne pas, choisis un par un.
    /// </summary>
    /// <remarks>
    /// <b>Lus sur le disque, sans rien lancer.</b> Windows garde une copie de chaque pilote tiers
    /// ajouté à son magasin sous <c>Windows\INF\oemNN.inf</c> : c'est la liste à cocher, avec ce
    /// que chaque fichier dit de lui-même (fabricant, classe, version, premier périphérique). Ces
    /// fichiers se lisent sans droits particuliers.
    /// <para>
    /// Le paquet complet, lui, est dans <c>System32\DriverStore\FileRepository</c>, dans un dossier
    /// que le registre SYSTEM de ce Windows désigne. Il est copié et vérifié comme n'importe quel
    /// dossier de la sauvegarde, sous <c>Pilotes\oemNN</c> : la réinstallation le reprend tel
    /// quel, et une sauvegarde interrompue le reprend avec le reste.
    /// </para>
    /// </remarks>
    public static class OfflineDrivers
    {
        /// <summary>Le paramètre qui demande tous les pilotes, sans choix.</summary>
        public const string All = "1";

        public static IReadOnlyList<DriverChoice> List(IFileSystemGateway files, string volume)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));

            var result = new List<DriverChoice>();
            var folder = Path.Combine(volume, @"Windows\INF");
            var scan = files.Scan(new DirectoryScanRequest(folder) { TopLevelOnly = true, MaxFiles = 20000 }, CancellationToken.None);
            if (!scan.HasValue) return result;

            foreach (var file in scan.Value.Files)
            {
                var name = Path.GetFileName(file.Path);
                if (!DriverBackup.IsThirdParty(name)) continue;

                var text = files.ReadText(file.Path);
                var summary = text.HasValue ? Parse(text.Value) : new InfSummary();
                result.Add(new DriverChoice
                {
                    InfName = name.ToLowerInvariant(),
                    Label = summary.Label(name),
                    DeviceClass = summary.DeviceClass,
                    Manufacturer = summary.Provider,
                    Version = summary.Version,
                    Kind = summary.Kind,
                });
            }

            DriverKinds.Sort(result);
            return result;
        }

        /// <summary>
        /// Le résumé d'un .inf : sections [Version], [Manufacturer], le premier modèle, et [Strings]
        /// pour traduire les « %Nom% ».
        /// </summary>
        public static InfSummary Parse(string text)
        {
            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string>? current = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = StripComment(raw.TrimEnd('\r')).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    var name = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.TryGetValue(name, out current)) sections[name] = current = new List<string>();
                    continue;
                }

                current?.Add(line);
            }

            // [Strings] d'abord, puis la langue du système quand elle est là : on garde l'anglais neutre.
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sections.TryGetValue("Strings", out var stringLines))
                foreach (var line in stringLines)
                {
                    var (key, value) = Pair(line);
                    if (key != null) strings[key] = Unquote(value);
                }

            string? Resolve(string? value)
            {
                if (value == null) return null;
                value = Unquote(value.Trim());
                if (value.Length > 2 && value[0] == '%' && value[value.Length - 1] == '%')
                    return strings.TryGetValue(value.Substring(1, value.Length - 2), out var resolved) ? resolved : null;
                return value.Length == 0 ? null : value;
            }

            string? deviceClass = null, provider = null, version = null, device = null;
            if (sections.TryGetValue("Version", out var versionLines))
                foreach (var line in versionLines)
                {
                    var (key, value) = Pair(line);
                    if (key == null) continue;
                    if (key.Equals("Class", StringComparison.OrdinalIgnoreCase)) deviceClass = Resolve(value);
                    else if (key.Equals("Provider", StringComparison.OrdinalIgnoreCase)) provider = Resolve(value);
                    else if (key.Equals("DriverVer", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = value.IndexOf(',');
                        version = comma >= 0 ? value.Substring(comma + 1).Trim() : null;
                    }
                }

            // [Manufacturer] : « %Fab% = Section, NTamd64 » ; le premier modèle de « Section.NTamd64 »
            // ou de « Section » donne le premier périphérique. Chaque modèle (« %Desc% = Install,
            // USB\VID_046D&PID_C52B ») nomme aussi le matériel qu'il équipe : son bus dit si c'est
            // un périphérique qu'on branche ou un composant de la machine.
            var buses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sections.TryGetValue("Manufacturer", out var manufacturers))
                foreach (var line in manufacturers)
                {
                    var (_, value) = Pair(line);
                    var parts = (value ?? line).Split(',');
                    var section = parts[0].Trim();
                    if (section.Length == 0) continue;

                    var candidates = new List<string>();
                    for (var index = 1; index < parts.Length; index++) candidates.Add(section + "." + parts[index].Trim());
                    candidates.Add(section);

                    foreach (var candidate in candidates)
                    {
                        if (!sections.TryGetValue(candidate, out var models) || models.Count == 0) continue;
                        if (device == null)
                        {
                            var (description, _) = Pair(models[0]);
                            device = Resolve(description);
                        }

                        foreach (var model in models)
                        {
                            var (_, install) = Pair(model);
                            var ids = install.Split(',');
                            for (var index = 1; index < ids.Length; index++)
                            {
                                var id = ids[index].Trim();
                                var slash = id.IndexOf('\\');
                                if (slash <= 0) continue;
                                var bus = id.Substring(0, slash).ToUpperInvariant();

                                // HID : une souris ou un clavier qu'on branche porte l'identifiant
                                // de son fabricant USB (« VID_ ») ; le pavé tactile d'un portable, non.
                                if (bus == "HID") bus = id.IndexOf("VID", StringComparison.OrdinalIgnoreCase) >= 0 ? "HID-VID" : "HID";
                                buses.Add(bus);
                            }
                        }
                    }
                }

            return new InfSummary { DeviceClass = deviceClass, Provider = provider, Version = version, Device = device, Buses = buses };
        }

        /// <summary>
        /// Les dossiers du magasin de pilotes, par nom publié, d'après une sortie de
        /// <c>reg query …\DriverDatabase\DriverInfFiles /s</c>.
        /// </summary>
        /// <remarks>
        /// Chaque clé « oemNN.inf » liste les paquets qui ont porté ce nom ; la valeur « Active »
        /// désigne celui qui est en service. Le nom de la valeur par défaut dépend de la langue
        /// (« (par défaut) »), son type non : c'est lui qui la fait reconnaître.
        /// </remarks>
        internal static Dictionary<string, string> ParsePackages(string output)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? inf = null, active = null, listed = null;

            void Flush()
            {
                var package = active ?? listed;
                if (inf != null && package != null && DriverBackup.IsThirdParty(inf) && Safe(package)) result[inf] = package;
                active = listed = null;
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    var slash = line.LastIndexOf('\\');
                    inf = slash >= 0 ? line.Substring(slash + 1).Trim() : null;
                    continue;
                }

                var parts = line.Trim().Split(new[] { "    " }, 3, StringSplitOptions.None);
                if (parts.Length != 3) continue;

                if (parts[0].Equals("Active", StringComparison.OrdinalIgnoreCase) && parts[1] == "REG_SZ") active = parts[2].Trim();
                else if (parts[1] == "REG_MULTI_SZ")
                {
                    var entries = parts[2].Split(new[] { "\\0" }, StringSplitOptions.RemoveEmptyEntries);
                    if (entries.Length > 0) listed = entries[entries.Length - 1].Trim();
                }
            }

            Flush();
            return result;
        }

        /// <summary>Le dossier du paquet dans le magasin de pilotes de ce volume.</summary>
        public static string Repository(string volume, string package)
            => Path.Combine(volume, @"Windows\System32\DriverStore\FileRepository", package);

        /// <summary>Un nom de dossier, rien qui sorte du magasin.</summary>
        private static bool Safe(string package)
            => package.Length > 0 && package.IndexOfAny(new[] { '\\', '/', ':', '"' }) < 0 && package != "." && package != "..";

        private static (string? Key, string Value) Pair(string line)
        {
            var equals = line.IndexOf('=');
            return equals <= 0 ? (null, line) : (line.Substring(0, equals).Trim(), line.Substring(equals + 1).Trim());
        }

        private static string Unquote(string value)
            => value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2) : value;

        /// <summary>Le « ; » ouvre un commentaire, sauf entre guillemets.</summary>
        private static string StripComment(string line)
        {
            var quoted = false;
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '"') quoted = !quoted;
                else if (line[index] == ';' && !quoted) return line.Substring(0, index);
            }

            return line;
        }
    }
}
