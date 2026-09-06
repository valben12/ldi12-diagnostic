using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Le filet de sécurité : ce qui permettrait de revenir en arrière.
    /// </summary>
    /// <remarks>
    /// <b>Le relevé qu'on lit avant de toucher à la machine.</b> Ce logiciel crée un point de
    /// restauration avant chaque réparation ; encore faut-il que la machine sache en créer. Et
    /// une intervention se décide autrement selon que les documents du client sont sur le disque
    /// qu'on va reformater ou sur un second disque auquel on ne touchera pas.
    /// <para>
    /// Trois lectures, aucune n'exigeant les privilèges administrateur :
    /// </para>
    /// <list type="bullet">
    /// <item>l'activité réelle de la protection du système, par le journal d'application : l'état
    /// déclaré dans les réglages ne dit pas si elle fonctionne encore ;</item>
    /// <item>l'environnement de récupération, par le fichier XML que Windows en tient ;</item>
    /// <item>l'emplacement réel des dossiers personnels, par le registre de l'utilisateur.</item>
    /// </list>
    /// <para>
    /// La liste des points de restauration encore présents, elle, demande une élévation : elle
    /// n'est pas ici, et son absence est dite plutôt que comblée par une supposition.
    /// </para>
    /// </remarks>
    public sealed class SafetyNetProbe : IDiagnosticProbe
    {
        private const int WindowDays = 90;

        /// <summary>Journal du service de restauration : « point créé » et « création échouée ».</summary>
        /// <remarks>
        /// Les identifiants viennent du manifeste, la description est traduite et n'est jamais
        /// lue. 8194 est un point demandé par un programme (une mise à jour, une installation)
        /// et 8212 le point planifié hebdomadaire ; les deux prouvent que la protection tourne.
        /// </remarks>
        private const int PointCreated = 8194;

        private const int ScheduledPointCreated = 8212;

        private const int CreationFailed = 8193;

        private static readonly string ReAgentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Recovery", "ReAgent.xml");

        private const string ShellFolders =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

        private const string OneDriveAccounts = @"Software\Microsoft\OneDrive\Accounts";

        private const string WindowsBackup =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsBackup";

        private static readonly string FileHistoryConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "FileHistory", "Configuration");

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SafetyNet,
            DisplayName = "Filet de sécurité",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(30),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var restore = ReadRestoreProtection(context, cancellationToken);
            var recovery = ReadRecoveryEnvironment();
            var backups = ReadBackups(context);
            var folders = ReadPersonalFolders(context, backups);

            context.Draft.SetSafetyNet(new SafetyNet
            {
                Restore = restore,
                Recovery = recovery,
                Folders = folders,
                Backups = backups,
            });

            var parts = new List<string>();

            parts.Add(restore.Enabled.Or(false)
                ? restore.LastPointCreated.HasValue
                    ? "dernier point de restauration le " + ValueFormat.Date(restore.LastPointCreated.Value)
                    : "protection active, aucun point créé sur " + WindowDays + " jours"
                : "protection du système désactivée");

            if (recovery.State.HasValue && recovery.State.Value != RecoveryEnvironmentState.Installed)
                parts.Add("environnement de récupération indisponible");

            var elsewhere = 0;
            foreach (var folder in folders) if (!folder.OnSystemVolume) elsewhere++;
            if (elsewhere > 0) parts.Add(elsewhere + " dossier(s) personnel(s) hors du disque système");

            if (backups.Count > 0) parts.Add(backups.Count + " moyen(s) de sauvegarde repéré(s)");

            return Task.FromResult(ProbeOutcome.Ok(Capitalize(string.Join(", ", parts.ToArray())) + "."));
        }

        // ============================================================ protection du système

        /// <summary>
        /// L'état déclaré, puis l'activité réelle.
        /// </summary>
        /// <remarks>
        /// Le second compte davantage que le premier. Une protection annoncée activée qui échoue
        /// à chaque tentative laisse la case cochée dans les réglages de Windows sans plus rien
        /// protéger, et c'est elle qui fera échouer le point de restauration créé avant une
        /// réparation, au moment le plus tardif possible.
        /// </remarks>
        private static RestoreProtection ReadRestoreProtection(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var registry = context.Registry;

            var disabled = registry.ReadInt32(
                RegistryHive.LocalMachine, RegistryPaths.SystemRestoreConfig, "DisableSR");
            var interval = registry.ReadInt32(
                RegistryHive.LocalMachine, RegistryPaths.SystemRestoreConfig, "RPSessionInterval");
            var percent = registry.ReadInt32(
                RegistryHive.LocalMachine, RegistryPaths.SystemRestoreConfig + @"\Cfg", "DiskPercent");

            // Deux valeurs, deux façons de couper la protection : la stratégie la désactive, et
            // un intervalle nul l'empêche de créer quoi que ce soit. L'une sans l'autre laisse
            // croire à une protection active.
            var enabled = disabled == null && interval == null
                ? Measured.Missing<bool>("L'état de la protection du système n'est pas renseigné dans le registre.")
                : Measured.Ok(disabled.GetValueOrDefault() == 0 && interval.GetValueOrDefault(1) != 0,
                    DataSource.Registry);

            var activity = ReadRestoreActivity(cancellationToken);

            return new RestoreProtection
            {
                Enabled = enabled,
                ReservedPercent = percent.HasValue
                    ? Measured.Ok(percent.Value, DataSource.Registry)
                    : Measured.Missing<int>("La part de disque réservée n'est pas renseignée."),
                LastPointCreated = activity.Last.HasValue
                    ? Measured.Partial(activity.Last.Value, DataSource.EventLog,
                        "Date de création. Windows supprime les points les plus anciens quand la réserve " +
                        "est pleine : la liste de ceux qui restent demande les privilèges administrateur.")
                    : Measured.Missing<DateTimeOffset>(activity.Reason ??
                        "Aucun point de restauration créé sur les " + WindowDays + " derniers jours."),
                PointsCreated = Count(activity.Reason, activity.Created),
                Failures = Count(activity.Reason, activity.Failed),
                WindowDays = Count(activity.Reason, WindowDays),
            };
        }

        private static Measured<int> Count(string? failure, int value)
            => failure == null ? Measured.Ok(value, DataSource.EventLog) : Measured.Missing<int>(failure);

        private static (DateTimeOffset? Last, int Created, int Failed, string? Reason) ReadRestoreActivity(
            CancellationToken cancellationToken)
        {
            var xpath =
                "*[System[Provider[@Name='System Restore'] and (EventID=" + PointCreated +
                " or EventID=" + ScheduledPointCreated + " or EventID=" + CreationFailed + ")" +
                " and TimeCreated[timediff(@SystemTime) <= " +
                ((long)WindowDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]]]";

            DateTimeOffset? last = null;
            var created = 0;
            var failed = 0;

            try
            {
                var query = new EventLogQuery("Application", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record)
                    {
                        if (record.Id == CreationFailed)
                        {
                            failed++;
                            continue;
                        }

                        created++;
                        var when = new DateTimeOffset(record.TimeCreated ?? DateTime.Now);
                        if (last == null || when > last.Value) last = when;
                    }
                }
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return (null, 0, 0, "Le journal d'application n'a pas pu être lu (" + ex.GetType().Name + ").");
            }

            return (last, created, failed, null);
        }

        // ============================================================ environnement de récupération

        /// <summary>
        /// L'environnement de récupération, lu dans le fichier que Windows en tient.
        /// </summary>
        /// <remarks>
        /// <c>ReAgent.xml</c> plutôt que <c>reagentc /info</c> : la commande rend un texte traduit
        /// et exige une élévation, le fichier est du XML lisible par tout le monde. Même règle que
        /// pour les tâches planifiées et les journaux d'événements.
        /// </remarks>
        private static RecoveryEnvironment ReadRecoveryEnvironment()
        {
            try
            {
                if (!File.Exists(ReAgentPath))
                    return new RecoveryEnvironment
                    {
                        State = Measured.Ok(RecoveryEnvironmentState.Missing, DataSource.FileSystem),
                        AutomaticRepair = Measured.Missing<bool>(
                            "Aucun environnement de récupération n'est déclaré sur cette machine."),
                    };

                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(
                    ReAgentPath, new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit }))
                {
                    document.Load(reader);
                }

                var install = State(document, "InstallState");
                var repair = State(document, "IsAutoRepairOn");
                var location = document.SelectSingleNode("/WindowsRE/WinreLocation/@path")?.Value;

                return new RecoveryEnvironment
                {
                    State = install.HasValue
                        ? Measured.Ok(
                            install.Value == 1 ? RecoveryEnvironmentState.Installed : RecoveryEnvironmentState.Disabled,
                            DataSource.FileSystem)
                        : Measured.Missing<RecoveryEnvironmentState>(
                            "Le fichier de l'environnement de récupération ne déclare pas son état."),
                    AutomaticRepair = repair.HasValue
                        ? Measured.Ok(repair.Value == 1, DataSource.FileSystem)
                        : Measured.Missing<bool>("La réparation automatique n'est pas déclarée."),
                    Location = string.IsNullOrWhiteSpace(location) ? null : location,
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new RecoveryEnvironment
                {
                    State = Measured.NeedsElevation<RecoveryEnvironmentState>(
                        "Le fichier de l'environnement de récupération n'est pas lisible depuis cette session."),
                    AutomaticRepair = Measured.NeedsElevation<bool>(
                        "Le fichier de l'environnement de récupération n'est pas lisible depuis cette session."),
                };
            }
            catch (Exception ex) when (ex is IOException || ex is XmlException || ex is System.Security.SecurityException)
            {
                return new RecoveryEnvironment
                {
                    State = Measured.Missing<RecoveryEnvironmentState>(
                        "L'environnement de récupération n'a pas pu être lu (" + ex.GetType().Name + ")."),
                    AutomaticRepair = Measured.Missing<bool>(
                        "L'environnement de récupération n'a pas pu être lu."),
                };
            }
        }

        private static int? State(XmlDocument document, string element)
        {
            var value = document.SelectSingleNode("/WindowsRE/" + element + "/@state")?.Value;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        // ============================================================ où sont les affaires du client

        private static readonly (string Value, PersonalFolderKind Kind)[] Folders =
        {
            ("Desktop", PersonalFolderKind.Desktop),
            ("Personal", PersonalFolderKind.Documents),
            ("My Pictures", PersonalFolderKind.Pictures),
            ("My Music", PersonalFolderKind.Music),
            ("My Video", PersonalFolderKind.Videos),
            ("{374DE290-123F-4565-9164-39C4925E467B}", PersonalFolderKind.Downloads),
        };

        /// <summary>
        /// L'emplacement réel des dossiers personnels.
        /// </summary>
        /// <remarks>
        /// <c>User Shell Folders</c> et non <c>Shell Folders</c> : la première clé porte le chemin
        /// tel qu'il a été configuré, avec ses variables d'environnement, la seconde n'en est
        /// qu'un cache que Windows réécrit et qui peut rester en retard.
        /// </remarks>
        private static IReadOnlyList<PersonalFolder> ReadPersonalFolders(
            ProbeContext context, IReadOnlyList<BackupTool> backups)
        {
            var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var cloud = CloudRoots(backups);

            var found = new List<PersonalFolder>();

            foreach (var entry in Folders)
            {
                var raw = context.Registry.ReadString(RegistryHive.CurrentUser, ShellFolders, entry.Value);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = Environment.ExpandEnvironmentVariables(raw!);
                var root = Root(path);

                found.Add(new PersonalFolder
                {
                    Kind = entry.Kind,
                    Path = path,
                    Exists = Exists(path),
                    OnSystemVolume = root != null && system != null &&
                                     string.Equals(root, system, StringComparison.OrdinalIgnoreCase),
                    InCloud = InCloud(path, cloud),
                });
            }

            return found;
        }

        private static string? Root(string path)
        {
            try { return Path.GetPathRoot(path); }
            catch (ArgumentException) { return null; }
        }

        private static bool Exists(string path)
        {
            try { return Directory.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private static bool InCloud(string path, IReadOnlyList<string> roots)
        {
            foreach (var root in roots)
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static IReadOnlyList<string> CloudRoots(IReadOnlyList<BackupTool> backups)
        {
            var roots = new List<string>();
            foreach (var backup in backups)
                if (backup.Kind == BackupKind.Cloud && !string.IsNullOrWhiteSpace(backup.Detail))
                    roots.Add(backup.Detail!);
            return roots;
        }

        // ============================================================ moyens de sauvegarde

        /// <summary>
        /// Ce qui est configuré pour sauvegarder, repéré et rien de plus.
        /// </summary>
        /// <remarks>
        /// Une clé de registre dit qu'un outil a été configuré un jour. Elle ne dit pas qu'il a
        /// tourné, ni que ce qu'il a écrit est récupérable. Le relevé s'arrête donc à la présence
        /// et l'écrit ainsi : annoncer « les données sont sauvegardées » sur cette base serait la
        /// pire affirmation que ce logiciel puisse produire.
        /// </remarks>
        private static IReadOnlyList<BackupTool> ReadBackups(ProbeContext context)
        {
            var registry = context.Registry;
            var tools = new List<BackupTool>();

            foreach (var account in registry.GetSubKeyNames(RegistryHive.CurrentUser, OneDriveAccounts))
            {
                var folder = registry.ReadString(
                    RegistryHive.CurrentUser, OneDriveAccounts + "\\" + account, "UserFolder");
                if (string.IsNullOrWhiteSpace(folder)) continue;

                tools.Add(new BackupTool
                {
                    Kind = BackupKind.Cloud,
                    Label = "OneDrive",
                    Detail = folder,
                });
            }

            // L'historique des fichiers écrit sa configuration dans ce dossier, et ne le crée
            // qu'une fois configuré. La clé de registre du même nom, elle, existe sur toutes les
            // machines : elle porte la position de la fenêtre de restauration.
            if (HasFiles(FileHistoryConfig))
                tools.Add(new BackupTool
                {
                    Kind = BackupKind.FileHistory,
                    Label = "Historique des fichiers",
                    Detail = "Configuré. La date de la dernière sauvegarde n'est pas relevée.",
                });

            if (HasContent(registry, WindowsBackup))
                tools.Add(new BackupTool
                {
                    Kind = BackupKind.WindowsBackup,
                    Label = "Sauvegarde Windows",
                    Detail = "Configurée. La date de la dernière sauvegarde n'est pas relevée.",
                });

            return tools;
        }

        /// <summary>
        /// Une clé qui contient réellement quelque chose.
        /// </summary>
        /// <remarks>
        /// L'existence de la clé ne suffit pas : Windows 11 crée <c>WindowsBackup</c> vide sur
        /// toutes les machines, et la première version de ce module annonçait donc une sauvegarde
        /// configurée sur une machine qui n'en avait aucune. Une fonction qu'on n'a jamais
        /// utilisée laisse la clé vide ; la configurer y écrit quelque chose.
        /// <para>
        /// Le sens de l'erreur compte plus que sa fréquence. Ne pas voir une sauvegarde fait
        /// vérifier le technicien ; en annoncer une qui n'existe pas fait reformater sans filet.
        /// Tout ce module penche donc du côté de ne rien affirmer.
        /// </para>
        /// </remarks>
        private static bool HasContent(IRegistryGateway registry, string path)
            => registry.GetValueNames(RegistryHive.LocalMachine, path).Count > 0 ||
               registry.GetSubKeyNames(RegistryHive.LocalMachine, path).Count > 0;

        private static bool HasFiles(string directory)
        {
            try { return Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private static string Capitalize(string value)
            => value.Length == 0 ? value : char.ToUpper(value[0], CultureInfo.CurrentCulture) + value.Substring(1);
    }
}
