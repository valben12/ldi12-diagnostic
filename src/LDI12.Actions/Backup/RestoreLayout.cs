using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Où les données reviennent, sur la machine où l'on restaure.</summary>
    /// <remarks>
    /// Un objet plutôt que des appels directs aux dossiers spéciaux : c'est ce qui permet
    /// d'éprouver une restauration dans un dossier jetable, y compris en lançant ensuite un vrai
    /// Chrome dessus, sans toucher au profil de la machine qui fait l'essai.
    /// </remarks>
    public sealed class RestoreTargets
    {
        public string Desktop { get; init; } = string.Empty;
        public string Documents { get; init; } = string.Empty;
        public string Pictures { get; init; } = string.Empty;
        public string Music { get; init; } = string.Empty;
        public string Videos { get; init; } = string.Empty;
        public string UserProfile { get; init; } = string.Empty;
        public string LocalAppData { get; init; } = string.Empty;
        public string RoamingAppData { get; init; } = string.Empty;

        public static RestoreTargets Current() => new RestoreTargets
        {
            Desktop = Special(Environment.SpecialFolder.DesktopDirectory),
            Documents = Special(Environment.SpecialFolder.MyDocuments),
            Pictures = Special(Environment.SpecialFolder.MyPictures),
            Music = Special(Environment.SpecialFolder.MyMusic),
            Videos = Special(Environment.SpecialFolder.MyVideos),
            UserProfile = Special(Environment.SpecialFolder.UserProfile),
            LocalAppData = Special(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Special(Environment.SpecialFolder.ApplicationData),
        };

        private static string Special(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (Exception) { return string.Empty; }
        }
    }

    /// <summary>Comment un dossier de la sauvegarde revient à sa place.</summary>
    public enum RestoreMode
    {
        /// <summary>Les fichiers s'ajoutent à ce qui existe. Rien n'est écrasé.</summary>
        Merge = 0,

        /// <summary>
        /// Le dossier existant est mis de côté, puis remplacé par celui de la sauvegarde.
        /// </summary>
        /// <remarks>
        /// Pour les profils de logiciels : un Chrome qu'on vient d'installer a déjà créé un profil
        /// vide, et mélanger ses fichiers avec ceux de l'ancien donnerait un profil incohérent.
        /// </remarks>
        ReplaceFolder = 1,

        /// <summary>Le fichier « Local State » d'un navigateur Chromium, fusionné plutôt que copié.</summary>
        ChromiumLocalState = 2,
    }

    /// <summary>Un dossier de la sauvegarde, et l'endroit où il revient.</summary>
    public sealed class RestoreItem
    {
        public string Label { get; init; } = string.Empty;

        /// <summary>Application concernée. Nul pour un dossier personnel.</summary>
        public string? Application { get; init; }

        public string Source { get; init; } = string.Empty;

        public string Destination { get; init; } = string.Empty;

        public RestoreMode Mode { get; init; }

        /// <summary>Filtre sur le nom de fichier. Nul : tout.</summary>
        public Func<string, bool>? Keep { get; init; }

        /// <summary>Noms d'image des processus qui doivent être fermés.</summary>
        public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();

        /// <summary>Profils à reprendre dans « Local State », pour <see cref="RestoreMode.ChromiumLocalState"/>.</summary>
        public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();
    }

    /// <summary>Ce que la sauvegarde contient, rapporté à la machine où l'on restaure.</summary>
    public sealed class RestoreLayout
    {
        public string Backup { get; init; } = string.Empty;

        public IReadOnlyList<RestoreItem> Items { get; init; } = Array.Empty<RestoreItem>();

        /// <summary>Profils Wi-Fi exportés, en chemin complet.</summary>
        public IReadOnlyList<string> WifiProfiles { get; init; } = Array.Empty<string>();

        /// <summary>Ce que la sauvegarde contient et que la restauration ne remet pas en place, avec la raison.</summary>
        public IReadOnlyList<string> Left { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Retrouve une sauvegarde LDI12 et décide où chaque partie revient.
    /// </summary>
    /// <remarks>
    /// <b>La disposition de la sauvegarde est le contrat.</b> Les noms de dossiers écrits par la
    /// sauvegarde (« Bureau », « Applications\Google Chrome\Default ») sont relus ici tels quels.
    /// Une sauvegarde faite par la version 1.25.0 se restaure donc sans fichier descriptif
    /// supplémentaire, et un dossier que ce catalogue ne reconnaît pas est signalé et laissé en
    /// place, jamais deviné.
    /// </remarks>
    public static class RestoreCatalog
    {
        private const string Prefix = "LDI12-Sauvegarde-";

        /// <summary>
        /// La sauvegarde désignée par un chemin : elle-même, ou la plus récente qu'il contient.
        /// </summary>
        /// <remarks>
        /// Le technicien branche le disque et tape sa lettre ; il n'a pas à retrouver le nom du
        /// dossier horodaté. Quand plusieurs sauvegardes s'y trouvent, la plus récente est prise
        /// et les autres sont comptées, pour qu'il sache qu'un choix a été fait.
        /// </remarks>
        public static string? Find(IFileSystemGateway files, string path, out int others)
        {
            others = 0;
            if (string.IsNullOrWhiteSpace(path) || !files.DirectoryExists(path)) return null;
            if (IsBackup(files, path)) return path;

            string? best = null;
            var bestStamp = string.Empty;
            foreach (var directory in files.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !IsBackup(files, directory)) continue;

                var stamp = Stamp(name);
                if (best != null) others++;
                if (best == null || string.CompareOrdinal(stamp, bestStamp) > 0)
                {
                    best = directory;
                    bestStamp = stamp;
                }
            }

            return best;
        }

        /// <summary>« yyyy-MM-dd-HHmm » à la fin du nom, le nom de machine pouvant contenir des tirets.</summary>
        internal static string Stamp(string name)
            => name.Length >= 15 && DateTime.TryParseExact(name.Substring(name.Length - 15), "yyyy-MM-dd-HHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? name.Substring(name.Length - 15)
                : string.Empty;

        private static bool IsBackup(IFileSystemGateway files, string path)
            => files.FileExists(Path.Combine(path, "manifeste.csv")) ||
               files.FileExists(Path.Combine(path, ReinstallSheet.FileName));

        public static RestoreLayout Build(IFileSystemGateway files, string backup, RestoreTargets targets)
        {
            var items = new List<RestoreItem>();
            var left = new List<string>();

            Personal(files, backup, targets, items, left);

            var applications = Path.Combine(backup, AppDataCatalog.Folder);
            if (files.DirectoryExists(applications))
                foreach (var directory in files.EnumerateDirectories(applications))
                    Application(files, directory, targets, items, left);

            var wifi = new List<string>();
            var wifiFolder = Path.Combine(backup, WifiExport.Folder);
            if (files.DirectoryExists(wifiFolder))
            {
                var scan = files.Scan(new DirectoryScanRequest(wifiFolder) { TopLevelOnly = true, MaxFiles = 2000 },
                    System.Threading.CancellationToken.None);
                if (scan.HasValue)
                    foreach (var file in scan.Value.Files)
                        if (file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) wifi.Add(file.Path);
            }

            return new RestoreLayout { Backup = backup, Items = items, WifiProfiles = wifi, Left = left };
        }

        private static void Personal(
            IFileSystemGateway files, string backup, RestoreTargets targets, ICollection<RestoreItem> items,
            ICollection<string> left)
        {
            var map = new[]
            {
                ("Bureau", targets.Desktop),
                ("Documents", targets.Documents),
                ("Images", targets.Pictures),
                ("Musique", targets.Music),
                ("Vidéos", targets.Videos),
                ("Téléchargements", Combine(targets.UserProfile, "Downloads")),
            };

            foreach (var (label, destination) in map)
            {
                var source = Path.Combine(backup, label);
                if (!files.DirectoryExists(source) || destination.Length == 0) continue;

                items.Add(new RestoreItem
                {
                    Label = label,
                    Source = source,
                    Destination = destination,
                    Mode = RestoreMode.Merge,

                    // desktop.ini décrit l'icône et le nom localisé du dossier : celui de la
                    // nouvelle installation est le bon, et l'ancien serait signalé en conflit
                    // dans chaque dossier.
                    Keep = file => !string.Equals(file, "desktop.ini", StringComparison.OrdinalIgnoreCase),
                });
            }

            if (files.DirectoryExists(Path.Combine(backup, "OneDrive")))
                left.Add("OneDrive : non restauré. Le contenu revient en se connectant au compte OneDrive du client ; " +
                         "la copie reste dans le dossier « OneDrive » de la sauvegarde si un fichier manque.");
        }

        private static void Application(
            IFileSystemGateway files, string directory, RestoreTargets targets, ICollection<RestoreItem> items,
            ICollection<string> left)
        {
            var name = Path.GetFileName(directory);

            switch (name)
            {
                case "Google Chrome":
                    Chromium(files, directory, name, Combine(targets.LocalAppData, @"Google\Chrome\User Data"), "chrome.exe", items);
                    return;

                case "Microsoft Edge":
                    Chromium(files, directory, name, Combine(targets.LocalAppData, @"Microsoft\Edge\User Data"), "msedge.exe", items);
                    return;

                case "Mozilla Firefox":
                    items.Add(Replace(name, name, directory, Combine(targets.RoamingAppData, @"Mozilla\Firefox"), "firefox.exe"));
                    return;

                case "Mozilla Thunderbird":
                    items.Add(Replace(name, name, directory, Combine(targets.RoamingAppData, "Thunderbird"), "thunderbird.exe"));
                    return;

                case "Outlook":
                    var data = Path.Combine(directory, "Fichiers de données");
                    if (files.DirectoryExists(data))
                        items.Add(new RestoreItem
                        {
                            Label = "Outlook / archives .pst",
                            Application = "Microsoft Outlook",
                            Source = data,
                            Destination = Combine(targets.Documents, "Fichiers Outlook"),
                            Mode = RestoreMode.Merge,
                        });

                    var signatures = Path.Combine(directory, "Signatures");
                    if (files.DirectoryExists(signatures))
                        items.Add(new RestoreItem
                        {
                            Label = "Outlook / signatures",
                            Application = "Microsoft Outlook",
                            Source = signatures,
                            Destination = Combine(targets.RoamingAppData, @"Microsoft\Signatures"),
                            Mode = RestoreMode.Merge,
                        });
                    return;

                case "Pense-bêtes":
                    var package = Combine(targets.LocalAppData, @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe");
                    if (!files.DirectoryExists(package))
                    {
                        left.Add("Pense-bêtes : l'application n'est pas encore installée sur cette machine. L'ouvrir une " +
                                 "fois depuis le menu Démarrer, la fermer, puis relancer la restauration.");
                        return;
                    }

                    items.Add(Replace(name, name, directory, Path.Combine(package, "LocalState"), "Microsoft.Notes.exe"));
                    return;

                case "OneNote":
                    items.Add(new RestoreItem
                    {
                        Label = "OneNote / sauvegardes",
                        Application = "OneNote",
                        Source = directory,
                        Destination = Combine(targets.Documents, "Sauvegardes OneNote"),
                        Mode = RestoreMode.Merge,
                    });
                    return;

                case "Microsoft Teams":
                    var backgrounds = Path.Combine(directory, "Arrière-plans");
                    if (files.DirectoryExists(backgrounds))
                        items.Add(new RestoreItem
                        {
                            Label = "Teams / arrière-plans",
                            Application = "Microsoft Teams",
                            Source = backgrounds,
                            Destination = Combine(targets.Pictures, "Arrière-plans Teams"),
                            Mode = RestoreMode.Merge,
                        });
                    return;

                case "Sauvegardes de jeux":
                    items.Add(new RestoreItem
                    {
                        Label = name,
                        Application = name,
                        Source = directory,
                        Destination = Combine(targets.UserProfile, "Saved Games"),
                        Mode = RestoreMode.Merge,
                    });
                    return;

                default:
                    left.Add("« " + Path.Combine(AppDataCatalog.Folder, name) + " » : dossier non reconnu par cette " +
                             "version, laissé dans la sauvegarde.");
                    return;
            }
        }

        private static void Chromium(
            IFileSystemGateway files, string directory, string name, string userData, string process,
            ICollection<RestoreItem> items)
        {
            var profiles = new List<string>();
            foreach (var profile in files.EnumerateDirectories(directory))
            {
                var profileName = Path.GetFileName(profile);
                profiles.Add(profileName);
                items.Add(Replace(name + " / " + profileName, name, profile, Path.Combine(userData, profileName), process));
            }

            var localState = Path.Combine(directory, "Local State");
            if (files.FileExists(localState))
                items.Add(new RestoreItem
                {
                    Label = name + " / liste des profils",
                    Application = name,
                    Source = localState,
                    Destination = Path.Combine(userData, "Local State"),
                    Mode = RestoreMode.ChromiumLocalState,
                    Processes = new[] { process },
                    Profiles = profiles,
                });
        }

        private static RestoreItem Replace(string label, string application, string source, string destination, string process)
            => new RestoreItem
            {
                Label = label,
                Application = application,
                Source = source,
                Destination = destination,
                Mode = RestoreMode.ReplaceFolder,
                Processes = new[] { process },
            };

        private static string Combine(string root, string relative)
            => string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, relative);
    }
}
