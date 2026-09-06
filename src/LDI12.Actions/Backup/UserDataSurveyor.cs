using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LDI12.Actions.Journal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// Pèse les données personnelles du compte courant, avant une réinstallation.
    /// </summary>
    /// <remarks>
    /// <b>Le pendant en lecture du nettoyage.</b> L'un montre ce qui peut disparaître, l'autre ce
    /// qu'il ne faut pas perdre. Les deux répondent à la même exigence : rien concernant les
    /// données personnelles ne se décide sans que le technicien ait vu de quoi il s'agit.
    /// <para>
    /// <b>Aucun nom de fichier n'est retenu.</b> La passerelle expose exprès deux opérations
    /// différentes : l'une rend des chemins parce qu'elle prépare une suppression, l'autre rend
    /// des totaux. Peser des dossiers personnels en gardant deux cent mille chemins en mémoire,
    /// avec le risque de les voir passer dans un journal, serait une prise de risque sans
    /// contrepartie.
    /// </para>
    /// <para>
    /// <b>Sur demande, jamais pendant une analyse.</b> Parcourir un profil peut demander une
    /// minute ; l'imposer à chaque diagnostic rapide serait payer très cher une information dont
    /// on n'a besoin qu'avant une réinstallation.
    /// </para>
    /// </remarks>
    public sealed class UserDataSurveyor
    {
        private const string Category = "Données";

        /// <summary>
        /// Budget de temps pour l'ensemble du relevé, partagé entre les dossiers.
        /// </summary>
        /// <remarks>
        /// Un budget par dossier ne tient pas : huit dossiers à quarante secondes font dix
        /// minutes dans le pire des cas, et la première version s'est arrêtée en route sur les
        /// deux dossiers qui comptaient : un bureau et un dossier de documents posés sur un
        /// disque mécanique. Le budget est donc global : chaque dossier reçoit ce qu'il reste,
        /// et l'ensemble ne dépasse jamais ce que le technicien peut attendre devant l'écran.
        /// </remarks>
        private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(6);

        /// <summary>Profils que Windows crée lui-même : ce ne sont les données de personne.</summary>
        private static readonly string[] SystemProfiles =
        {
            "Public", "Default", "Default User", "All Users", "defaultuser0",
        };

        private readonly IFileSystemGateway _files;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;

        public UserDataSurveyor(IFileSystemGateway files, InterventionJournal journal, ILdiLogger logger)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Les dossiers qui seront pesés, avant de les peser.</summary>
        public IReadOnlyList<(UserDataKind Kind, string Label, string Path)> Plan()
        {
            var planned = new List<(UserDataKind, string, string)>();

            foreach (var entry in Candidates())
                if (!string.IsNullOrEmpty(entry.Path) && _files.DirectoryExists(entry.Path))
                    planned.Add(entry);

            return planned;
        }

        /// <summary>
        /// Les dossiers considérés, qu'ils existent ou non sur cette machine.
        /// </summary>
        /// <remarks>
        /// Séparé du relevé pour que la sauvegarde s'appuie exactement sur la même liste. Deux
        /// listes parallèles finiraient par diverger, et le technicien copierait un jour autre
        /// chose que ce que l'écran lui a montré.
        /// </remarks>
        public static IReadOnlyList<(UserDataKind Kind, string Label, string Path)> Candidates()
            => new[]
            {
                (UserDataKind.Desktop, "Bureau", Folder(Environment.SpecialFolder.DesktopDirectory)),
                (UserDataKind.Documents, "Documents", Folder(Environment.SpecialFolder.MyDocuments)),
                (UserDataKind.Pictures, "Images", Folder(Environment.SpecialFolder.MyPictures)),
                (UserDataKind.Music, "Musique", Folder(Environment.SpecialFolder.MyMusic)),
                (UserDataKind.Videos, "Vidéos", Folder(Environment.SpecialFolder.MyVideos)),

                // Le dossier des téléchargements n'a pas d'entrée dans l'énumération du framework :
                // il est arrivé avec Windows Vista, l'énumération date de .NET 1.
                (UserDataKind.Downloads, "Téléchargements", Path.Combine(Profile(), "Downloads")),
                (UserDataKind.Cloud, "OneDrive", Path.Combine(Profile(), "OneDrive")),
                (UserDataKind.RestOfProfile, "Reste du profil", Profile()),
            };

        /// <summary>
        /// Les dossiers qu'une sauvegarde copie.
        /// </summary>
        /// <remarks>
        /// Le reste du profil en est écarté : il porte les profils de navigateur et les
        /// messageries locales, mais aussi des dizaines de gigaoctets de caches qui n'ont aucun
        /// intérêt à être copiés. Le relevé continue de le peser et de le montrer, ce qu'on en
        /// fait reste une décision du technicien, pas une décision de ce logiciel.
        /// </remarks>
        public static IReadOnlyList<(string Label, string Path)> PersonalFolders()
        {
            var folders = new List<(string, string)>();
            foreach (var entry in Candidates())
                if (entry.Kind != UserDataKind.RestOfProfile && !string.IsNullOrEmpty(entry.Path))
                    folders.Add((entry.Label, entry.Path));
            return folders;
        }

        public UserDataSurvey Run(
            SystemSnapshot? snapshot, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var planned = Plan();
            var folders = new List<UserDataFolder>();
            var caveats = new List<string>();

            // Le reste du profil se pèse en excluant ce qui a déjà été compté : sans cela, un
            // dossier d'images de deux cents gigaoctets serait parcouru deux fois.
            var excluded = new List<string>();
            foreach (var entry in planned)
                if (entry.Kind != UserDataKind.RestOfProfile) excluded.Add(entry.Path);

            long total = 0;
            long onDisk = 0;
            long personal = 0;
            long profileRest = 0;
            long cloud = 0;
            var files = 0;
            var truncated = 0;
            var skipped = 0;
            var inaccessible = 0;

            foreach (var entry in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Pesée de « " + entry.Label + " »…");

                var remaining = TotalBudget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    folders.Add(Describe(entry.Kind, entry.Label, entry.Path,
                        Measured.Missing<DirectoryMeasure>(
                            "Le temps imparti au relevé était écoulé avant d'arriver à ce dossier.",
                            DataSource.FileSystem)));
                    skipped++;
                    continue;
                }

                var request = new DirectoryMeasureRequest(entry.Path)
                {
                    Budget = remaining,
                    Exclude = entry.Kind == UserDataKind.RestOfProfile ? excluded : Array.Empty<string>(),
                };

                var measured = _files.Measure(request, cancellationToken);
                folders.Add(Describe(entry.Kind, entry.Label, entry.Path, measured));

                if (!measured.HasValue) continue;

                var measure = measured.Value;
                total += measure.TotalBytes;
                onDisk += measure.OnDiskBytes;
                cloud += measure.CloudOnlyBytes;
                files += measure.FileCount;
                inaccessible += measure.InaccessibleDirectories;
                if (measure.Truncated) truncated++;

                if (entry.Kind == UserDataKind.RestOfProfile) profileRest += measure.OnDiskBytes;
                else personal += measure.OnDiskBytes;
            }

            var others = OtherProfiles();
            Caveats(caveats, cloud, onDisk, truncated, skipped, inaccessible, others, snapshot);

            var survey = new UserDataSurvey
            {
                Duration = stopwatch.Elapsed,
                Account = Environment.UserName,
                Folders = folders,
                TotalBytes = Measured.Ok(total, DataSource.FileSystem),
                OnDiskBytes = Measured.Ok(onDisk, DataSource.FileSystem),
                CloudOnlyBytes = cloud,
                FileCount = files,
                OtherProfiles = others,
                Caveats = caveats,
                Headline = Headline(personal, profileRest, cloud, files),
            };

            Record(survey);
            return survey;
        }

        private static UserDataFolder Describe(
            UserDataKind kind, string label, string path, Measured<DirectoryMeasure> measured)
        {
            if (!measured.HasValue)
            {
                var reason = measured.Reason ?? "Ce dossier n'a pas pu être pesé.";
                return new UserDataFolder
                {
                    Kind = kind,
                    Label = label,
                    Path = path,
                    TotalBytes = Measured.Missing<long>(reason, DataSource.FileSystem),
                    OnDiskBytes = Measured.Missing<long>(reason, DataSource.FileSystem),
                };
            }

            var measure = measured.Value;

            // Un relevé interrompu rend un minimum, pas une mesure : il ressort partiel pour que
            // l'affichage et le journal le disent tous les deux.
            var total = measure.Truncated
                ? Measured.Partial(measure.TotalBytes, DataSource.FileSystem,
                    "Le relevé s'est arrêté sur son budget de temps : ce dossier contient au moins cela.")
                : Measured.Ok(measure.TotalBytes, DataSource.FileSystem);

            return new UserDataFolder
            {
                Kind = kind,
                Label = label,
                Path = path,
                TotalBytes = total,
                OnDiskBytes = total.Map(_ => measure.OnDiskBytes),
                CloudOnlyBytes = measure.CloudOnlyBytes,
                FileCount = measure.FileCount,
                CloudOnlyFileCount = measure.CloudOnlyFileCount,
                Truncated = measure.Truncated,
            };
        }

        /// <summary>
        /// Les autres profils présents sur la machine.
        /// </summary>
        /// <remarks>
        /// Énumérés, jamais pesés : lire le profil d'un autre compte demande des privilèges, et
        /// les données d'une autre personne ne se comptent pas parce qu'on en a l'occasion. Ce
        /// qui compte ici est de ne pas laisser croire que le total couvre toute la machine.
        /// </remarks>
        private IReadOnlyList<ForeignProfile> OtherProfiles()
        {
            var profiles = new List<ForeignProfile>();

            var mine = Profile();
            var root = Path.GetDirectoryName(mine);
            if (string.IsNullOrEmpty(root)) return profiles;

            foreach (var directory in _files.EnumerateDirectories(root!))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(directory, mine, StringComparison.OrdinalIgnoreCase)) continue;

                var system = false;
                foreach (var reserved in SystemProfiles)
                    if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase)) { system = true; break; }

                if (system) continue;

                profiles.Add(new ForeignProfile { Name = name, Path = directory });
            }

            return profiles;
        }

        private static void Caveats(
            ICollection<string> caveats, long cloud, long onDisk, int truncated, int skipped,
            int inaccessible, IReadOnlyList<ForeignProfile> others, SystemSnapshot? snapshot)
        {
            if (cloud > 0)
            {
                caveats.Add(
                    "Sur le total annoncé, " + ValueFormat.Bytes(cloud) + " ne sont pas sur ce " +
                    "disque : ce sont des fichiers qui restent dans le nuage et dont Windows " +
                    "n'affiche que le nom. Copier les dossiers ne les emporterait pas : il faut " +
                    "demander à les rendre disponibles hors connexion avant de sauvegarder, et " +
                    "prévoir " + ValueFormat.Bytes(onDisk + cloud) + " de place au lieu de " +
                    ValueFormat.Bytes(onDisk) + ".");
            }

            if (truncated > 0 || skipped > 0)
            {
                caveats.Add(
                    (truncated + skipped) + " dossier(s) n'ont pas été parcourus jusqu'au bout dans " +
                    "le temps imparti : le total est un minimum, pas une mesure. Relancer le relevé " +
                    "sur un dossier seul, ou depuis un disque plus rapide, donnera le compte exact.");
            }

            if (inaccessible > 0)
            {
                caveats.Add(
                    inaccessible + " dossier(s) ont été refusés faute de droits et ne sont comptés " +
                    "nulle part.");
            }

            if (others.Count > 0)
            {
                var names = new List<string>();
                foreach (var profile in others) names.Add(profile.Name);

                caveats.Add(
                    "Ce relevé ne couvre que le compte ouvert. " + others.Count + " autre(s) " +
                    "profil(s) existent sur cette machine : " + string.Join(", ", names.ToArray()) +
                    ", et leurs données ne sont pas mesurables depuis cette session.");
            }

            Volumes(caveats, snapshot);
        }

        /// <summary>
        /// Les autres volumes, annoncés sans être parcourus.
        /// </summary>
        /// <remarks>
        /// Un disque de données de deux téraoctets n'est pas dans le profil, et le parcourir
        /// prendrait des heures. Mais un relevé qui n'en dirait rien laisserait croire qu'il n'y a
        /// que ce qu'il a compté : c'est l'oubli qui coûte le plus cher avant une réinstallation.
        /// </remarks>
        private static void Volumes(ICollection<string> caveats, SystemSnapshot? snapshot)
        {
            if (snapshot == null) return;

            var lines = new List<string>();
            foreach (var volume in snapshot.Storage.Volumes)
            {
                if (volume.IsSystemVolume.Or(false)) continue;
                if (!volume.DriveLetter.HasValue || !volume.TotalBytes.HasValue || !volume.FreeBytes.HasValue) continue;

                var used = volume.TotalBytes.Value - volume.FreeBytes.Value;
                if (used <= 0) continue;

                lines.Add(volume.DriveLetter.Value + " (" + ValueFormat.Bytes(used) + " occupés)");
            }

            if (lines.Count == 0) return;

            caveats.Add(
                "D'autres volumes contiennent des données que ce relevé n'a pas détaillées : " +
                string.Join(", ", lines.ToArray()) + ".");
        }

        /// <summary>
        /// Ce que pèsent les données, en séparant ce qu'on sauvegarde toujours du reste.
        /// </summary>
        /// <remarks>
        /// Annoncer un total unique serait trompeur : le reste du profil contient les réglages,
        /// la messagerie et les caches des logiciels, et personne ne recopie deux cents
        /// gigaoctets de caches sur une machine réinstallée. Les deux chiffres se lisent
        /// ensemble : l'un est ce qu'il faut emporter, l'autre ce dans quoi il faudra trier.
        /// </remarks>
        private static string Headline(long personal, long profileRest, long cloud, int files)
        {
            if (files == 0) return "Aucune donnée personnelle n'a été trouvée dans ce profil.";

            var headline = ValueFormat.Bytes(personal) + " de dossiers personnels";

            if (profileRest > 0)
                headline += ", plus " + ValueFormat.Bytes(profileRest) +
                            " dans le reste du profil (réglages, messagerie, caches)";

            headline += " : " + ValueFormat.Number(files) + " fichiers en tout.";

            return cloud > 0
                ? headline + " " + ValueFormat.Bytes(cloud) + " de plus attendent dans le nuage."
                : headline;
        }

        /// <summary>
        /// Consigne le relevé dans le journal d'intervention.
        /// </summary>
        /// <remarks>
        /// En « prévisualisation » : rien n'a été modifié, rien n'a été copié. Les totaux y
        /// figurent, les noms de dossiers aussi, jamais un nom de fichier.
        /// </remarks>
        private void Record(UserDataSurvey survey)
        {
            var details = new List<string>();
            foreach (var folder in survey.Folders)
                details.Add(folder.Label + " : " +
                            (folder.OnDiskBytes.HasValue
                                ? ValueFormat.Bytes(folder.OnDiskBytes.Value)
                                : folder.OnDiskBytes.Reason ?? "non pesé"));

            foreach (var caveat in survey.Caveats) details.Add("Réserve : " + caveat);

            _journal.Record(
                InterventionKind.Preview, ActionIds.UserData, "Données à sauvegarder",
                "Relevé", survey.Headline, duration: survey.Duration, details: details);

            _logger.For(Category).Info(
                "Relevé des données terminé en " + (int)survey.Duration.TotalSeconds + " s : " +
                survey.FileCount + " fichiers.");
        }

        private static string Folder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Profile()
            => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
