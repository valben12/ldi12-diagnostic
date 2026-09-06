using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Core.Execution
{
    /// <summary>Description d'un disque physique obtenue par IOCTL, sans passer par WMI.</summary>
    public sealed class PhysicalDriveDescriptor
    {
        public int Index { get; init; }
        public string? Model { get; init; }
        public string? Vendor { get; init; }
        public string? Firmware { get; init; }
        public string? SerialNumber { get; init; }
        public StorageBusType BusType { get; init; }
        public long SizeBytes { get; init; }
        public bool IsRemovable { get; init; }

        /// <summary>
        /// Pénalité de recherche : vrai pour un plateau mécanique, faux pour de la mémoire flash.
        /// Disponible dès Windows 7 et sans élévation : c'est le repli de détection SSD/HDD
        /// quand MSFT_PhysicalDisk n'existe pas.
        /// </summary>
        public bool? IncursSeekPenalty { get; init; }
    }

    /// <summary>
    /// Accès bas niveau aux disques.
    /// </summary>
    /// <remarks>
    /// L'énumération et les propriétés de périphérique s'obtiennent en ouvrant le disque avec un
    /// droit d'accès nul, ce qui ne demande aucun privilège. La lecture SMART, elle, exige un
    /// accès en lecture et donc l'élévation : les deux opérations sont volontairement séparées
    /// pour qu'un technicien non élevé obtienne tout de même l'inventaire des disques.
    /// </remarks>
    public interface IStorageApi
    {
        Measured<IReadOnlyList<PhysicalDriveDescriptor>> EnumeratePhysicalDrives();

        /// <summary>
        /// Index du disque physique portant un volume (« C: »).
        /// </summary>
        /// <remarks>
        /// Passe par IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS : instantané, sans élévation, et
        /// disponible dès Windows 7 : là où le rapprochement équivalent par WMI coûte deux
        /// requêtes lentes et s'appuie sur un identifiant de partition traduit.
        /// </remarks>
        int? ReadVolumeDiskIndex(string driveLetter);

        /// <summary>
        /// Lit les attributs SMART d'un disque ATA/SATA.
        /// </summary>
        /// <remarks>
        /// Deux chemins : les commandes SMART, qui exigent l'élévation et rendent aussi les
        /// seuils constructeur, et la demande de prédiction de panne, qui n'exige rien et rend
        /// la même table d'attributs. Le second est ce qui permet de lire les heures de
        /// fonctionnement et les cycles d'allumage d'un disque SATA en compte standard.
        /// </remarks>
        Measured<SmartData> ReadAtaSmart(int driveIndex);

        /// <summary>
        /// Lit ce que le disque déclare de lui-même : rotation et génération de liaison.
        /// </summary>
        /// <remarks>
        /// <b>Exige l'élévation, sans échappatoire.</b> La commande ATA brute est le seul chemin
        /// vers ces deux caractéristiques, et son code de contrôle est déclaré en lecture et en
        /// écriture, contrairement à la prédiction de panne, il n'existe ici aucune porte
        /// dérobée. Sur un compte standard, la réponse est « privilèges requis », ce qui est une
        /// information et non une absence.
        /// </remarks>
        Measured<AtaIdentity> ReadAtaIdentity(int driveIndex);

        /// <summary>
        /// Lit le journal de santé NVMe (page 0x02). Nécessite Windows 10 1607 ou supérieur,
        /// et, contrairement à l'ATA, ne demande pas d'élévation sur la plupart des pilotes.
        /// </summary>
        Measured<SmartData> ReadNvmeSmart(int driveIndex);
    }
}
