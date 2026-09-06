using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    /// <summary>Un dossier à copier, avec les fichiers qui le composent.</summary>
    public sealed class BackupFolder
    {
        public string Label { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();

        public long Bytes { get; init; }

        public int CloudOnlyFiles { get; init; }

        /// <summary>Le relevé s'est arrêté avant la fin : la copie serait incomplète.</summary>
        public bool Truncated { get; init; }
    }

    /// <summary>Ce que la prévisualisation a établi, et que l'exécution se contente de suivre.</summary>
    public sealed class BackupPlan
    {
        public string Destination { get; init; } = string.Empty;

        public IReadOnlyList<BackupFolder> Folders { get; init; } = Array.Empty<BackupFolder>();

        public long Bytes { get; init; }

        public int Files { get; init; }
    }

    /// <summary>
    /// Copie les données du client vers un support externe, et vérifie la copie.
    /// </summary>
    /// <remarks>
    /// <b>La moitié manquante d'un écran qui existait déjà.</b> Le relevé des données répondait à
    /// « qu'y a-t-il à sauvegarder » depuis longtemps ; il fallait ensuite le faire à la main.
    /// <para>
    /// Trois règles gouvernent cette action, et elles ne sont pas négociables :
    /// </para>
    /// <list type="bullet">
    /// <item>la source n'est jamais touchée, ni déplacée, ni modifiée, ni supprimée. Le disque
    /// d'origine ressort de l'opération exactement comme il y est entré ;</item>
    /// <item>rien n'est écrasé à la destination. Un fichier différent portant déjà le même nom
    /// est signalé, jamais remplacé ;</item>
    /// <item>chaque fichier copié est relu et comparé à sa source. Ce qui ne se relit pas
    /// identique est retiré et compté comme un échec : un fichier à moitié écrit ressemble à une
    /// sauvegarde, et c'est ce qui rend son existence pire que son absence.</item>
    /// </list>
    /// <para>
    /// Le relevé des données, lui, ne retient toujours aucun nom de fichier : la liste de ce qui
    /// a été copié n'existe que dans le manifeste déposé <i>dans</i> la sauvegarde, avec le
    /// client, et ne remonte ni dans un rapport ni dans un journal.
    /// </para>
    /// </remarks>
    public sealed class BackupUserDataAction : IRepairAction
    {
        /// <summary>Nom du paramètre portant le dossier choisi par le technicien.</summary>
        public const string DestinationParameter = "destination";

        /// <summary>
        /// Marge exigée en plus de la taille des données.
        /// </summary>
        /// <remarks>
        /// Un support rempli au dernier octet échoue sur le dernier fichier, c'est-à-dire après
        /// une heure de copie. La marge est un pourcentage plutôt qu'une taille fixe : elle suit
        /// l'échelle de ce qu'on déplace.
        /// </remarks>
        private const double SpaceMargin = 1.05;

        /// <summary>Plafond de relevé par dossier. Au-delà, la sauvegarde est annoncée incomplète.</summary>
        private const int MaxFilesPerFolder = 400_000;

        private static readonly TimeSpan ScanBudget = TimeSpan.FromMinutes(3);

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.BackupUserData,
            DisplayName = "Sauvegarder les données du client",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Moderate,
            Purpose = "Copie les dossiers personnels vers un support externe, relit chaque fichier copié " +
                      "et dépose un manifeste de ce qui a été sauvegardé.",
            PlainPurpose = "Vos documents, images, musiques et fichiers du bureau sont copiés sur le support " +
                           "choisi. Rien n'est déplacé ni effacé : l'original reste exactement où il est.",
            TypicalDuration = TimeSpan.FromMinutes(20),
            HardTimeout = TimeSpan.FromHours(8),
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var destination = context.Parameter(DestinationParameter);
            if (destination == null)
                return ActionReadiness.No("Aucun dossier de destination n'a été choisi.",
                    "Choisissez le support sur lequel copier les données.");

            return context.Files.DirectoryExists(destination)
                ? ActionReadiness.Ready
                : ActionReadiness.No("Le dossier « " + destination + " » n'existe pas ou n'est pas accessible.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var destination = context.Parameter(DestinationParameter);
            if (destination == null)
                return Task.FromResult(Blocked("Aucun dossier de destination n'a été choisi."));

            var plan = new List<BackupFolder>();
            long bytes = 0;
            var files = 0;
            var cloud = 0;
            var truncated = false;

            foreach (var folder in Sources(context))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scan = context.Files.Scan(
                    new DirectoryScanRequest(folder.Path) { MaxFiles = MaxFilesPerFolder, Budget = ScanBudget },
                    cancellationToken);

                if (!scan.HasValue) continue;

                var keep = new List<FileEntry>();
                long size = 0;
                var cloudHere = 0;

                foreach (var file in scan.Value.Files)
                {
                    if (file.CloudOnly)
                    {
                        cloudHere++;
                        continue;
                    }

                    keep.Add(file);
                    size += file.SizeBytes;
                }

                if (keep.Count == 0 && cloudHere == 0) continue;

                plan.Add(new BackupFolder
                {
                    Label = folder.Label,
                    Path = folder.Path,
                    Files = keep,
                    Bytes = size,
                    CloudOnlyFiles = cloudHere,
                    Truncated = scan.Value.Truncated,
                });

                bytes += size;
                files += keep.Count;
                cloud += cloudHere;
                truncated |= scan.Value.Truncated;
            }

            if (files == 0)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Aucun fichier à sauvegarder dans les dossiers personnels de cette session.",
                });

            var free = context.Files.FreeSpace(destination);
            var needed = (long)(bytes * SpaceMargin);

            if (free.IsReliable && free.Value < needed)
                return Task.FromResult(Blocked(
                    "Il manque de la place sur la destination : " + ValueFormat.Bytes(bytes) +
                    " à copier, " + ValueFormat.Bytes(free.Value) + " disponibles."));

            var root = Path.Combine(destination, FolderName(context));

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Destination", root),
                new PreviewLine("À copier", ValueFormat.Bytes(bytes) + " en " + files + " fichier(s)"),
                new PreviewLine("Place disponible",
                    free.IsReliable ? ValueFormat.Bytes(free.Value) : "non lue"),
            };

            var willDo = new List<string>();
            foreach (var folder in plan)
                willDo.Add("Copier « " + folder.Label + " » : " + ValueFormat.Bytes(folder.Bytes) +
                           " en " + folder.Files.Count + " fichier(s)");

            willDo.Add("Relire chaque fichier copié et comparer son empreinte à celle de la source");
            willDo.Add("Déposer un manifeste de tout ce qui a été copié, dans le dossier de sauvegarde");

            var willNotDo = new List<string>
            {
                "Ne déplace, ne modifie et ne supprime rien à la source : le disque d'origine ressort " +
                "de l'opération tel qu'il y est entré.",
                "N'écrase aucun fichier de la destination : un fichier différent portant le même nom est " +
                "signalé, jamais remplacé.",
                "Ne copie pas les logiciels installés, ni les réglages de Windows, ni les mots de passe " +
                "enregistrés : ce sont des données du système, pas des fichiers du client.",
            };

            if (cloud > 0)
            {
                measurements.Add(new PreviewLine(
                    "Fichiers en ligne seulement", cloud + " fichier(s)", PreviewLineKind.Caution));
                willNotDo.Add(
                    cloud + " fichier(s) ne sont présents que dans le nuage : les copier les téléchargerait " +
                    "d'abord sur cette machine. Ils restent disponibles depuis le compte en ligne du client.");
            }

            if (truncated)
                measurements.Add(new PreviewLine(
                    "Relevé incomplet",
                    "un dossier compte plus de fichiers que ce relevé ne peut en tenir : la copie sera partielle",
                    PreviewLineKind.Caution));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = ValueFormat.Bytes(bytes) + " en " + files + " fichier(s) seront copiés vers " +
                          root + ", puis relus un par un.",
                WillDo = willDo,
                WillNotDo = willNotDo,
                Measurements = measurements,
                Plan = new BackupPlan { Destination = root, Folders = plan, Bytes = bytes, Files = files },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not BackupPlan plan)
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le relevé des données à copier n'a pas été établi : rien n'a été copié.",
                });

            var stopwatch = Stopwatch.StartNew();
            var manifest = new StringBuilder();
            manifest.AppendLine("dossier;fichier;octets;résultat");

            var counters = new Counters();
            var details = new List<string>();

            if (!context.Files.CreateDirectory(plan.Destination))
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le dossier de sauvegarde n'a pas pu être créé sur la destination.",
                });

            foreach (var folder in plan.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var here = new Counters();

                foreach (var file in folder.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Relative(folder.Path, file.Path);
                    var target = Path.Combine(plan.Destination, folder.Label, relative);

                    var result = context.Files.Copy(new FileCopyRequest(file, target), cancellationToken);

                    here.Add(result);
                    counters.Add(result);

                    manifest.Append(Csv(folder.Label)).Append(';')
                        .Append(Csv(relative)).Append(';')
                        .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(';')
                        .AppendLine(Describe(result.Outcome));

                    if (counters.Total % 25 == 0)
                        progress?.Report(new ActionProgress(
                            folder.Label + " : " + counters.Copied + " fichier(s) copiés",
                            plan.Files == 0 ? 0 : (double)counters.Total / plan.Files));

                    // Un support plein n'a aucune chance de se libérer pendant la copie :
                    // continuer produirait des milliers d'échecs identiques et ferait perdre une
                    // heure avant de le dire.
                    if (result.Outcome == FileCopyOutcome.NoSpace)
                    {
                        details.Add(folder.Label + ", copie interrompue : la destination est pleine.");
                        return Task.FromResult(Finish(context, plan, manifest, counters, details, stopwatch, true));
                    }
                }

                details.Add(folder.Label + " : " + here.Describe());
            }

            return Task.FromResult(Finish(context, plan, manifest, counters, details, stopwatch, false));
        }

        private static ActionOutcome Finish(
            ActionContext context, BackupPlan plan, StringBuilder manifest, Counters counters,
            List<string> details, Stopwatch stopwatch, bool interrupted)
        {
            stopwatch.Stop();

            var written = context.Files.WriteText(
                Path.Combine(plan.Destination, "manifeste.csv"), manifest.ToString());

            if (!written) details.Add("Le manifeste n'a pas pu être écrit dans le dossier de sauvegarde.");

            var status = interrupted || counters.Failed > 0
                ? counters.Copied > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed
                : ActionStatus.Succeeded;

            var summary = counters.Copied + " fichier(s) copiés et vérifiés, " +
                          ValueFormat.Bytes(counters.Bytes) + " écrits";

            if (counters.Skipped > 0) summary += ", " + counters.Skipped + " déjà présent(s)";
            if (counters.Failed > 0) summary += ", " + counters.Failed + " non copié(s)";
            summary += interrupted ? " : copie interrompue, la destination est pleine." : ".";

            return new ActionOutcome
            {
                Status = status,
                Summary = summary,
                Details = details,
                Duration = stopwatch.Elapsed,
            };
        }

        /// <summary>Les dossiers à copier, dans l'ordre de leur importance pour le client.</summary>
        /// <remarks>
        /// Le reste du profil n'en fait pas partie : il contient les profils de navigateur et les
        /// messageries, mais aussi des dizaines de gigaoctets de caches de logiciels qui n'ont
        /// aucun intérêt à être copiés. Le relevé continue de le peser et de le montrer : c'est
        /// au technicien de décider ce qu'il en fait.
        /// </remarks>
        private static IEnumerable<(string Label, string Path)> Sources(ActionContext context)
        {
            foreach (var entry in UserDataSurveyor.PersonalFolders())
                if (context.Files.DirectoryExists(entry.Path))
                    yield return entry;
        }

        private static string FolderName(ActionContext context)
        {
            var machine = context.Snapshot?.Machine.MachineName;
            if (string.IsNullOrWhiteSpace(machine)) machine = Environment.MachineName;

            return "LDI12-Sauvegarde-" + Sanitize(machine!) + "-" +
                   DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                builder.Append(char.IsLetterOrDigit(character) || character == '-' ? character : '-');
            return builder.ToString();
        }

        /// <summary>Le chemin du fichier sous son dossier d'origine, qui sera recréé à la destination.</summary>
        private static string Relative(string root, string path)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length)
                return path.Substring(root.Length).TrimStart('\\', '/');

            return Path.GetFileName(path);
        }

        private static string Csv(string value)
            => value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;

        internal static string Describe(FileCopyOutcome outcome) => outcome switch
        {
            FileCopyOutcome.Copied => "copié et vérifié",
            FileCopyOutcome.AlreadyPresent => "déjà présent",
            FileCopyOutcome.Conflict => "un autre fichier porte ce nom à la destination",
            FileCopyOutcome.Locked => "ouvert par un programme",
            FileCopyOutcome.NotFound => "disparu depuis le relevé",
            FileCopyOutcome.Changed => "modifié depuis le relevé",
            FileCopyOutcome.Denied => "accès refusé",
            FileCopyOutcome.NoSpace => "destination pleine",
            FileCopyOutcome.PathTooLong => "chemin trop long pour la destination",
            FileCopyOutcome.VerificationFailed => "relecture différente de la source : copie retirée",
            FileCopyOutcome.CloudOnly => "présent seulement en ligne",
            _ => "échec",
        };

        private static ActionPreview Blocked(string reason) => new ActionPreview
        {
            Outcome = PreviewOutcome.Blocked,
            Summary = reason,
            Blocker = reason,
        };

        /// <summary>Le compte de ce qui s'est passé, séparé de la façon de le raconter.</summary>
        private sealed class Counters
        {
            internal int Copied { get; private set; }
            internal int Skipped { get; private set; }
            internal int Failed { get; private set; }
            internal long Bytes { get; private set; }

            internal int Total => Copied + Skipped + Failed;

            internal void Add(FileCopyResult result)
            {
                switch (result.Outcome)
                {
                    case FileCopyOutcome.Copied:
                        Copied++;
                        Bytes += result.Bytes;
                        break;

                    case FileCopyOutcome.AlreadyPresent:
                        Skipped++;
                        break;

                    default:
                        Failed++;
                        break;
                }
            }

            internal string Describe()
            {
                var text = Copied + " fichier(s) copiés";
                if (Skipped > 0) text += ", " + Skipped + " déjà présent(s)";
                if (Failed > 0) text += ", " + Failed + " non copié(s)";
                return text + ".";
            }
        }
    }
}
