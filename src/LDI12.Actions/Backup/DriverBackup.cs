using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    /// <summary>Un paquet de pilote tiers, tel qu'on le coche.</summary>
    public sealed class DriverChoice
    {
        /// <summary>Nom publié dans le magasin de pilotes : « oem12.inf ».</summary>
        public string InfName { get; init; } = string.Empty;

        /// <summary>Les périphériques qu'il fait fonctionner, pour que le technicien sache ce qu'il coche.</summary>
        public string Label { get; init; } = string.Empty;

        public string? DeviceClass { get; init; }

        public string? Manufacturer { get; init; }

        public string? Version { get; init; }
    }

    /// <summary>
    /// Les pilotes d'une sauvegarde : ce qu'on peut en garder, et où ils sont rangés.
    /// </summary>
    /// <remarks>
    /// <b>Seuls les pilotes tiers.</b> Un paquet publié sous le nom <c>oemNN.inf</c> a été ajouté
    /// au magasin de pilotes par un constructeur ou un installateur : c'est lui qu'une
    /// réinstallation perd. Les pilotes fournis avec Windows reviennent avec Windows, et les
    /// exporter ne ferait qu'alourdir la sauvegarde.
    /// </remarks>
    public static class DriverBackup
    {
        public const string Folder = "Pilotes";

        public const string ListFileName = "pilotes.csv";

        /// <summary>Windows 10 1607 : <c>pnputil /export-driver</c> et <c>/add-driver</c> existent.</summary>
        internal const int PerDriverBuild = 14393;

        /// <summary>Windows 8.1 : <c>dism /export-driver</c>, qui exporte tous les pilotes tiers d'un coup.</summary>
        internal const int ExportBuild = 9600;

        /// <summary>
        /// Les pilotes tiers relevés par le diagnostic, un par paquet.
        /// </summary>
        /// <remarks>
        /// Un même paquet sert souvent plusieurs périphériques (un pilote de chipset en couvre une
        /// dizaine) : ils sont réunis sous une seule case, avec leurs noms.
        /// </remarks>
        public static IReadOnlyList<DriverChoice> Choices(SystemSnapshot? snapshot)
        {
            var drivers = snapshot?.Windows.Drivers;
            if (drivers == null) return Array.Empty<DriverChoice>();

            var packages = new Dictionary<string, (DriverInfo First, SortedSet<string> Devices)>(StringComparer.OrdinalIgnoreCase);
            foreach (var driver in drivers)
            {
                var inf = driver.InfName;
                if (!IsThirdParty(inf)) continue;

                if (!packages.TryGetValue(inf!, out var package))
                {
                    package = (driver, new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase));
                    packages[inf!] = package;
                }

                package.Devices.Add(driver.DeviceName);
            }

            var choices = new List<DriverChoice>(packages.Count);
            foreach (var pair in packages)
            {
                var first = pair.Value.First;
                choices.Add(new DriverChoice
                {
                    InfName = pair.Key.ToLowerInvariant(),
                    Label = string.Join(", ", pair.Value.Devices),
                    DeviceClass = first.DeviceClass,
                    Manufacturer = first.Manufacturer,
                    Version = first.Version,
                });
            }

            choices.Sort((a, b) =>
            {
                var byClass = string.Compare(a.DeviceClass, b.DeviceClass, StringComparison.CurrentCultureIgnoreCase);
                return byClass != 0 ? byClass : string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase);
            });

            return choices;
        }

        internal static bool IsThirdParty(string? inf)
            => inf != null &&
               inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
               inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
               inf.IndexOfAny(new[] { '\\', '/', '"', ' ' }) < 0;

        /// <summary>« oem12.inf », une tabulation, le libellé : une ligne par pilote coché.</summary>
        public static string Encode(IEnumerable<DriverChoice> choices)
        {
            var builder = new StringBuilder();
            foreach (var choice in choices)
                builder.Append(choice.InfName).Append('\t')
                    .Append(Flatten(choice.Label)).Append('\t')
                    .Append(Flatten(choice.DeviceClass)).Append('\t')
                    .Append(Flatten(choice.Version)).Append('\n');
            return builder.ToString();
        }

        internal static IReadOnlyList<DriverChoice> Decode(string? text)
        {
            var result = new List<DriverChoice>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text!.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('\t');

                // Le nom passe dans une ligne de commande : rien d'autre qu'un oemNN.inf n'est admis.
                if (!IsThirdParty(fields[0]) || !seen.Add(fields[0])) continue;

                result.Add(new DriverChoice
                {
                    InfName = fields[0].ToLowerInvariant(),
                    Label = fields.Length > 1 ? fields[1] : string.Empty,
                    DeviceClass = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : null,
                    Version = fields.Length > 3 && fields[3].Length > 0 ? fields[3] : null,
                });
            }

            return result;
        }

        internal static string Csv(IEnumerable<DriverChoice> exported)
        {
            var builder = new StringBuilder("inf;périphériques;classe;version\r\n");
            foreach (var driver in exported)
                builder.Append(CsvField(driver.InfName)).Append(';')
                    .Append(CsvField(driver.Label)).Append(';')
                    .Append(CsvField(driver.DeviceClass ?? string.Empty)).Append(';')
                    .Append(CsvField(driver.Version ?? string.Empty)).Append("\r\n");
            return builder.ToString();
        }

        /// <summary>Libellés relus dans la sauvegarde, par nom de paquet.</summary>
        internal static Dictionary<string, string> Labels(string? csv)
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (csv == null) return labels;

            var first = true;
            foreach (var line in csv.Split('\n'))
            {
                if (first) { first = false; continue; }
                var fields = SplitCsv(line.TrimEnd('\r'));
                if (fields.Count >= 2 && fields[0].Length > 0) labels[fields[0]] = fields[1];
            }

            return labels;
        }

        /// <summary>Le dernier message significatif d'un outil, pour le compte rendu.</summary>
        internal static string Tail(ProcessResult result)
        {
            if (result.LaunchFailed) return "l'outil n'a pas pu être lancé";
            if (result.TimedOut) return "l'outil n'a pas répondu dans le délai";

            string? last = null;
            foreach (var line in (result.StandardOutput + "\n" + result.StandardError).Split('\r', '\n'))
                if (line.Trim().Length > 0) last = line.Trim();

            return "code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) +
                   (last == null ? string.Empty : ", « " + WifiExport.Repair(last) + " »");
        }

        private static string Flatten(string? value)
            => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static string CsvField(string value)
            => value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;

        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var character = line[i];
                if (quoted)
                {
                    if (character == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (character == '"') quoted = false;
                    else current.Append(character);
                }
                else if (character == '"') quoted = true;
                else if (character == ';') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(character);
            }

            fields.Add(current.ToString());
            return fields;
        }
    }

    public sealed class DriverExportPlan
    {
        /// <summary>Dossier de la sauvegarde. Les pilotes vont dans son sous-dossier « Pilotes ».</summary>
        public string Destination { get; init; } = string.Empty;

        public IReadOnlyList<DriverChoice> Drivers { get; init; } = Array.Empty<DriverChoice>();

        /// <summary>Avant Windows 10 1607 : un seul export de tous les pilotes tiers, par DISM.</summary>
        public bool AllAtOnce { get; init; }
    }

    /// <summary>
    /// Exporte les pilotes tiers cochés dans la sauvegarde.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi une action à part, et élevée.</b> <c>pnputil /export-driver</c> lit le magasin
    /// de pilotes de Windows, qui n'est accessible qu'en administrateur. La copie des données,
    /// elle, reste volontairement dans la session du client : élevée par un autre compte, elle
    /// sauvegarderait le mauvais profil. Les deux actions écrivent donc dans le même dossier de
    /// sauvegarde, chacune avec ses droits.
    /// <para>
    /// Chaque pilote est exporté dans son propre dossier, au nom de son paquet : deux pilotes qui
    /// livrent un fichier de même nom ne s'écrasent pas, et la restauration les réinstalle un par
    /// un, avec un résultat pour chacun.
    /// </para>
    /// </remarks>
    public sealed class ExportDriversAction : IRepairAction
    {
        /// <summary>Dossier de la sauvegarde, tel que la copie des données l'a établi.</summary>
        public const string DestinationParameter = "destination";

        /// <summary>Les pilotes cochés, voir <see cref="DriverBackup.Encode"/>.</summary>
        public const string DriversParameter = "drivers";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.ExportDrivers,
            DisplayName = "Sauvegarder les pilotes",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Low,
            Purpose = "Exporte les pilotes tiers cochés (carte graphique, son, Wi-Fi, imprimante…) depuis le magasin de " +
                      "pilotes de Windows vers la sauvegarde, pour les réinstaller après la remise à neuf.",
            PlainPurpose = "Les pilotes de vos périphériques sont mis de côté avec vos données, pour qu'ils " +
                           "fonctionnent de nouveau tout de suite après la réinstallation.",
            TypicalDuration = TimeSpan.FromMinutes(2),
            HardTimeout = TimeSpan.FromMinutes(30),
            Requirements = new ActionRequirements
            {
                RequiresElevation = true,
                MinimumBuild = DriverBackup.ExportBuild,
                Workaround = "Sous Windows 7, copier le dossier C:\\Windows\\System32\\DriverStore\\FileRepository sur " +
                             "le support, ou télécharger les pilotes chez le constructeur de la machine.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (context.Platform.Profile.Build < DriverBackup.ExportBuild)
                return ActionReadiness.No("L'export des pilotes demande Windows 8.1 ou plus récent.",
                    Descriptor.Requirements.Workaround);

            var destination = context.Parameter(DestinationParameter);
            if (destination == null) return ActionReadiness.No("Aucun dossier de sauvegarde n'a été établi.");

            var parent = Path.GetDirectoryName(destination.TrimEnd('\\'));
            if (!context.Files.DirectoryExists(destination) && (parent == null || !context.Files.DirectoryExists(parent)))
                return ActionReadiness.No("Le support de sauvegarde « " + destination + " » n'est pas accessible.");

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("le magasin de pilotes de Windows ne se lit qu'en administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var destination = context.Parameter(DestinationParameter);
            if (destination == null) return Task.FromResult(Blocked("Aucun dossier de sauvegarde n'a été établi."));

            var drivers = DriverBackup.Decode(context.Parameter(DriversParameter));
            var allAtOnce = context.Platform.Profile.Build < DriverBackup.PerDriverBuild;

            if (drivers.Count == 0 && !allAtOnce)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Aucun pilote n'est coché.",
                });

            var folder = Path.Combine(destination, DriverBackup.Folder);
            var willDo = new List<string>();

            if (allAtOnce)
                willDo.Add("Exporter tous les pilotes tiers de la machine dans « " + folder + " » par DISM : avant " +
                           "Windows 10 1607, Windows ne sait pas les exporter un par un");
            else
                foreach (var driver in drivers)
                    willDo.Add("Exporter " + driver.InfName + " : " + driver.Label +
                               (driver.Version == null ? string.Empty : " (version " + driver.Version + ")"));

            willDo.Add("Déposer la liste des pilotes exportés dans « " + DriverBackup.ListFileName + " »");

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = allAtOnce
                    ? "Tous les pilotes tiers seront exportés dans la sauvegarde."
                    : drivers.Count + " pilote(s) seront exportés dans la sauvegarde.",
                WillDo = willDo,
                WillNotDo = new[]
                {
                    "N'installe, ne désinstalle et ne modifie aucun pilote de cette machine : l'export lit le " +
                    "magasin de pilotes, il n'y touche pas.",
                    "N'exporte pas les pilotes fournis avec Windows : ils reviennent avec lui.",
                },
                Measurements = new[] { new PreviewLine("Destination", folder) },
                Plan = new DriverExportPlan { Destination = destination, Drivers = drivers, AllAtOnce = allAtOnce },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not DriverExportPlan plan)
                return ActionOutcome.Simple(ActionStatus.Failed, "La liste des pilotes n'a pas été établie : rien n'a été exporté.");

            var stopwatch = Stopwatch.StartNew();
            var folder = Path.Combine(plan.Destination, DriverBackup.Folder);
            if (!context.Files.CreateDirectory(folder))
                return ActionOutcome.Simple(ActionStatus.Failed, "Le dossier « " + folder + " » n'a pas pu être créé.");

            var details = new List<string>();
            var exported = new List<DriverChoice>();
            var failed = 0;

            if (plan.AllAtOnce)
            {
                progress?.Report(new ActionProgress("Export de tous les pilotes tiers…"));
                var run = await context.Processes.RunAsync(
                    new ProcessRequest("dism.exe", "/Online /Export-Driver /Destination:\"" + folder + "\"")
                    {
                        Timeout = TimeSpan.FromMinutes(20),
                        OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (run.Completed && run.ExitCode == 0) details.Add("Tous les pilotes tiers ont été exportés par DISM.");
                else
                {
                    failed++;
                    details.Add("L'export par DISM a échoué : " + DriverBackup.Tail(run) + ".");
                }

                exported.AddRange(plan.Drivers);
            }
            else
            {
                for (var index = 0; index < plan.Drivers.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var driver = plan.Drivers[index];
                    progress?.Report(new ActionProgress("Export du pilote " + driver.Label + "…",
                        (double)index / plan.Drivers.Count));

                    var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(driver.InfName));
                    if (!context.Files.CreateDirectory(target))
                    {
                        failed++;
                        details.Add(driver.Label + " : le dossier d'export n'a pas pu être créé.");
                        continue;
                    }

                    var run = await context.Processes.RunAsync(
                        new ProcessRequest("pnputil.exe", "/export-driver " + driver.InfName + " \"" + target + "\"")
                        {
                            Timeout = TimeSpan.FromMinutes(5),
                            OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (run.Completed && run.ExitCode == 0)
                    {
                        exported.Add(driver);
                        continue;
                    }

                    failed++;
                    details.Add(driver.Label + " (" + driver.InfName + ") : non exporté, " + DriverBackup.Tail(run) + ".");
                }

                if (exported.Count > 0)
                    details.Insert(0, exported.Count + " pilote(s) exporté(s) dans « " + folder + " ».");
            }

            if (exported.Count > 0 &&
                !context.Files.WriteText(Path.Combine(folder, DriverBackup.ListFileName), DriverBackup.Csv(exported)))
                details.Add("La liste des pilotes n'a pas pu être écrite dans la sauvegarde.");

            stopwatch.Stop();

            var status = failed == 0
                ? ActionStatus.Succeeded
                : exported.Count > 0 && !plan.AllAtOnce ? ActionStatus.PartiallySucceeded : ActionStatus.Failed;

            var summary = plan.AllAtOnce
                ? failed == 0 ? "Pilotes tiers exportés dans la sauvegarde." : "Les pilotes n'ont pas pu être exportés."
                : exported.Count + " pilote(s) exporté(s)" + (failed > 0 ? ", " + failed + " en échec." : ".");

            return new ActionOutcome { Status = status, Summary = summary, Details = details, Duration = stopwatch.Elapsed };
        }

        private static ActionPreview Blocked(string reason)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = reason, Blocker = reason };
    }

    public sealed class DriverRestorePlan
    {
        /// <summary>Un dossier par paquet exporté, avec son libellé quand la sauvegarde le donne.</summary>
        public IReadOnlyList<(string Folder, string Label)> Packages { get; init; } = Array.Empty<(string, string)>();
    }

    /// <summary>
    /// Réinstalle les pilotes d'une sauvegarde LDI12.
    /// </summary>
    /// <remarks>
    /// <c>pnputil</c> ajoute chaque paquet au magasin de pilotes et l'installe sur les
    /// périphériques présents qui lui correspondent. Un pilote dont le matériel n'est pas branché
    /// (une imprimante restée à la maison) est ajouté quand même : Windows le prendra seul au
    /// prochain branchement.
    /// <para>
    /// Une version plus récente déjà installée n'est pas remplacée : Windows garde le meilleur
    /// pilote qu'il connaît, et c'est lui qui en juge, pas cette action.
    /// </para>
    /// </remarks>
    public sealed class RestoreDriversAction : IRepairAction
    {
        public const string SourceParameter = "source";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestoreDrivers,
            DisplayName = "Réinstaller les pilotes de la sauvegarde",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Moderate,
            Purpose = "Ajoute au magasin de pilotes et installe les pilotes exportés dans une sauvegarde LDI12, par pnputil.",
            PlainPurpose = "Les pilotes de vos périphériques sont remis en place, pour que tout fonctionne comme avant.",
            TypicalDuration = TimeSpan.FromMinutes(5),
            HardTimeout = TimeSpan.FromHours(1),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null) return ActionReadiness.No("Aucune sauvegarde n'a été désignée.");
            if (!context.Files.DirectoryExists(source))
                return ActionReadiness.No("Le dossier « " + source + " » n'existe pas ou n'est pas accessible.");

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("l'installation d'un pilote demande les droits administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var source = context.Parameter(SourceParameter);
            var backup = source == null ? null : RestoreCatalog.Find(context.Files, source, out _);
            if (backup == null)
                return Task.FromResult(Blocked("Aucune sauvegarde LDI12 n'a été trouvée dans « " + source + " »."));

            var packages = Packages(context.Files, backup);
            if (packages.Count == 0)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "La sauvegarde « " + Path.GetFileName(backup) + " » ne contient pas de pilotes.",
                });

            var willDo = new List<string>();
            foreach (var package in packages) willDo.Add("Installer " + package.Label);

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = packages.Count + " pilote(s) de la sauvegarde seront réinstallés.",
                WillDo = willDo,
                WillNotDo = new[]
                {
                    "Ne remplace pas un pilote plus récent déjà installé : Windows garde le meilleur qu'il connaît.",
                    "Ne supprime aucun pilote de cette machine.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Redémarrage", "certains pilotes (carte graphique, chipset) ne prennent effet " +
                                                   "qu'après un redémarrage", PreviewLineKind.Caution),
                },
                Plan = new DriverRestorePlan { Packages = packages },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not DriverRestorePlan plan)
                return ActionOutcome.Simple(ActionStatus.Failed, "La liste des pilotes n'a pas été établie : rien n'a été installé.");

            var stopwatch = Stopwatch.StartNew();
            var modern = context.Platform.Profile.Build >= DriverBackup.PerDriverBuild;
            var details = new List<string>();
            int installed = 0, failed = 0;
            var restart = false;

            for (var index = 0; index < plan.Packages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (folder, label) = plan.Packages[index];
                progress?.Report(new ActionProgress("Installation du pilote " + label + "…", (double)index / plan.Packages.Count));

                var infs = Path.Combine(folder, "*.inf");
                var run = await context.Processes.RunAsync(
                    new ProcessRequest("pnputil.exe", modern
                        ? "/add-driver \"" + infs + "\" /subdirs /install"
                        : "-i -a \"" + infs + "\"")
                    {
                        Timeout = TimeSpan.FromMinutes(10),
                        OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    },
                    cancellationToken).ConfigureAwait(false);

                // 3010 : installé, un redémarrage le mettra en service.
                if (run.Completed && (run.ExitCode == 0 || run.ExitCode == 3010))
                {
                    installed++;
                    restart |= run.ExitCode == 3010;
                    continue;
                }

                failed++;
                details.Add(label + " : non installé, " + DriverBackup.Tail(run) + ".");
            }

            if (installed > 0) details.Insert(0, installed + " pilote(s) ajouté(s) et installé(s) sur les périphériques présents.");

            stopwatch.Stop();
            return new ActionOutcome
            {
                Status = failed == 0 ? ActionStatus.Succeeded : installed > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = installed + " pilote(s) réinstallé(s)" + (failed > 0 ? ", " + failed + " en échec" : string.Empty) +
                          (restart ? " : redémarrer pour les mettre en service." : "."),
                Details = details,
                Duration = stopwatch.Elapsed,
                RestartRequired = restart,
            };
        }

        /// <summary>Les dossiers de la sauvegarde qui contiennent un pilote, avec leur libellé.</summary>
        internal static IReadOnlyList<(string Folder, string Label)> Packages(IFileSystemGateway files, string backup)
        {
            var result = new List<(string, string)>();
            var root = Path.Combine(backup, DriverBackup.Folder);
            if (!files.DirectoryExists(root)) return result;

            var list = files.ReadText(Path.Combine(root, DriverBackup.ListFileName));
            var labels = DriverBackup.Labels(list.HasValue ? list.Value : null);

            // Un export par DISM range les paquets dans des dossiers à son nom (« nvlt.inf_amd64_… ») :
            // tout sous-dossier qui porte un .inf est un paquet.
            foreach (var directory in files.EnumerateDirectories(root))
            {
                var scan = files.Scan(new DirectoryScanRequest(directory) { TopLevelOnly = true, MaxFiles = 5000 },
                    CancellationToken.None);
                if (!scan.HasValue) continue;

                var hasInf = false;
                foreach (var file in scan.Value.Files)
                    if (file.Path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) { hasInf = true; break; }
                if (!hasInf) continue;

                var name = Path.GetFileName(directory);
                result.Add((directory, labels.TryGetValue(name + ".inf", out var label) && label.Length > 0
                    ? label + " (" + name + ")"
                    : name));
            }

            return result;
        }

        private static ActionPreview Blocked(string reason)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = reason, Blocker = reason };
    }
}
