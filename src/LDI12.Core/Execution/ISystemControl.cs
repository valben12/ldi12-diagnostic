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

        /// <summary>Le mode d'alimentation actif, ou null s'il ne se lit pas.</summary>
        Guid? ActivePowerScheme();

        bool PowerSchemeExists(Guid scheme);

        ControlResult ActivatePowerScheme(Guid scheme);

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
        public Guid? ActivePowerScheme() => null;
        public bool PowerSchemeExists(Guid scheme) => false;
        public ControlResult ActivatePowerScheme(Guid scheme) => No();

        public Task<DriverUpdateSearch> SearchDriverUpdatesAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DriverUpdateSearch { Error = "Windows Update indisponible dans ce contexte." });

        public Task<ControlResult> InstallDriverUpdatesAsync(
            IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken)
            => Task.FromResult(No());
    }
}
