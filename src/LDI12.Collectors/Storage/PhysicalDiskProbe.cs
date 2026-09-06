using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Storage
{
    /// <summary>
    /// Inventaire des disques physiques : modèle, bus, capacité et type de support.
    /// </summary>
    /// <remarks>
    /// L'énumération se fait par IOCTL et non par WMI : elle fonctionne sans privilèges, sans
    /// dépendre du dépôt WMI, et donne le numéro de série que Win32_DiskDrive omet souvent.
    /// <para>
    /// Le type de support suit trois chemins par ordre de fiabilité décroissante : le bus NVMe,
    /// puis MSFT_PhysicalDisk (Windows 8+), puis la pénalité de recherche qui, elle, existe
    /// depuis Windows 7. C'est ce dernier repli qui permet de distinguer un SSD d'un disque dur
    /// sur les machines les plus anciennes.
    /// </para>
    /// </remarks>
    public sealed class PhysicalDiskProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.PhysicalDisks,
            DisplayName = "Disques physiques",
            Category = DiagnosticCategory.Storage,
            EstimatedDuration = TimeSpan.FromSeconds(1.5),
            HardTimeout = TimeSpan.FromSeconds(30),
            Isolation = IsolationMode.SeparateProcess,
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var drives = context.Storage.EnumeratePhysicalDrives();
            if (!drives.HasValue)
            {
                context.Draft.SetPhysicalDisks(Array.Empty<PhysicalDiskInfo>());
                return ProbeOutcome.Failed(drives.Reason ?? "Aucun disque physique n'a pu être énuméré.");
            }

            var mediaTypes = await ReadMediaTypesAsync(context, cancellationToken).ConfigureAwait(false);
            var systemDiskIndex = await FindSystemDiskAsync(context, cancellationToken).ConfigureAwait(false);

            var disks = new List<PhysicalDiskInfo>(drives.Value.Count);
            foreach (var drive in drives.Value)
            {
                mediaTypes.TryGetValue(drive.Index, out var reportedMedia);
                disks.Add(ToDisk(drive, reportedMedia, systemDiskIndex, context));
            }

            context.Draft.SetPhysicalDisks(disks);

            var unknownMedia = 0;
            foreach (var disk in disks)
                if (!disk.MediaType.IsReliable) unknownMedia++;

            return unknownMedia > 0
                ? ProbeOutcome.Partial(disks.Count + " disque(s) : le type de support de " + unknownMedia +
                                       " d'entre eux n'a pas pu être déterminé.")
                : ProbeOutcome.Ok(disks.Count + " disque(s)");
        }

        private static PhysicalDiskInfo ToDisk(
            PhysicalDriveDescriptor drive, StorageMediaType reported, int? systemDiskIndex, ProbeContext context)
        {
            return new PhysicalDiskInfo
            {
                Index = drive.Index,
                Model = Text(drive.Model, "Le modèle du disque"),
                Manufacturer = Text(drive.Vendor, "Le fabricant du disque"),
                Firmware = Text(drive.Firmware, "La version du micrologiciel"),
                SerialNumber = Text(drive.SerialNumber, "Le numéro de série du disque"),
                BusType = drive.BusType != StorageBusType.Unknown
                    ? Measured.Ok(drive.BusType, DataSource.NativeApi)
                    : Measured.Missing<StorageBusType>("Le type de bus n'est pas rapporté par ce contrôleur."),
                MediaType = DetermineMediaType(drive, reported, context),
                CapacityBytes = drive.SizeBytes > 0
                    ? Measured.Ok(drive.SizeBytes, DataSource.NativeApi)
                    : Measured.Missing<long>("La capacité du disque n'a pas pu être lue."),
                IsSystemDisk = systemDiskIndex.HasValue
                    ? Measured.Ok(systemDiskIndex.Value == drive.Index, DataSource.Wmi)
                    : Measured.Missing<bool>("Le disque système n'a pas pu être identifié."),
                Identity = ReadIdentity(drive.Index, context),
            };
        }

        /// <summary>
        /// Rotation et génération de liaison, quand la session le permet.
        /// </summary>
        /// <remarks>
        /// Le refus se propage tel quel dans chacun des trois champs : c'est la seule façon pour
        /// que l'écran affiche « privilèges requis » à côté de la ligne concernée, plutôt qu'un
        /// tiret qui laisserait croire que le disque ne déclare rien.
        /// </remarks>
        private static AtaIdentity ReadIdentity(int index, ProbeContext context)
        {
            var identity = context.Storage.ReadAtaIdentity(index);
            if (identity.HasValue) return identity.Value;

            return new AtaIdentity
            {
                RotationRpm = identity.Map(_ => 0),
                LinkGigabitsPerSecond = identity.Map(_ => 0d),
                MaximumGigabitsPerSecond = identity.Map(_ => 0d),
            };
        }

        private static Measured<StorageMediaType> DetermineMediaType(
            PhysicalDriveDescriptor drive, StorageMediaType reported, ProbeContext context)
        {
            if (drive.BusType == StorageBusType.Nvme)
                return Measured.Ok(StorageMediaType.Nvme, DataSource.NativeApi);

            if (drive.BusType == StorageBusType.Virtual)
                return Measured.Ok(StorageMediaType.Virtual, DataSource.NativeApi);

            if (reported != StorageMediaType.Unknown)
                return Measured.Ok(reported, DataSource.Wmi);

            if (drive.IncursSeekPenalty.HasValue)
            {
                // Un plateau mécanique subit une pénalité de recherche, pas la mémoire flash.
                return Measured.Ok(
                    drive.IncursSeekPenalty.Value ? StorageMediaType.Hdd : StorageMediaType.Ssd,
                    DataSource.NativeApi);
            }

            if (drive.IsRemovable)
                return Measured.Ok(StorageMediaType.Removable, DataSource.NativeApi);

            var feature = context.Platform.Features.Get(FeatureId.StorageManagementWmi);
            return Measured.Missing<StorageMediaType>(
                "Le type de support n'a pas pu être déterminé : ce contrôleur ne rapporte pas la " +
                "pénalité de recherche" +
                (feature.Availability == Availability.Available ? "." : " et " + feature.Reason.ToLowerInvariant()));
        }

        /// <summary>
        /// MSFT_PhysicalDisk (Windows 8+) est la source la plus fiable du type de support, mais
        /// n'existe pas sous Windows 7 : son absence est un repli prévu, pas une erreur.
        /// </summary>
        private static async Task<Dictionary<int, StorageMediaType>> ReadMediaTypesAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, StorageMediaType>();
            if (!context.Platform.Features.IsAvailable(FeatureId.StorageManagementWmi)) return result;

            var query = await context.Wmi.QueryAsync(WmiNamespaces.Storage,
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk",
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!query.Succeeded) return result;

            foreach (var record in query.Records)
            {
                var deviceId = record.GetString("DeviceId");
                if (deviceId == null || !int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    continue;

                // 3 = disque à plateaux, 4 = SSD, 5 = mémoire persistante.
                result[index] = record.GetInt32("MediaType") switch
                {
                    3 => StorageMediaType.Hdd,
                    4 => StorageMediaType.Ssd,
                    5 => StorageMediaType.Ssd,
                    _ => StorageMediaType.Unknown,
                };
            }

            return result;
        }

        private static async Task<int?> FindSystemDiskAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var map = await DiskVolumeMap.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!map.IsAvailable) return null;

            var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (systemDrive.Length < 2) return null;
            return map.DiskIndexFor(systemDrive.Substring(0, 2));
        }

        private static Measured<string> Text(string? value, string label)
        {
            var cleaned = Measure.Clean(value);
            return cleaned == null
                ? Measured.Missing<string>(Measure.Absent(label), DataSource.NativeApi)
                : Measured.Ok(cleaned, DataSource.NativeApi);
        }
    }
}
