using System;
using System.Collections.Generic;
using System.IO;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Un disque dont on peut sauvegarder des données.</summary>
    public sealed class SourceVolume
    {
        public string Root { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public DestinationKind Kind { get; init; }

        public long TotalBytes { get; init; }

        public long UsedBytes { get; init; }

        /// <summary>Le disque du Windows en cours : ses autres comptes seulement, le compte ouvert a sa propre tuile.</summary>
        public bool IsSystem { get; init; }

        public bool HasWindows { get; init; }

        /// <summary>Les comptes de ce Windows : nom et dossier du profil.</summary>
        public IReadOnlyList<(string Name, string Path)> Profiles { get; init; } = Array.Empty<(string, string)>();

        /// <summary>
        /// Les dossiers de la racine qui peuvent porter des données : tous pour un disque de données,
        /// ceux qui ne sont pas à Windows pour un disque système.
        /// </summary>
        public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();

        public string Title => Root.TrimEnd('\\') + (Label.Length > 0 ? " · " + Label : string.Empty);

        public string Description
            => HasWindows
                ? (IsSystem ? "Ce PC : autres comptes" : "Windows") + " · " + Profiles.Count + " compte(s)"
                : "Données · " + ValueFormat.Bytes(UsedBytes) + " utilisés";
    }

    /// <summary>
    /// Les disques branchés d'où une sauvegarde peut partir, à l'atelier comme en intervention.
    /// </summary>
    /// <remarks>
    /// <b>Deux cas, reconnus d'eux-mêmes.</b> Un disque qui porte un Windows (son registre est là)
    /// se sauvegarde par comptes, comme le compte ouvert : c'est le disque d'un PC en panne, branché
    /// sur le PC de l'atelier. Un disque sans Windows se sauvegarde par dossiers : un disque de
    /// données, un second disque interne. Rien n'est lu au-delà des noms de dossiers : les comptes
    /// d'un autre Windows sont protégés, et seul l'hôte élevé les ouvre, au moment de la copie.
    /// </remarks>
    public static class SourceVolumes
    {
        /// <summary>Comptes que Windows crée pour lui-même, qui ne sont ceux de personne.</summary>
        private static readonly HashSet<string> SystemProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Default", "Default User", "Public", "All Users", "defaultuser0", "defaultuser100000",
            "WDAGUtilityAccount", "Classic .NET AppPool",
        };

        /// <summary>Dossiers de la racine d'un disque système qui sont à Windows ou aux logiciels.</summary>
        private static readonly HashSet<string> WindowsFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "PerfLogs",
            "Documents and Settings", "MSOCache", "Intel", "AMD", "NVIDIA", "Drivers", "OneDriveTemp",
            "ESD", "inetpub", "$GetCurrent", "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$SysReset",
            "Windows.old", "XboxGames", "Program Files (Arm)",
        };

        public static IReadOnlyList<SourceVolume> Detect(IFileSystemGateway files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<SourceVolume>();
            }

            var system = SystemRoot();
            var result = new List<SourceVolume>();

            foreach (var drive in drives)
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;

                try
                {
                    if (!drive.IsReady) continue;
                    result.Add(Describe(files, drive.Name, drive.VolumeLabel ?? string.Empty,
                        drive.DriveType == DriveType.Removable ? DestinationKind.Removable : DestinationKind.Fixed,
                        drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace,
                        string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Un lecteur qui ne répond pas n'est simplement pas proposé.
                }
            }

            return result;
        }

        /// <summary>Ce qu'un volume contient, d'après ses seuls noms de dossiers.</summary>
        internal static SourceVolume Describe(
            IFileSystemGateway files, string root, string label, DestinationKind kind, long total, long used, bool isSystem)
        {
            var hasWindows = OfflineWindows.IsWindows(files, root);

            var profiles = new List<(string, string)>();
            if (hasWindows)
            {
                var current = isSystem ? Environment.UserName : null;
                foreach (var directory in files.EnumerateDirectories(Path.Combine(root, "Users")))
                {
                    var name = Path.GetFileName(directory.TrimEnd('\\'));
                    if (SystemProfiles.Contains(name)) continue;
                    if (current != null && string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                    profiles.Add((name, directory));
                }
            }

            var folders = new List<string>();
            foreach (var directory in files.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory.TrimEnd('\\'));
                if (name.Length == 0 || name.StartsWith("$", StringComparison.Ordinal)) continue;
                if (IsSystemDirectory(name)) continue;
                if (hasWindows && WindowsFolders.Contains(name)) continue;
                if (name.StartsWith(BackupState.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                folders.Add(directory);
            }

            profiles.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.CurrentCultureIgnoreCase));
            folders.Sort(StringComparer.CurrentCultureIgnoreCase);

            return new SourceVolume
            {
                Root = root,
                Label = label,
                Kind = kind,
                TotalBytes = total,
                UsedBytes = used,
                IsSystem = isSystem,
                HasWindows = hasWindows,
                Profiles = profiles,
                Folders = folders,
            };
        }

        private static bool IsSystemDirectory(string name)
        {
            foreach (var system in ExtraFolders.SystemDirectories)
                if (string.Equals(system, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string SystemRoot()
        {
            try
            {
                return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                return string.Empty;
            }
        }
    }
}
