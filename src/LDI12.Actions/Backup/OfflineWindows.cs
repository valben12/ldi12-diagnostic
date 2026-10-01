using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    /// <summary>Ce qu'on sait d'un Windows dont on sauvegarde un compte.</summary>
    public sealed class OfflineWindowsInfo
    {
        /// <summary>Le nom de la machine, tel que ce Windows le portait. Nul s'il n'a pas pu être lu.</summary>
        public string? MachineName { get; init; }

        /// <summary>« Windows 10 Famille, build 19045 ».</summary>
        public string? Description { get; init; }

        public SoftwareInventory? Software { get; init; }

        /// <summary>Le dossier du magasin de pilotes de chaque pilote tiers, par nom publié (« oem12.inf »).</summary>
        public IReadOnlyDictionary<string, string> DriverPackages { get; init; } = new Dictionary<string, string>();

        /// <summary>La clé de produit inscrite dans le registre de ce Windows ; nulle pour une licence numérique.</summary>
        public string? ProductKey { get; init; }

        /// <summary>Pourquoi le registre n'a pas pu être lu, quand il ne l'a pas été.</summary>
        public string? Failure { get; init; }
    }

    /// <summary>
    /// Lit le registre d'un Windows qui ne tourne pas : le disque d'un PC en panne, branché à
    /// l'atelier.
    /// </summary>
    /// <remarks>
    /// Le nom de la machine nomme la sauvegarde et permet de la reprendre ; la liste des logiciels
    /// remplit la fiche de réinstallation, comme pour la machine qu'on a sous la main. Les deux
    /// viennent des fichiers du registre de ce Windows (SYSTEM et SOFTWARE), chargés le temps de la
    /// lecture par <c>reg load</c>, qui demande les droits administrateur, puis toujours déchargés.
    /// <para>
    /// Si le disque désigné porte le Windows en cours d'exécution (un autre compte de ce PC), son
    /// registre est déjà chargé : il est lu directement. Un registre abîmé, sur un disque qui
    /// lâche, ne se charge pas : la sauvegarde se fait quand même, la fiche le dit.
    /// </para>
    /// </remarks>
    public static class OfflineWindows
    {
        /// <summary>Vrai si ce volume porte un Windows : son registre est là.</summary>
        public static bool IsWindows(IFileSystemGateway files, string volume)
            => files.FileExists(Path.Combine(volume, @"Windows\System32\config\SOFTWARE"));

        public static async Task<OfflineWindowsInfo> ReadAsync(
            ActionContext context, string volume, CancellationToken cancellationToken)
        {
            var root = volume.TrimEnd('\\') + "\\";

            if (IsRunning(root))
                return new OfflineWindowsInfo
                {
                    MachineName = Environment.MachineName,
                    Description = context.Platform.Profile.ShortName + ", build " +
                                  context.Platform.Profile.Build.ToString(CultureInfo.InvariantCulture),
                    Software = await SoftwareAsync(context, @"HKLM\SOFTWARE", cancellationToken).ConfigureAwait(false),
                    DriverPackages = OfflineDrivers.ParsePackages(
                        await QueryAsync(context, @"HKLM\SYSTEM\DriverDatabase\DriverInfFiles", true, cancellationToken).ConfigureAwait(false)),
                };

            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var system = @"HKLM\LDI12-HL-SYS-" + suffix;
            var software = @"HKLM\LDI12-HL-SW-" + suffix;

            string? machine = null, description = null, failure = null, productKey = null;
            SoftwareInventory? programs = null;
            IReadOnlyDictionary<string, string> packages = new Dictionary<string, string>();

            if (await LoadAsync(context, system, Path.Combine(root, @"Windows\System32\config\SYSTEM"), cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var values = await QueryAsync(context, system + @"\ControlSet001\Control\ComputerName\ComputerName", false, cancellationToken)
                        .ConfigureAwait(false);
                    machine = Value(values, "ComputerName");
                    packages = OfflineDrivers.ParsePackages(
                        await QueryAsync(context, system + @"\DriverDatabase\DriverInfFiles", true, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    await UnloadAsync(context, system).ConfigureAwait(false);
                }
            }
            else
            {
                failure = "le registre SYSTEM de ce Windows n'a pas pu être chargé";
            }

            if (await LoadAsync(context, software, Path.Combine(root, @"Windows\System32\config\SOFTWARE"), cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var version = await QueryAsync(context, software + @"\Microsoft\Windows NT\CurrentVersion", false, cancellationToken)
                        .ConfigureAwait(false);
                    var product = Value(version, "ProductName");
                    var build = Value(version, "CurrentBuild");
                    productKey = MachineSettings.Decode(MachineSettings.Hex(Value(version, "DigitalProductId")));
                    if (product != null)
                    {
                        // Windows 11 s'annonce encore « Windows 10 » dans ce champ : le numéro de build tranche.
                        if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                            number >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
                            product = "Windows 11" + product.Substring("Windows 10".Length);
                        description = product + (build == null ? string.Empty : ", build " + build);
                    }

                    programs = await SoftwareAsync(context, software, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await UnloadAsync(context, software).ConfigureAwait(false);
                }
            }
            else
            {
                failure ??= "le registre SOFTWARE de ce Windows n'a pas pu être chargé";
            }

            return new OfflineWindowsInfo { MachineName = machine, Description = description, Software = programs, Failure = failure, DriverPackages = packages, ProductKey = productKey };
        }

        /// <summary>Les logiciels installés pour la machine, vue 64 et 32 bits, tels que les liste le panneau de configuration.</summary>
        private static async Task<SoftwareInventory?> SoftwareAsync(ActionContext context, string software, CancellationToken cancellationToken)
        {
            var programs = new List<InstalledProgram>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, scope) in new[]
                     {
                         (software + @"\Microsoft\Windows\CurrentVersion\Uninstall", SoftwareScope.Machine),
                         (software + @"\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", SoftwareScope.Machine32),
                     })
            {
                var output = await QueryAsync(context, key, true, cancellationToken).ConfigureAwait(false);
                foreach (var entry in ParseUninstall(output))
                {
                    if (!seen.Add(entry.Name + "|" + entry.Version)) continue;
                    programs.Add(new InstalledProgram
                    {
                        Name = entry.Name,
                        Publisher = entry.Publisher,
                        Version = entry.Version,
                        Scope = scope,

                        // Pas de clé de registre : celle-ci n'existe que le temps de la lecture.
                    });
                }
            }

            programs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return programs.Count == 0
                ? null
                : new SoftwareInventory
                {
                    Programs = programs,
                    Limitation = "Relevé dans le registre du disque : les logiciels installés pour un seul compte n'y figurent pas.",
                };
        }

        /// <summary>
        /// Les entrées de désinstallation d'une sortie de <c>reg query /s</c>.
        /// </summary>
        /// <remarks>
        /// Le format de <c>reg query</c> ne dépend pas de la langue : chaque clé sur sa ligne, puis
        /// ses valeurs, indentées, nom, type et donnée séparés par quatre espaces. Sont écartés, comme
        /// le fait le panneau de configuration : les composants système, les mises à jour et ce qui
        /// n'a pas de nom affiché.
        /// </remarks>
        internal static IReadOnlyList<(string Key, string Name, string? Version, string? Publisher)> ParseUninstall(string output)
        {
            var result = new List<(string, string, string?, string?)>();
            string? key = null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                if (key != null && values.TryGetValue("DisplayName", out var name) && name.Trim().Length > 0 &&
                    !(values.TryGetValue("SystemComponent", out var system) && system.Trim() == "0x1") &&
                    !values.ContainsKey("ParentKeyName") &&
                    !(values.TryGetValue("ReleaseType", out var release) && release.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    values.TryGetValue("DisplayVersion", out var version);
                    values.TryGetValue("Publisher", out var publisher);
                    result.Add((key, name.Trim(), version?.Trim(), publisher?.Trim()));
                }

                values.Clear();
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    key = line.Trim();
                    continue;
                }

                if (!line.StartsWith("    ", StringComparison.Ordinal)) continue;

                var parts = line.Trim().Split(new[] { "    " }, 3, StringSplitOptions.None);
                if (parts.Length == 3 && parts[1].StartsWith("REG_", StringComparison.Ordinal)) values[parts[0]] = parts[2];
                else if (parts.Length == 2 && parts[1].StartsWith("REG_", StringComparison.Ordinal)) values[parts[0]] = string.Empty;
            }

            Flush();
            return result;
        }

        internal static string? Value(string output, string name)
        {
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.TrimEnd('\r').Trim().Split(new[] { "    " }, 3, StringSplitOptions.None);
                if (parts.Length == 3 && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase)) return parts[2];
            }

            return null;
        }

        private static bool IsRunning(string root)
        {
            try
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return string.Equals(Path.GetPathRoot(windows), root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static async Task<bool> LoadAsync(ActionContext context, string key, string file, CancellationToken cancellationToken)
        {
            if (!context.Files.FileExists(file)) return false;

            var run = await context.Processes.RunAsync(
                new ProcessRequest("reg.exe", "load \"" + key + "\" \"" + file + "\"") { Timeout = TimeSpan.FromMinutes(1) },
                cancellationToken).ConfigureAwait(false);
            return run.Completed && run.ExitCode == 0;
        }

        /// <summary>Toujours tenté, sans annulation : un registre resté chargé le serait jusqu'au redémarrage.</summary>
        private static Task UnloadAsync(ActionContext context, string key)
            => context.Processes.RunAsync(
                new ProcessRequest("reg.exe", "unload \"" + key + "\"") { Timeout = TimeSpan.FromMinutes(1) },
                CancellationToken.None);

        private static async Task<string> QueryAsync(ActionContext context, string key, bool recursive, CancellationToken cancellationToken)
        {
            var run = await context.Processes.RunAsync(
                new ProcessRequest("reg.exe", "query \"" + key + "\"" + (recursive ? " /s" : string.Empty))
                {
                    Timeout = TimeSpan.FromMinutes(2),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    MaxOutputChars = 8 * 1024 * 1024,
                },
                cancellationToken).ConfigureAwait(false);
            return run.Completed && run.ExitCode == 0 ? run.StandardOutput : string.Empty;
        }
    }
}
