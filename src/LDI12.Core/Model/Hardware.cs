using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public sealed class CpuInfo
    {
        public Measured<string> Manufacturer { get; init; }
        public Measured<string> Model { get; init; }
        public Measured<string> Socket { get; init; }
        public Measured<string> Architecture { get; init; }
        public Measured<int> PhysicalCores { get; init; }
        public Measured<int> LogicalCores { get; init; }
        public Measured<int> BaseClockMhz { get; init; }
        public Measured<int> MaxClockMhz { get; init; }
        public Measured<int> CurrentClockMhz { get; init; }
        public Measured<int> L2CacheKb { get; init; }
        public Measured<int> L3CacheKb { get; init; }
        public Measured<bool> VirtualizationEnabled { get; init; }
        public Measured<double> UsagePercent { get; init; }
        public Measured<double> TemperatureCelsius { get; init; }

        /// <summary>
        /// Bridage détecté : la fréquence courante reste durablement sous la fréquence de base.
        /// Indice de surchauffe ou d'alimentation insuffisante, deux causes fréquentes de lenteur
        /// sur les portables anciens.
        /// </summary>
        public Measured<bool> ThrottlingDetected { get; init; }
    }

    public sealed class MemoryModule
    {
        public Measured<string> BankLabel { get; init; }
        public Measured<string> DeviceLocator { get; init; }
        public Measured<long> CapacityBytes { get; init; }
        public Measured<int> SpeedMhz { get; init; }

        /// <summary>Fréquence réellement appliquée. Inférieure à <see cref="SpeedMhz"/> quand la
        /// carte mère sous-cadence le module : cause classique de performances décevantes.</summary>
        public Measured<int> ConfiguredSpeedMhz { get; init; }
        public Measured<string> Manufacturer { get; init; }
        public Measured<string> PartNumber { get; init; }
        public Measured<string> SerialNumber { get; init; }
        public Measured<string> MemoryType { get; init; }
        public Measured<string> FormFactor { get; init; }
    }

    public sealed class MemoryInfo
    {
        public Measured<long> TotalBytes { get; init; }
        public Measured<long> AvailableBytes { get; init; }
        public Measured<double> UsagePercent { get; init; }
        public Measured<long> CommittedBytes { get; init; }
        public Measured<long> CommitLimitBytes { get; init; }
        public Measured<int> SlotsTotal { get; init; }
        public Measured<int> SlotsUsed { get; init; }
        public IReadOnlyList<MemoryModule> Modules { get; init; } = Array.Empty<MemoryModule>();
    }

    public sealed class GpuInfo
    {
        public Measured<string> Manufacturer { get; init; }
        public Measured<string> Model { get; init; }
        public Measured<long> VideoMemoryBytes { get; init; }
        public Measured<string> DriverVersion { get; init; }
        public Measured<DateTimeOffset> DriverDate { get; init; }
        public Measured<string> DriverProvider { get; init; }
        public Measured<string> CurrentResolution { get; init; }
        public Measured<int> RefreshHz { get; init; }
        public Measured<double> UsagePercent { get; init; }
        public Measured<double> TemperatureCelsius { get; init; }

        /// <summary>
        /// Vitesse du ventilateur, en pourcentage de son maximum.
        /// </summary>
        /// <remarks>
        /// Publiée par la bibliothèque du constructeur en même temps que la température, et utile
        /// avec elle : une carte chaude dont le ventilateur tourne à 20 % ne dit pas la même chose
        /// qu'une carte chaude dont le ventilateur est à fond.
        /// </remarks>
        public Measured<double> FanPercent { get; init; }

        /// <summary>
        /// Pilote d'affichage générique Microsoft : la carte fonctionne en mode dégradé, sans
        /// accélération. Réponse directe à « pourquoi cette carte graphique ne fonctionne pas ».
        /// </summary>
        public Measured<bool> UsesGenericMicrosoftDriver { get; init; }
    }

    public enum FirmwareMode
    {
        Unknown = 0,
        LegacyBios = 1,
        Uefi = 2,
    }

    public sealed class BiosInfo
    {
        public Measured<string> Vendor { get; init; }
        public Measured<string> Version { get; init; }
        public Measured<DateTimeOffset> ReleaseDate { get; init; }
        public Measured<FirmwareMode> Mode { get; init; }
    }

    public sealed class TpmInfo
    {
        public Measured<bool> Present { get; init; }
        public Measured<bool> Enabled { get; init; }
        public Measured<bool> Ready { get; init; }
        public Measured<string> SpecVersion { get; init; }
        public Measured<string> Manufacturer { get; init; }
    }

    public sealed class MotherboardInfo
    {
        public Measured<string> Manufacturer { get; init; }
        public Measured<string> Model { get; init; }
        public Measured<string> Version { get; init; }
        public Measured<string> SerialNumber { get; init; }

        public Measured<string> SystemManufacturer { get; init; }
        public Measured<string> SystemModel { get; init; }
        public Measured<string> SystemSku { get; init; }
        public Measured<string> ChassisType { get; init; }
        public Measured<string> SystemUuid { get; init; }

        public BiosInfo Bios { get; init; } = new BiosInfo();
        public TpmInfo Tpm { get; init; } = new TpmInfo();
        public Measured<bool> SecureBootEnabled { get; init; }
    }

    public sealed class BatteryInfo
    {
        public Measured<string> Name { get; init; }
        public Measured<string> Chemistry { get; init; }
        public Measured<string> Status { get; init; }
        public Measured<int> ChargePercent { get; init; }
        public Measured<long> DesignCapacityMwh { get; init; }
        public Measured<long> FullChargeCapacityMwh { get; init; }

        /// <summary>Usure = 1 − (capacité à pleine charge / capacité d'origine).</summary>
        public Measured<double> WearPercent { get; init; }
        public Measured<int> CycleCount { get; init; }
    }

    /// <summary>D'où vient une lecture de température.</summary>
    public enum ThermalSource
    {
        /// <summary>Zone thermique déclarée par le firmware ACPI de la carte mère.</summary>
        AcpiZone = 0,

        /// <summary>Sonde déclarée dans le SMBIOS (type 28). Presque jamais renseignée.</summary>
        Smbios = 1,

        /// <summary>
        /// Capteur physique lu par pilote noyau : DTS d'Intel, Tctl d'AMD, puce Super-I/O.
        /// </summary>
        /// <remarks>
        /// La seule source qui mesure vraiment un composant nommé. Elle n'est disponible que
        /// lorsque le technicien a activé la couche de capteurs, qui charge un pilote.
        /// </remarks>
        HardwareDriver = 2,
    }

    /// <summary>
    /// Une zone thermique, telle que le firmware la déclare.
    /// </summary>
    /// <remarks>
    /// <b>Une zone n'est pas un composant.</b> Le firmware décide de ce que chaque zone mesure et
    /// ne le dit nulle part : selon la carte, <c>\_TZ.TZ00</c> peut suivre le processeur, le
    /// chipset, l'air d'admission, ou rien du tout. Présenter une zone comme « température du
    /// processeur » serait une affirmation qu'aucune mesure ne soutient, d'où le nom ACPI brut,
    /// affiché tel quel.
    /// </remarks>
    public sealed class ThermalZoneReading
    {
        /// <summary>Nom ACPI de la zone, affiché tel quel faute de savoir ce qu'elle mesure.</summary>
        public string Name { get; init; } = string.Empty;

        public ThermalSource Source { get; init; }

        public Measured<double> Celsius { get; init; }

        /// <summary>
        /// La valeur est dans une plage physiquement crédible pour une machine en marche.
        /// </summary>
        /// <remarks>
        /// Une zone à 17 °C dans un boîtier en fonctionnement ne mesure pas ce qu'on croit : elle
        /// est relevée et affichée, mais aucune règle ne conclut dessus. Écarter la valeur
        /// silencieusement serait pire, le technicien ne saurait pas qu'elle existe.
        /// </remarks>
        public bool Plausible { get; init; }

        /// <summary>Vrai si le firmware déclare la zone comme activement refroidie.</summary>
        public Measured<bool> ActivelyCooled { get; init; }

        /// <summary>Composant porteur, quand la source le nomme : « AMD Ryzen 7 5700G ».</summary>
        public string? Component { get; init; }

        /// <summary>
        /// Le capteur appartient au processeur.
        /// </summary>
        /// <remarks>
        /// Sert à ne pas produire deux constats pour une seule surchauffe : la règle des zones
        /// thermiques laisse ces capteurs à la règle du processeur, qui les nomme mieux et
        /// applique les seuils qui leur correspondent.
        /// </remarks>
        public bool IsProcessor { get; init; }
    }

    /// <summary>Un ventilateur et sa vitesse, lus par la couche de capteurs matériels.</summary>
    public sealed class FanReading
    {
        public string Name { get; init; } = string.Empty;

        public string? Component { get; init; }

        /// <summary>
        /// Vitesse en tours par minute. Zéro est une valeur réelle : beaucoup de cartes mères
        /// déclarent des connecteurs de ventilateur inoccupés.
        /// </summary>
        public Measured<double> Rpm { get; init; }
    }

    /// <summary>
    /// Températures relevées sans installer de pilote.
    /// </summary>
    /// <remarks>
    /// Windows n'expose aucune température de processeur sans pilote noyau : ni le DTS d'Intel ni
    /// le Tctl d'AMD ne sont accessibles depuis l'espace utilisateur. Les outils qui les affichent
    /// (HWMonitor, HWiNFO, Core Temp) installent tous un pilote signé qui lit directement les
    /// registres du processeur. LDI12 ne le fait pas : installer un pilote noyau chez un client
    /// pour lire un chiffre serait disproportionné, et ces pilotes sont une porte d'entrée connue.
    /// <para>
    /// Restent les zones ACPI, que le firmware expose sans privilège particulier, et la
    /// température des disques, que le SMART donne. C'est ce qu'on relève, en le nommant pour ce
    /// que c'est.
    /// </para>
    /// </remarks>
    public sealed class ThermalSnapshot
    {
        public IReadOnlyList<ThermalZoneReading> Zones { get; init; } = Array.Empty<ThermalZoneReading>();

        /// <summary>
        /// Température du paquet processeur, Tctl/Tdie chez AMD, DTS chez Intel.
        /// </summary>
        /// <remarks>
        /// Renseignée uniquement quand la couche de capteurs matériels est activée : c'est la
        /// seule source qui la donne, sous Windows, quel que soit le logiciel.
        /// </remarks>
        public Measured<double> CpuPackageCelsius { get; init; }

        public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();

        /// <summary>Vrai quand les valeurs viennent du pilote de capteurs et non des zones ACPI.</summary>
        public bool AdvancedSensorsUsed { get; init; }

        /// <summary>
        /// Ce que la machine n'a pas su rendre, et pourquoi.
        /// </summary>
        /// <remarks>
        /// Renseignée même quand des zones ont été lues : « trois zones relevées » ne répond pas
        /// à la question « quelle est la température du processeur ».
        /// </remarks>
        public string? Limitation { get; init; }
    }

    public sealed class HardwareSnapshot
    {
        public CpuInfo Cpu { get; init; } = new CpuInfo();
        public MemoryInfo Memory { get; init; } = new MemoryInfo();
        public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
        public MotherboardInfo Motherboard { get; init; } = new MotherboardInfo();

        /// <summary>Absent sur un poste fixe : c'est une information, pas une lacune.</summary>
        public IReadOnlyList<BatteryInfo> Batteries { get; init; } = Array.Empty<BatteryInfo>();

        public ThermalSnapshot Thermal { get; init; } = new ThermalSnapshot();

        /// <summary>Les écrans branchés, et l'écart entre ce qu'ils valent et ce qu'on leur demande.</summary>
        public DisplaySnapshot Displays { get; init; } = new DisplaySnapshot();

        /// <summary>Ce que le matériel a déjà signalé de lui-même.</summary>
        public HardwareErrorsInfo Errors { get; init; } = new HardwareErrorsInfo();
    }
}
