using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public enum StorageMediaType
    {
        Unknown = 0,
        Hdd = 1,
        Ssd = 2,
        Nvme = 3,
        Removable = 4,
        Virtual = 5,
    }

    public enum StorageBusType
    {
        Unknown = 0,
        Ata = 1,
        Sata = 2,
        Nvme = 3,
        Usb = 4,
        Scsi = 5,
        Sas = 6,
        Raid = 7,
        Virtual = 8,
        SecureDigital = 9,
    }

    public enum SmartOverallStatus
    {
        Unknown = 0,

        /// <summary>Aucun seuil constructeur franchi.</summary>
        Ok = 1,

        /// <summary>Attributs dégradés sans franchissement de seuil : à surveiller.</summary>
        Warning = 2,

        /// <summary>Seuil constructeur franchi : défaillance annoncée par le disque lui-même.</summary>
        Failing = 3,

        /// <summary>Le disque ou son contrôleur n'expose pas le SMART (fréquent en USB et en RAID).</summary>
        NotSupported = 4,
    }

    /// <summary>Attribut SMART brut, tel que rapporté par le disque.</summary>
    public sealed class SmartAttribute
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Current { get; init; }
        public int Worst { get; init; }
        public int Threshold { get; init; }
        public long Raw { get; init; }

        /// <summary>Attribut dont la dégradation annonce une panne (secteurs, erreurs, usure).</summary>
        public bool IsCritical { get; init; }

        /// <summary>Vrai si la valeur normalisée est retombée au seuil constructeur.</summary>
        public bool ThresholdExceeded => Threshold > 0 && Current > 0 && Current <= Threshold;
    }

    public sealed class SmartData
    {
        public Measured<SmartOverallStatus> OverallStatus { get; init; }
        public Measured<long> PowerOnHours { get; init; }
        public Measured<long> PowerCycles { get; init; }
        public Measured<int> TemperatureCelsius { get; init; }

        /// <summary>Secteurs réalloués (05). Toute valeur non nulle mérite l'attention.</summary>
        public Measured<long> ReallocatedSectors { get; init; }

        /// <summary>Secteurs instables en attente de réallocation (C5). Le signal le plus précoce.</summary>
        public Measured<long> PendingSectors { get; init; }

        /// <summary>Erreurs non corrigeables (C6).</summary>
        public Measured<long> UncorrectableErrors { get; init; }

        /// <summary>Usure d'un SSD, en pourcentage de la durée de vie consommée.</summary>
        public Measured<double> WearPercent { get; init; }

        public Measured<long> TotalBytesWritten { get; init; }
        public IReadOnlyList<SmartAttribute> Attributes { get; init; } = Array.Empty<SmartAttribute>();
    }

    /// <summary>
    /// Ce que le disque déclare de lui-même, par la commande ATA « IDENTIFY DEVICE ».
    /// </summary>
    /// <remarks>
    /// <b>Réservé aux sessions administrateur.</b> Contrairement aux attributs SMART, qui passent
    /// par la demande de prédiction de panne, ces deux caractéristiques n'ont aucune porte
    /// dérobée : <c>IOCTL_ATA_PASS_THROUGH</c> est déclaré en lecture et en écriture. Sur un
    /// compte standard, elles ressortent « privilèges requis », ce qui est une information, et
    /// non une absence.
    /// <para>
    /// Elles valent l'élévation qu'elles coûtent. La génération négociée explique en une ligne
    /// un disque à mémoire flash deux fois trop lent : branché sur un port d'une génération
    /// antérieure, il tient exactement la moitié de son débit, et rien d'autre dans Windows ne
    /// le dit.
    /// </para>
    /// </remarks>
    public sealed class AtaIdentity
    {
        /// <summary>
        /// Vitesse de rotation des plateaux.
        /// </summary>
        /// <remarks>
        /// Absente sur un disque à mémoire flash, et c'est alors une confirmation plutôt qu'une
        /// lacune : le disque déclare lui-même n'avoir rien qui tourne.
        /// </remarks>
        public Measured<int> RotationRpm { get; init; }

        /// <summary>Débit de la liaison réellement négociée avec le port, en gigabits par seconde.</summary>
        public Measured<double> LinkGigabitsPerSecond { get; init; }

        /// <summary>Débit le plus élevé que le disque sait tenir.</summary>
        public Measured<double> MaximumGigabitsPerSecond { get; init; }
    }

    public sealed class PhysicalDiskInfo
    {
        /// <summary>Index de \\.\PhysicalDriveN. Clé de rapprochement avec les volumes.</summary>
        public int Index { get; init; }

        public Measured<string> Model { get; init; }
        public Measured<string> Manufacturer { get; init; }
        public Measured<string> Firmware { get; init; }
        public Measured<string> SerialNumber { get; init; }
        public Measured<StorageBusType> BusType { get; init; }
        public Measured<StorageMediaType> MediaType { get; init; }
        public Measured<long> CapacityBytes { get; init; }
        public Measured<bool> IsSystemDisk { get; init; }
        public SmartData Smart { get; init; } = new SmartData();

        /// <summary>Rotation et génération de liaison, lues en session administrateur.</summary>
        public AtaIdentity Identity { get; init; } = new AtaIdentity();

        /// <summary>
        /// Le même disque, avec ses données de santé.
        /// </summary>
        /// <remarks>
        /// La sonde SMART reçoit des disques déjà décrits par la sonde d'énumération et doit leur
        /// adjoindre leur santé. Elle les recopiait champ par champ, et le premier champ ajouté
        /// depuis (la rotation et la génération de liaison) a été silencieusement perdu : la
        /// valeur remontait de la passerelle, traversait la première sonde, et disparaissait dans
        /// la seconde sans qu'aucune erreur ne soit levée.
        /// <para>
        /// La recopie vit désormais ici, à côté des champs qu'elle recopie, et un test la
        /// parcourt par réflexion : ajouter une propriété sans l'ajouter ici fait échouer la
        /// construction du diagnostic, au lieu de vider une ligne d'écran.
        /// </para>
        /// </remarks>
        public PhysicalDiskInfo WithSmart(SmartData smart) => new PhysicalDiskInfo
        {
            Index = Index,
            Model = Model,
            Manufacturer = Manufacturer,
            Firmware = Firmware,
            SerialNumber = SerialNumber,
            BusType = BusType,
            MediaType = MediaType,
            CapacityBytes = CapacityBytes,
            IsSystemDisk = IsSystemDisk,
            Identity = Identity,
            Smart = smart,
        };
    }

    public sealed class VolumeInfo
    {
        public Measured<string> DriveLetter { get; init; }
        public Measured<string> Label { get; init; }
        public Measured<string> FileSystem { get; init; }
        public Measured<long> TotalBytes { get; init; }
        public Measured<long> FreeBytes { get; init; }
        public Measured<double> UsagePercent { get; init; }
        public Measured<bool> IsSystemVolume { get; init; }
        public Measured<string> BitLockerStatus { get; init; }

        /// <summary>Disque physique porteur, quand la correspondance a pu être établie.</summary>
        public Measured<int> DiskIndex { get; init; }
    }

    public sealed class StorageSnapshot
    {
        /// <summary>Ce qui occupe le disque système et que rien d'autre ne mesure.</summary>
        public SystemSpaceSnapshot SystemSpace { get; init; } = new SystemSpaceSnapshot();

        /// <summary>Ce que Windows fait, ou ne fait plus, pour les disques.</summary>
        public StorageMaintenance Maintenance { get; init; } = new StorageMaintenance();

        public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks { get; init; } = Array.Empty<PhysicalDiskInfo>();
        public IReadOnlyList<VolumeInfo> Volumes { get; init; } = Array.Empty<VolumeInfo>();
    }
}
