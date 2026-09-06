using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Platform
{
    /// <summary>
    /// Fonctionnalités système dont la disponibilité varie de Windows 7 à Windows 11.
    /// Chaque entrée est sondée une fois par session par le registre de fonctionnalités.
    /// </summary>
    public enum FeatureId
    {
        /// <summary>IsWow64Process2 : seul moyen de distinguer un vrai ARM64 d'un x64 (Win10 1511+).</summary>
        Wow64Process2 = 1,

        /// <summary>Lecture SMART ATA par DeviceIoControl. Disponible partout, sans élévation.</summary>
        SmartAta = 2,

        /// <summary>SMART NVMe via StorageDeviceProtocolSpecificProperty (Windows 10 1607+).</summary>
        SmartNvme = 3,

        /// <summary>Espace de noms WMI root\Microsoft\Windows\Storage, MSFT_PhysicalDisk (Windows 8+).</summary>
        StorageManagementWmi = 4,

        /// <summary>StorageDeviceSeekPenaltyProperty, détection SSD/HDD de repli, disponible dès Windows 7.</summary>
        SeekPenaltyQuery = 5,

        /// <summary>Compteurs de performance « GPU Engine » (Windows 10 1709+).</summary>
        GpuEngineCounters = 6,

        /// <summary>État Secure Boot (Windows 8+, et machine en mode UEFI).</summary>
        SecureBootState = 7,

        /// <summary>Classe WMI Win32_Tpm dans root\CIMV2\Security\MicrosoftTpm.</summary>
        TpmWmi = 8,

        /// <summary>Espace de noms root\Microsoft\Windows\Defender (Windows 10+).</summary>
        DefenderWmi = 9,

        /// <summary>Espace de noms root\SecurityCenter2, antivirus tiers. Disponible dès Windows 7.</summary>
        SecurityCenter2 = 10,

        /// <summary>DISM /Online /Cleanup-Image /RestoreHealth (Windows 8+).</summary>
        DismRestoreHealth = 11,

        /// <summary>PowerShell 3.0 ou supérieur. Jamais requis, accélérateur optionnel uniquement.</summary>
        PowerShell3 = 12,

        /// <summary>API WLAN native (wlanapi.dll), préférée au parsing localisé de netsh.</summary>
        WlanApi = 13,

        /// <summary>System.Diagnostics.Eventing.Reader, lecture XML des journaux (Windows 7+).</summary>
        EventLogXmlApi = 14,

        /// <summary>Service de restauration système présent ET activé sur le volume système.</summary>
        SystemRestore = 15,

        /// <summary>powercfg /sleepstudy et /batteryreport (Windows 8+).</summary>
        PowerCfgReports = 16,
    }

    /// <summary>Disponibilité d'une fonctionnalité sur la machine courante, avec sa justification.</summary>
    public sealed class FeatureState
    {
        public FeatureId Id { get; init; }

        public string DisplayName { get; init; } = string.Empty;

        public Availability Availability { get; init; }

        /// <summary>
        /// Toujours renseignée, y compris quand la fonctionnalité est disponible.
        /// C'est ce texte qui s'affiche dans l'infobulle du badge ✅ / ⚠️ / ❌.
        /// </summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>Solution de repli employée quand la fonctionnalité manque, s'il en existe une.</summary>
        public string? Workaround { get; init; }

        /// <summary>Vrai si la fonctionnalité peut être utilisée, éventuellement après élévation.</summary>
        public bool IsUsable =>
            Availability == Availability.Available ||
            Availability == Availability.Partial ||
            Availability == Availability.RequiresElevation;

        public override string ToString() => Id + " = " + Availability + " (" + Reason + ")";
    }

    /// <summary>
    /// Registre des fonctionnalités. Les résultats sont sondés une seule fois puis mis en cache
    /// pour toute la session.
    /// </summary>
    public interface IFeatureRegistry
    {
        FeatureState Get(FeatureId id);

        /// <summary>Vrai uniquement si la fonctionnalité est pleinement disponible sans élévation.</summary>
        bool IsAvailable(FeatureId id);

        IReadOnlyList<FeatureState> All { get; }
    }
}
