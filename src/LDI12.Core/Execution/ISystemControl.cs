using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LDI12.Core.Execution
{
    /// <summary>Le compte rendu d'un geste sur le système.</summary>
    public sealed class ControlResult
    {
        public bool Succeeded { get; init; }

        public string Message { get; init; } = string.Empty;

        /// <summary>Windows demande un redémarrage pour achever le geste.</summary>
        public bool RestartRequired { get; init; }

        /// <summary>Code d'erreur Windows, 0 en cas de réussite.</summary>
        public int ErrorCode { get; init; }

        public static ControlResult Ok(string message, bool restartRequired = false)
            => new ControlResult { Succeeded = true, Message = message, RestartRequired = restartRequired };

        public static ControlResult Fail(string message, int errorCode = 0)
            => new ControlResult { Succeeded = false, Message = message, ErrorCode = errorCode };
    }

    /// <summary>Un périphérique, tel que le Gestionnaire de périphériques le connaît à l'instant.</summary>
    public sealed class DeviceState
    {
        public string InstanceId { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        /// <summary>Classe d'installation : « Display », « Net », « MEDIA »… null si inconnue.</summary>
        public string? Class { get; init; }

        /// <summary>Code d'erreur (CM_PROB_*), 0 si le périphérique fonctionne.</summary>
        public int ProblemCode { get; init; }

        public bool IsDisabled => ProblemCode == 22;
    }

    /// <summary>Un pilote proposé par Windows Update pour ce PC.</summary>
    public sealed class DriverUpdateInfo
    {
        public string UpdateId { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string? DriverClass { get; init; }

        public string? Manufacturer { get; init; }

        public string? Model { get; init; }

        public DateTime? DriverDate { get; init; }

        public long DownloadBytes { get; init; }
    }

    /// <summary>Ce que Windows Update propose, ou pourquoi il n'a pas pu répondre.</summary>
    public sealed class DriverUpdateSearch
    {
        public bool Succeeded { get; init; }

        public string? Error { get; init; }

        public IReadOnlyList<DriverUpdateInfo> Updates { get; init; } = Array.Empty<DriverUpdateInfo>();
    }

    /// <summary>Une sortie ou une entrée audio, telle que le panneau Son de Windows la montre.</summary>
    public sealed class AudioOutputState
    {
        /// <summary>Identifiant Windows de la sortie : « {0.0.0.00000000}.{…} ».</summary>
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        /// <summary>Lecture (haut-parleurs, casque, HDMI) ou enregistrement (micro).</summary>
        public bool IsCapture { get; init; }

        /// <summary>« active », « désactivée », « débranchée », « absente ».</summary>
        public string State { get; init; } = string.Empty;

        /// <summary>Périphérique par défaut pour le son ordinaire.</summary>
        public bool IsDefault { get; init; }

        /// <summary>Périphérique par défaut pour les communications (Teams, Zoom…).</summary>
        public bool IsDefaultCommunications { get; init; }

        /// <summary>Volume principal en pourcentage ; null quand la sortie n'est pas active.</summary>
        public int? VolumePercent { get; init; }

        public bool? Muted { get; init; }
    }

    /// <summary>
    /// Les gestes sur le système que les opérations de réparation demandent, en appels natifs.
    /// </summary>
    /// <remarks>
    /// <b>Aucun script.</b> Ni PowerShell, ni VBScript, ni fichier de commandes : chacun de ces
    /// gestes passe par l'interface que Windows publie pour lui (SetupAPI et Configuration
    /// Manager pour les périphériques, le gestionnaire de services, le registre, la gestion de
    /// l'alimentation, l'agent Windows Update). Toutes existent depuis Windows 7 SP1 et ne
    /// dépendent d'aucun composant facultatif : elles répondent aussi sur un poste où PowerShell
    /// est bridé par une stratégie, désinstallé ou cassé.
    /// <para>
    /// Comme les autres passerelles d'écriture, celle-ci ne sert qu'aux actions, et chaque
    /// méthode fait un seul geste, nommé : pas d'appel générique qui en ferait un canal ouvert.
    /// </para>
    /// </remarks>
    public interface ISystemControl
    {
        /// <summary>« Rechercher les modifications sur le matériel » : Windows réexamine les périphériques et installe ce qui manque.</summary>
        ControlResult RescanDevices();

        /// <summary>Le périphérique par son identifiant d'instance, ou null s'il n'existe pas sur ce PC.</summary>
        DeviceState? FindDevice(string instanceId);

        /// <summary>Arrête et relance le pilote d'un périphérique, comme le fait un débranchement.</summary>
        ControlResult RestartDevice(string instanceId);

        /// <summary>Réactive un périphérique désactivé.</summary>
        ControlResult EnableDevice(string instanceId);

        /// <summary>Désinstalle le périphérique puis le fait redétecter : Windows lui réinstalle son pilote.</summary>
        ControlResult ReinstallDevice(string instanceId);

        /// <summary>État d'un service (« Running », « Stopped »…), ou null s'il n'existe pas.</summary>
        string? ServiceStatus(string name);

        /// <summary>Arrête un service, et ceux qui en dépendent ; rend les dépendants qui tournaient.</summary>
        ControlResult StopService(string name, TimeSpan timeout, out IReadOnlyList<string> stoppedDependents);

        ControlResult StartService(string name, TimeSpan timeout);

        /// <summary>Vide un dossier de travail du système, sans le supprimer ; les fichiers verrouillés restent.</summary>
        ControlResult ClearFolder(string path);

        ControlResult SetRegistryDword(RegistryHive hive, string subKey, string name, int value);

        ControlResult SetRegistryBinary(RegistryHive hive, string subKey, string name, byte[] value);

        /// <summary>Écrit une chaîne (extensible si elle contient une variable d'environnement).</summary>
        ControlResult SetRegistryString(RegistryHive hive, string subKey, string name, string value);

        /// <summary>Supprime une valeur ; réussit aussi si elle n'existait déjà plus.</summary>
        ControlResult DeleteRegistryValue(RegistryHive hive, string subKey, string name);

        /// <summary>Change le type de démarrage d'un service ou d'un pilote (0 à 4), par le gestionnaire de services.</summary>
        ControlResult SetServiceStart(string name, int start);

        /// <summary>Active ou désactive une tâche planifiée, désignée par son chemin complet.</summary>
        ControlResult SetTaskEnabled(string path, bool enabled);

        /// <summary>Met fin à un processus.</summary>
        ControlResult EndProcess(int processId);

        /// <summary>
        /// Retire un paquet du magasin de pilotes (« oem12.inf »). Jamais forcé : Windows refuse
        /// lui-même un paquet qu'un périphérique présent utilise.
        /// </summary>
        ControlResult DeleteDriverPackage(string publishedName);

        /// <summary>
        /// Capture le trafic IPv4 de l'adresse donnée pendant la durée demandée, et l'écrit au
        /// format pcap si un chemin est donné. Réservé aux administrateurs.
        /// </summary>
        Task<Network.TrafficSummary?> CaptureTrafficAsync(string localAddress, TimeSpan duration, string? pcapPath, CancellationToken cancellationToken);

        /// <summary>Lance l'autotest intégré d'un disque ATA (court ou long).</summary>
        ControlResult StartDiskSelfTest(int diskIndex, bool extended);

        /// <summary>L'état de l'autotest et son journal ; nul si le disque ne répond pas aux commandes SMART.</summary>
        Disks.SelfTestState? ReadDiskSelfTest(int diskIndex);

        /// <summary>Lit la surface d'un disque, en entier ou par échantillons, sans rien y écrire.</summary>
        Task<Disks.SurfaceScan?> ScanDiskSurfaceAsync(int diskIndex, bool full, IProgress<(double Fraction, Disks.SurfaceScan Partial)>? progress, CancellationToken cancellationToken);

        /// <summary>Le mode d'alimentation actif, ou null s'il ne se lit pas.</summary>
        Guid? ActivePowerScheme();

        bool PowerSchemeExists(Guid scheme);

        ControlResult ActivatePowerScheme(Guid scheme);

        /// <summary>Les sorties et entrées audio, avec la sortie par défaut, le volume et la coupure. Lecture seule.</summary>
        IReadOnlyList<AudioOutputState> AudioEndpoints();

        /// <summary>Fait d'une sortie audio la sortie par défaut, pour tous les usages.</summary>
        ControlResult SetDefaultAudioEndpoint(string endpointId);

        /// <summary>Rétablit le son d'une sortie (la sortie par défaut si null) : coupure levée, volume remonté s'il était presque nul.</summary>
        ControlResult RestoreAudioVolume(string? endpointId);

        /// <summary>Joue un son de test d'une seconde et demie sur la sortie par défaut.</summary>
        ControlResult PlayTestSound();

        /// <summary>Les pilotes que Windows Update propose pour ce PC. Lecture seule.</summary>
        Task<DriverUpdateSearch> SearchDriverUpdatesAsync(CancellationToken cancellationToken);

        /// <summary>Télécharge et installe les pilotes désignés, parmi ceux que Windows Update propose.</summary>
        Task<ControlResult> InstallDriverUpdatesAsync(
            IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken);
    }

    /// <summary>Pour les contextes sans plateforme (tests, outils) : tout geste est refusé, poliment.</summary>
    public sealed class NoSystemControl : ISystemControl
    {
        public static readonly NoSystemControl Instance = new NoSystemControl();

        private static ControlResult No() => ControlResult.Fail("Geste indisponible dans ce contexte.");

        public ControlResult RescanDevices() => No();
        public DeviceState? FindDevice(string instanceId) => null;
        public ControlResult RestartDevice(string instanceId) => No();
        public ControlResult EnableDevice(string instanceId) => No();
        public ControlResult ReinstallDevice(string instanceId) => No();
        public string? ServiceStatus(string name) => null;

        public ControlResult StopService(string name, TimeSpan timeout, out IReadOnlyList<string> stoppedDependents)
        {
            stoppedDependents = Array.Empty<string>();
            return No();
        }

        public ControlResult StartService(string name, TimeSpan timeout) => No();
        public ControlResult ClearFolder(string path) => No();
        public ControlResult SetRegistryDword(RegistryHive hive, string subKey, string name, int value) => No();
        public ControlResult SetRegistryBinary(RegistryHive hive, string subKey, string name, byte[] value) => No();
        public ControlResult SetRegistryString(RegistryHive hive, string subKey, string name, string value) => No();
        public ControlResult DeleteRegistryValue(RegistryHive hive, string subKey, string name) => No();
        public ControlResult SetServiceStart(string name, int start) => No();
        public ControlResult SetTaskEnabled(string path, bool enabled) => No();
        public ControlResult EndProcess(int processId) => No();
        public ControlResult DeleteDriverPackage(string publishedName) => No();
        public Task<Network.TrafficSummary?> CaptureTrafficAsync(string localAddress, TimeSpan duration, string? pcapPath, CancellationToken cancellationToken)
            => Task.FromResult<Network.TrafficSummary?>(null);
        public ControlResult StartDiskSelfTest(int diskIndex, bool extended) => No();
        public Disks.SelfTestState? ReadDiskSelfTest(int diskIndex) => null;
        public Task<Disks.SurfaceScan?> ScanDiskSurfaceAsync(int diskIndex, bool full, IProgress<(double Fraction, Disks.SurfaceScan Partial)>? progress, CancellationToken cancellationToken)
            => Task.FromResult<Disks.SurfaceScan?>(null);
        public Guid? ActivePowerScheme() => null;
        public bool PowerSchemeExists(Guid scheme) => false;
        public ControlResult ActivatePowerScheme(Guid scheme) => No();

        public IReadOnlyList<AudioOutputState> AudioEndpoints() => Array.Empty<AudioOutputState>();
        public ControlResult SetDefaultAudioEndpoint(string endpointId) => No();
        public ControlResult RestoreAudioVolume(string? endpointId) => No();
        public ControlResult PlayTestSound() => No();

        public Task<DriverUpdateSearch> SearchDriverUpdatesAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DriverUpdateSearch { Error = "Windows Update indisponible dans ce contexte." });

        public Task<ControlResult> InstallDriverUpdatesAsync(
            IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken)
            => Task.FromResult(No());
    }
}
