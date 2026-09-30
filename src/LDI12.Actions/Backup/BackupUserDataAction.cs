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

        /// <summary>Emplacement dans la sauvegarde. Vide : le nom du dossier.</summary>
        public string Target { get; init; } = string.Empty;

        /// <summary>Application d'origine. Nul pour un dossier personnel.</summary>
        public string? Application { get; init; }

        public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();

        public long Bytes { get; init; }

        public int CloudOnlyFiles { get; init; }

        /// <summary>Le relevé s'est arrêté avant la fin : la copie serait incomplète.</summary>
        public bool Truncated { get; init; }

        /// <summary>Dossiers que le relevé n'a pas pu ouvrir : leur contenu ne sera pas copié.</summary>
        public int InaccessibleDirectories { get; init; }

        internal string Destination => Target.Length > 0 ? Target : Label;
    }

    /// <summary>Ce que la prévisualisation a établi, et que l'exécution se contente de suivre.</summary>
    public sealed class BackupPlan
    {
        public string Destination { get; init; } = string.Empty;

        public IReadOnlyList<BackupFolder> Folders { get; init; } = Array.Empty<BackupFolder>();

        public long Bytes { get; init; }

        public int Files { get; init; }

        /// <summary>Applications trouvées, qu'il y ait ou non quelque chose à copier chez elles.</summary>
        public IReadOnlyList<AppDataApplication> Applications { get; init; } = Array.Empty<AppDataApplication>();

        public bool ExportWifi { get; init; }

        /// <summary>Applications cochées, à réinstaller par winget. Vide : aucune liste déposée.</summary>
        public IReadOnlyList<string> WingetPackages { get; init; } = Array.Empty<string>();

        public int CloudOnlyFiles { get; init; }

        /// <summary>
        /// Reprise d'une sauvegarde interrompue : son dossier est réutilisé, et ce qui y porte déjà
        /// son vrai nom, à la même taille et à la même date, n'est pas recopié.
        /// </summary>
        public bool Resumed { get; init; }

        /// <summary>À la reprise, ce que le dossier contient déjà : la durée ne compte que le reste.</summary>
        public long AlreadyBytes { get; init; }
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
    /// <b>Tout ce qu'il faut pour réinstaller, en un clic.</b> Aux dossiers personnels
    /// s'ajoutent les données des applications que le catalogue connaît (profils de
    /// navigateurs, messagerie, pense-bêtes), et une fiche de réinstallation qui dit ce qui a été
    /// copié, ce qui ne suit pas, et quels logiciels remettre. Les profils Wi-Fi, eux, restent une
    /// option explicite : leurs clés sortent en clair.
    /// </para>
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
        /// « 0 » écarte les dossiers personnels. Absent : ils sont copiés.
        /// </summary>
        /// <remarks>
        /// Pour l'atelier où les documents ont déjà été recopiés à la main, ou vivent sur une
        /// partition qu'on ne réinstalle pas : relancer quarante gigaoctets de copie pour
        /// récupérer des favoris serait absurde.
        /// </remarks>
        public const string PersonalParameter = "personal";

        /// <summary>« 0 » écarte les données d'applications. Absent : elles sont copiées.</summary>
        public const string ApplicationsParameter = "applications";

        /// <summary>« 1 » exporte les profils Wi-Fi avec leurs clés. Absent : rien n'est exporté.</summary>
        public const string WifiParameter = "wifi";

        /// <summary>
        /// Les applications à réinstaller, une par ligne, en identifiants winget. Absent : aucune.
        /// </summary>
        /// <remarks>
        /// Seule leur liste est déposée : un logiciel installé ne se copie pas, voir
        /// <see cref="WingetApplications"/>.
        /// </remarks>
        public const string WingetParameter = "winget";

        /// <summary>
        /// « 1 » : des pilotes accompagnent cette sauvegarde. Absent : non.
        /// </summary>
        /// <remarks>
        /// Ils sont exportés par <see cref="ExportDriversAction"/>, avec les droits administrateur,
        /// dans le dossier que celle-ci établit. Même sans aucune donnée à copier, la sauvegarde
        /// dépose alors son manifeste et sa fiche : c'est à eux qu'une restauration la reconnaît.
        /// </remarks>
        public const string DriversParameter = "drivers";

        /// <summary>
        /// « 0 » : toujours commencer une nouvelle sauvegarde. Absent : reprendre la dernière
        /// sauvegarde inachevée de cette machine et de ce compte sur ce support, s'il y en a une.
        /// </summary>
        public const string ResumeParameter = "resume";

        /// <summary>
        /// La vitesse mesurée du support, voir <see cref="CopySpeed.Encode"/>. Absent : la durée
        /// n'est pas estimée, et la prévisualisation dit comment l'obtenir.
        /// </summary>
        public const string SpeedParameter = "speed";

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
            Purpose = "Copie les dossiers personnels et les données d'applications vers un support externe, relit " +
                      "chaque fichier copié, et dépose un manifeste et une fiche de réinstallation.",
            PlainPurpose = "Vos documents, images, musiques, fichiers du bureau et données de logiciels (favoris, " +
                           "messagerie, pense-bêtes) sont copiés sur le support choisi. Rien n'est déplacé ni " +
                           "effacé : l'original reste exactement où il est.",
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

        public async Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var destination = context.Parameter(DestinationParameter);
            if (destination == null) return Blocked("Aucun dossier de destination n'a été choisi.");

            var withPersonal = context.Parameter(PersonalParameter) != "0";
            var withApplications = context.Parameter(ApplicationsParameter) != "0";
            var withWifi = context.Parameter(WifiParameter) == "1";
            var winget = WingetApplications.Decode(context.Parameter(WingetParameter));
            var withDrivers = context.Parameter(DriversParameter) == "1";

            var plan = new List<BackupFolder>();
            long bytes = 0;
            var files = 0;
            var cloud = 0;
            var truncated = false;
            var unreadable = 0;

            void Take(BackupFolder? folder)
            {
                if (folder == null) return;
                plan.Add(folder);
                bytes += folder.Bytes;
                files += folder.Files.Count;
                cloud += folder.CloudOnlyFiles;
                truncated |= folder.Truncated;
                unreadable += folder.InaccessibleDirectories;
            }

            foreach (var folder in withPersonal ? Sources(context) : Array.Empty<(string Label, string Path)>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Take(Survey(context, folder.Label, folder.Path, new DirectoryScanRequest(folder.Path)
                {
                    MaxFiles = MaxFilesPerFolder,
                    Budget = ScanBudget,
                }, null, null, null, cancellationToken));
            }

            var applications = withApplications
                ? AppDataCatalog.Detect(context.Files)
                : Array.Empty<AppDataApplication>();

            foreach (var application in applications)
                foreach (var source in application.Sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Take(Survey(context, Label(source.Target), source.Root, new DirectoryScanRequest(source.Root)
                    {
                        MaxFiles = MaxFilesPerFolder,
                        Budget = ScanBudget,
                        TopLevelOnly = source.TopLevelOnly,
                        ExcludeRelative = source.ExcludeRelative,
                    }, source.Keep, source.Target, application.Name, cancellationToken));
                }

            if (files == 0 && !withWifi && winget.Count == 0 && !withDrivers)
                return new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Aucun fichier à sauvegarder dans les dossiers personnels ni les données d'applications " +
                              "de cette session.",
                };

            var resume = context.Parameter(ResumeParameter) == "0"
                ? null
                : BackupState.FindResumable(context.Files, destination, MachineName(context), Environment.UserName);

            // À la reprise, ce qui est déjà sur le support n'a pas à y trouver de place une seconde fois.
            long already = 0;
            if (resume != null)
            {
                var measure = context.Files.Measure(new DirectoryMeasureRequest(resume.Path), cancellationToken);
                if (measure.HasValue) already = measure.Value.TotalBytes;
            }

            var free = context.Files.FreeSpace(destination);
            var needed = Math.Max(0, (long)(bytes * SpaceMargin) - already);

            if (free.IsReliable && free.Value < needed)
                return Blocked(
                    "Il manque de la place sur la destination : " + ValueFormat.Bytes(bytes) +
                    " à copier, " + ValueFormat.Bytes(free.Value) + " disponibles.");

            var root = resume?.Path ?? Path.Combine(destination, FolderName(context));

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Destination", root),
                new PreviewLine("Reprise", resume == null
                    ? "nouvelle sauvegarde"
                    : "sauvegarde interrompue du " + resume.StartedText + " : les fichiers déjà copiés et vérifiés " +
                      "(" + ValueFormat.Bytes(already) + " sur le support) ne seront pas recopiés"),
                new PreviewLine("À copier", ValueFormat.Bytes(bytes) + " en " + files + " fichier(s)"),
                new PreviewLine("Place disponible",
                    free.IsReliable ? ValueFormat.Bytes(free.Value) : "non lue"),
            };

            // La durée, avant tout le reste : c'est elle qui décide de lancer maintenant, de changer
            // de port ou de support. À la reprise, seule la part qui reste à copier est comptée.
            var speed = CopySpeed.Decode(context.Parameter(SpeedParameter));
            string? duration = null;
            if (speed != null && files > 0)
            {
                var remaining = Math.Max(0, bytes - already);
                var share = bytes > 0 ? (double)remaining / bytes : 1;
                duration = CopyEstimate.Describe(CopyEstimate.Duration(remaining, (int)Math.Ceiling(files * share), speed));

                measurements.Add(new PreviewLine("Durée estimée",
                    duration + ", d'après la mesure du support : " + CopyEstimate.Speeds(speed) +
                    ". Plus long si le disque de cette machine est lent ou abîmé"));

                var advice = CopyEstimate.Advice(speed);
                if (advice != null) measurements.Add(new PreviewLine("Support lent", advice, PreviewLineKind.Caution));
            }
            else if (files > 0)
            {
                measurements.Add(new PreviewLine("Durée estimée",
                    "non estimée : le support n'a pas encore été mesuré"));
            }

            var willDo = new List<string>();
            foreach (var folder in plan)
                if (folder.Application == null)
                    willDo.Add("Copier « " + folder.Label + " » : " + ValueFormat.Bytes(folder.Bytes) +
                               " en " + folder.Files.Count + " fichier(s)");

            var willNotDo = new List<string>
            {
                "Ne déplace, ne modifie et ne supprime rien à la source : le disque d'origine ressort " +
                "de l'opération tel qu'il y est entré.",
                "N'écrase aucun fichier de la destination : un fichier différent portant le même nom est " +
                "signalé, jamais remplacé.",
                "Ne copie pas les logiciels eux-mêmes ni les réglages de Windows : la fiche de réinstallation " +
                "liste les logiciels à remettre, avec leurs versions.",
            };

            if (!withPersonal)
                willNotDo.Add("Ne copie pas les dossiers personnels (Bureau, Documents, Images…) : l'option n'est pas cochée.");

            Applications(applications, plan, withApplications, willDo, willNotDo);

            willDo.Add("Relire chaque fichier copié et comparer son empreinte à celle de la source");

            if (withWifi)
                willDo.Add("Exporter les profils Wi-Fi enregistrés dans « " + WifiExport.Folder + " », avec leurs clés " +
                           "en clair quand Windows les livre : le support de sauvegarde devra être gardé en conséquence");
            else
                willNotDo.Add("N'exporte pas les profils Wi-Fi : l'option n'est pas cochée.");

            if (withDrivers)
                willDo.Add("Recevoir les pilotes cochés dans « " + DriverBackup.Folder + " », exportés à part avec les " +
                           "droits administrateur, avant la copie des fichiers");

            if (winget.Count > 0)
                willDo.Add("Déposer la liste des " + winget.Count + " application(s) cochée(s), à réinstaller par winget " +
                           "(« " + WingetApplications.FileName + " »), et un script qui les réinstalle d'un double clic");

            var software = Inventory(context);
            willDo.Add(software == null
                ? "Déposer la fiche de réinstallation et le manifeste de la copie"
                : "Déposer la fiche de réinstallation (" + software.Programs.Count + " logiciel(s) et " +
                  software.StoreApps.Count + " application(s) du Store, avec leurs versions) et le manifeste de la copie");

            if (software == null)
                measurements.Add(new PreviewLine(
                    "Logiciels installés",
                    "aucune analyse ne les a relevés, la fiche ne pourra pas les lister",
                    PreviewLineKind.Caution));

            var running = await RunningAsync(context, applications, cancellationToken).ConfigureAwait(false);
            if (running.Count > 0)
                measurements.Add(new PreviewLine(
                    "Programmes ouverts",
                    string.Join(", ", running) + ". À fermer avant la copie : un fichier tenu ouvert est " +
                    "refusé, ou copié en cours d'écriture",
                    PreviewLineKind.Caution));

            if (cloud > 0)
            {
                measurements.Add(new PreviewLine(
                    "Fichiers en ligne seulement", cloud + " fichier(s)", PreviewLineKind.Caution));
                willNotDo.Add(
                    cloud + " fichier(s) ne sont présents que dans le nuage : les copier les téléchargerait " +
                    "d'abord sur cette machine. Ils restent disponibles depuis le compte en ligne du client.");
            }

            if (unreadable > 0)
                measurements.Add(new PreviewLine(
                    "Dossiers illisibles",
                    unreadable + " dossier(s) n'ont pas pu être ouverts, faute de droits, leur contenu ne sera pas copié",
                    PreviewLineKind.Caution));

            if (truncated)
                measurements.Add(new PreviewLine(
                    "Relevé incomplet",
                    "un dossier compte plus de fichiers que ce relevé ne peut en tenir, la copie sera partielle",
                    PreviewLineKind.Caution));

            return new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = (resume == null ? string.Empty : "Reprise de la sauvegarde interrompue du " + resume.StartedText + ". ") +
                          ValueFormat.Bytes(bytes) + " en " + files + " fichier(s) seront copiés vers " +
                          root + ", puis relus un par un." +
                          (duration == null ? string.Empty : " Durée estimée : " + duration + "."),
                WillDo = willDo,
                WillNotDo = willNotDo,
                Measurements = measurements,
                Plan = new BackupPlan
                {
                    Destination = root,
                    Resumed = resume != null,
                    AlreadyBytes = already,
                    Folders = plan,
                    Bytes = bytes,
                    Files = files,
                    Applications = applications,
                    ExportWifi = withWifi,
                    WingetPackages = winget,
                    CloudOnlyFiles = cloud,
                },
            };
        }

        /// <summary>Relève un dossier et ne garde que ce qu'une copie emporterait.</summary>
        private static BackupFolder? Survey(
            ActionContext context, string label, string path, DirectoryScanRequest request, Func<string, bool>? keep,
            string? target, string? application, CancellationToken cancellationToken)
        {
            var scan = context.Files.Scan(request, cancellationToken);
            if (!scan.HasValue) return null;

            var kept = new List<FileEntry>();
            long size = 0;
            var cloudHere = 0;

            foreach (var file in scan.Value.Files)
            {
                if (keep != null && !keep(System.IO.Path.GetFileName(file.Path))) continue;

                if (file.CloudOnly)
                {
                    cloudHere++;
                    continue;
                }

                kept.Add(file);
                size += file.SizeBytes;
            }

            if (kept.Count == 0 && cloudHere == 0) return null;

            return new BackupFolder
            {
                Label = label,
                Path = path,
                Target = target ?? string.Empty,
                Application = application,
                Files = kept,
                Bytes = size,
                CloudOnlyFiles = cloudHere,
                Truncated = scan.Value.Truncated,
                InaccessibleDirectories = scan.Value.InaccessibleDirectories,
            };
        }

        /// <summary>Ce que la prévisualisation dit des applications : une ligne par application trouvée.</summary>
        private static void Applications(
            IReadOnlyList<AppDataApplication> applications, IReadOnlyList<BackupFolder> plan, bool enabled,
            ICollection<string> willDo, ICollection<string> willNotDo)
        {
            if (!enabled)
            {
                willNotDo.Add("Ne copie pas les données d'applications : l'option n'est pas cochée.");
                return;
            }

            foreach (var application in applications)
            {
                long size = 0;
                var count = 0;
                foreach (var folder in plan)
                    if (folder.Application == application.Name)
                    {
                        size += folder.Bytes;
                        count += folder.Files.Count;
                    }

                var name = application.Name + (application.Detail == null ? string.Empty : " (" + application.Detail + ")");

                var caches = false;
                foreach (var source in application.Sources) caches |= source.ExcludeRelative.Count > 0;

                if (count > 0)
                    willDo.Add("Copier « " + name + " » : " + ValueFormat.Bytes(size) + " en " + count +
                               " fichier(s)" + (caches ? ", sans les caches" : string.Empty));
                else
                    willNotDo.Add(name + " : rien à copier sur cette machine. " +
                                  (application.Limits.Count > 0 ? application.Limits[0] : string.Empty));

                foreach (var line in application.BeforeWipe)
                    willNotDo.Add("À régler avant d'effacer le disque. " + line);
            }
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not BackupPlan plan)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le relevé des données à copier n'a pas été établi : rien n'a été copié.",
                };

            var stopwatch = Stopwatch.StartNew();
            var manifest = new StringBuilder();
            manifest.AppendLine("dossier;fichier;octets;résultat");

            var counters = new Counters();
            var details = new List<string>();
            var results = new List<BackupFolderResult>();

            if (!context.Files.CreateDirectory(plan.Destination))
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le dossier de sauvegarde n'a pas pu être créé sur la destination.",
                };

            // Avant le premier fichier : si le support est arraché en cours de route, c'est ce
            // « en cours » qui restera, et qui fera reconnaître la sauvegarde comme à reprendre.
            SetState(context, plan, BackupProgressState.Running);

            var interrupted = false;
            var lost = false;
            var tracker = new CopyProgress(progress, plan.Bytes, plan.Files, "Copie de");

            // Un support plein n'a aucune chance de se libérer pendant la copie, un support
            // débranché fait échouer chaque fichier restant en une fraction de seconde : dans les
            // deux cas, continuer produirait des milliers d'échecs identiques. La présence du
            // dossier n'est vérifiée qu'après un échec, pour ne rien coûter aux fichiers qui passent.
            var copier = new BatchCopier(context.Files, tracker, result =>
                result.Outcome == FileCopyOutcome.NoSpace ||
                (!Succeeded(result.Outcome) && !context.Files.DirectoryExists(plan.Destination)));

            foreach (var folder in plan.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracker.Folder(folder.Label);

                var here = new BackupFolderResult
                {
                    Label = folder.Label,
                    Target = folder.Destination,
                    Application = folder.Application,
                };
                results.Add(here);

                var items = new List<(FileEntry File, string Target)>(folder.Files.Count);
                var relatives = new List<string>(folder.Files.Count);
                foreach (var file in folder.Files)
                {
                    var relative = Relative(folder.Path, file.Path);
                    relatives.Add(relative);
                    items.Add((file, System.IO.Path.Combine(plan.Destination, folder.Destination, relative)));
                }

                var noSpace = false;
                var completed = copier.Run(items, (index, result) =>
                {
                    Count(here, result);
                    counters.Add(result);

                    manifest.Append(Csv(folder.Destination)).Append(';')
                        .Append(Csv(relatives[index])).Append(';')
                        .Append(items[index].File.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(';')
                        .AppendLine(Describe(result.Outcome));

                    noSpace |= result.Outcome == FileCopyOutcome.NoSpace;
                }, cancellationToken);

                if (!completed)
                {
                    interrupted = true;
                    if (noSpace)
                    {
                        details.Add(folder.Label + ", copie interrompue : la destination est pleine.");
                    }
                    else
                    {
                        details.Add(folder.Label + ", copie interrompue : le support de sauvegarde ne répond plus. " +
                                    "Rebranchez-le et relancez la sauvegarde : elle reprendra où elle s'est arrêtée.");
                        lost = true;
                    }
                }

                if (interrupted) break;

                details.Add(folder.Label + " : " + Describe(here));
            }

            // Support parti : plus rien ne peut s'y écrire, et chaque tentative prendrait son délai.
            if (lost)
                return Finish(context, plan, manifest, counters, details, stopwatch, interrupted, lost);

            WifiExportResult? wifi = null;
            if (plan.ExportWifi && !interrupted)
            {
                progress?.Report(new ActionProgress("Export des profils Wi-Fi…", 1));
                wifi = await WifiExport.RunAsync(context, plan.Destination, cancellationToken).ConfigureAwait(false);
                details.Add("Wi-Fi : " + wifi.Describe());
            }

            if (plan.WingetPackages.Count > 0)
            {
                // Sans marque d'ordre d'octets : cmd.exe ne lit pas un script qui commence par elle,
                // et le lecteur JSON de winget n'en attend pas.
                var listed = context.Files.ReplaceText(
                    System.IO.Path.Combine(plan.Destination, WingetApplications.FileName),
                    WingetApplications.Document(plan.WingetPackages));
                listed &= context.Files.ReplaceText(
                    System.IO.Path.Combine(plan.Destination, WingetApplications.ScriptName), WingetApplications.Script());

                details.Add(listed
                    ? "Applications : " + plan.WingetPackages.Count + " à réinstaller par winget, listées dans " +
                      WingetApplications.FileName + "."
                    : "La liste des applications à réinstaller n'a pas pu être écrite dans le dossier de sauvegarde.");
            }

            progress?.Report(new ActionProgress("Écriture de la fiche de réinstallation…", 1));
            Sheet(context, plan, results, wifi, interrupted, details);

            var outcome = Finish(context, plan, manifest, counters, details, stopwatch, interrupted, lost);
            SetState(context, plan, interrupted ? BackupProgressState.Interrupted : BackupProgressState.Finished);
            return outcome;
        }

        private void SetState(ActionContext context, BackupPlan plan, BackupProgressState state)
            => context.Files.ReplaceText(
                System.IO.Path.Combine(plan.Destination, BackupState.FileName),
                BackupState.Render(state, MachineName(context), Environment.UserName));

        private static bool Succeeded(FileCopyOutcome outcome)
            => outcome == FileCopyOutcome.Copied || outcome == FileCopyOutcome.AlreadyPresent;

        /// <summary>
        /// Dépose un fichier du logiciel dans la sauvegarde : fiche, manifeste, liste des logiciels.
        /// </summary>
        /// <remarks>
        /// Une première fois, il n'écrase rien, comme tout ce que cette action écrit. À la reprise,
        /// celui du passage précédent est remplacé : il décrivait une copie inachevée. La marque
        /// d'ordre d'octets est remise dans ce cas, sans elle Excel lirait mal les accents.
        /// </remarks>
        private static bool Deposit(ActionContext context, BackupPlan plan, string name, string content)
        {
            var path = System.IO.Path.Combine(plan.Destination, name);
            return plan.Resumed
                ? context.Files.ReplaceText(path, "\uFEFF" + content)
                : context.Files.WriteText(path, content);
        }

        /// <summary>
        /// Écrit la fiche de réinstallation et la liste des logiciels.
        /// </summary>
        /// <remarks>
        /// Écrite même après une copie interrompue : c'est alors elle qui dit ce qui manque, et
        /// c'est à ce moment-là qu'on en a le plus besoin.
        /// </remarks>
        private void Sheet(
            ActionContext context, BackupPlan plan, IReadOnlyList<BackupFolderResult> results,
            WifiExportResult? wifi, bool interrupted, ICollection<string> details)
        {
            var software = Inventory(context);
            var windows = context.Platform.Profile;

            var model = new ReinstallSheetModel
            {
                Machine = MachineName(context),
                Account = Environment.UserName,
                // Le nom court, pas ProductName : sous Windows 11, le registre annonce toujours
                // « Windows 10 », et la première fiche d'essai l'a recopié tel quel.
                Windows = windows.ShortName + ", build " + windows.Build.ToString(CultureInfo.InvariantCulture),
                Destination = plan.Destination,
                ToolVersion = ToolVersion(),
                Folders = results,
                Applications = plan.Applications,
                Wifi = wifi,
                Software = software,
                CloudOnlyFiles = plan.CloudOnlyFiles,
                Interrupted = interrupted,
            };

            var written = Deposit(context, plan, ReinstallSheet.FileName, ReinstallSheet.Render(model));

            details.Add(written
                ? "Fiche de réinstallation déposée : " + ReinstallSheet.FileName + "."
                : "La fiche de réinstallation n'a pas pu être écrite dans le dossier de sauvegarde.");

            if (software != null &&
                !Deposit(context, plan, ReinstallSheet.SoftwareFileName, ReinstallSheet.SoftwareCsv(software)))
                details.Add("La liste des logiciels n'a pas pu être écrite dans le dossier de sauvegarde.");
        }

        private static ActionOutcome Finish(
            ActionContext context, BackupPlan plan, StringBuilder manifest, Counters counters,
            List<string> details, Stopwatch stopwatch, bool interrupted, bool lost)
        {
            stopwatch.Stop();

            if (!lost && !Deposit(context, plan, "manifeste.csv", manifest.ToString()))
                details.Add("Le manifeste n'a pas pu être écrit dans le dossier de sauvegarde.");

            var status = interrupted || counters.Failed > 0
                ? counters.Copied > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed
                : ActionStatus.Succeeded;

            var summary = counters.Copied + " fichier(s) copiés et vérifiés, " +
                          ValueFormat.Bytes(counters.Bytes) + " écrits";

            if (counters.Skipped > 0)
                summary += ", " + counters.Skipped + (plan.Resumed ? " déjà copié(s) lors du passage précédent" : " déjà présent(s)");
            if (counters.Failed > 0) summary += ", " + counters.Failed + " non copié(s)";
            summary += lost
                ? " : copie interrompue, le support ne répond plus. Rebranchez-le et relancez : elle reprendra où elle s'est arrêtée."
                : interrupted ? " : copie interrompue, la destination est pleine." : ".";

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
        /// Le reste du profil n'en fait pas partie en bloc : il contient des dizaines de gigaoctets
        /// de caches de logiciels. Ce qui compte chez lui passe par le catalogue des données
        /// d'applications, qui sait quoi prendre et quoi laisser.
        /// </remarks>
        private static IEnumerable<(string Label, string Path)> Sources(ActionContext context)
        {
            foreach (var entry in UserDataSurveyor.PersonalFolders())
                if (context.Files.DirectoryExists(entry.Path))
                    yield return entry;
        }

        /// <summary>
        /// Les applications du catalogue dont un processus tourne en ce moment.
        /// </summary>
        /// <remarks>
        /// Lu par <c>tasklist</c>, dont les noms d'image ne dépendent pas de la langue du système.
        /// Un navigateur ouvert tient son historique et ses cookies : la copie les refuserait, ou
        /// les prendrait au milieu d'une écriture. Si la liste ne peut pas être lue, rien n'est
        /// annoncé : les fichiers refusés le seront à la copie, comptés et nommés dans la fiche.
        /// </remarks>
        internal static async Task<IReadOnlyList<string>> RunningAsync(
            ActionContext context, IReadOnlyList<AppDataApplication> applications, CancellationToken cancellationToken)
        {
            var running = new List<string>();
            if (applications.Count == 0) return running;

            var result = await context.Processes.RunAsync(
                new ProcessRequest("tasklist.exe", "/fo csv /nh") { Timeout = TimeSpan.FromSeconds(20) },
                cancellationToken).ConfigureAwait(false);

            if (!result.Completed || result.ExitCode != 0) return running;

            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in result.StandardOutput.Split('\r', '\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"", StringComparison.Ordinal)) continue;

                var end = trimmed.IndexOf('"', 1);
                if (end > 1) images.Add(trimmed.Substring(1, end - 1));
            }

            foreach (var application in applications)
                foreach (var process in application.Processes)
                    if (images.Contains(process))
                    {
                        running.Add(application.Name);
                        break;
                    }

            return running;
        }

        /// <summary>L'inventaire des logiciels du dernier diagnostic, s'il en a relevé un.</summary>
        private static SoftwareInventory? Inventory(ActionContext context)
        {
            var software = context.Snapshot?.Windows.Software;
            return software == null || (software.Programs.Count == 0 && software.StoreApps.Count == 0)
                ? null
                : software;
        }

        /// <summary>« Applications\Google Chrome\Default » devient « Google Chrome / Default ».</summary>
        private static string Label(string target)
        {
            var prefix = AppDataCatalog.Folder + "\\";
            var name = target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? target.Substring(prefix.Length)
                : target;
            return name.Replace("\\", " / ");
        }

        private static string MachineName(ActionContext context)
        {
            var machine = context.Snapshot?.Machine.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine!;
        }

        private static string FolderName(ActionContext context)
            => BackupState.Prefix + Sanitize(MachineName(context)) + "-" +
               DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

        private static string? ToolVersion()
        {
            var version = typeof(BackupUserDataAction).Assembly.GetName().Version;
            return version == null ? null : version.Major + "." + version.Minor + "." + version.Build;
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

            return System.IO.Path.GetFileName(path);
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
            FileCopyOutcome.DeviceError => "support injoignable ou illisible",
            _ => "échec",
        };

        private static void Count(BackupFolderResult folder, FileCopyResult result)
        {
            switch (result.Outcome)
            {
                case FileCopyOutcome.Copied:
                    folder.Copied++;
                    folder.Bytes += result.Bytes;
                    break;

                case FileCopyOutcome.AlreadyPresent:
                    folder.Skipped++;
                    break;

                case FileCopyOutcome.Locked:
                    folder.Failed++;
                    folder.Locked++;
                    break;

                default:
                    folder.Failed++;
                    break;
            }
        }

        private static string Describe(BackupFolderResult folder)
        {
            var text = folder.Copied + " fichier(s) copiés";
            if (folder.Skipped > 0) text += ", " + folder.Skipped + " déjà présent(s)";
            if (folder.Failed > 0) text += ", " + folder.Failed + " non copié(s)";
            if (folder.Locked > 0) text += " dont " + folder.Locked + " tenu(s) ouvert(s) par un programme";
            return text + ".";
        }

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
        }
    }
}
