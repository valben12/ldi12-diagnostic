using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;

namespace LDI12.Core.Probes
{
    public enum ProbeStatus
    {
        /// <summary>La sonde n'a pas été exécutée.</summary>
        NotRun = 0,

        /// <summary>✅ Toutes les données attendues ont été collectées.</summary>
        Ok = 1,

        /// <summary>⚠️ Une partie des données a été collectée.</summary>
        Partial = 2,

        /// <summary>❌ La sonde a échoué. Le diagnostic continue.</summary>
        Failed = 3,

        /// <summary>La sonde a été écartée du plan (mode rapide, décochée par le technicien).</summary>
        Skipped = 4,

        /// <summary>❌ Prérequis non satisfaits sur cette version de Windows.</summary>
        Unavailable = 5,

        /// <summary>La sonde a dépassé son délai maximal et a été interrompue.</summary>
        TimedOut = 6,

        /// <summary>Le technicien a annulé le diagnostic.</summary>
        Cancelled = 7,

        /// <summary>🔒 Privilèges administrateur requis et non accordés.</summary>
        ElevationRequired = 8,
    }

    /// <summary>Une sonde bloquante tourne dans un processus séparé, que l'on peut tuer.</summary>
    public enum IsolationMode
    {
        InProcess = 0,
        SeparateProcess = 1,
    }

    public sealed class ProbeRequirements
    {
        public static readonly ProbeRequirements None = new ProbeRequirements();

        /// <summary>Build minimal de Windows. Null si la sonde fonctionne dès Windows 7 SP1.</summary>
        public int? MinimumBuild { get; init; }

        public IReadOnlyList<FeatureId> RequiredFeatures { get; init; } = Array.Empty<FeatureId>();

        /// <summary>Espaces de noms WMI dont l'absence rend la sonde inutilisable.</summary>
        public IReadOnlyList<string> RequiredWmiNamespaces { get; init; } = Array.Empty<string>();

        /// <summary>La sonde ne peut rien collecter sans privilèges administrateur.</summary>
        public bool RequiresElevation { get; init; }
    }

    public sealed class ProbeDescriptor
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public DiagnosticCategory Category { get; init; }

        public ProbeRequirements Requirements { get; init; } = ProbeRequirements.None;

        /// <summary>Sert à pondérer la barre de progression, pas à borner l'exécution.</summary>
        public TimeSpan EstimatedDuration { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>Délai au-delà duquel la sonde est abandonnée ou son processus tué.</summary>
        public TimeSpan HardTimeout { get; init; } = TimeSpan.FromSeconds(20);

        public IsolationMode Isolation { get; init; } = IsolationMode.InProcess;

        /// <summary>Sondes devant avoir été exécutées avant celle-ci.</summary>
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        /// <summary>Exclue du mode rapide (sonde lente ou lançant un outil externe).</summary>
        public bool FullScanOnly { get; init; }

        public override string ToString() => Id + " (" + DisplayName + ")";
    }

    public sealed class ProbeOutcome
    {
        public static ProbeOutcome Ok(string? message = null)
            => new ProbeOutcome { Status = ProbeStatus.Ok, Message = message };

        public static ProbeOutcome Partial(string message)
            => new ProbeOutcome { Status = ProbeStatus.Partial, Message = message };

        public static ProbeOutcome Failed(string message, Exception? ex = null)
            => new ProbeOutcome { Status = ProbeStatus.Failed, Message = message, Exception = ex };

        public static ProbeOutcome Unavailable(string reason)
            => new ProbeOutcome { Status = ProbeStatus.Unavailable, Message = reason };

        public static ProbeOutcome ElevationRequired(string what)
            => new ProbeOutcome { Status = ProbeStatus.ElevationRequired, Message = what };

        public static ProbeOutcome Skipped(string reason)
            => new ProbeOutcome { Status = ProbeStatus.Skipped, Message = reason };

        public ProbeStatus Status { get; init; }

        public string? Message { get; init; }

        public Exception? Exception { get; init; }
    }

    /// <summary>Tout ce dont une sonde a le droit de se servir. Aucun accès direct à l'OS.</summary>
    public sealed class ProbeContext
    {
        public ProbeContext(
            IPlatformInfo platform,
            IProcessRunner processes,
            IWmiGateway wmi,
            IRegistryGateway registry,
            ILdiLogger logger,
            INativeSystemApi? native = null,
            IStorageApi? storage = null,
            INetworkApi? network = null,
            IGpuSensorApi? gpuSensors = null,
            IAdvancedSensorApi? sensors = null,
            IDisplayApi? displays = null,
            ITcpTableApi? tcp = null,
            Model.SnapshotDraft? draft = null,
            Model.RunMode mode = Model.RunMode.Full)
        {
            Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            Processes = processes ?? throw new ArgumentNullException(nameof(processes));
            Wmi = wmi ?? throw new ArgumentNullException(nameof(wmi));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Native = native!;
            Storage = storage!;
            Network = network!;
            GpuSensors = gpuSensors!;
            Displays = displays!;
            Tcp = tcp!;
            Sensors = sensors!;
            Draft = draft ?? new Model.SnapshotDraft();
            Mode = mode;
        }

        public IPlatformInfo Platform { get; }
        public IProcessRunner Processes { get; }
        public IWmiGateway Wmi { get; }
        public IRegistryGateway Registry { get; }
        public ILdiLogger Logger { get; }

        /// <summary>Mesures par API native, préférées à WMI partout où elles existent.</summary>
        public INativeSystemApi Native { get; }

        /// <summary>Accès bas niveau aux disques : énumération et SMART.</summary>
        public IStorageApi Storage { get; }

        /// <summary>Lectures réseau natives, dont l'état des liaisons sans fil.</summary>
        public INetworkApi Network { get; }

        /// <summary>
        /// Capteurs de la carte graphique, par la bibliothèque de son constructeur.
        /// </summary>
        /// <remarks>
        /// Seule température, avec celle des disques, que le logiciel sache lire sans installer
        /// quoi que ce soit : le pilote graphique fournit lui-même la bibliothèque.
        /// </remarks>
        public IGpuSensorApi GpuSensors { get; }

        /// <summary>Sorties graphiques actives : une carte n'est pas un écran, et WMI confond les deux.</summary>
        public IDisplayApi Displays { get; }

        /// <summary>Points d'écoute TCP, avec le processus qui les tient.</summary>
        public ITcpTableApi Tcp { get; }

        /// <summary>
        /// Capteurs matériels par pilote noyau : température du processeur, sondes de la carte
        /// mère, ventilateurs.
        /// </summary>
        /// <remarks>
        /// Désactivés tant que le technicien ne les a pas autorisés dans les réglages : les
        /// activer charge un pilote sur la machine du client. Une sonde ne doit donc jamais
        /// supposer qu'ils répondront.
        /// </remarks>
        public IAdvancedSensorApi Sensors { get; }

        /// <summary>Réceptacle où la sonde dépose ce qu'elle a collecté.</summary>
        public Model.SnapshotDraft Draft { get; }

        public Model.RunMode Mode { get; }

        private readonly ConcurrentDictionary<string, object> _shared =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// Mémoïse une lecture coûteuse partagée par plusieurs sondes d'une même vague : la
        /// correspondance volume/disque, par exemple, coûte deux requêtes WMI lentes que deux
        /// modules réclament simultanément. La clé doit toujours être associée au même type.
        /// </summary>
        public Task<T> SharedAsync<T>(string key, Func<Task<T>> factory)
        {
            var lazy = (Lazy<Task<T>>)_shared.GetOrAdd(
                key, _ => new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }
    }

    /// <summary>
    /// Unité de collecte, en lecture seule.
    /// </summary>
    /// <remarks>
    /// Le pendant en écriture est <c>IRepairAction</c> (LDI12.Actions). La séparation est portée
    /// par le typage : une sonde ne peut pas modifier la machine, ce n'est pas une convention
    /// d'équipe mais une garantie du compilateur.
    /// </remarks>
    public interface IDiagnosticProbe
    {
        ProbeDescriptor Descriptor { get; }

        Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken);
    }
}
