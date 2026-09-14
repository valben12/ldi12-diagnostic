using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Benchmarks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Monitoring;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using LDI12.Platform.Detection;
using LDI12.Platform.Features;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;

namespace LDI12.Platform
{
    internal sealed class PlatformInfo : IPlatformInfo
    {
        public PlatformInfo(WindowsProfile profile, IFeatureRegistry features, ElevationState elevation)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Features = features ?? throw new ArgumentNullException(nameof(features));
            Elevation = elevation;
        }

        public WindowsProfile Profile { get; }

        public IFeatureRegistry Features { get; }

        public ElevationState Elevation { get; }

        public bool IsElevated =>
            Elevation == ElevationState.Elevated || Elevation == ElevationState.ElevatedByDefault;
    }

    /// <summary>
    /// Racine de composition de la couche plateforme : détecte Windows, sonde les fonctionnalités
    /// et assemble les passerelles. C'est le seul endroit où ces objets sont construits.
    /// </summary>
    public sealed class PlatformServices : IDisposable
    {
        private const string Category = "Platform";

        private PlatformServices(
            IPlatformInfo platform, IProcessRunner processes, IWmiGateway wmi,
            IRegistryGateway registry, INativeSystemApi native, IStorageApi storage, INetworkApi network,
            IGpuSensorApi gpuSensors, IAdvancedSensorApi sensors, IDisplayApi displays,
            ITcpTableApi tcp, IFileSystemGateway files,
            IProcessLauncher launcher, ISystemRestoreGateway restore,
            IStorageBenchmarkGateway storageBenchmarks, ILdiLogger logger)
        {
            Platform = platform;
            Processes = processes;
            Wmi = wmi;
            Registry = registry;
            Native = native;
            Storage = storage;
            Network = network;
            GpuSensors = gpuSensors;
            Displays = displays;
            Tcp = tcp;
            Sensors = sensors;
            Files = files;
            Launcher = launcher;
            Restore = restore;
            StorageBenchmarks = storageBenchmarks;
            Logger = logger;
        }

        public IPlatformInfo Platform { get; }
        public IProcessRunner Processes { get; }
        public IWmiGateway Wmi { get; }
        public IRegistryGateway Registry { get; }
        public INativeSystemApi Native { get; }
        public IStorageApi Storage { get; }
        public INetworkApi Network { get; }

        public IGpuSensorApi GpuSensors { get; }

        /// <summary>Sorties graphiques actives et leur mode courant.</summary>
        public IDisplayApi Displays { get; }

        /// <summary>Points d'écoute TCP, avec le processus qui les tient.</summary>
        public ITcpTableApi Tcp { get; }

        /// <summary>
        /// Capteurs matériels par pilote noyau. Désactivés tant que le technicien ne les a pas
        /// autorisés, voir <c>LibreHardwareSensorApi</c>.
        /// </summary>
        public IAdvancedSensorApi Sensors { get; }

        /// <summary>Passerelles ajoutées en phase 5 : elles ne servent qu'aux actions, jamais aux sondes.</summary>
        public IFileSystemGateway Files { get; }

        public IProcessLauncher Launcher { get; }

        public ISystemRestoreGateway Restore { get; }

        /// <summary>
        /// Mesure du débit réel d'un volume, sans passer par le cache de Windows.
        /// </summary>
        /// <remarks>
        /// Isolée dans sa propre passerelle plutôt qu'ajoutée au système de fichiers : c'est la
        /// seule qui écrive sur le disque du client sans rien réparer, et cette différence mérite
        /// d'être visible dans le typage.
        /// </remarks>
        public IStorageBenchmarkGateway StorageBenchmarks { get; }

        public ILdiLogger Logger { get; }

        /// <summary>
        /// Source d'échantillons pour une session de surveillance en direct.
        /// </summary>
        /// <remarks>
        /// Une instance neuve à chaque session, et non une passerelle partagée : ce qu'elle rend
        /// sont des différences entre deux instants, et un état conservé d'une session à l'autre
        /// ferait apparaître, au premier relevé, un débit réseau accumulé pendant que personne ne
        /// regardait.
        /// </remarks>
        public ILiveMetricSource CreateLiveMetrics() => new LiveMetricSource(Native, Sensors, Logger);

        /// <summary>Contexte fourni aux sondes. Elles n'ont accès à rien d'autre.</summary>
        public ProbeContext CreateProbeContext(SnapshotDraft draft, RunMode mode)
            => new ProbeContext(
                Platform, Processes, Wmi, Registry, Logger, Native, Storage, Network, GpuSensors,
                Sensors, Displays, Tcp, draft, mode);

        public static async Task<PlatformServices> CreateAsync(ILdiLogger logger, CancellationToken cancellationToken)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            var log = logger.For(Category);
            var stopwatch = Stopwatch.StartNew();

            var registry = new RegistryGateway(logger);
            var processes = new ProcessRunner(logger);
            var wmi = new WmiGateway(logger);

            var profile = new WindowsProfileProvider(registry, logger).Detect();
            var elevation = ElevationDetector.Detect();
            log.Info("Élévation : " + Describe(elevation) + ".");

            var features = await FeatureRegistry
                .BuildAsync(profile, registry, wmi,
                    elevation == ElevationState.Elevated || elevation == ElevationState.ElevatedByDefault,
                    logger, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            log.Info("Couche plateforme prête en " + stopwatch.ElapsedMilliseconds + " ms.");

            // Dit une fois ce que le processus accepte : c'est la première chose à lire dans le
            // journal d'un poste où une copie ou un nettoyage n'aboutit pas.
            log.Info(Gateways.FileSystemGateway.ExtendedPathsAccepted
                ? "Chemins longs : acceptés par ce processus."
                : "Chemins longs : refusés par ce processus. Les fichiers au chemin de plus de 260 " +
                  "caractères seront signalés un par un ; tous les autres sont traités normalement.");

            var info = new PlatformInfo(profile, features, elevation);

            return new PlatformServices(
                info, processes, wmi, registry,
                new NativeSystemApi(logger), new StorageApi(logger), new NetworkApi(logger),
                new NvidiaGpuSensorApi(logger), new LibreHardwareSensorApi(info, logger),
                new DisplayNative(logger), new TcpTableNative(logger),
                new FileSystemGateway(logger), new ProcessLauncher(logger),
                new SystemRestoreGateway(info, logger), new StorageBenchmarkGateway(logger), logger);
        }

        private static string Describe(ElevationState state) => state switch
        {
            ElevationState.NotElevated => "utilisateur standard (mode nominal)",
            ElevationState.Elevated => "privilèges administrateur accordés",
            ElevationState.ElevatedByDefault =>
                "privilèges administrateur sans élévation : UAC désactivé ou compte Administrateur intégré",
            _ => "indéterminée",
        };

        public void Dispose() => (Logger as IDisposable)?.Dispose();
    }
}
