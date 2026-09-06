using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Storage
{
    /// <summary>
    /// Correspondance entre lettres de lecteur et disques physiques.
    /// </summary>
    /// <remarks>
    /// Voie principale : un IOCTL par volume, instantané et sans privilèges. La voie WMI n'est
    /// qu'un repli, car elle coûte deux requêtes lentes (plus de cinq secondes sur une machine
    /// portant un périphérique USB) et parce que le rapprochement y passerait par
    /// <c>DeviceID</c> de Win32_DiskPartition, dont le texte est traduit
    /// (« Disque n° 0, Partition n° 1 » sur un Windows français).
    /// </remarks>
    internal sealed class DiskVolumeMap
    {
        private const string CacheKey = "storage.disk-volume-map";

        private readonly Dictionary<string, int> _letterToDisk =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private DiskVolumeMap(bool available, string? reason)
        {
            IsAvailable = available;
            Reason = reason;
        }

        public bool IsAvailable { get; }

        public string? Reason { get; }

        public int? DiskIndexFor(string driveLetter)
            => _letterToDisk.TryGetValue(driveLetter, out var index) ? index : (int?)null;

        /// <summary>Chargement mémoïsé : deux sondes de la même vague ne paient la lecture qu'une fois.</summary>
        public static Task<DiskVolumeMap> LoadAsync(ProbeContext context, CancellationToken cancellationToken)
            => context.SharedAsync(CacheKey, () => BuildAsync(context, cancellationToken));

        private static async Task<DiskVolumeMap> BuildAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var map = new DiskVolumeMap(true, null);

            foreach (var letter in FixedDriveLetters())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = context.Storage.ReadVolumeDiskIndex(letter);
                if (index.HasValue) map._letterToDisk[letter] = index.Value;
            }

            if (map._letterToDisk.Count > 0) return map;

            context.Logger.For("Collectors.Storage")
                .Debug("Aucun volume rattaché par IOCTL, repli sur WMI.");
            return await BuildFromWmiAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private static IEnumerable<string> FixedDriveLetters()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var drive in drives)
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                if (drive.Name.Length >= 2) yield return drive.Name.Substring(0, 2);
            }
        }

        private static async Task<DiskVolumeMap> BuildFromWmiAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(12);

            var partitions = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition", timeout, cancellationToken).ConfigureAwait(false);
            if (!partitions.Succeeded) return new DiskVolumeMap(false, partitions.Reason);

            var links = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition", timeout, cancellationToken).ConfigureAwait(false);
            if (!links.Succeeded) return new DiskVolumeMap(false, links.Reason);

            var partitionToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in partitions.Records)
            {
                var id = record.GetString("DeviceID");
                var diskIndex = record.GetInt32("DiskIndex");
                if (id != null && diskIndex.HasValue) partitionToDisk[id] = diskIndex.Value;
            }

            var map = new DiskVolumeMap(true, null);
            foreach (var record in links.Records)
            {
                var partitionId = ExtractDeviceId(record.GetString("Antecedent"));
                var letter = ExtractDeviceId(record.GetString("Dependent"));
                if (partitionId == null || letter == null) continue;
                if (partitionToDisk.TryGetValue(partitionId, out var diskIndex))
                    map._letterToDisk[letter] = diskIndex;
            }

            return map;
        }

        /// <summary>
        /// Extrait la valeur de DeviceID d'une référence WMI de la forme
        /// <c>\\PC\root\cimv2:Win32_LogicalDisk.DeviceID="C:"</c>. On lit la structure de la
        /// référence, pas le contenu traduit de l'identifiant.
        /// </summary>
        private static string? ExtractDeviceId(string? reference)
        {
            if (reference == null) return null;
            const string marker = "DeviceID=\"";
            var start = reference.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += marker.Length;
            var end = reference.IndexOf('"', start);
            return end <= start ? null : reference.Substring(start, end - start);
        }
    }
}
