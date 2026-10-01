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

namespace LDI12.Actions.Backup
{
    /// <summary>Un fichier que la copie n'a pas pu lire parce qu'un programme le tenait ouvert.</summary>
    public sealed class OpenFile
    {
        public string Source { get; init; } = string.Empty;

        public string Target { get; init; } = string.Empty;
    }

    public sealed class OpenFilesPlan
    {
        public string Destination { get; init; } = string.Empty;

        /// <summary>Les fichiers, rangés par volume : un cliché par volume.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<OpenFile>> ByVolume { get; init; } =
            new Dictionary<string, IReadOnlyList<OpenFile>>();

        public int Count { get; init; }
    }

    /// <summary>
    /// Copie, depuis un cliché instantané, les fichiers qu'un programme tenait ouverts.
    /// </summary>
    /// <remarks>
    /// <b>Ce que font les logiciels de sauvegarde, et que faisait le technicien à la main.</b> Une
    /// archive Outlook ouverte, la base d'un navigateur resté lancé : Windows en refuse la lecture
    /// tant que le programme les tient. Le cliché instantané (VSS) fige le volume tel qu'il est à
    /// un instant, et ses fichiers se lisent comme les autres, programme ouvert ou non.
    /// <para>
    /// Seuls les fichiers refusés par la copie ordinaire passent par là, après elle : un cliché
    /// coûte quelques secondes et un peu de place sur le volume, inutile quand tout a pu se lire.
    /// Le cliché est supprimé à la fin, y compris si la copie échoue en route. Ce qu'il contient
    /// est l'état du fichier à l'instant du cliché, comme après une coupure de courant : une
    /// archive Outlook s'y ouvre normalement, au pire après une vérification par Outlook.
    /// </para>
    /// <para>
    /// Élevée : Windows ne crée de cliché que pour un administrateur. Réservée aux volumes locaux
    /// en NTFS, les seuls que VSS sache figer.
    /// </para>
    /// </remarks>
    public sealed class OpenFilesAction : IRepairAction
    {
        public const string DestinationParameter = "destination";

        /// <summary>Une ligne par fichier : source, tabulation, destination.</summary>
        public const string FilesParameter = "files";

        public const string ReportFileName = "fichiers-ouverts.csv";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.CopyOpenFiles,
            DisplayName = "Copier les fichiers ouverts",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Low,
            Purpose = "Copie dans la sauvegarde, depuis un cliché instantané du volume, les fichiers qu'un programme " +
                      "tenait ouverts. Le cliché est supprimé à la fin.",
            PlainPurpose = "Les fichiers qu'un logiciel ouvert empêchait de copier sont récupérés quand même, sans " +
                           "rien fermer.",
            TypicalDuration = TimeSpan.FromMinutes(2),
            HardTimeout = TimeSpan.FromHours(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public static string Encode(IEnumerable<OpenFile> files)
        {
            var builder = new StringBuilder();
            foreach (var file in files)
                if (file.Source.IndexOf('\t') < 0 && file.Target.IndexOf('\t') < 0)
                    builder.Append(file.Source).Append('\t').Append(file.Target).Append('\n');
            return builder.ToString();
        }

        internal static IReadOnlyList<OpenFile> Decode(string? text)
        {
            var result = new List<OpenFile>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var line in text!.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length != 2 || VolumeOf(fields[0]) == null || fields[1].Length == 0) continue;
                result.Add(new OpenFile { Source = fields[0], Target = fields[1] });
            }

            return result;
        }

        /// <summary>« C:\ » pour un fichier d'un volume local ; nul pour un partage réseau ou un chemin douteux.</summary>
        internal static string? VolumeOf(string path)
            => path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\'
                ? char.ToUpperInvariant(path[0]) + ":\\"
                : null;

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (context.Parameter(FilesParameter) == null) return ActionReadiness.No("Aucun fichier ouvert à copier.");

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("Windows ne crée de cliché instantané que pour un administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var destination = context.Parameter(DestinationParameter) ?? string.Empty;
            var files = Decode(context.Parameter(FilesParameter));

            var byVolume = new Dictionary<string, List<OpenFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var volume = VolumeOf(file.Source)!;
                var format = context.Files.VolumeFormat(volume);
                if (format != null && !format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;

                if (!byVolume.TryGetValue(volume, out var list)) byVolume[volume] = list = new List<OpenFile>();
                list.Add(file);
            }

            var count = 0;
            var willDo = new List<string>();
            var plan = new Dictionary<string, IReadOnlyList<OpenFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in byVolume)
            {
                plan[pair.Key] = pair.Value;
                count += pair.Value.Count;
                willDo.Add("Figer le volume " + pair.Key + " par un cliché instantané, y copier " + pair.Value.Count +
                           " fichier(s) ouvert(s), puis supprimer le cliché");
            }

            if (count == 0)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Aucun fichier ouvert ne se trouve sur un volume local en NTFS : pas de cliché possible.",
                });

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = count + " fichier(s) tenus ouverts par un programme seront copiés depuis un cliché instantané.",
                WillDo = willDo,
                WillNotDo = new[]
                {
                    "Ne ferme aucun programme, et ne touche pas aux fichiers d'origine.",
                    "Ne laisse pas le cliché derrière elle : il est supprimé, même si la copie échoue en route.",
                },
                Plan = new OpenFilesPlan { Destination = destination, ByVolume = plan, Count = count },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not OpenFilesPlan plan)
                return ActionOutcome.Simple(ActionStatus.Failed, "La liste des fichiers ouverts n'a pas été établie : rien n'a été copié.");

            var stopwatch = Stopwatch.StartNew();
            var details = new List<string>();
            var report = new StringBuilder("fichier;résultat\r\n");
            int copied = 0, failed = 0, done = 0;

            foreach (var pair in plan.ByVolume)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress("Cliché instantané de " + pair.Key + "…", (double)done / plan.Count));

                var shadow = await CreateAsync(context, pair.Key, cancellationToken).ConfigureAwait(false);
                if (shadow.Device == null)
                {
                    failed += pair.Value.Count;
                    done += pair.Value.Count;
                    details.Add(pair.Key + " : le cliché instantané n'a pas pu être créé, " + shadow.Failure + ".");
                    foreach (var file in pair.Value) report.Append(Csv(file.Source)).Append(";cliché impossible\r\n");
                    continue;
                }

                try
                {
                    foreach (var file in pair.Value)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ActionProgress("Copie de " + Path.GetFileName(file.Source) + " depuis le cliché…",
                            (double)done++ / plan.Count));

                        var result = CopyFromShadow(context, shadow.Device, pair.Key, file, cancellationToken);
                        if (result.Outcome == FileCopyOutcome.Copied || result.Outcome == FileCopyOutcome.AlreadyPresent) copied++;
                        else failed++;

                        report.Append(Csv(file.Source)).Append(';').Append(BackupUserDataAction.Describe(result.Outcome)).Append("\r\n");
                    }
                }
                finally
                {
                    // Jamais laissé derrière : un cliché occupe de la place sur le volume du client
                    // tant qu'il existe, et personne ne penserait à le chercher.
                    await DeleteAsync(context, shadow.Id!).ConfigureAwait(false);
                }
            }

            if (plan.Destination.Length > 0 &&
                !context.Files.ReplaceText(Path.Combine(plan.Destination, ReportFileName), "\uFEFF" + report))
                details.Add("La liste des fichiers ouverts n'a pas pu être écrite dans la sauvegarde.");

            if (copied > 0) details.Insert(0, copied + " fichier(s) ouvert(s) copié(s) et vérifié(s) depuis un cliché instantané.");

            stopwatch.Stop();
            return new ActionOutcome
            {
                Status = failed == 0 ? ActionStatus.Succeeded : copied > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = copied + " fichier(s) ouvert(s) récupéré(s) depuis un cliché instantané" +
                          (failed > 0 ? ", " + failed + " non récupéré(s)." : "."),
                Details = details,
                Duration = stopwatch.Elapsed,
            };
        }

        /// <summary>Copie un fichier depuis le cliché, tel qu'il y est figé.</summary>
        private static FileCopyResult CopyFromShadow(
            ActionContext context, string device, string volume, OpenFile file, CancellationToken cancellationToken)
        {
            var shadowPath = device.TrimEnd('\\') + "\\" + file.Source.Substring(volume.Length);
            var directory = Path.GetDirectoryName(shadowPath);
            if (directory == null) return FileCopyResult.Of(FileCopyOutcome.NotFound);

            // La taille et la date lues dans le cliché : le fichier d'origine a continué de changer
            // depuis le relevé, c'est même pour cela qu'il était ouvert.
            var scan = context.Files.Scan(new DirectoryScanRequest(directory) { TopLevelOnly = true, MaxFiles = 100_000 },
                cancellationToken);
            if (!scan.HasValue) return FileCopyResult.Of(FileCopyOutcome.NotFound);

            foreach (var entry in scan.Value.Files)
                if (string.Equals(Path.GetFileName(entry.Path), Path.GetFileName(file.Source), StringComparison.OrdinalIgnoreCase))
                    return context.Files.Copy(new FileCopyRequest(entry, file.Target), cancellationToken);

            return FileCopyResult.Of(FileCopyOutcome.NotFound);
        }

        private static async Task<(string? Id, string? Device, string? Failure)> CreateAsync(
            ActionContext context, string volume, CancellationToken cancellationToken)
        {
            var script =
                "$ErrorActionPreference = 'Stop'\n" +
                "$r = ([WMICLASS]'root\\cimv2:Win32_ShadowCopy').Create('" + volume + "', 'ClientAccessible')\n" +
                "if ($r.ReturnValue -ne 0) { Write-Output ('ERREUR ' + $r.ReturnValue); exit 1 }\n" +
                "$s = Get-WmiObject Win32_ShadowCopy -Filter (\"ID='\" + $r.ShadowID + \"'\")\n" +
                "Write-Output ('CLICHE ' + $s.ID + ' ' + $s.DeviceObject)\n";

            var run = await context.Processes.RunAsync(PowerShell(script, TimeSpan.FromMinutes(3)), cancellationToken)
                .ConfigureAwait(false);

            foreach (var raw in run.StandardOutput.Split('\r', '\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("CLICHE ", StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && parts[2].StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
                        return (parts[1], parts[2], null);
                }

                if (line.StartsWith("ERREUR ", StringComparison.Ordinal) &&
                    int.TryParse(line.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                    return (null, null, Explain(code));
            }

            return (null, null, run.Completed ? "Windows n'a rien répondu d'exploitable" : DriverBackup.Tail(run));
        }

        private static Task DeleteAsync(ActionContext context, string id)
            => context.Processes.RunAsync(PowerShell(
                "Get-WmiObject Win32_ShadowCopy -Filter \"ID='" + id.Replace("'", string.Empty) + "'\" | " +
                "ForEach-Object { $_.Delete() }\n", TimeSpan.FromMinutes(2)), CancellationToken.None);

        /// <summary>
        /// Un script PowerShell, passé encodé.
        /// </summary>
        /// <remarks>
        /// Encodé plutôt qu'écrit dans la ligne de commande : aucun guillemet, aucun caractère d'un
        /// chemin ne peut alors casser le script. Windows PowerShell est présent de Windows 7 à 11.
        /// </remarks>
        internal static ProcessRequest PowerShell(string script, TimeSpan timeout)
            => new ProcessRequest("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                Timeout = timeout,
            };

        /// <summary>Les codes de Win32_ShadowCopy.Create, en clair.</summary>
        internal static string Explain(int code) => code switch
        {
            1 => "accès refusé",
            3 => "volume introuvable",
            4 => "ce volume ne prend pas en charge les clichés (il faut du NTFS)",
            6 => "place insuffisante sur le volume pour le cliché",
            8 => "trop de clichés existent déjà sur ce volume",
            9 => "un autre cliché est en cours de création",
            10 => "un composant de Windows a refusé le cliché",
            12 => "le service de clichés instantanés a échoué",
            _ => "code " + code.ToString(CultureInfo.InvariantCulture),
        };

        private static string Csv(string value)
            => value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
