using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Storage
{
    /// <summary>
    /// Volumes montés : espace, système de fichiers, chiffrement.
    /// </summary>
    /// <remarks>
    /// Les tailles viennent de l'API de fichiers, pas de WMI : c'est la donnée la plus souvent
    /// décisive en intervention (« disque plein » est la première cause de lenteur) et elle
    /// doit rester disponible même quand tout le reste échoue.
    /// </remarks>
    public sealed class VolumeProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Volumes,
            DisplayName = "Volumes et espace disque",
            Category = DiagnosticCategory.Storage,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(25),
            Isolation = IsolationMode.SeparateProcess,
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            // Les deux lectures sont indépendantes : la détection BitLocker attend une réponse
            // du service WMI, la correspondance disque/volume non. Les enchaîner ajouterait
            // l'attente de l'une à la durée de l'autre.
            var mapTask = DiskVolumeMap.LoadAsync(context, cancellationToken);
            var bitLockerTask = ReadBitLockerAsync(context, cancellationToken);
            var map = await mapTask.ConfigureAwait(false);
            var bitLocker = await bitLockerTask.ConfigureAwait(false);

            var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemLetter = systemDrive.Length >= 2 ? systemDrive.Substring(0, 2) : null;

            var volumes = new List<VolumeInfo>();
            var unreadable = 0;

            foreach (var drive in SafeGetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Un lecteur réseau déconnecté ou un lecteur optique vide lève à la lecture :
                // c'est un cas courant, pas une anomalie.
                if (drive.DriveType != DriveType.Fixed) continue;

                var letter = drive.Name.Length >= 2 ? drive.Name.Substring(0, 2) : drive.Name;

                long total = 0, free = 0;
                string? label = null, fileSystem = null;
                var readable = false;
                try
                {
                    if (drive.IsReady)
                    {
                        total = drive.TotalSize;
                        free = drive.TotalFreeSpace;
                        label = drive.VolumeLabel;
                        fileSystem = drive.DriveFormat;
                        readable = true;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    unreadable++;
                }

                var diskIndex = map.IsAvailable ? map.DiskIndexFor(letter) : null;

                volumes.Add(new VolumeInfo
                {
                    DriveLetter = Measured.Ok(letter, DataSource.NativeApi),
                    Label = string.IsNullOrWhiteSpace(label)
                        ? Measured.Missing<string>("Ce volume n'a pas de nom.")
                        : Measured.Ok(label!, DataSource.NativeApi),
                    FileSystem = fileSystem == null
                        ? Measured.Missing<string>("Le système de fichiers n'a pas pu être lu.")
                        : Measured.Ok(fileSystem, DataSource.NativeApi),
                    TotalBytes = readable
                        ? Measured.Ok(total, DataSource.NativeApi)
                        : Measured.Missing<long>("Le volume n'a pas répondu à la lecture de sa taille."),
                    FreeBytes = readable
                        ? Measured.Ok(free, DataSource.NativeApi)
                        : Measured.Missing<long>("Le volume n'a pas répondu à la lecture de son espace libre."),
                    UsagePercent = readable && total > 0
                        ? Measured.Ok(Math.Round(100d * (total - free) / total, 1), DataSource.Inferred)
                        : Measured.Missing<double>("Le taux d'occupation n'a pas pu être calculé."),
                    IsSystemVolume = systemLetter == null
                        ? Measured.Missing<bool>("Le volume système n'a pas pu être identifié.")
                        : Measured.Ok(string.Equals(letter, systemLetter, StringComparison.OrdinalIgnoreCase),
                            DataSource.NativeApi),
                    BitLockerStatus = bitLocker.TryGetValue(letter, out var status)
                        ? status
                        : bitLocker.Count == 0
                            ? BitLockerUnavailable(context)
                            : Measured.Ok("Non chiffré", DataSource.Wmi),
                    DiskIndex = diskIndex.HasValue
                        ? Measured.Ok(diskIndex.Value, DataSource.Wmi)
                        : Measured.Missing<int>(
                            map.IsAvailable
                                ? "Ce volume n'a pas pu être rattaché à un disque physique."
                                : "La correspondance volume/disque est indisponible : " + map.Reason),
                });
            }

            context.Draft.SetVolumes(volumes, Maintenance(context));

            if (volumes.Count == 0)
                return ProbeOutcome.Failed("Aucun volume fixe n'a été trouvé sur cette machine.");

            return unreadable > 0
                ? ProbeOutcome.Partial(volumes.Count + " volume(s) : " + unreadable + " n'a pas répondu.")
                : ProbeOutcome.Ok(volumes.Count + " volume(s)");
        }

        private static IEnumerable<DriveInfo> SafeGetDrives()
        {
            try { return DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<DriveInfo>();
            }
        }

        private static Measured<string> BitLockerUnavailable(ProbeContext context)
            => context.Platform.IsElevated
                ? Measured.Missing<string>(
                    "Le chiffrement BitLocker n'est pas géré par cette édition de Windows.")
                : Measured.NeedsElevation<string>("lecture de l'état de chiffrement BitLocker");

        /// <summary>
        /// BitLocker se lit dans un espace de noms dédié, absent des éditions Famille et
        /// inaccessible sans élévation. Les deux cas se distinguent, comme partout ailleurs.
        /// </summary>
        private static async Task<Dictionary<string, Measured<string>>> ReadBitLockerAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, Measured<string>>(StringComparer.OrdinalIgnoreCase);
            const string encryptionNamespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

            // Win32_EncryptableVolume exige des privilèges administrateur par conception : sans
            // élévation, la réponse est connue d'avance. Et il ne s'agit pas d'une économie
            // théorique : ManagementScope.Connect() ignore son délai sur le chemin « accès
            // refusé » et immobilise cinq secondes pleines, mesurées sur cette machine.
            if (!context.Platform.IsElevated) return result;

            var state = await context.Wmi.ProbeNamespaceAsync(encryptionNamespace, cancellationToken).ConfigureAwait(false);
            if (state != WmiNamespaceState.Present) return result;

            var query = await context.Wmi.QueryAsync(encryptionNamespace,
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume",
                TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            if (!query.Succeeded) return result;

            foreach (var record in query.Records)
            {
                var letter = record.GetString("DriveLetter");
                if (letter == null) continue;
                result[letter] = Measured.Ok(DescribeProtection(record.GetInt32("ProtectionStatus")), DataSource.Wmi);
            }

            return result;
        }

        private static string DescribeProtection(int? status) => status switch
        {
            0 => "Non chiffré",
            1 => "Chiffré et protégé",
            2 => "Chiffré, protection suspendue",
            _ => "État de chiffrement inconnu",
        };
        /// <summary>
        /// Ce que Windows fait, ou ne fait plus, pour les disques.
        /// </summary>
        /// <remarks>
        /// Comme pour le bridage du processeur, une valeur absente du registre n'est pas une
        /// valeur manquante : Windows n'écrit ce réglage que si quelqu'un l'a changé, et son
        /// absence signifie « activée ». La mesure est donc rendue déduite plutôt que lue.
        /// </remarks>
        private static StorageMaintenance Maintenance(ProbeContext context)
        {
            const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";

            var disabled = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, FileSystemKey, "DisableDeleteNotify");

            return new StorageMaintenance
            {
                TrimEnabled = disabled.HasValue
                    ? Measured.Ok(disabled.Value == 0, DataSource.Registry)
                    : Measured.Ok(true, DataSource.Inferred),
            };
        }

    }
}
