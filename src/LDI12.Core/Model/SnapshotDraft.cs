using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Réceptacle mutable dans lequel les sondes déposent leurs résultats pendant la collecte.
    /// </summary>
    /// <remarks>
    /// Le <see cref="SystemSnapshot"/> est immuable ; il faut donc un intermédiaire le temps de la
    /// collecte. Les sondes s'exécutant en parallèle, chaque écriture est protégée, mais chaque
    /// section n'a de toute façon qu'un seul propriétaire : une sonde n'écrit jamais dans la
    /// section d'une autre, et aucune ne lit ce que les autres ont déposé. Les corrélations entre
    /// domaines sont le travail du moteur de règles, sur le snapshot figé.
    /// </remarks>
    public sealed class SnapshotDraft
    {
        private readonly object _gate = new object();

        /// <summary>
        /// Ce que les sondes ont déposé, dans l'ordre.
        /// </summary>
        /// <remarks>
        /// Le relevé n'est pas transportable (mutable, verrouillé, plein de types génériques)
        /// mais la liste de ce qu'on y a écrit l'est. C'est ce qui permet à une sonde de
        /// s'exécuter dans un autre processus et de rendre son résultat sans que le relevé ait
        /// à traverser quoi que ce soit.
        /// </remarks>
        private readonly List<DraftSection> _writes = new List<DraftSection>();

        private CpuInfo _cpu = new CpuInfo();
        private MemoryInfo _memory = new MemoryInfo();
        private IReadOnlyList<GpuInfo> _gpus = Array.Empty<GpuInfo>();
        private MotherboardInfo _motherboard = new MotherboardInfo();
        private IReadOnlyList<BatteryInfo> _batteries = Array.Empty<BatteryInfo>();
        private ThermalSnapshot _thermal = new ThermalSnapshot();
        private DisplaySnapshot _displays = new DisplaySnapshot();
        private HardwareErrorsInfo _hardwareErrors = new HardwareErrorsInfo();

        private IReadOnlyList<PhysicalDiskInfo> _disks = Array.Empty<PhysicalDiskInfo>();
        private IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();
        private SystemSpaceSnapshot _systemSpace = new SystemSpaceSnapshot();

        private WindowsInstallInfo _install = new WindowsInstallInfo();
        private SystemFilesInfo _systemFiles = new SystemFilesInfo();
        private UpdatesInfo _updates = new UpdatesInfo();
        private EventsInfo _events = new EventsInfo();
        private IReadOnlyList<ServiceInfo> _services = Array.Empty<ServiceInfo>();
        private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
        private IReadOnlyList<DriverInfo> _drivers = Array.Empty<DriverInfo>();
        private IReadOnlyList<StartupItem> _startup = Array.Empty<StartupItem>();
        private SoftwareInventory _software = new SoftwareInventory();
        private StabilityInfo _stability = new StabilityInfo();
        private SafetyNet _safetyNet = new SafetyNet();
        private ProfileInventory _profiles = new ProfileInventory();
        private SystemTimeInfo _systemTime = new SystemTimeInfo();
        private SerialPortsInfo _serialPorts = new SerialPortsInfo();
        private PrintingInfo _printing = new PrintingInfo();
        private AudioInfo _audio = new AudioInfo();
        private ScheduledTaskInventory _tasks = new ScheduledTaskInventory();

        private IReadOnlyList<NetworkAdapterInfo> _adapters = Array.Empty<NetworkAdapterInfo>();
        private NetworkTests _networkTests = new NetworkTests();
        private NetworkEnvironment _networkEnvironment = new NetworkEnvironment();
        private ListeningPortsInfo _listeningPorts = new ListeningPortsInfo();
        private NetworkPathsInfo _networkPaths = new NetworkPathsInfo();

        private IReadOnlyList<SecurityProductInfo> _securityProducts = Array.Empty<SecurityProductInfo>();
        private DefenderStatus _defender = new DefenderStatus();
        private IReadOnlyList<FirewallProfileState> _firewall = Array.Empty<FirewallProfileState>();
        private UacState _uac = new UacState();
        private IReadOnlyList<LocalAccountInfo> _accounts = Array.Empty<LocalAccountInfo>();
        private RemoteAccessState _remoteAccess = new RemoteAccessState();
        private Measured<bool> _smartScreen;

        private ResponsivenessInfo _responsiveness = new ResponsivenessInfo();
        private BootPerformance _boot = new BootPerformance();
        private PowerConfiguration _power = new PowerConfiguration();
        private StorageMaintenance _maintenance = new StorageMaintenance();
        private IReadOnlyList<ProcessUsage> _topByMemory = Array.Empty<ProcessUsage>();
        private IReadOnlyList<ProcessUsage> _topByCpu = Array.Empty<ProcessUsage>();
        private Measured<int> _processCount;
        private Measured<TimeSpan> _uptime;

        public void SetCpu(CpuInfo value) => Write(DraftSections.Cpu, value, v => _cpu = v);
        public void SetMemory(MemoryInfo value) => Write(DraftSections.Memory, value, v => _memory = v);
        public void SetGpus(IReadOnlyList<GpuInfo> value) => Write(DraftSections.Gpus, value, v => _gpus = v);
        public void SetMotherboard(MotherboardInfo value) => Write(DraftSections.Motherboard, value, v => _motherboard = v);
        public void SetBatteries(IReadOnlyList<BatteryInfo> value) => Write(DraftSections.Batteries, value, v => _batteries = v);
        public void SetThermal(ThermalSnapshot value) => Write(DraftSections.Thermal, value, v => _thermal = v);
        public void SetDisplays(DisplaySnapshot value) => Write(DraftSections.Displays, value, v => _displays = v);
        public void SetHardwareErrors(HardwareErrorsInfo value) => Write(DraftSections.HardwareErrors, value, v => _hardwareErrors = v);

        public void SetPhysicalDisks(IReadOnlyList<PhysicalDiskInfo> value) => Write(DraftSections.PhysicalDisks, value, v => _disks = v);
        public void SetSystemSpace(SystemSpaceSnapshot value) => Write(DraftSections.SystemSpace, value, v => _systemSpace = v);
        public void SetVolumes(IReadOnlyList<VolumeInfo> value, StorageMaintenance maintenance)
        {
            Write(DraftSections.Volumes, value, v => _volumes = v);
            Write(DraftSections.StorageMaintenance, maintenance, v => _maintenance = v);
        }

        public void SetInstall(WindowsInstallInfo value) => Write(DraftSections.Install, value, v => _install = v);
        public void SetSystemFiles(SystemFilesInfo value) => Write(DraftSections.SystemFiles, value, v => _systemFiles = v);
        public void SetUpdates(UpdatesInfo value) => Write(DraftSections.Updates, value, v => _updates = v);
        public void SetEvents(EventsInfo value) => Write(DraftSections.Events, value, v => _events = v);
        public void SetServices(IReadOnlyList<ServiceInfo> value) => Write(DraftSections.Services, value, v => _services = v);
        public void SetDevices(IReadOnlyList<DeviceInfo> value) => Write(DraftSections.Devices, value, v => _devices = v);
        public void SetDrivers(IReadOnlyList<DriverInfo> value) => Write(DraftSections.Drivers, value, v => _drivers = v);
        public void SetStartup(IReadOnlyList<StartupItem> value, ScheduledTaskInventory tasks)
        {
            Write(DraftSections.Startup, value, v => _startup = v);
            Write(DraftSections.Tasks, tasks, v => _tasks = v);
        }
        public void SetSoftware(SoftwareInventory value) => Write(DraftSections.Software, value, v => _software = v);
        public void SetStability(StabilityInfo value) => Write(DraftSections.Stability, value, v => _stability = v);
        public void SetSafetyNet(SafetyNet value) => Write(DraftSections.SafetyNet, value, v => _safetyNet = v);
        public void SetProfiles(ProfileInventory value) => Write(DraftSections.Profiles, value, v => _profiles = v);
        public void SetSystemTime(SystemTimeInfo value) => Write(DraftSections.SystemTime, value, v => _systemTime = v);
        public void SetSerialPorts(SerialPortsInfo value) => Write(DraftSections.SerialPorts, value, v => _serialPorts = v);
        public void SetPrinting(PrintingInfo value) => Write(DraftSections.Printing, value, v => _printing = v);
        public void SetAudio(AudioInfo value) => Write(DraftSections.Audio, value, v => _audio = v);

        public void SetNetworkAdapters(IReadOnlyList<NetworkAdapterInfo> value) => Write(DraftSections.NetworkAdapters, value, v => _adapters = v);
        public void SetNetworkTests(NetworkTests value) => Write(DraftSections.NetworkTests, value, v => _networkTests = v);
        public void SetNetworkEnvironment(NetworkEnvironment value) => Write(DraftSections.NetworkEnvironment, value, v => _networkEnvironment = v);
        public void SetListeningPorts(ListeningPortsInfo value) => Write(DraftSections.ListeningPorts, value, v => _listeningPorts = v);
        public void SetNetworkPaths(NetworkPathsInfo value) => Write(DraftSections.NetworkPaths, value, v => _networkPaths = v);

        public void SetSecurityProducts(IReadOnlyList<SecurityProductInfo> value, DefenderStatus defender)
        {
            Write(DraftSections.SecurityProducts, value, v => _securityProducts = v);
            Write(DraftSections.Defender, defender, v => _defender = v);
        }

        public void SetSecurityPolicy(
            IReadOnlyList<FirewallProfileState> firewall, UacState uac, RemoteAccessState remoteAccess,
            Measured<bool> smartScreen)
        {
            Write(DraftSections.Firewall, firewall, v => _firewall = v);
            Write(DraftSections.Uac, uac, v => _uac = v);
            Write(DraftSections.RemoteAccess, remoteAccess, v => _remoteAccess = v);
            Write(DraftSections.SmartScreen, smartScreen, v => _smartScreen = v);
        }

        public void SetLocalAccounts(IReadOnlyList<LocalAccountInfo> value) => Write(DraftSections.LocalAccounts, value, v => _accounts = v);

        public void SetResponsiveness(
            ResponsivenessInfo responsiveness, IReadOnlyList<ProcessUsage> topByMemory,
            IReadOnlyList<ProcessUsage> topByCpu, Measured<int> processCount, Measured<TimeSpan> uptime)
        {
            Write(DraftSections.Responsiveness, responsiveness, v => _responsiveness = v);
            Write(DraftSections.TopByMemory, topByMemory, v => _topByMemory = v);
            Write(DraftSections.TopByCpu, topByCpu, v => _topByCpu = v);
            Write(DraftSections.ProcessCount, processCount, v => _processCount = v);
            Write(DraftSections.Uptime, uptime, v => _uptime = v);
        }

        public void SetBootPerformance(BootPerformance value) => Write(DraftSections.BootPerformance, value, v => _boot = v);

        public void SetPower(PowerConfiguration value) => Write(DraftSections.Power, value, v => _power = v);

        /// <summary>
        /// Dépose une valeur dans une section, et retient qu'on l'y a déposée.
        /// </summary>
        /// <remarks>
        /// Chaque écriture passe par ici, sans exception : c'est ce qui garantit qu'une sonde
        /// exécutée dans un processus isolé peut rendre exactement ce qu'elle a produit, ni plus
        /// ni moins.
        /// </remarks>
        private void Write<T>(string section, T value, Action<T> assign)
        {
            lock (_gate)
            {
                assign(value);
                if (value != null) _writes.Add(new DraftSection(section, value));
            }
        }

        /// <summary>Ce qui a été déposé depuis le dernier <see cref="ClearWrites"/>.</summary>
        public IReadOnlyList<DraftSection> Writes
        {
            get { lock (_gate) return _writes.ToArray(); }
        }

        /// <summary>Repart d'un journal vide, avant d'exécuter une sonde dont on veut le résultat seul.</summary>
        public void ClearWrites()
        {
            lock (_gate) _writes.Clear();
        }

        /// <summary>
        /// Rejoue une écriture venue d'ailleurs, d'un processus isolé, en pratique.
        /// </summary>
        /// <returns>Faux si la section est inconnue ou si la valeur n'a pas le type attendu.</returns>
        /// <remarks>
        /// Le rejeu alimente le journal comme une écriture ordinaire, et il le faut : la sonde
        /// SMART s'exécute elle aussi dans le processus isolé et a besoin d'y retrouver la liste
        /// des disques que la sonde précédente y a produite. Un rejeu muet la lui cacherait.
        /// </remarks>
        public bool Apply(string section, object value)
        {
            if (section == null || value == null) return false;

            lock (_gate)
            {
                if (Store(section, value))
                {
                    _writes.Add(new DraftSection(section, value));
                    return true;
                }

                return false;
            }
        }

        private bool Store(string section, object value)
        {
            {
                switch (section)
                {
                    case DraftSections.Cpu: return Assign<CpuInfo>(value, v => _cpu = v);
                    case DraftSections.Memory: return Assign<MemoryInfo>(value, v => _memory = v);
                    case DraftSections.Gpus: return Assign<IReadOnlyList<GpuInfo>>(value, v => _gpus = v);
                    case DraftSections.Motherboard: return Assign<MotherboardInfo>(value, v => _motherboard = v);
                    case DraftSections.Batteries: return Assign<IReadOnlyList<BatteryInfo>>(value, v => _batteries = v);
                    case DraftSections.Thermal: return Assign<ThermalSnapshot>(value, v => _thermal = v);
                    case DraftSections.Displays: return Assign<DisplaySnapshot>(value, v => _displays = v);
                    case DraftSections.HardwareErrors: return Assign<HardwareErrorsInfo>(value, v => _hardwareErrors = v);

                    case DraftSections.PhysicalDisks: return Assign<IReadOnlyList<PhysicalDiskInfo>>(value, v => _disks = v);
                    case DraftSections.SystemSpace: return Assign<SystemSpaceSnapshot>(value, v => _systemSpace = v);
                    case DraftSections.Volumes: return Assign<IReadOnlyList<VolumeInfo>>(value, v => _volumes = v);

                    case DraftSections.Install: return Assign<WindowsInstallInfo>(value, v => _install = v);
                    case DraftSections.SystemFiles: return Assign<SystemFilesInfo>(value, v => _systemFiles = v);
                    case DraftSections.Updates: return Assign<UpdatesInfo>(value, v => _updates = v);
                    case DraftSections.Events: return Assign<EventsInfo>(value, v => _events = v);
                    case DraftSections.Services: return Assign<IReadOnlyList<ServiceInfo>>(value, v => _services = v);
                    case DraftSections.Devices: return Assign<IReadOnlyList<DeviceInfo>>(value, v => _devices = v);
                    case DraftSections.Drivers: return Assign<IReadOnlyList<DriverInfo>>(value, v => _drivers = v);
                    case DraftSections.Startup: return Assign<IReadOnlyList<StartupItem>>(value, v => _startup = v);
                    case DraftSections.Tasks: return Assign<ScheduledTaskInventory>(value, v => _tasks = v);
                    case DraftSections.Software: return Assign<SoftwareInventory>(value, v => _software = v);
                    case DraftSections.Stability: return Assign<StabilityInfo>(value, v => _stability = v);
                    case DraftSections.SafetyNet: return Assign<SafetyNet>(value, v => _safetyNet = v);
                    case DraftSections.Profiles: return Assign<ProfileInventory>(value, v => _profiles = v);
                    case DraftSections.SystemTime: return Assign<SystemTimeInfo>(value, v => _systemTime = v);
                    case DraftSections.SerialPorts: return Assign<SerialPortsInfo>(value, v => _serialPorts = v);
                    case DraftSections.Printing: return Assign<PrintingInfo>(value, v => _printing = v);
                    case DraftSections.Audio: return Assign<AudioInfo>(value, v => _audio = v);

                    case DraftSections.NetworkAdapters: return Assign<IReadOnlyList<NetworkAdapterInfo>>(value, v => _adapters = v);
                    case DraftSections.NetworkTests: return Assign<NetworkTests>(value, v => _networkTests = v);
                    case DraftSections.NetworkEnvironment: return Assign<NetworkEnvironment>(value, v => _networkEnvironment = v);
                    case DraftSections.ListeningPorts: return Assign<ListeningPortsInfo>(value, v => _listeningPorts = v);
                    case DraftSections.NetworkPaths: return Assign<NetworkPathsInfo>(value, v => _networkPaths = v);

                    case DraftSections.SecurityProducts: return Assign<IReadOnlyList<SecurityProductInfo>>(value, v => _securityProducts = v);
                    case DraftSections.Defender: return Assign<DefenderStatus>(value, v => _defender = v);
                    case DraftSections.Firewall: return Assign<IReadOnlyList<FirewallProfileState>>(value, v => _firewall = v);
                    case DraftSections.Uac: return Assign<UacState>(value, v => _uac = v);
                    case DraftSections.RemoteAccess: return Assign<RemoteAccessState>(value, v => _remoteAccess = v);
                    case DraftSections.SmartScreen: return Assign<Measured<bool>>(value, v => _smartScreen = v);
                    case DraftSections.LocalAccounts: return Assign<IReadOnlyList<LocalAccountInfo>>(value, v => _accounts = v);

                    case DraftSections.Responsiveness: return Assign<ResponsivenessInfo>(value, v => _responsiveness = v);
                    case DraftSections.TopByMemory: return Assign<IReadOnlyList<ProcessUsage>>(value, v => _topByMemory = v);
                    case DraftSections.TopByCpu: return Assign<IReadOnlyList<ProcessUsage>>(value, v => _topByCpu = v);
                    case DraftSections.ProcessCount: return Assign<Measured<int>>(value, v => _processCount = v);
                    case DraftSections.Uptime: return Assign<Measured<TimeSpan>>(value, v => _uptime = v);
                    case DraftSections.BootPerformance: return Assign<BootPerformance>(value, v => _boot = v);
                    case DraftSections.Power: return Assign<PowerConfiguration>(value, v => _power = v);
                    case DraftSections.StorageMaintenance: return Assign<StorageMaintenance>(value, v => _maintenance = v);

                    default: return false;
                }
            }
        }

        private static bool Assign<T>(object value, Action<T> assign)
        {
            if (value is not T typed) return false;
            assign(typed);
            return true;
        }

        /// <summary>
        /// Lecture réservée aux sondes qui dépendent explicitement d'une autre par
        /// <c>ProbeDescriptor.DependsOn</c>, la sonde SMART a besoin de la liste des disques.
        /// </summary>
        public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks { get { lock (_gate) return _disks; } }

        public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get { lock (_gate) return _adapters; } }

        public HardwareSnapshot BuildHardware()
        {
            lock (_gate)
            {
                return new HardwareSnapshot
                {
                    Cpu = _cpu,
                    Memory = _memory,
                    Gpus = _gpus,
                    Motherboard = _motherboard,
                    Batteries = _batteries,
                    Thermal = _thermal,
                    Displays = _displays,
                    Errors = _hardwareErrors,
                };
            }
        }

        public StorageSnapshot BuildStorage()
        {
            lock (_gate)
                return new StorageSnapshot
                {
                    PhysicalDisks = _disks,
                    Volumes = _volumes,
                    Maintenance = _maintenance,
                    SystemSpace = _systemSpace,
                };
        }

        public WindowsSnapshot BuildWindows()
        {
            lock (_gate)
            {
                return new WindowsSnapshot
                {
                    Install = _install,
                    SystemFiles = _systemFiles,
                    Updates = _updates,
                    Events = _events,
                    Services = _services,
                    Devices = _devices,
                    Drivers = _drivers,
                    Startup = _startup,
                    Tasks = _tasks,
                    Software = _software,
                    Stability = _stability,
                    Profiles = _profiles,
                    Time = _systemTime,
                    SerialPorts = _serialPorts,
                    SafetyNet = _safetyNet,
                    Printing = _printing,
                    Audio = _audio,
                };
            }
        }

        public NetworkSnapshot BuildNetwork()
        {
            lock (_gate)
                return new NetworkSnapshot
                {
                    Adapters = _adapters,
                    Tests = _networkTests,
                    Environment = _networkEnvironment,
                    Listening = _listeningPorts,
                    Paths = _networkPaths,
                };
        }

        public SecuritySnapshot BuildSecurity()
        {
            lock (_gate)
            {
                return new SecuritySnapshot
                {
                    Products = _securityProducts,
                    Defender = _defender,
                    Firewall = _firewall,
                    Uac = _uac,
                    Accounts = _accounts,
                    RemoteAccess = _remoteAccess,
                    SmartScreenEnabled = _smartScreen,
                };
            }
        }

        public PerformanceSnapshot BuildPerformance()
        {
            lock (_gate)
            {
                return new PerformanceSnapshot
                {
                    Responsiveness = _responsiveness,
                    Boot = _boot,
                    TopByMemory = _topByMemory,
                    TopByCpu = _topByCpu,
                    ProcessCount = _processCount,
                    Uptime = _uptime,
                    Power = _power,
                };
            }
        }
    }
}
