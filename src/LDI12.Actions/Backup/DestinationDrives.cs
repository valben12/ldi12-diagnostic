using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    public enum DestinationKind
    {
        /// <summary>Clé USB ou carte mémoire : Windows la déclare amovible.</summary>
        Removable = 0,

        /// <summary>
        /// Disque interne ou externe.
        /// </summary>
        /// <remarks>
        /// Un disque dur externe en USB se déclare presque toujours « fixe », comme un disque
        /// interne : Windows ne réserve le type amovible qu'aux supports à média retirable.
        /// </remarks>
        Fixed = 1,

        /// <summary>Lecteur réseau : un NAS, un partage de l'atelier.</summary>
        Network = 2,
    }

    /// <summary>Un volume où une sauvegarde peut être déposée, ou retrouvée.</summary>
    public sealed class DestinationDrive
    {
        /// <summary>Racine du volume, « E:\ ».</summary>
        public string Root { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string? Format { get; init; }

        public DestinationKind Kind { get; init; }

        public long TotalBytes { get; init; }

        public long FreeBytes { get; init; }

        /// <summary>Sauvegardes LDI12 trouvées à la racine du volume.</summary>
        public int Backups { get; init; }

        /// <summary>Date de la plus récente, telle qu'elle figure dans le nom de son dossier.</summary>
        public DateTime? LatestBackup { get; init; }

        /// <summary>Une sauvegarde inachevée de cette machine et de ce compte, que la prochaine copie reprendra.</summary>
        public ResumableBackup? Resumable { get; init; }

        public bool HasResumable => Resumable != null;

        public string? ResumableText
            => Resumable == null
                ? null
                : "Sauvegarde interrompue de cette machine, du " + Resumable.StartedText + " : la copie la reprendra.";

        /// <summary>« E: · Sauvegardes » : la lettre d'abord, c'est elle qu'on lit sur l'Explorateur.</summary>
        public string Title => Root.TrimEnd('\\') + (Label.Length > 0 ? " · " + Label : string.Empty);

        public string KindText => Kind switch
        {
            DestinationKind.Removable => "Clé USB ou carte mémoire",
            DestinationKind.Network => "Lecteur réseau",
            _ => "Disque",
        };

        public string SpaceText
            => ValueFormat.Bytes(FreeBytes) + " libres sur " + ValueFormat.Bytes(TotalBytes) +
               (Format == null ? string.Empty : " · " + Format);

        public string BackupsText
            => Backups == 0
                ? "Aucune sauvegarde LDI12"
                : Backups + " sauvegarde(s) LDI12" + (LatestBackup.HasValue
                    ? ", la plus récente du " + LatestBackup.Value.ToString("d MMMM yyyy 'à' HH'h'mm", Culture)
                    : string.Empty);

        /// <summary>
        /// Un support en FAT32 refuse tout fichier de 4 Go ou plus.
        /// </summary>
        /// <remarks>
        /// C'est le format d'origine de la plupart des clés USB. Une vidéo de vacances ou une
        /// archive Outlook y échoue fichier par fichier, et on ne le découvre qu'au compte rendu.
        /// </remarks>
        public bool IsFat32 => string.Equals(Format, "FAT32", StringComparison.OrdinalIgnoreCase);

        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");
    }

    /// <summary>
    /// Les volumes branchés sur lesquels une sauvegarde peut aller.
    /// </summary>
    /// <remarks>
    /// <b>Le volume de Windows n'en fait jamais partie.</b> Sauvegarder sur le disque qu'on
    /// s'apprête à effacer n'est pas une sauvegarde, et le proposer d'un clic reviendrait à
    /// rendre cette erreur facile. Les lecteurs optiques non plus : on n'y écrit pas ainsi.
    /// <para>
    /// La lecture est faite par <see cref="DriveInfo"/>, sans privilèges et sans WMI. Un volume
    /// qui ne répond pas (lecteur de cartes vide, lecteur réseau déconnecté) est simplement
    /// absent de la liste.
    /// </para>
    /// </remarks>
    public static class DestinationDrives
    {
        /// <param name="machine">Nom de la machine, pour repérer une sauvegarde à reprendre. Nul : on ne cherche pas.</param>
        /// <param name="account">Compte ouvert, pour la même raison.</param>
        public static IReadOnlyList<DestinationDrive> Detect(IFileSystemGateway files, string? machine = null, string? account = null)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<DestinationDrive>();
            }

            var system = SystemRoot();
            var result = new List<DestinationDrive>();

            foreach (var drive in drives)
            {
                var kind = Kind(drive.DriveType);
                if (kind == null) continue;
                if (string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)) continue;

                var found = Describe(files, drive, kind.Value, machine, account);
                if (found != null) result.Add(found);
            }

            return result;
        }

        /// <summary>La sauvegarde la plus récente d'un volume, et combien d'autres il en porte.</summary>
        public static (int Count, DateTime? Latest) Backups(IFileSystemGateway files, string root)
        {
            var latest = RestoreCatalog.Find(files, root, out var others);
            if (latest == null) return (0, null);

            var stamp = RestoreCatalog.Stamp(Path.GetFileName(latest.TrimEnd('\\')));
            return (others + 1, RestoreCatalog.StampDate(stamp));
        }

        private static DestinationDrive? Describe(
            IFileSystemGateway files, DriveInfo drive, DestinationKind kind, string? machine, string? account)
        {
            try
            {
                if (!drive.IsReady) return null;

                var (count, latest) = Backups(files, drive.Name);

                return new DestinationDrive
                {
                    Root = drive.Name,
                    Label = drive.VolumeLabel ?? string.Empty,
                    Format = string.IsNullOrWhiteSpace(drive.DriveFormat) ? null : drive.DriveFormat,
                    Kind = kind,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    Backups = count,
                    LatestBackup = latest,
                    Resumable = machine == null || account == null
                        ? null
                        : BackupState.FindResumable(files, drive.Name, machine, account),
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        internal static DestinationKind? Kind(DriveType type) => type switch
        {
            DriveType.Removable => DestinationKind.Removable,
            DriveType.Fixed => DestinationKind.Fixed,
            DriveType.Network => DestinationKind.Network,
            _ => null,
        };

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
