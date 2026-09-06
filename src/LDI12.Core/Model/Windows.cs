using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public sealed class WindowsInstallInfo
    {
        public Measured<DateTimeOffset> InstallDate { get; init; }
        public Measured<DateTimeOffset> LastBootTime { get; init; }
        public Measured<TimeSpan> Uptime { get; init; }
        public Measured<string> ActivationStatus { get; init; }
        public Measured<string> Locale { get; init; }
        public Measured<string> TimeZone { get; init; }

        /// <summary>Un redémarrage en attente bloque les mises à jour et fausse tout diagnostic.</summary>
        public Measured<bool> RebootPending { get; init; }

        /// <summary>Démarrage rapide : explique un « redémarrage » qui ne redémarre rien.</summary>
        public Measured<bool> FastStartupEnabled { get; init; }
        public Measured<bool> SystemRestoreEnabled { get; init; }
        public Measured<long> PageFileSizeBytes { get; init; }
    }

    public enum SfcStatus
    {
        Unknown = 0,
        NotRun = 1,
        Clean = 2,
        RepairedSuccessfully = 3,
        CorruptionFound = 4,
        RepairFailed = 5,
    }

    public enum ComponentStoreState
    {
        Unknown = 0,
        Healthy = 1,
        Repairable = 2,
        NonRepairable = 3,
    }

    /// <summary>
    /// État d'intégrité des fichiers système, obtenu <b>sans rien modifier</b> : lecture du
    /// journal CBS et de l'état du magasin de composants. Les réparations relèvent de
    /// LDI12.Actions et exigent une confirmation du technicien.
    /// </summary>
    public sealed class SystemFilesInfo
    {
        public Measured<SfcStatus> Sfc { get; init; }
        public Measured<ComponentStoreState> ComponentStore { get; init; }
        public Measured<DateTimeOffset> LastCheck { get; init; }

        /// <summary>Extrait du journal justifiant le verdict. Affiché au niveau « données techniques ».</summary>
        public Measured<string> Evidence { get; init; }
    }

    public sealed class FailedUpdate
    {
        public string Title { get; init; } = string.Empty;
        public string? KbNumber { get; init; }
        public DateTimeOffset? Date { get; init; }
        public string? ResultCode { get; init; }
    }

    /// <summary>Un correctif installé, à sa date.</summary>
    /// <remarks>
    /// Sert la chronologie, et rien d'autre : savoir <i>quand</i> la machine a été mise à jour
    /// est ce qui permet de lire une série de plantages à côté de ce qui a changé avant elle.
    /// Les correctifs sans date exploitable n'y figurent pas : Windows ne renseigne ce champ
    /// qu'une fois sur deux, et une date inventée serait pire que rien.
    /// </remarks>
    public sealed class InstalledUpdate
    {
        /// <summary>Numéro du correctif, tel que Microsoft le publie.</summary>
        public string Identifier { get; init; } = string.Empty;

        public DateTimeOffset InstalledOn { get; init; }
    }

    public sealed class UpdatesInfo
    {
        public Measured<DateTimeOffset> LastInstalled { get; init; }
        public Measured<int> DaysSinceLastUpdate { get; init; }
        public Measured<string> ServiceState { get; init; }
        public Measured<int> InstalledCount { get; init; }
        public IReadOnlyList<FailedUpdate> Failures { get; init; } = Array.Empty<FailedUpdate>();

        /// <summary>Les correctifs dont la date d'installation est exploitable.</summary>
        public IReadOnlyList<InstalledUpdate> Installed { get; init; } = Array.Empty<InstalledUpdate>();
    }

    public sealed class ServiceInfo
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string StartMode { get; init; } = string.Empty;
        public string? Account { get; init; }

        /// <summary>Service dont l'arrêt casse une fonction visible par le client.</summary>
        public bool IsEssential { get; init; }

        /// <summary>Renseigné quand l'état observé s'écarte de l'état attendu.</summary>
        public string? Deviation { get; init; }
    }

    public sealed class EventSummary
    {
        public string LogName { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public int EventId { get; init; }
        public string Level { get; init; } = string.Empty;

        /// <summary>
        /// Nombre d'occurrences sur la fenêtre analysée. Les événements sont regroupés par
        /// (source, identifiant) : une erreur répétée 400 fois est un signal, pas 400 signaux.
        /// </summary>
        public int Count { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public string? SampleMessage { get; init; }
    }

    public sealed class BsodInfo
    {
        public DateTimeOffset Date { get; init; }
        public string? BugCheckCode { get; init; }
        public string? Parameters { get; init; }
        public string? DumpPath { get; init; }
    }

    public sealed class EventsInfo
    {
        public Measured<int> WindowDays { get; init; }
        public IReadOnlyList<EventSummary> CriticalErrors { get; init; } = Array.Empty<EventSummary>();
        public IReadOnlyList<EventSummary> RecurringErrors { get; init; } = Array.Empty<EventSummary>();
        public IReadOnlyList<EventSummary> DiskErrors { get; init; } = Array.Empty<EventSummary>();
        public IReadOnlyList<BsodInfo> Bsods { get; init; } = Array.Empty<BsodInfo>();
        public Measured<int> UnexpectedShutdowns { get; init; }
    }

    public sealed class DeviceInfo
    {
        public string Name { get; init; } = string.Empty;
        public string? DeviceClass { get; init; }
        public string? Manufacturer { get; init; }
        public string? HardwareId { get; init; }

        /// <summary>Code d'erreur du Gestionnaire de périphériques (CM_PROB_*). 0 = aucun.</summary>
        public int ProblemCode { get; init; }

        /// <summary>Traduction du code en français, telle qu'affichée au technicien.</summary>
        public string? ProblemLabel { get; init; }
        public bool IsDisabled { get; init; }

        /// <summary>Périphérique sans pilote : le « point d'exclamation jaune ».</summary>
        public bool IsUnknownDevice { get; init; }
    }

    public sealed class DriverInfo
    {
        public string DeviceName { get; init; } = string.Empty;
        public string? DeviceClass { get; init; }
        public string? Manufacturer { get; init; }
        public string? Version { get; init; }
        public DateTimeOffset? Date { get; init; }
        public string? InfName { get; init; }
        public bool? IsSigned { get; init; }

        /// <summary>Pilote générique Microsoft là où un pilote constructeur est attendu.</summary>
        public bool IsMicrosoftGeneric { get; init; }
    }

    public enum StartupLocation
    {
        Unknown = 0,
        RegistryRunMachine = 1,
        RegistryRunUser = 2,
        RegistryRunOnce = 3,
        StartupFolderMachine = 4,
        StartupFolderUser = 5,
        ScheduledTask = 6,
    }

    public sealed class StartupItem
    {
        public string Name { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public StartupLocation Location { get; init; }
        public string? ImagePath { get; init; }
        public string? Publisher { get; init; }
        public bool Enabled { get; init; } = true;

        /// <summary>Vrai si l'exécutable référencé n'existe plus : entrée orpheline.</summary>
        public bool TargetMissing { get; init; }

        /// <summary>
        /// Renseigné pour une tâche planifiée : ouverture de session ou démarrage de Windows.
        /// </summary>
        /// <remarks>
        /// Les entrées du registre et des dossiers de démarrage n'ont pas de déclencheur à
        /// décrire : elles se lancent toutes à l'ouverture de session, et le seul fait d'y
        /// figurer le dit déjà.
        /// </remarks>
        public TaskTriggerKind Trigger { get; init; }

        /// <summary>
        /// Délai avant le lancement, pour une tâche qui en déclare un.
        /// </summary>
        /// <remarks>
        /// La nuance compte : une tâche lancée dix minutes après l'ouverture de session ne pèse
        /// pas sur le démarrage de la même façon qu'un programme lancé immédiatement.
        /// </remarks>
        public int? DelaySeconds { get; init; }
    }

    public sealed class WindowsSnapshot
    {
        /// <summary>Les comptes qui ont ouvert une session ici, et l'état de leurs profils.</summary>
        public ProfileInventory Profiles { get; init; } = new ProfileInventory();

        /// <summary>L'heure de la machine, et de quoi juger si elle est juste.</summary>
        public SystemTimeInfo Time { get; init; } = new SystemTimeInfo();

        /// <summary>Numéros de port série retenus par Windows, présents ou non.</summary>
        public SerialPortsInfo SerialPorts { get; init; } = new SerialPortsInfo();

        public WindowsInstallInfo Install { get; init; } = new WindowsInstallInfo();
        public SystemFilesInfo SystemFiles { get; init; } = new SystemFilesInfo();
        public UpdatesInfo Updates { get; init; } = new UpdatesInfo();
        public EventsInfo Events { get; init; } = new EventsInfo();
        public IReadOnlyList<ServiceInfo> Services { get; init; } = Array.Empty<ServiceInfo>();
        public IReadOnlyList<DeviceInfo> Devices { get; init; } = Array.Empty<DeviceInfo>();
        public IReadOnlyList<DriverInfo> Drivers { get; init; } = Array.Empty<DriverInfo>();
        public IReadOnlyList<StartupItem> Startup { get; init; } = Array.Empty<StartupItem>();

        /// <summary>Les tâches planifiées : ce qui se lance sans figurer au démarrage.</summary>
        public ScheduledTaskInventory Tasks { get; init; } = new ScheduledTaskInventory();

        /// <summary>Ce qui est installé sur la machine, lu dans le registre.</summary>
        public SoftwareInventory Software { get; init; } = new SoftwareInventory();

        /// <summary>L'histoire récente de la machine : ce qui plante, et depuis quand.</summary>
        public StabilityInfo Stability { get; init; } = new StabilityInfo();

        /// <summary>Ce qui permettrait de revenir en arrière, à lire avant d'intervenir.</summary>
        public SafetyNet SafetyNet { get; init; } = new SafetyNet();

        /// <summary>L'impression : le spouleur, les imprimantes, la file d'attente.</summary>
        public PrintingInfo Printing { get; init; } = new PrintingInfo();

        /// <summary>Le son : ce qui peut jouer, ce qui peut enregistrer.</summary>
        public AudioInfo Audio { get; init; } = new AudioInfo();
    }
}
