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
    /// <summary>Un dossier de la sauvegarde, relevé et prêt à revenir.</summary>
    public sealed class RestoreFolder
    {
        public RestoreItem Item { get; init; } = new RestoreItem();

        public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();

        public long Bytes { get; init; }

        /// <summary>Le dossier de destination existe et sera mis de côté avant la copie.</summary>
        public bool SetsAside { get; init; }
    }

    public sealed class RestorePlan
    {
        public string Backup { get; init; } = string.Empty;

        public IReadOnlyList<RestoreFolder> Folders { get; init; } = Array.Empty<RestoreFolder>();

        public IReadOnlyList<string> WifiProfiles { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> MissingSoftware { get; init; } = Array.Empty<string>();

        public long Bytes { get; init; }

        public int Files { get; init; }
    }

    /// <summary>
    /// Remet sur la machine les données d'une sauvegarde LDI12 : dossiers personnels, données
    /// d'applications et profils Wi-Fi.
    /// </summary>
    /// <remarks>
    /// <b>La moitié retour de la sauvegarde avant réinstallation.</b> Même exigence dans l'autre
    /// sens, avec une règle de plus : cette action écrit dans le profil d'un compte en service, et
    /// elle ne doit rien y faire disparaître.
    /// <list type="bullet">
    /// <item>rien n'est supprimé sur la machine. Un profil de navigateur créé par l'installation
    /// neuve est renommé et laissé à côté, jamais effacé ;</item>
    /// <item>rien n'est écrasé : dans un dossier fusionné, un fichier différent portant le même nom
    /// est signalé et laissé tel quel ;</item>
    /// <item>la sauvegarde n'est pas modifiée, elle reste la référence ;</item>
    /// <item>chaque fichier restauré est relu et comparé à celui de la sauvegarde.</item>
    /// </list>
    /// </remarks>
    public sealed class RestoreUserDataAction : IRepairAction
    {
        /// <summary>Le disque ou le dossier où chercher la sauvegarde.</summary>
        public const string SourceParameter = "source";

        /// <summary>« 0 » : ne pas réimporter les profils Wi-Fi. Absent : ils sont réimportés.</summary>
        public const string WifiParameter = "wifi";

        /// <summary>La lecture mesurée du support de la sauvegarde, voir <see cref="ReadSpeed.Encode"/>.</summary>
        public const string SourceSpeedParameter = "source-speed";

        /// <summary>La vitesse mesurée du disque de cette machine, voir <see cref="CopySpeed.Encode"/>.</summary>
        public const string TargetSpeedParameter = "target-speed";

        private const double SpaceMargin = 1.05;

        private const int MaxFilesPerFolder = 400_000;

        private static readonly TimeSpan ScanBudget = TimeSpan.FromMinutes(3);

        private readonly RestoreTargets? _targets;

        public RestoreUserDataAction() { }

        /// <summary>Restauration vers d'autres dossiers que ceux du compte : réservé aux essais.</summary>
        public RestoreUserDataAction(RestoreTargets targets)
            => _targets = targets ?? throw new ArgumentNullException(nameof(targets));

        private RestoreTargets Targets => _targets ?? RestoreTargets.Current();

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestoreUserData,
            DisplayName = "Restaurer une sauvegarde",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Moderate,
            Purpose = "Remet dans le compte ouvert les dossiers personnels, les données d'applications et les profils " +
                      "Wi-Fi d'une sauvegarde LDI12, en relisant chaque fichier et sans rien supprimer.",
            PlainPurpose = "Vos documents, vos favoris, votre courrier et vos pense-bêtes reviennent à leur place. " +
                           "Rien n'est effacé sur l'ordinateur, et la sauvegarde n'est pas modifiée.",
            TypicalDuration = TimeSpan.FromMinutes(20),
            HardTimeout = TimeSpan.FromHours(8),
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null)
                return ActionReadiness.No("Aucune sauvegarde n'a été désignée.",
                    "Indiquez le disque ou le dossier qui contient la sauvegarde.");

            return context.Files.DirectoryExists(source)
                ? ActionReadiness.Ready
                : ActionReadiness.No("Le dossier « " + source + " » n'existe pas ou n'est pas accessible.");
        }

        public async Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null) return Blocked("Aucune sauvegarde n'a été désignée.");

            var backup = RestoreCatalog.Find(context.Files, source, out var others);
            if (backup == null)
                return Blocked("Aucune sauvegarde LDI12 n'a été trouvée dans « " + source + " ». Une sauvegarde se " +
                               "reconnaît à son manifeste ou à sa fiche de réinstallation.");

            var targets = Targets;
            var layout = RestoreCatalog.Build(context.Files, backup, targets);
            var withWifi = context.Parameter(WifiParameter) != "0";
            var running = await RunningAsync(context, cancellationToken).ConfigureAwait(false);

            var folders = new List<RestoreFolder>();
            var blockedByProcess = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            long bytes = 0;
            var files = 0;
            var unreadable = 0;

            foreach (var item in layout.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsRunning(item, running))
                {
                    blockedByProcess.Add(item.Application ?? item.Label);
                    continue;
                }

                if (item.Mode == RestoreMode.ChromiumLocalState)
                {
                    folders.Add(new RestoreFolder { Item = item });
                    continue;
                }

                var scan = context.Files.Scan(
                    new DirectoryScanRequest(item.Source) { MaxFiles = MaxFilesPerFolder, Budget = ScanBudget },
                    cancellationToken);
                if (!scan.HasValue) continue;
                unreadable += scan.Value.InaccessibleDirectories;

                var kept = new List<FileEntry>();
                long size = 0;
                foreach (var file in scan.Value.Files)
                {
                    // Le reste d'une copie interrompue pendant la sauvegarde : jamais vérifié, jamais restauré.
                    if (file.Path.EndsWith(FileCopyRequest.PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Keep != null && !item.Keep(Path.GetFileName(file.Path))) continue;
                    kept.Add(file);
                    size += file.SizeBytes;
                }

                if (kept.Count == 0) continue;

                folders.Add(new RestoreFolder
                {
                    Item = item,
                    Files = kept,
                    Bytes = size,
                    SetsAside = item.Mode == RestoreMode.ReplaceFolder && context.Files.DirectoryExists(item.Destination),
                });
                bytes += size;
                files += kept.Count;
            }

            var wifi = withWifi ? layout.WifiProfiles : Array.Empty<string>();

            if (files == 0 && wifi.Count == 0)
                return new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = blockedByProcess.Count > 0
                        ? "Rien ne peut être restauré tant que ces programmes sont ouverts : " +
                          string.Join(", ", blockedByProcess) + "."
                        : "La sauvegarde « " + Path.GetFileName(backup) + " » ne contient rien à remettre sur cette machine.",
                };

            var free = context.Files.FreeSpace(targets.UserProfile);
            if (free.IsReliable && free.Value < (long)(bytes * SpaceMargin))
                return Blocked("Il manque de la place sur cette machine : " + ValueFormat.Bytes(bytes) + " à restaurer, " +
                               ValueFormat.Bytes(free.Value) + " disponibles.");

            var missing = MissingSoftware(context, backup, out var softwareNote);

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Sauvegarde", backup),
                new PreviewLine("À restaurer", ValueFormat.Bytes(bytes) + " en " + files + " fichier(s)"),
            };

            var state = BackupState.Parse(Text(context, Path.Combine(backup, BackupState.FileName)));
            if (state != null && state.State != BackupProgressState.Finished)
                measurements.Add(new PreviewLine("Sauvegarde incomplète",
                    "elle s'est arrêtée avant la fin. Ce qu'elle contient a été vérifié et se restaure, mais des " +
                    "fichiers peuvent manquer : la reprendre sur l'ancienne machine si elle est encore disponible",
                    PreviewLineKind.Caution));

            if (others > 0)
                measurements.Add(new PreviewLine("Autres sauvegardes",
                    others + " plus ancienne(s) dans le même dossier, laissée(s) de côté", PreviewLineKind.Caution));

            if (unreadable > 0)
                measurements.Add(new PreviewLine("Dossiers illisibles",
                    unreadable + " dossier(s) de la sauvegarde n'ont pas pu être ouverts, leur contenu ne sera pas restauré",
                    PreviewLineKind.Caution));

            if (blockedByProcess.Count > 0)
                measurements.Add(new PreviewLine("Programmes ouverts",
                    string.Join(", ", blockedByProcess) + ". Leurs données ne seront pas restaurées tant qu'ils ne sont " +
                    "pas fermés : les fermer, puis préparer de nouveau", PreviewLineKind.Caution));

            // La durée : les deux supports doivent avoir été mesurés, la sauvegarde qu'on lit et le
            // disque qui reçoit. Les pilotes et les applications n'y figurent pas : les premiers
            // prennent quelques minutes, les secondes dépendent de la connexion.
            var sourceSpeed = ReadSpeed.Decode(context.Parameter(SourceSpeedParameter));
            var targetSpeed = CopySpeed.Decode(context.Parameter(TargetSpeedParameter));
            string? duration = null;
            if (files > 0 && sourceSpeed != null && targetSpeed != null)
            {
                duration = CopyEstimate.Describe(CopyEstimate.RestoreDuration(bytes, files, sourceSpeed, targetSpeed));
                measurements.Add(new PreviewLine("Durée estimée",
                    duration + " pour les fichiers, d'après la mesure des deux disques : sauvegarde en " +
                    CopyEstimate.Speeds(sourceSpeed) + ", cette machine en " + CopyEstimate.Speeds(targetSpeed) +
                    ". Sans compter les applications, qui dépendent de la connexion"));

                if (CopyEstimate.Advice(sourceSpeed) is string advice)
                    measurements.Add(new PreviewLine("Support lent", advice, PreviewLineKind.Caution));
            }
            else if (files > 0)
            {
                measurements.Add(new PreviewLine("Durée estimée",
                    "non estimée : les deux disques n'ont pas encore été mesurés"));
            }

            if (missing.Count > 0)
                measurements.Add(new PreviewLine("Logiciels à réinstaller",
                    missing.Count + " logiciel(s) de l'ancienne installation absents ici", PreviewLineKind.Caution));

            var willDo = new List<string>();
            foreach (var folder in folders)
            {
                var item = folder.Item;
                if (item.Mode == RestoreMode.ChromiumLocalState)
                {
                    willDo.Add("Ajouter les profils restaurés à la liste des profils de " + item.Application +
                               ", sans reprendre l'ancienne clé de chiffrement");
                    continue;
                }

                if (folder.SetsAside)
                    willDo.Add("Mettre de côté le dossier actuel « " + item.Destination + " » sous le nom « " +
                               Path.GetFileName(item.Destination) + ".avant-restauration-… »");

                willDo.Add("Remettre « " + item.Label + " » : " + ValueFormat.Bytes(folder.Bytes) + " en " +
                           folder.Files.Count + " fichier(s), dans " + item.Destination);
            }

            willDo.Add("Relire chaque fichier restauré et comparer son empreinte à celle de la sauvegarde");

            if (wifi.Count > 0)
                willDo.Add("Réimporter " + wifi.Count + " profil(s) Wi-Fi pour ce compte");

            var willNotDo = new List<string>
            {
                "Ne supprime rien sur cette machine : un profil remplacé est renommé et laissé à côté, à effacer une " +
                "fois le résultat vérifié.",
                "N'écrase aucun fichier : dans un dossier fusionné, un fichier différent portant le même nom est " +
                "signalé et laissé tel quel.",
                "Ne modifie pas la sauvegarde, qui reste la référence.",
                "Ne réinstalle aucun logiciel : ceux qui manquent sont listés ci-dessous.",
            };

            if (HasChromium(folders))
                willNotDo.Add("Ne ramène pas les mots de passe ni les connexions aux sites de Chrome et d'Edge : ils étaient " +
                              "chiffrés pour l'ancienne installation. Favoris, historique, extensions et réglages reviennent. " +
                              "Le navigateur peut aussi remettre à zéro sa page d'accueil et son moteur de recherche, qu'il " +
                              "lie à la machine.");

            if (!withWifi && layout.WifiProfiles.Count > 0)
                willNotDo.Add("Ne réimporte pas les " + layout.WifiProfiles.Count + " profil(s) Wi-Fi : l'option n'est pas cochée.");

            foreach (var line in layout.Left) willNotDo.Add(line);
            if (softwareNote != null) willNotDo.Add(softwareNote);
            foreach (var name in missing) willNotDo.Add("À réinstaller : " + name);

            return new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = ValueFormat.Bytes(bytes) + " en " + files + " fichier(s) seront restaurés depuis « " +
                          Path.GetFileName(backup) + " », puis relus un par un." +
                          (duration == null ? string.Empty : " Durée estimée : " + duration + "."),
                WillDo = willDo,
                WillNotDo = willNotDo,
                Measurements = measurements,
                Plan = new RestorePlan
                {
                    Backup = backup,
                    Folders = folders,
                    WifiProfiles = wifi,
                    MissingSoftware = missing,
                    Bytes = bytes,
                    Files = files,
                },
            };
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not RestorePlan plan)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le relevé de la sauvegarde n'a pas été établi : rien n'a été restauré.",
                };

            var stopwatch = Stopwatch.StartNew();
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            var details = new List<string>();
            var running = await RunningAsync(context, cancellationToken).ConfigureAwait(false);

            int copied = 0, skipped = 0, failed = 0;
            long written = 0;
            var itemsFailed = 0;
            var tracker = new CopyProgress(progress, plan.Bytes, plan.Files, "Restauration de");

            foreach (var folder in plan.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = folder.Item;
                tracker.Folder(item.Label);

                // Relu au moment d'agir : entre la préparation et le clic, le client a pu rouvrir
                // son navigateur. Un profil remplacé sous un navigateur ouvert serait réécrit par
                // lui à la fermeture.
                if (IsRunning(item, running))
                {
                    details.Add(item.Label + " : non restauré, " + item.Application + " est ouvert.");
                    itemsFailed++;
                    tracker.Skip(folder.Files);
                    continue;
                }

                if (item.Mode == RestoreMode.ChromiumLocalState)
                {
                    details.Add(item.Label + " : " + LocalState(context, item, stamp));
                    continue;
                }

                if (folder.SetsAside && context.Files.DirectoryExists(item.Destination))
                {
                    var aside = item.Destination + ".avant-restauration-" + stamp;
                    if (!context.Files.MoveDirectory(item.Destination, aside))
                    {
                        details.Add(item.Label + " : non restauré. Le dossier actuel n'a pas pu être mis de côté, " +
                                    "probablement parce qu'un programme le tient ouvert.");
                        itemsFailed++;
                        failed += folder.Files.Count;
                        tracker.Skip(folder.Files);
                        continue;
                    }

                    details.Add(item.Label + " : le dossier présent a été mis de côté sous « " + Path.GetFileName(aside) + " ».");
                }

                int here = 0, hereSkipped = 0, hereFailed = 0;
                foreach (var file in folder.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = file.Path.Length > item.Source.Length
                        ? file.Path.Substring(item.Source.Length).TrimStart('\\', '/')
                        : Path.GetFileName(file.Path);

                    var result = context.Files.Copy(
                        new FileCopyRequest(file, Path.Combine(item.Destination, relative)) { Progressed = tracker.Bytes },
                        cancellationToken);
                    tracker.FileDone(file.SizeBytes);

                    switch (result.Outcome)
                    {
                        case FileCopyOutcome.Copied: here++; written += result.Bytes; break;
                        case FileCopyOutcome.AlreadyPresent: hereSkipped++; break;
                        default: hereFailed++; break;
                    }

                    if (result.Outcome == FileCopyOutcome.NoSpace)
                    {
                        details.Add(item.Label + " : restauration interrompue, le disque est plein.");
                        copied += here; skipped += hereSkipped; failed += hereFailed;
                        return Finish(plan, copied, skipped, failed, written, itemsFailed + 1, details, stopwatch,
                            " : restauration interrompue, le disque est plein.");
                    }

                    // Le support de la sauvegarde débranché : chaque fichier restant échouerait.
                    // Ce qui est déjà restauré porte son vrai nom et a été vérifié : relancer la
                    // restauration le reconnaîtra et ne refera que le reste.
                    if (result.Outcome != FileCopyOutcome.Copied && result.Outcome != FileCopyOutcome.AlreadyPresent &&
                        !context.Files.DirectoryExists(plan.Backup))
                    {
                        details.Add(item.Label + " : restauration interrompue, le support de la sauvegarde ne répond plus.");
                        copied += here; skipped += hereSkipped; failed += hereFailed;
                        return Finish(plan, copied, skipped, failed, written, itemsFailed + 1, details, stopwatch,
                            " : restauration interrompue, le support de la sauvegarde ne répond plus. Rebranchez-le et " +
                            "relancez : les fichiers déjà restaurés ne seront pas recopiés.");
                    }
                }

                copied += here;
                skipped += hereSkipped;
                failed += hereFailed;

                var text = item.Label + " : " + here + " fichier(s) restaurés";
                if (hereSkipped > 0) text += ", " + hereSkipped + " déjà présent(s)";
                if (hereFailed > 0) text += ", " + hereFailed + " non restauré(s), le plus souvent parce qu'un fichier différent porte déjà ce nom";
                details.Add(text + ".");
            }

            if (plan.WifiProfiles.Count > 0)
            {
                progress?.Report(new ActionProgress("Réimport des profils Wi-Fi…", 1));
                details.Add("Wi-Fi : " + await WifiAsync(context, plan.WifiProfiles, cancellationToken).ConfigureAwait(false));
            }

            if (plan.MissingSoftware.Count > 0)
                details.Add("Logiciels encore à réinstaller : " + string.Join(", ", plan.MissingSoftware) + ".");

            return Finish(plan, copied, skipped, failed, written, itemsFailed, details, stopwatch, null);
        }

        private static string? Text(ActionContext context, string path)
        {
            var text = context.Files.ReadText(path);
            return text.HasValue ? text.Value : null;
        }

        private static ActionOutcome Finish(
            RestorePlan plan, int copied, int skipped, int failed, long written, int itemsFailed,
            List<string> details, Stopwatch stopwatch, string? interruption)
        {
            stopwatch.Stop();
            var interrupted = interruption != null;

            var status = interrupted || failed > 0 || itemsFailed > 0
                ? copied > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed
                : ActionStatus.Succeeded;

            var summary = copied + " fichier(s) restaurés et vérifiés, " + ValueFormat.Bytes(written) + " écrits";
            if (skipped > 0) summary += ", " + skipped + " déjà présent(s)";
            if (failed > 0) summary += ", " + failed + " non restauré(s)";
            summary += interruption ?? ".";

            return new ActionOutcome { Status = status, Summary = summary, Details = details, Duration = stopwatch.Elapsed };
        }

        private static string LocalState(ActionContext context, RestoreItem item, string stamp)
        {
            var saved = context.Files.ReadText(item.Source);
            if (!saved.HasValue) return "la liste des profils n'a pas pu être lue dans la sauvegarde.";

            var exists = context.Files.FileExists(item.Destination);
            var current = exists ? context.Files.ReadText(item.Destination) : default;
            if (exists && !current.HasValue) return "la liste actuelle des profils n'a pas pu être lue, elle est laissée telle quelle.";

            var merged = ChromiumLocalState.Merge(saved.Value, exists ? current.Value : null, item.Profiles);
            if (merged == null) return "la liste des profils de la sauvegarde est illisible, elle n'a pas été reprise.";

            if (!exists)
            {
                var parent = Path.GetDirectoryName(item.Destination);
                if (!string.IsNullOrEmpty(parent)) context.Files.CreateDirectory(parent!);
                return context.Files.WriteText(item.Destination, merged)
                    ? "reprise de la sauvegarde, sans l'ancienne clé de chiffrement."
                    : "la liste des profils n'a pas pu être écrite.";
            }

            // L'original est gardé à côté avant d'être réécrit : c'est la seule écriture de cette
            // action qui remplace un fichier existant.
            if (!context.Files.WriteText(item.Destination + ".avant-restauration-" + stamp, current.Value))
                return "la liste actuelle des profils n'a pas pu être mise de côté, elle est laissée telle quelle.";

            return context.Files.ReplaceText(item.Destination, merged)
                ? "profils restaurés ajoutés à la liste du navigateur, qui garde sa propre clé de chiffrement."
                : "la liste des profils n'a pas pu être mise à jour.";
        }

        /// <summary>
        /// Réimporte les profils Wi-Fi, un par un.
        /// </summary>
        /// <remarks>
        /// Pour le compte ouvert seulement : l'import pour tous les utilisateurs demande les droits
        /// administrateur, et ce n'est pas à une restauration de données de les réclamer. Un profil
        /// dont la clé était chiffrée pour l'ancienne machine est refusé par Windows, et compté
        /// comme tel.
        /// </remarks>
        private static async Task<string> WifiAsync(
            ActionContext context, IReadOnlyList<string> profiles, CancellationToken cancellationToken)
        {
            var imported = 0;
            var refused = new List<string>();

            foreach (var path in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await context.Processes.RunAsync(
                    new ProcessRequest("netsh.exe", "wlan add profile filename=\"" + path + "\" user=current")
                    {
                        Timeout = TimeSpan.FromSeconds(30),
                        OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    },
                    cancellationToken).ConfigureAwait(false);

                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("Wi-Fi-", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);

                if (result.Completed && result.ExitCode == 0) imported++;
                else refused.Add(name);
            }

            var text = imported + " profil(s) réimporté(s)";
            if (refused.Count > 0)
                text += ", " + refused.Count + " refusé(s) par Windows (" + string.Join(", ", refused) + ") : service Wi-Fi " +
                        "absent, ou clé chiffrée pour l'ancienne machine";
            return text + ".";
        }

        /// <summary>
        /// Les logiciels de l'ancienne installation qui ne figurent pas sur celle-ci.
        /// </summary>
        /// <remarks>
        /// Rapprochés par le nom, sans les numéros de version ni les mentions entre parenthèses :
        /// « 7-Zip 23.01 (x64) » et « 7-Zip 24.08 (x64) » sont le même logiciel. Rien de plus
        /// malin : un rapprochement plus lâche finirait par déclarer présent un logiciel absent, et
        /// c'est l'erreur qui coûte, puisqu'on ne le réinstallerait pas.
        /// </remarks>
        internal static IReadOnlyList<string> MissingSoftware(ActionContext context, string backup, out string? note)
        {
            note = null;
            var missing = new List<string>();

            var csv = context.Files.ReadText(Path.Combine(backup, ReinstallSheet.SoftwareFileName));
            if (!csv.HasValue)
            {
                note = "La sauvegarde ne contient pas de liste de logiciels : les logiciels à réinstaller ne peuvent pas être comparés.";
                return missing;
            }

            var software = context.Snapshot?.Windows.Software;
            if (software == null || (software.Programs.Count == 0 && software.StoreApps.Count == 0))
            {
                note = "Aucune analyse de cette machine n'a relevé ses logiciels : la liste de ceux à réinstaller est dans " +
                       "la fiche de réinstallation de la sauvegarde.";
                return missing;
            }

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var program in software.Programs) present.Add(Normalize(program.Name));
            foreach (var app in software.StoreApps) present.Add(Normalize(app.Name));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var line in csv.Value.Split('\n'))
            {
                if (first) { first = false; continue; }
                var fields = SplitCsv(line.TrimEnd('\r'));
                if (fields.Count == 0 || fields[0].Length == 0) continue;

                var key = Normalize(fields[0]);
                if (key.Length == 0 || present.Contains(key) || !seen.Add(key)) continue;
                missing.Add(fields[0]);
            }

            missing.Sort(StringComparer.CurrentCultureIgnoreCase);
            return missing;
        }

        internal static string Normalize(string name)
        {
            var builder = new StringBuilder();
            var depth = 0;
            foreach (var character in name.ToLowerInvariant())
            {
                if (character == '(') { depth++; continue; }
                if (character == ')') { if (depth > 0) depth--; continue; }
                if (depth == 0) builder.Append(character);
            }

            var words = new List<string>();
            foreach (var word in builder.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var version = word.TrimStart('v');
                var numeric = version.Length > 0;
                foreach (var character in version)
                    if (!char.IsDigit(character) && character != '.') { numeric = false; break; }
                if (!numeric) words.Add(word);
            }

            return string.Join(" ", words);
        }

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

        private static async Task<HashSet<string>> RunningAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = await context.Processes.RunAsync(
                new ProcessRequest("tasklist.exe", "/fo csv /nh") { Timeout = TimeSpan.FromSeconds(20) },
                cancellationToken).ConfigureAwait(false);

            if (!result.Completed || result.ExitCode != 0) return images;

            foreach (var line in result.StandardOutput.Split('\r', '\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"", StringComparison.Ordinal)) continue;
                var end = trimmed.IndexOf('"', 1);
                if (end > 1) images.Add(trimmed.Substring(1, end - 1));
            }

            return images;
        }

        private static bool IsRunning(RestoreItem item, ISet<string> running)
        {
            foreach (var process in item.Processes)
                if (running.Contains(process)) return true;
            return false;
        }

        private static bool HasChromium(IEnumerable<RestoreFolder> folders)
        {
            foreach (var folder in folders)
                if (folder.Item.Application == "Google Chrome" || folder.Item.Application == "Microsoft Edge") return true;
            return false;
        }

        private static ActionPreview Blocked(string reason) => new ActionPreview
        {
            Outcome = PreviewOutcome.Blocked,
            Summary = reason,
            Blocker = reason,
        };
    }
}
