using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;

namespace LDI12.Tests
{
    /// <summary>
    /// Machines de référence, construites en code.
    /// </summary>
    /// <remarks>
    /// Des cas synthétiques plutôt que des captures réelles : ils sont déterministes, ne
    /// contiennent aucune donnée personnelle, et surtout ils isolent exactement le comportement
    /// que chaque test veut vérifier. Les captures de vraies machines viendront s'ajouter dans
    /// <c>build/fixtures/</c> pour surveiller les dérives de score dans le temps.
    /// </remarks>
    internal static class Fixtures
    {
        /// <summary>Machine saine : tout est mesuré, rien ne cloche.</summary>
        public static SystemSnapshot Healthy() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 8, usage: 12);
            b.Memory(totalGb: 16, usagePercent: 45, modules: 2, slots: 4);
            b.Gpu(generic: false, driverAgeMonths: 6);
            b.Motherboard(uefi: true, secureBoot: true, tpm: true, biosAgeMonths: 12, chassis: "Tour");
            b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
            b.Volume("C:", totalGb: 500, freeGb: 250, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 2, restoreEnabled: true);
            b.Updates(daysSince: 5, serviceRunning: true);
            b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
            b.Startup(count: 4, orphans: 0);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
            b.NetworkQuality();
            b.Security();
            b.Performance();
            b.Thermal(42, 51);
        });

        /// <summary>Disque en fin de vie : SMART dégradé confirmé par les journaux.</summary>
        public static SystemSnapshot DyingDisk() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 8, usage: 20);
            b.Memory(totalGb: 16, usagePercent: 50, modules: 2, slots: 4);
            b.Gpu(generic: false, driverAgeMonths: 6);
            b.Motherboard(uefi: true, secureBoot: true, tpm: true, biosAgeMonths: 12, chassis: "Tour");
            b.Disk(0, StorageMediaType.Hdd, SmartOverallStatus.Failing, isSystem: true,
                reallocated: 180, pending: 24, uncorrectable: 12, hours: 52000);
            b.Volume("C:", totalGb: 500, freeGb: 200, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 3, restoreEnabled: true);
            b.Updates(daysSince: 10, serviceRunning: true);
            b.Events(critical: 4, shutdowns: 1, diskErrors: 3, bsods: 0);
            b.Startup(count: 5, orphans: 0);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
        });

        /// <summary>Machine lente : plusieurs facteurs cumulés, aucun critique.</summary>
        public static SystemSnapshot SlowMachine() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 2, usage: 92);
            b.Memory(totalGb: 4, usagePercent: 95, modules: 1, slots: 2);
            b.Gpu(generic: true, driverAgeMonths: 40);
            b.Motherboard(uefi: false, secureBoot: false, tpm: false, biosAgeMonths: 90, chassis: "Ordinateur portable");
            b.Disk(0, StorageMediaType.Hdd, SmartOverallStatus.Ok, isSystem: true, hours: 30000);
            b.Volume("C:", totalGb: 500, freeGb: 18, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: true, uptimeDays: 45, restoreEnabled: false);
            b.Updates(daysSince: 120, serviceRunning: false);
            b.Events(critical: 6, shutdowns: 5, diskErrors: 0, bsods: 0);
            b.Startup(count: 22, orphans: 4);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
            b.NetworkQuality(lossPercent: 0, jitterMs: 4);
            b.Security();
            b.Performance(cpuPercent: 92, commitRatio: 168, bootSeconds: 145, degradedBoots: 6,
                topProcessBytes: 2L * 1024 * 1024 * 1024);
        });

        /// <summary>Windows 7 : beaucoup de contrôles ne peuvent pas être évalués.</summary>
        public static SystemSnapshot Windows7Minimal() => Build(b =>
        {
            b.Windows7();
            b.Cpu(cores: 4, usage: 30);
            b.Memory(totalGb: 8, usagePercent: 60, modules: 2, slots: 2);
            b.Volume("C:", totalGb: 250, freeGb: 120, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 5, restoreEnabled: true);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
        });

        /// <summary>Aucune donnée collectée : le score doit refuser de conclure, pas afficher zéro.</summary>
        public static SystemSnapshot Empty() => Build(b => b.Windows11());

        /// <summary>Panne DNS seule : la connexion fonctionne, la résolution non.</summary>
        public static SystemSnapshot DnsFailure() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 8, usage: 15);
            b.Memory(totalGb: 16, usagePercent: 40, modules: 2, slots: 4);
            b.Volume("C:", totalGb: 500, freeGb: 300, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 1, restoreEnabled: true);
            b.Network(connected: true, gatewayReachable: true, dnsOk: false, httpsOk: false);
        });

        /// <summary>
        /// Poste exposé : la machine fonctionne parfaitement, ses protections non.
        /// </summary>
        /// <remarks>
        /// Le cas que le client ne voit jamais seul, et le seul de la série où rien ne « rame » :
        /// antivirus expiré, pare-feu coupé sur le profil public, contrôle de compte réglé pour
        /// ne jamais avertir, compte administrateur sans mot de passe. Chacun de ces défauts
        /// laisse l'impression d'une machine protégée.
        /// </remarks>
        public static SystemSnapshot ExposedMachine() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 8, usage: 18);
            b.Memory(totalGb: 16, usagePercent: 42, modules: 2, slots: 4);
            b.Gpu(generic: false, driverAgeMonths: 4);
            b.Motherboard(uefi: true, secureBoot: true, tpm: true, biosAgeMonths: 10, chassis: "Tour");
            b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
            b.Volume("C:", totalGb: 1000, freeGb: 600, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 4, restoreEnabled: true);
            b.Updates(daysSince: 12, serviceRunning: true);
            b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
            b.Startup(count: 6, orphans: 0);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
            b.NetworkQuality(lossPercent: 13, jitterMs: 48, pathMtu: 1420);
            b.Security(antivirusActive: false, signatureAgeDays: 96, firewall: false,
                uac: true, uacPrompt: 0, remoteDesktop: true, passwordlessAdmin: true, administrators: 4);
            b.Performance();
        });

        /// <summary>
        /// Portable en Wi-Fi : le signal est excellent et le débit s'effondre quand même.
        /// </summary>
        /// <remarks>
        /// Le cas qu'aucun autre relevé n'explique, et celui où le client s'entend répondre que
        /// tout est normal : neuf réseaux se partagent le canal 6 en 2,4 GHz, la liaison retombe
        /// sur un débit de secours, et la barre de signal reste pleine. Rien n'est en panne.
        /// </remarks>
        public static SystemSnapshot CrowdedWifi() => Build(b =>
        {
            b.Windows11();
            b.Cpu(cores: 4, usage: 22);
            b.Memory(totalGb: 8, usagePercent: 55, modules: 2, slots: 2);
            b.Motherboard(uefi: true, secureBoot: true, tpm: true, biosAgeMonths: 20, chassis: "Ordinateur portable");
            b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
            b.Volume("C:", totalGb: 500, freeGb: 210, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 3, restoreEnabled: true);
            b.Updates(daysSince: 8, serviceRunning: true);
            b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
            b.Startup(count: 5, orphans: 0);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
            b.WifiLink(signalPercent: 82, rssiDbm: -58, radio: "802.11n", channel: 6,
                band: WifiBand.Band24, txMbps: 6, sameChannel: 5, overlappingChannel: 4, neighbours: 21);
            b.NetworkQuality(lossPercent: 2, jitterMs: 26);
            b.Security();
            b.Performance();
        });

        public static IEnumerable<(string Name, SystemSnapshot Snapshot)> All()
        {
            yield return ("machine saine", Healthy());
            yield return ("disque en fin de vie", DyingDisk());
            yield return ("machine lente", SlowMachine());
            yield return ("Windows 7 minimal", Windows7Minimal());
            yield return ("aucune donnée", Empty());
            yield return ("panne DNS", DnsFailure());
            yield return ("poste exposé", ExposedMachine());
            yield return ("Wi-Fi encombré", CrowdedWifi());
        }

        /// <summary>Machine sur mesure, pour un test qui isole un cas que la série ne couvre pas.</summary>
        public static SystemSnapshot Build(Action<Builder> configure)
        {
            var builder = new Builder();
            configure(builder);
            return builder.ToSnapshot();
        }

        internal sealed class Builder
        {
            private WindowsProfile _windows = new WindowsProfile();
            private CpuInfo _cpu = new CpuInfo();
            private MemoryInfo _memory = new MemoryInfo();
            private IReadOnlyList<GpuInfo> _gpus = Array.Empty<GpuInfo>();
            private MotherboardInfo _motherboard = new MotherboardInfo();
            private readonly List<PhysicalDiskInfo> _disks = new List<PhysicalDiskInfo>();
            private readonly List<VolumeInfo> _volumes = new List<VolumeInfo>();
            private WindowsInstallInfo _install = new WindowsInstallInfo();
            private UpdatesInfo _updates = new UpdatesInfo();
            private EventsInfo _events = new EventsInfo();
            private StabilityInfo _stability = new StabilityInfo();
            private SafetyNet _safetyNet = new SafetyNet();
            private readonly List<UserProfile> _profiles = new List<UserProfile>();
            private ProfileInventory _profileInventory = new ProfileInventory();
            private SystemTimeInfo _time = new SystemTimeInfo();
            private SerialPortsInfo _serialPorts = new SerialPortsInfo();
            private readonly List<DeviceInfo> _extraDevices = new List<DeviceInfo>();
            private readonly List<ListeningPort> _listening = new List<ListeningPort>();
            private ListeningPortsInfo _listeningInfo = new ListeningPortsInfo();
            private readonly List<RouteEntry> _routes = new List<RouteEntry>();
            private HostsFile _hosts = new HostsFile();
            private WinsockCatalog _winsock = new WinsockCatalog();
            private readonly List<DnsServerUse> _dnsServers = new List<DnsServerUse>();
            private SharingState _sharing = new SharingState();
            private readonly List<ServiceInfo> _services = new List<ServiceInfo>();
            private readonly List<FirewallProfileState> _firewallProfiles = new List<FirewallProfileState>();
            private PrintingInfo _printing = new PrintingInfo();
            private AudioInfo _audio = new AudioInfo();
            private IReadOnlyList<StartupItem> _startup = Array.Empty<StartupItem>();
            private SoftwareInventory _software = new SoftwareInventory();
            private ScheduledTaskInventory _tasks = new ScheduledTaskInventory();
            private IReadOnlyList<BatteryInfo> _batteries = Array.Empty<BatteryInfo>();
            private PowerConfiguration _power = new PowerConfiguration();
            private StorageMaintenance _maintenance = new StorageMaintenance();
            private readonly List<SpaceConsumer> _space = new List<SpaceConsumer>();
            private NetworkSnapshot _network = new NetworkSnapshot();
            private SecuritySnapshot _security = new SecuritySnapshot();
            private PerformanceSnapshot _performance = new PerformanceSnapshot();
            private ThermalSnapshot _thermal = new ThermalSnapshot();
            private readonly List<MonitorInfo> _monitors = new List<MonitorInfo>();
            private DisplaySnapshot _displays = new DisplaySnapshot();
            private HardwareErrorsInfo _hardwareErrors = new HardwareErrorsInfo();
            private DateTimeOffset _createdAt = DateTimeOffset.Now;

            private static Measured<T> Ok<T>(T value) => Measured.Ok(value, DataSource.NativeApi);

            /// <summary>
            /// Fixe la date du relevé.
            /// </summary>
            /// <remarks>
            /// Indispensable dès qu'un test porte sur une échéance : sans elle, il passerait
            /// aujourd'hui et échouerait le jour où la date qu'il cite serait dépassée.
            /// </remarks>
            public void TakenOn(DateTimeOffset date) => _createdAt = date;

            public void Windows11() => _windows = new WindowsProfile
            {
                Family = WindowsFamily.Windows11,
                Version = new Version(10, 0, 22631),
                Build = 22631,
                Level = CompatibilityLevel.Full,
                NativeArchitecture = ProcessorArchitecture.X64,
            };

            /// <summary>Machine jointe à un domaine, pour les règles qui ne valent qu'en entreprise.</summary>
            public void DomainMember()
            {
                var current = _windows;
                _windows = new WindowsProfile
                {
                    Family = current.Family == WindowsFamily.Unknown ? WindowsFamily.Windows11 : current.Family,
                    Version = current.Version,
                    Build = current.Build,
                    DisplayVersion = current.DisplayVersion,
                    EditionId = current.EditionId,
                    Level = current.Level,
                    NativeArchitecture = current.NativeArchitecture,
                    IsDomainJoined = true,
                };
            }

            public void Windows11(string displayVersion, string edition = "Professional") => _windows = new WindowsProfile
            {
                Family = WindowsFamily.Windows11,
                Version = new Version(10, 0, 22631),
                Build = 22631,
                DisplayVersion = displayVersion,
                EditionId = edition,
                Level = CompatibilityLevel.Full,
                NativeArchitecture = ProcessorArchitecture.X64,
            };

            public void Windows10(
                string displayVersion = "22H2", string edition = "Professional",
                ProcessorArchitecture architecture = ProcessorArchitecture.X64) => _windows = new WindowsProfile
            {
                Family = WindowsFamily.Windows10,
                Version = new Version(10, 0, 19045),
                Build = 19045,
                DisplayVersion = displayVersion,
                EditionId = edition,
                Level = CompatibilityLevel.Full,
                NativeArchitecture = architecture,
            };

            public void Windows7() => _windows = new WindowsProfile
            {
                Family = WindowsFamily.Windows7,
                Version = new Version(6, 1, 7601),
                Build = 7601,
                ServicePackMajor = 1,
                Level = CompatibilityLevel.Minimal,
                NativeArchitecture = ProcessorArchitecture.X64,
            };

            public void Cpu(int cores, double usage) => _cpu = new CpuInfo
            {
                Model = Ok("Processeur de test"),
                LogicalCores = Ok(cores),
                PhysicalCores = Ok(cores / 2),
                UsagePercent = Ok(usage),
                VirtualizationEnabled = Ok(true),
            };

            /// <summary>Processeur nommé et cadencé, pour ce qui regarde le modèle et la fréquence.</summary>
            public void CpuModel(string model, int physicalCores = 4, int maxClockMhz = 3600) => _cpu = new CpuInfo
            {
                Model = Ok(model),
                PhysicalCores = Ok(physicalCores),
                LogicalCores = Ok(physicalCores * 2),
                MaxClockMhz = Ok(maxClockMhz),
                UsagePercent = Ok(20d),
                VirtualizationEnabled = Ok(true),
            };

            public void Memory(int totalGb, double usagePercent, int modules, int slots)
            {
                var list = new List<MemoryModule>();
                for (var i = 0; i < modules; i++)
                {
                    list.Add(new MemoryModule
                    {
                        DeviceLocator = Ok("DIMM" + i),
                        CapacityBytes = Ok((long)totalGb / Math.Max(1, modules) * 1024L * 1024 * 1024),
                        SpeedMhz = Ok(3200),
                        ConfiguredSpeedMhz = Ok(3200),
                    });
                }

                var total = (long)totalGb * 1024 * 1024 * 1024;
                _memory = new MemoryInfo
                {
                    TotalBytes = Ok(total),
                    AvailableBytes = Ok((long)(total * (1 - usagePercent / 100))),
                    UsagePercent = Ok(usagePercent),
                    SlotsTotal = Ok(slots),
                    SlotsUsed = Ok(modules),
                    Modules = list,
                };
            }

            public void Gpu(bool generic, int driverAgeMonths) => _gpus = new[]
            {
                new GpuInfo
                {
                    Model = Ok("Carte de test"),
                    UsesGenericMicrosoftDriver = Ok(generic),
                    DriverDate = Ok(DateTimeOffset.Now.AddMonths(-driverAgeMonths)),
                },
            };

            public void Motherboard(bool uefi, bool secureBoot, bool tpm, int biosAgeMonths, string chassis)
                => _motherboard = new MotherboardInfo
                {
                    Manufacturer = Ok("Fabricant de test"),
                    Model = Ok("Carte de test"),
                    ChassisType = Ok(chassis),
                    SecureBootEnabled = uefi ? Ok(secureBoot) : Measured.Missing<bool>("Mode BIOS hérité."),
                    Bios = new BiosInfo
                    {
                        Version = Ok("1.0"),
                        ReleaseDate = Ok(DateTimeOffset.Now.AddMonths(-biosAgeMonths)),
                        Mode = Ok(uefi ? FirmwareMode.Uefi : FirmwareMode.LegacyBios),
                    },
                    Tpm = new TpmInfo
                    {
                        Present = Ok(tpm),
                        Enabled = Ok(tpm),
                        Ready = Ok(tpm),
                        SpecVersion = tpm
                            ? Ok("2.0, 0, 1.38")
                            : Measured.Missing<string>("Cette machine ne dispose pas de module TPM."),
                    },
                };

            /// <summary>Mémoire à l'octet près, quand le test porte sur un seuil et non sur un ordre de grandeur.</summary>
            public void MemoryBytes(long total) => _memory = new MemoryInfo
            {
                TotalBytes = Ok(total),
                AvailableBytes = Ok(total / 2),
                UsagePercent = Ok(50d),
            };

            /// <summary>Module TPM détaillé, quand le test porte sur sa version ou son activation.</summary>
            public void Tpm(Measured<bool> present, Measured<bool> enabled, Measured<string> specVersion)
                => _motherboard = new MotherboardInfo
                {
                    Manufacturer = _motherboard.Manufacturer,
                    Model = _motherboard.Model,
                    ChassisType = _motherboard.ChassisType,
                    SecureBootEnabled = _motherboard.SecureBootEnabled,
                    Bios = _motherboard.Bios,
                    Tpm = new TpmInfo { Present = present, Enabled = enabled, SpecVersion = specVersion },
                };

            public void Disk(
                int index, StorageMediaType media, SmartOverallStatus status, bool isSystem,
                long reallocated = 0, long pending = 0, long uncorrectable = 0, long hours = 5000)
                => _disks.Add(new PhysicalDiskInfo
                {
                    Index = index,
                    Model = Ok("Disque de test " + index),
                    MediaType = Ok(media),
                    BusType = Ok(StorageBusType.Sata),
                    CapacityBytes = Ok(500L * 1024 * 1024 * 1024),
                    IsSystemDisk = Ok(isSystem),
                    Smart = new SmartData
                    {
                        OverallStatus = Ok(status),
                        PowerOnHours = Ok(hours),
                        PowerCycles = Ok(1200L),
                        TemperatureCelsius = Ok(35),
                        ReallocatedSectors = Ok(reallocated),
                        PendingSectors = Ok(pending),
                        UncorrectableErrors = Ok(uncorrectable),
                    },
                });

            public void Volume(string letter, int totalGb, int freeGb, bool isSystem)
            {
                var total = (long)totalGb * 1024 * 1024 * 1024;
                var free = (long)freeGb * 1024 * 1024 * 1024;
                _volumes.Add(new VolumeInfo
                {
                    DriveLetter = Ok(letter),
                    FileSystem = Ok("NTFS"),
                    TotalBytes = Ok(total),
                    FreeBytes = Ok(free),
                    UsagePercent = Ok(Math.Round(100d * (total - free) / total, 1)),
                    IsSystemVolume = Ok(isSystem),
                    BitLockerStatus = Ok("Non chiffré"),
                    DiskIndex = Ok(0),
                });
            }

            public void WindowsInstall(bool activated, bool rebootPending, int uptimeDays, bool restoreEnabled)
                => _install = new WindowsInstallInfo
                {
                    ActivationStatus = Ok(activated ? "Activé" : "Non activé"),
                    RebootPending = Ok(rebootPending),
                    Uptime = Ok(TimeSpan.FromDays(uptimeDays)),
                    SystemRestoreEnabled = Ok(restoreEnabled),
                    FastStartupEnabled = Ok(false),
                };

            /// <summary>
            /// La protection du système : ce qu'elle déclare, et ce qu'elle fait.
            /// </summary>
            /// <remarks>
            /// Les deux se règlent séparément parce que c'est leur écart qui intéresse les
            /// règles : une protection annoncée active qui ne crée plus rien est le cas que ce
            /// module existe pour attraper.
            /// </remarks>
            public void SafetyNet(
                bool restoreEnabled = true, int pointsCreated = 6, int failures = 0,
                RecoveryEnvironmentState recovery = RecoveryEnvironmentState.Installed)
                => _safetyNet = new SafetyNet
                {
                    Restore = new RestoreProtection
                    {
                        Enabled = Ok(restoreEnabled),
                        ReservedPercent = Ok(10),
                        PointsCreated = Ok(pointsCreated),
                        Failures = Ok(failures),
                        WindowDays = Ok(90),
                        LastPointCreated = pointsCreated > 0
                            ? Measured.Partial(DateTimeOffset.Now.AddDays(-2), DataSource.EventLog, "Date de création.")
                            : Measured.Missing<DateTimeOffset>("Aucun point créé."),
                    },
                    Recovery = new RecoveryEnvironment
                    {
                        State = Ok(recovery),
                        AutomaticRepair = Ok(recovery == RecoveryEnvironmentState.Installed),
                    },
                    Folders = _safetyNet.Folders,
                    Backups = _safetyNet.Backups,
                };

            /// <summary>Où sont les affaires du client.</summary>
            public void PersonalFolders(bool onSystemVolume, bool exists = true, bool inCloud = false)
            {
                var root = onSystemVolume ? @"C:\Utilisateurs\Client\" : @"D:\Client\";

                _safetyNet = Rebuild(new[]
                {
                    Folder(PersonalFolderKind.Desktop, root + "Bureau", onSystemVolume, exists, inCloud),
                    Folder(PersonalFolderKind.Documents, root + "Documents", onSystemVolume, exists, inCloud),
                }, _safetyNet.Backups);
            }

            /// <summary>Un moyen de sauvegarde repéré sur la machine.</summary>
            public void BackupDetected(BackupKind kind, string label)
            {
                var backups = new List<BackupTool>(_safetyNet.Backups)
                {
                    new BackupTool { Kind = kind, Label = label },
                };

                _safetyNet = Rebuild(_safetyNet.Folders, backups);
            }

            private SafetyNet Rebuild(IReadOnlyList<PersonalFolder> folders, IReadOnlyList<BackupTool> backups)
                => new SafetyNet
                {
                    Restore = _safetyNet.Restore,
                    Recovery = _safetyNet.Recovery,
                    Folders = folders,
                    Backups = backups,
                };

            private static PersonalFolder Folder(
                PersonalFolderKind kind, string path, bool onSystemVolume, bool exists, bool inCloud)
                => new PersonalFolder
                {
                    Kind = kind,
                    Path = path,
                    Exists = exists,
                    OnSystemVolume = onSystemVolume,
                    InCloud = inCloud,
                };

            public void Printing(bool spoolerRunning = true, int pendingJobs = 0, params PrinterInfo[] printers)
                => _printing = new PrintingInfo
                {
                    SpoolerState = Ok(spoolerRunning ? "Démarré" : "Arrêté"),
                    SpoolerRunning = Ok(spoolerRunning),
                    Printers = printers,
                    PendingJobs = Ok(pendingJobs),
                    SpoolFiles = Measured.NeedsElevation<int>("Dossier de spoule fermé."),
                    RecentErrors = Ok(0),
                    ErrorWindowDays = Ok(30),
                };

            public static PrinterInfo Printer(
                string name,
                PrinterAvailability availability = PrinterAvailability.Ready,
                bool offlineByChoice = false,
                bool isDefault = true)
                => new PrinterInfo
                {
                    Name = name,
                    IsDefault = isDefault,
                    Availability = availability,
                    OfflineByChoice = offlineByChoice,
                    Driver = "Pilote de test",
                    Port = "USB001",
                };

            /// <summary>
            /// Les périphériques audio, dont les compteurs se déduisent de la liste.
            /// </summary>
            /// <remarks>
            /// Déduits et non fournis : un test qui pourrait décrire trois sorties actives et
            /// annoncer un compteur à zéro vérifierait une machine qui n'existe pas.
            /// </remarks>
            public void Audio(
                bool serviceRunning = true, bool builderRunning = true, params AudioEndpoint[] endpoints)
            {
                var outputs = 0;
                var inputs = 0;

                foreach (var endpoint in endpoints)
                {
                    if (endpoint.State != AudioEndpointState.Active) continue;
                    if (endpoint.Direction == AudioDirection.Output) outputs++;
                    else inputs++;
                }

                _audio = new AudioInfo
                {
                    ServiceRunning = Ok(serviceRunning),
                    EndpointBuilderRunning = Ok(builderRunning),
                    Endpoints = endpoints,
                    ActiveOutputs = Ok(outputs),
                    ActiveInputs = Ok(inputs),
                    ForgottenEndpoints = Ok(12),
                };
            }

            public static AudioEndpoint Endpoint(
                string name, AudioDirection direction, AudioEndpointState state)
                => new AudioEndpoint { Name = name, Direction = direction, State = state };

            public void Updates(int daysSince, bool serviceRunning) => _updates = new UpdatesInfo
            {
                DaysSinceLastUpdate = Ok(daysSince),
                ServiceState = Ok(serviceRunning ? "Running" : "Stopped"),
                InstalledCount = Ok(40),
            };

            public void Events(int critical, int shutdowns, int diskErrors, int bsods)
            {
                var criticalList = new List<EventSummary>();
                if (critical > 0)
                {
                    criticalList.Add(new EventSummary
                    {
                        LogName = "System", Source = "Test", EventId = 41, Level = "Critique",
                        Count = critical, FirstSeen = DateTimeOffset.Now.AddDays(-10), LastSeen = DateTimeOffset.Now,
                    });
                }

                var diskList = new List<EventSummary>();
                for (var i = 0; i < diskErrors; i++)
                {
                    diskList.Add(new EventSummary
                    {
                        LogName = "System", Source = "disk", EventId = 7 + i, Level = "Erreur",
                        Count = 5, FirstSeen = DateTimeOffset.Now.AddDays(-10), LastSeen = DateTimeOffset.Now,
                    });
                }

                var bsodList = new List<BsodInfo>();
                for (var i = 0; i < bsods; i++)
                    bsodList.Add(new BsodInfo { Date = DateTimeOffset.Now.AddDays(-i - 1), BugCheckCode = "0x0000007E" });

                _events = new EventsInfo
                {
                    WindowDays = Ok(14),
                    CriticalErrors = criticalList,
                    DiskErrors = diskList,
                    Bsods = bsodList,
                    UnexpectedShutdowns = Ok(shutdowns),
                };
            }

            /// <summary>
            /// Une machine avec une histoire : des pannes à des dates données.
            /// </summary>
            /// <remarks>
            /// Les jours sont comptés à rebours du relevé, comme les journaux le font. Les
            /// tests décrivent donc « il y a douze jours » plutôt qu'une date, et restent
            /// valables demain.
            /// </remarks>
            public void Stability(
                string name, IncidentKind kind, IReadOnlyList<int> daysAgo,
                string? module = null, int windowDays = 90, int? coveredDays = null)
            {
                var incidents = new List<Incident>(_stability.Incidents);
                var programs = new List<FailingProgram>(_stability.Programs);

                var now = DateTimeOffset.Now;
                DateTimeOffset first = now, last = now.AddYears(-10);

                foreach (var day in daysAgo)
                {
                    var when = now.AddDays(-day);
                    incidents.Add(new Incident { Date = when, Kind = kind, Subject = name });
                    if (when < first) first = when;
                    if (when > last) last = when;
                }

                programs.Add(new FailingProgram
                {
                    Name = name,
                    Kind = kind,
                    Count = daysAgo.Count,
                    FirstSeen = first,
                    LastSeen = last,
                    Module = module,
                });

                var oldest = now;
                foreach (var incident in incidents) if (incident.Date < oldest) oldest = incident.Date;

                var crashes = 0;
                var hangs = 0;
                var services = 0;
                foreach (var incident in incidents)
                {
                    if (incident.Kind == IncidentKind.ProgramCrash) crashes++;
                    else if (incident.Kind == IncidentKind.ProgramHang) hangs++;
                    else if (incident.Kind == IncidentKind.ServiceCrash) services++;
                }

                _stability = new StabilityInfo
                {
                    WindowDays = Ok(windowDays),
                    OldestEntry = Ok(coveredDays.HasValue ? now.AddDays(-coveredDays.Value) : oldest),
                    Programs = programs,
                    Incidents = incidents,
                    ProgramCrashes = Ok(crashes),
                    ProgramHangs = Ok(hangs),
                    ServiceCrashes = Ok(services),
                };
            }

            /// <summary>Historique illisible : le journal a été refusé.</summary>
            public void StabilityUnreadable(string reason)
            {
                _stability = new StabilityInfo
                {
                    WindowDays = Measured.Missing<int>(reason),
                    OldestEntry = Measured.Missing<DateTimeOffset>(reason),
                    ProgramCrashes = Measured.Missing<int>(reason),
                    ProgramHangs = Measured.Missing<int>(reason),
                    ServiceCrashes = Measured.Missing<int>(reason),
                };
            }

            public void Startup(int count, int orphans)
            {
                var items = new List<StartupItem>();
                for (var i = 0; i < count; i++)
                {
                    items.Add(new StartupItem
                    {
                        Name = "Programme " + i,
                        Command = @"C:\Test\prog" + i + ".exe",
                        Location = StartupLocation.RegistryRunMachine,
                        Enabled = true,
                        TargetMissing = i < orphans,
                    });
                }
                _startup = items;
            }

            /// <summary>
            /// Ce que le matériel a signalé de lui-même.
            /// </summary>
            /// <remarks>
            /// <paramref name="memoryTest"/> à <c>null</c> décrit une machine où le diagnostic
            /// mémoire n'a jamais tourné, le cas de très loin le plus fréquent en clientèle.
            /// </remarks>
            public void HardwareErrors(
                int corrected = 0, int uncorrected = 0, MemoryTestOutcome? memoryTest = null,
                int crashDumps = 0, bool dumpsEnabled = true)
            {
                var groups = new List<HardwareErrorGroup>();
                if (uncorrected > 0)
                    groups.Add(new HardwareErrorGroup
                    {
                        EventId = 18, Kind = HardwareErrorKind.Uncorrected, Count = uncorrected,
                        FirstSeen = DateTimeOffset.Now.AddDays(-30), LastSeen = DateTimeOffset.Now.AddDays(-2),
                        Sample = "Une erreur matérielle irrécupérable s'est produite.",
                    });

                if (corrected > 0)
                    groups.Add(new HardwareErrorGroup
                    {
                        EventId = 17, Kind = HardwareErrorKind.Corrected, Count = corrected,
                        FirstSeen = DateTimeOffset.Now.AddDays(-60), LastSeen = DateTimeOffset.Now.AddDays(-1),
                        Sample = "Une erreur matérielle corrigée s'est produite.",
                    });

                var dumps = new List<CrashDump>();
                for (var i = 0; i < crashDumps; i++)
                    dumps.Add(new CrashDump
                    {
                        Path = @"C:\Windows\Minidump\dump" + i + ".dmp",
                        Date = DateTimeOffset.Now.AddDays(-i),
                        SizeBytes = 512 * 1024,
                    });

                _hardwareErrors = new HardwareErrorsInfo
                {
                    WindowDays = Measured.Ok(90, DataSource.EventLog),
                    Errors = groups,
                    CorrectedCount = Measured.Ok(corrected, DataSource.EventLog),
                    UncorrectedCount = Measured.Ok(uncorrected, DataSource.EventLog),
                    MemoryTest = Measured.Ok(memoryTest ?? MemoryTestOutcome.NeverRun, DataSource.EventLog),
                    LastMemoryTest = memoryTest == null ? null : new MemoryTestRun
                    {
                        Date = DateTimeOffset.Now.AddDays(-5),
                        EventId = memoryTest == MemoryTestOutcome.ErrorsFound ? 1202 : 1201,
                        Outcome = memoryTest.Value,
                    },
                    CrashDumpsEnabled = Measured.Ok(dumpsEnabled, DataSource.Registry),
                    CrashDumpMode = Measured.Ok(dumpsEnabled ? "Mini-vidage" : "Aucun rapport", DataSource.Registry),
                    CrashDumps = dumps,
                };
            }

            /// <summary>Une batterie : ce qui distingue un portable d'un poste fixe.</summary>
            public void Battery(double wearPercent = 8)
                => _batteries = new[]
                {
                    new BatteryInfo
                    {
                        Name = Ok("Batterie principale"),
                        Status = Ok("En charge"),
                        ChargePercent = Ok(80),
                        WearPercent = Measured.Ok(wearPercent, DataSource.Wmi),
                    },
                };

            /// <summary>Réglages d'alimentation de la machine.</summary>
            public void Power(
                PowerPlanKind plan = PowerPlanKind.Balanced, int maximumOnAc = 100,
                PageFileMode pageFile = PageFileMode.SystemManaged)
                => _power = new PowerConfiguration
                {
                    Plan = Measured.Ok(plan, DataSource.Registry),
                    PlanId = Measured.Ok("381b4222-f694-41f0-9685-ff5bb260df2e", DataSource.Registry),
                    ProcessorMaximumOnAc = Measured.Ok(maximumOnAc, DataSource.Registry),
                    ProcessorMinimumOnAc = Measured.Ok(5, DataSource.Inferred),
                    ProcessorMaximumOnBattery = Measured.Ok(100, DataSource.Inferred),
                    PageFile = Measured.Ok(pageFile, DataSource.Registry),
                    PageFileSetting = Measured.Ok("?:\\pagefile.sys", DataSource.Registry),
                };

            /// <summary>Entretien du stockage.</summary>
            public void Maintenance(bool trim)
                => _maintenance = new StorageMaintenance { TrimEnabled = Measured.Ok(trim, DataSource.Registry) };

            /// <summary>Tâches planifiées ajoutées à la machine.</summary>
            public void Tasks(params ScheduledTaskInfo[] tasks)
                => _tasks = new ScheduledTaskInventory
                {
                    TotalCount = Measured.Ok(180 + tasks.Length, DataSource.NativeApi),
                    MicrosoftFolderCount = Measured.Ok(180, DataSource.NativeApi),
                    Tasks = tasks,
                    Unreadable = Measured.Ok(0, DataSource.NativeApi),
                };

            /// <summary>Une tâche planifiée, réduite à ce dont les règles se servent.</summary>
            public static ScheduledTaskInfo Task(
                string path, TaskTriggerKind trigger = TaskTriggerKind.Scheduled, bool enabled = true,
                bool targetMissing = false, int? lastResult = 0, int? repetitionMinutes = null,
                bool everRun = true)
                => new ScheduledTaskInfo
                {
                    Path = path,
                    Name = path,
                    Enabled = enabled,
                    Triggers = new[] { trigger },
                    RepetitionMinutes = repetitionMinutes,
                    ImagePath = @"C:\Test\tache.exe",
                    TargetMissing = targetMissing,
                    LastRun = everRun ? DateTimeOffset.Now.AddDays(-1) : (DateTimeOffset?)null,
                    LastResult = lastResult,
                    ResultKind = everRun
                        ? LDI12.Collectors.Internal.TaskResultCatalog.Classify(lastResult)
                        : TaskResultKind.Unknown,
                };

            /// <summary>Inventaire logiciel de la machine de référence.</summary>
            public void Software(params InstalledProgram[] programs)
                => _software = new SoftwareInventory
                {
                    Programs = programs,
                    HiddenEntries = Measured.Ok(programs.Length, DataSource.Registry),
                };

            /// <summary>
            /// Un logiciel installé il y a tant de jours, ajouté à l'inventaire.
            /// </summary>
            /// <remarks>
            /// Distinct de la fabrique voisine, qui remplace l'inventaire et pose une date fixe :
            /// la chronologie a besoin de dates relatives au relevé, sans quoi les tests
            /// vieilliraient.
            /// </remarks>
            public void Software(string name, int installedDaysAgo)
            {
                var programs = new List<InstalledProgram>(_software.Programs)
                {
                    new InstalledProgram
                    {
                        Name = name,
                        Publisher = "Éditeur",
                        Scope = SoftwareScope.Machine,
                        InstalledOn = Measured.Ok(
                            DateTimeOffset.Now.AddDays(-installedDaysAgo), DataSource.Registry),
                    },
                };

                _software = new SoftwareInventory
                {
                    Programs = programs,
                    HiddenEntries = Measured.Ok(programs.Count, DataSource.Registry),
                };
            }

            /// <summary>Un programme installé, réduit à ce dont les règles se servent.</summary>
            public static InstalledProgram Installed(string name, string? version = null)
                => new InstalledProgram
                {
                    Name = name,
                    Version = version,
                    Publisher = "Éditeur",
                    Scope = SoftwareScope.Machine,
                    InstalledOn = Measured.Ok(
                        new DateTimeOffset(2019, 4, 2, 0, 0, 0, TimeSpan.Zero), DataSource.Registry),
                };

            public void Network(bool connected, bool gatewayReachable, bool dnsOk, bool httpsOk)
            {
                var adapters = new List<NetworkAdapterInfo>
                {
                    new NetworkAdapterInfo
                    {
                        Name = "Ethernet",
                        Description = "Carte de test",
                        Kind = NetworkAdapterKind.Ethernet,
                        Status = Ok(connected ? "Connectée" : "Déconnectée"),
                        IsUp = connected,
                        HasApipaAddress = Ok(false),
                        IPv4Addresses = new[] { "192.168.1.20" },
                        Gateways = new[] { "192.168.1.1" },
                        DnsServers = new[] { "192.168.1.1" },
                        IsPrimary = true,
                    },
                };

                _network = new NetworkSnapshot
                {
                    Adapters = adapters,
                    Tests = new NetworkTests
                    {
                        Gateway = Ping("192.168.1.1", gatewayReachable),
                        Internet = Ping("1.1.1.1", true),
                        DnsResolutions = new[]
                        {
                            new DnsResolutionResult { Host = "www.microsoft.com", Resolved = Ok(dnsOk) },
                        },
                        HttpChecks = new[]
                        {
                            new HttpCheckResult { Url = "https://www.microsoft.com/", Succeeded = Ok(httpsOk) },
                        },
                    },
                };
            }

            /// <summary>Un port série ou un convertisseur, tel que l'inventaire PnP le rend.</summary>
            public void SerialDevice(
                string name, string deviceClass = "Ports", string? hardwareId = "USB\\VID_0403&PID_6001",
                int problemCode = 0, string? manufacturer = "FTDI")
                => _extraDevices.Add(new DeviceInfo
                {
                    Name = name,
                    DeviceClass = deviceClass,
                    Manufacturer = manufacturer,
                    HardwareId = hardwareId,
                    ProblemCode = problemCode,
                    ProblemLabel = problemCode == 0 ? null : "Pilote absent",
                });

            /// <summary>Numéros de port série retenus par Windows.</summary>
            public void ReservedSerialPorts(params int[] numbers)
                => _serialPorts = new SerialPortsInfo
                {
                    Reserved = numbers,
                    ReservedCount = Ok(numbers.Length),
                };

            /// <summary>Un port en écoute, avec le programme qui le tient.</summary>
            public void Listening(
                int port, bool allInterfaces = true, string process = "svchost", int processId = 100)
            {
                _listening.Add(new ListeningPort
                {
                    Port = port,
                    Address = allInterfaces ? "0.0.0.0" : "127.0.0.1",
                    ProcessId = processId,
                    ProcessName = Ok(process),
                    AllInterfaces = allInterfaces,
                    Service = WellKnown.Describe(port),
                });

                _listeningInfo = new ListeningPortsInfo
                {
                    Ports = _listening,
                    Count = Ok(_listening.Count),
                    Established = Ok(0),
                };
            }

            /// <summary>Un profil de pare-feu et son état.</summary>
            public void FirewallProfile(string profile, bool enabled)
                => _firewallProfiles.Add(new FirewallProfileState { Profile = profile, Enabled = Ok(enabled) });

            /// <summary>L'heure de la machine, avec la référence qui permet de la juger.</summary>
            public void Clock(
                int clockOffsetDays = 0, int anchorDaysAgo = 10, int lastSyncDaysAgo = 2,
                bool serviceDisabled = false, bool daylightAdjustment = true,
                TimeSource source = TimeSource.Ntp, bool anchor = true, bool synchronised = true)
            {
                var now = DateTimeOffset.Now.AddDays(clockOffsetDays);

                _time = new SystemTimeInfo
                {
                    SystemTime = Ok(now),
                    TimeZone = Ok("(UTC+01:00) Paris"),
                    UtcOffset = Ok(TimeSpan.FromHours(1)),
                    DaylightAdjustment = Ok(daylightAdjustment),
                    InDaylightSaving = Ok(false),
                    NewestSystemFile = anchor
                        ? Ok(DateTimeOffset.Now.AddDays(-anchorDaysAgo))
                        : Measured.Missing<DateTimeOffset>("Aucun fichier du noyau n'a pu être daté."),
                    Sync = new TimeSyncInfo
                    {
                        LastSynchronised = synchronised
                            ? Ok(DateTimeOffset.Now.AddDays(-lastSyncDaysAgo))
                            : Measured.Missing<DateTimeOffset>("Aucune remise à l'heure dans les journaux."),
                        Server = Ok("time.windows.com"),
                        Source = Ok(source),
                        ServiceDisabled = Ok(serviceDisabled),
                    },
                };
            }

            /// <summary>Un occupant du disque système, tel que la sonde d'occupation le relève.</summary>
            public void SystemFile(string name, SpaceKind kind, long bytes, int daysAgo = 1)
                => _space.Add(new SpaceConsumer
                {
                    Name = name,
                    Path = @"C:\" + name,
                    Kind = kind,
                    Bytes = Ok(bytes),
                    Since = Ok(DateTimeOffset.Now.AddDays(-daysAgo)),
                    Purpose = "Élément de test.",
                    Reclaim = SpaceReclaim.Setting,
                });

            /// <summary>Une installation précédente de Windows, avec son ancienneté.</summary>
            public void PreviousWindows(int daysAgo)
                => _space.Add(new SpaceConsumer
                {
                    Name = "Windows.old",
                    Path = @"C:\Windows.old",
                    Kind = SpaceKind.PreviousWindows,
                    Bytes = Measured.Missing<long>("Taille non mesurable sans privilèges."),
                    Since = Ok(DateTimeOffset.Now.AddDays(-daysAgo)),
                    Purpose = "Installation précédente de Windows.",
                    Reclaim = SpaceReclaim.Automatic,
                });

            /// <summary>Un écran branché, avec ce que la dalle déclare et ce que le pilote applique.</summary>
            public void Monitor(
                string model = "Écran de test", int width = 1920, int height = 1080,
                int nativeWidth = 1920, int nativeHeight = 1080,
                int refreshHz = 60, int maxRefreshHz = 60,
                double diagonalInches = 24, int scalingPercent = 100, bool primary = true)
            {
                var diagonalPixels = Math.Sqrt((double)width * width + (double)height * height);

                _monitors.Add(new MonitorInfo
                {
                    Output = @"\\.\DISPLAY" + (_monitors.Count + 1),
                    Manufacturer = Ok("TST"),
                    Model = Ok(model),
                    YearOfManufacture = Ok(2020),
                    SerialNumber = Ok("SN" + _monitors.Count),
                    Current = Ok(new DisplayMode { Width = width, Height = height, RefreshHz = refreshHz }),
                    Native = Ok(new DisplayMode { Width = nativeWidth, Height = nativeHeight, RefreshHz = 60 }),
                    MaxRefreshHz = Ok(maxRefreshHz),
                    DiagonalInches = Ok(diagonalInches),
                    PixelsPerInch = Ok(Math.Round(diagonalPixels / diagonalInches)),
                    IsPrimary = primary,
                });

                _displays = new DisplaySnapshot
                {
                    Monitors = _monitors,
                    ScalingPercent = Ok(scalingPercent),
                };
            }

            /// <summary>Un profil utilisateur, ajouté à l'inventaire déjà décrit.</summary>
            public void Profile(
                string account, ProfileState state = ProfileState.Normal,
                bool loaded = false, bool current = false, int monthsSinceUse = 1,
                bool folderExists = true, string? path = null)
            {
                var profile = new UserProfile
                {
                    Sid = "S-1-5-21-1-2-3-" + (1000 + _profiles.Count),
                    AccountName = Ok(account),
                    Path = path ?? @"C:\Users\" + account,
                    FolderExists = Ok(folderExists),
                    LastUsed = Ok(DateTimeOffset.Now.AddDays(-30.44 * monthsSinceUse)),
                    Loaded = loaded || current,
                    IsCurrent = current,
                    State = state,
                };

                _profiles.Add(profile);

                var open = 0;
                foreach (var item in _profiles) if (item.Loaded) open++;
                _profileInventory = new ProfileInventory
                {
                    Profiles = _profiles,
                    LoadedCount = Ok(open),
                };
            }

            /// <summary>Une carte, ses compteurs et sa nature, pour ce qui regarde la liaison elle-même.</summary>
            public void Link(
                string name = "Ethernet", NetworkAdapterKind kind = NetworkAdapterKind.Ethernet,
                bool up = true, long packets = 1_000_000, long errorsIn = 0, long errorsOut = 0,
                long discardsIn = 0, long discardsOut = 0,
                string? gateway = "192.168.1.1", params string[] dns)
            {
                var rate = packets + errorsIn > 0
                    ? Measured.Ok(Math.Round(1_000_000d * errorsIn / (packets + errorsIn), 1), DataSource.NativeApi)
                    : Measured.Missing<double>("Aucune trame n'a circulé.");

                var adapter = new NetworkAdapterInfo
                {
                    Name = name,
                    Description = name,
                    Kind = kind,
                    Status = Ok(up ? "Connectée" : "Déconnectée"),
                    IsUp = up,
                    HasApipaAddress = Ok(false),
                    IPv4Addresses = new[] { "192.168.1.20" },
                    Gateways = gateway == null ? Array.Empty<string>() : new[] { gateway },
                    DnsServers = dns.Length == 0 ? new[] { "192.168.1.1" } : dns,
                    IsPrimary = true,
                    Counters = new LinkCounters
                    {
                        PacketsReceived = Ok(packets),
                        PacketsSent = Ok(packets),
                        BytesReceived = Ok(packets * 800),
                        BytesSent = Ok(packets * 800),
                        ErrorsReceived = Ok(errorsIn),
                        ErrorsSent = Ok(errorsOut),
                        DiscardsReceived = Ok(discardsIn),
                        DiscardsSent = Ok(discardsOut),
                        ErrorsPerMillion = rate,
                    },
                };

                var adapters = new List<NetworkAdapterInfo>(_network.Adapters) { adapter };
                _network = new NetworkSnapshot
                {
                    Adapters = adapters,
                    Tests = _network.Tests,
                    Environment = _network.Environment,
                };
            }

            /// <summary>L'environnement réseau : réseau reconnu, mandataires, domaine.</summary>
            public void Environment(
                NetworkCategory category = NetworkCategory.Private,
                NetworkReach reach = NetworkReach.Internet,
                string? userProxy = null, string? machineProxy = null,
                bool domainJoined = false, string? domainName = null, string? dnsSuffix = null)
            {
                _network = new NetworkSnapshot
                {
                    Adapters = _network.Adapters,
                    Tests = _network.Tests,
                    Environment = new NetworkEnvironment
                    {
                        Locations = new[]
                        {
                            new NetworkLocation
                            {
                                Name = "Réseau de test",
                                Category = category,
                                Reach = reach,
                                Managed = category == NetworkCategory.DomainAuthenticated,
                            },
                        },
                        UserProxy = Proxy(userProxy),
                        MachineProxy = Proxy(machineProxy),
                        Domain = new DomainMembership
                        {
                            Joined = Ok(domainJoined),
                            Name = domainName == null
                                ? Measured.Missing<string>("Hors domaine.")
                                : Ok(domainName),
                            PrimaryDnsSuffix = dnsSuffix == null
                                ? Measured.Missing<string>("Aucun suffixe DNS principal.")
                                : Ok(dnsSuffix),
                            LogonServer = Measured.Missing<string>("Session locale."),
                        },
                    },
                };
            }

            private static ProxySettings Proxy(string? server) => new ProxySettings
            {
                Enabled = Ok(server != null),
                Server = server == null ? Measured.Missing<string>("Aucun mandataire.") : Ok(server),
                Bypass = Measured.Missing<string>("Aucune exception."),
                AutoConfigUrl = Measured.Missing<string>("Aucun script."),
                AutoDetect = Ok(false),
            };

            /// <summary>Une ligne de la table de routage.</summary>
            public void Route(
                string destination = "10.0.0.0", string mask = "255.0.0.0",
                string nextHop = "192.168.1.254", RouteOrigin origin = RouteOrigin.Manual,
                int metric = 25, string interfaceName = "Ethernet", int prefixLength = 8)
                => _routes.Add(new RouteEntry
                {
                    Destination = destination,
                    Mask = mask,
                    PrefixLength = prefixLength,
                    NextHop = nextHop,
                    InterfaceIndex = 9,
                    InterfaceName = interfaceName,
                    Metric = metric,
                    Origin = origin,
                });

            /// <summary>Une route de sortie, celle qui emporte tout ce qui n'est pas local.</summary>
            public void DefaultRoute(
                string nextHop = "192.168.1.254", int metric = 25, string interfaceName = "Ethernet")
                => Route("0.0.0.0", "0.0.0.0", nextHop, RouteOrigin.Configured, metric, interfaceName, 0);

            /// <summary>
            /// Le fichier hosts, décrit par ses lignes actives telles qu'elles sont écrites.
            /// </summary>
            /// <remarks>
            /// Les lignes sont passées brutes plutôt que déjà découpées : c'est la forme dans
            /// laquelle une opération devra les montrer au technicien, et la seule qui vérifie que
            /// l'analyse de la sonde et l'affichage disent la même chose.
            /// </remarks>
            public void Hosts(params string[] lines)
            {
                var entries = new List<HostsEntry>();

                for (var index = 0; index < lines.Length; index++)
                {
                    var fields = lines[index].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2) continue;

                    var names = new List<string>();
                    for (var field = 1; field < fields.Length; field++) names.Add(fields[field]);

                    entries.Add(new HostsEntry
                    {
                        Line = index + 1,
                        Address = fields[0],
                        Names = names,
                        Target = fields[0] == "0.0.0.0" ? HostsTarget.Blocked
                            : fields[0].StartsWith("127.", StringComparison.Ordinal) ? HostsTarget.Loopback
                            : HostsTarget.Redirected,
                        Raw = lines[index],
                    });
                }

                _hosts = new HostsFile
                {
                    Path = @"C:\Windows\System32\drivers\etc\hosts",
                    LineCount = lines.Length + 20,
                    Entries = entries,
                    ActiveCount = Ok(entries.Count),
                };
            }

            /// <summary>Un fichier hosts lu et sans aucune redirection.</summary>
            public void HostsUntouched()
                => _hosts = new HostsFile
                {
                    Path = @"C:\Windows\System32\drivers\etc\hosts",
                    LineCount = 21,
                    ActiveCount = Ok(0),
                };

            /// <summary>Le catalogue Winsock, par les bibliothèques qu'il désigne.</summary>
            public void Winsock(params string[] libraries)
            {
                var providers = new List<WinsockProvider>();

                for (var index = 0; index < libraries.Length; index++)
                {
                    var path = libraries[index];
                    providers.Add(new WinsockProvider
                    {
                        Index = index + 1,
                        LibraryPath = path,
                        FromWindows = path.ToLowerInvariant().StartsWith(
                            @"%systemroot%\system32\", StringComparison.Ordinal),
                    });
                }

                _winsock = new WinsockCatalog { Providers = providers, Count = Ok(providers.Count) };
            }

            /// <summary>Un serveur de noms configuré, et ce qu'il est pour la machine.</summary>
            public void DnsServer(string address, DnsServerNature nature, string? op = null)
                => _dnsServers.Add(new DnsServerUse
                {
                    Address = address,
                    InterfaceName = "Ethernet",
                    Nature = nature,
                    Operator = op,
                });

            /// <summary>L'état du partage de première génération et de NetBIOS.</summary>
            public void Sharing(
                bool smb1Installed = false, bool smb1Enabled = false,
                int netbiosDisabled = 0, int netbiosInterfaces = 3)
                => _sharing = new SharingState
                {
                    Smb1Installed = Ok(smb1Installed),
                    Smb1Enabled = Ok(smb1Enabled),
                    NetbiosDisabled = Ok(netbiosDisabled),
                    NetbiosInterfaces = Ok(netbiosInterfaces),
                };

            /// <summary>Un service de Windows, avec son état.</summary>
            public void Service(string name, string state = "Running", string startMode = "Manual")
                => _services.Add(new ServiceInfo
                {
                    Name = name,
                    DisplayName = name,
                    State = state,
                    StartMode = startMode,
                });

            /// <summary>
            /// Liaison sans fil active, avec son voisinage.
            /// </summary>
            /// <remarks>
            /// Remplace l'inventaire des cartes : une machine décrite comme portable en Wi-Fi ne
            /// doit pas conserver l'Ethernet du décor commun, sinon la règle de radio éteinte se
            /// tairait pour la mauvaise raison.
            /// </remarks>
            public void WifiLink(
                int signalPercent = 78, int rssiDbm = -62, string radio = "802.11n",
                int channel = 6, WifiBand band = WifiBand.Band24, int txMbps = 65,
                int sameChannel = 0, int overlappingChannel = 0, int neighbours = 1,
                bool radioEnabled = true, string security = "WPA2-PSK", bool wired = false)
            {
                var strongest = new List<WifiNeighbour>();
                for (var i = 0; i < Math.Min(3, sameChannel); i++)
                {
                    strongest.Add(new WifiNeighbour
                    {
                        Ssid = "Voisin " + (i + 1),
                        Bssid = "00:11:22:33:44:0" + i,
                        Channel = channel,
                        Band = band,
                        RssiDbm = -70 - i,
                    });
                }

                var wifi = new WifiInfo
                {
                    Ssid = Ok("Reseau-Test"),
                    Bssid = Ok("AA:BB:CC:DD:EE:FF"),
                    Security = Ok(security),
                    RadioType = Ok(radio),
                    Band = Ok(band),
                    Channel = Ok(channel),
                    SignalPercent = Ok(signalPercent),
                    RssiDbm = Ok(rssiDbm),
                    TxRateMbps = Ok(txMbps),
                    RxRateMbps = Ok(txMbps),
                    State = Ok(radioEnabled ? "Connectée" : "Déconnectée"),
                    RadioEnabled = Ok(radioEnabled),
                    Neighbourhood = new WifiNeighbourhood
                    {
                        Total = Ok(neighbours),
                        SameChannel = Ok(sameChannel),
                        OverlappingChannel = Ok(overlappingChannel),
                        Strongest = strongest,
                    },
                };

                var adapters = new List<NetworkAdapterInfo>
                {
                    new NetworkAdapterInfo
                    {
                        Name = "Wi-Fi",
                        Description = "Carte sans fil de test",
                        Kind = NetworkAdapterKind.WiFi,
                        Status = Ok(radioEnabled ? "Connectée" : "Déconnectée"),
                        HasApipaAddress = Ok(false),
                        IPv4Addresses = new[] { "192.168.1.42" },
                        Gateways = new[] { "192.168.1.1" },
                        DnsServers = new[] { "192.168.1.1" },
                        IsPrimary = !wired,
                        Wifi = wifi,
                    },
                };

                if (wired)
                {
                    adapters.Add(new NetworkAdapterInfo
                    {
                        Name = "Ethernet",
                        Description = "Carte filaire de test",
                        Kind = NetworkAdapterKind.Ethernet,
                        Status = Ok("Connectée"),
                        HasApipaAddress = Ok(false),
                        IPv4Addresses = new[] { "192.168.1.20" },
                        Gateways = new[] { "192.168.1.1" },
                        DnsServers = new[] { "192.168.1.1" },
                        IsPrimary = true,
                    });
                }

                _network = new NetworkSnapshot { Adapters = adapters, Tests = _network.Tests };
            }

            private static PingResult Ping(string target, bool reachable) => new PingResult
            {
                Target = target,
                Sent = Ok(4),
                Received = Ok(reachable ? 4 : 0),
                LossPercent = Ok(reachable ? 0d : 100d),
                Reachable = Ok(reachable),
                AverageMs = reachable ? Ok(4d) : Measured.Missing<double>("Aucune réponse."),
            };

            /// <summary>
            /// Zones thermiques. <paramref name="celsius"/> nul décrit une machine qui déclare
            /// une zone sans l'alimenter, cas réel, et le plus piégeux.
            /// </summary>
            public void Thermal(params double?[] celsius)
            {
                var zones = new List<ThermalZoneReading>();
                for (var i = 0; i < celsius.Length; i++)
                    zones.Add(LDI12.Collectors.Hardware.ThermalProbe.Build(
                        "TZ0" + i, ThermalSource.AcpiZone, celsius[i], DataSource.PerformanceCounter));

                _thermal = new ThermalSnapshot
                {
                    Zones = zones,
                    Limitation = LDI12.Collectors.Hardware.ThermalProbe.Limitation(elevated: false, elevationBlocked: false),
                };
            }

            /// <summary>
            /// Machine dont les capteurs matériels sont actifs : la température du processeur
            /// est alors une vraie mesure, et non une zone dont on ignore ce qu'elle suit.
            /// </summary>
            public void ThermalSensors(double cpuCelsius, double motherboardCelsius)
            {
                var zones = new List<ThermalZoneReading>();
                var fans = new List<FanReading>();

                var cpu = LDI12.Collectors.Hardware.ThermalProbe.Harvest(
                    new[]
                    {
                        new Core.Execution.HardwareSensorReading
                        {
                            Component = "Processeur de test",
                            Name = "Core (Tctl/Tdie)",
                            Kind = Core.Execution.SensorKind.Temperature,
                            IsCpu = true,
                            Value = Ok(cpuCelsius),
                        },
                        new Core.Execution.HardwareSensorReading
                        {
                            Component = "ITE IT8686E",
                            Name = "Temperature #3",
                            Kind = Core.Execution.SensorKind.Temperature,
                            IsMotherboard = true,
                            Value = Ok(motherboardCelsius),
                        },
                    },
                    zones, fans);

                _thermal = new ThermalSnapshot
                {
                    Zones = zones,
                    Fans = fans,
                    CpuPackageCelsius = cpu,
                    AdvancedSensorsUsed = true,
                };
            }

            /// <summary>
            /// Protections de la machine. Les valeurs par défaut décrivent une configuration
            /// saine : un test qui veut un défaut ne change que le paramètre qui l'intéresse.
            /// </summary>
            public void Security(
                bool antivirusActive = true, int signatureAgeDays = 1, bool firewall = true,
                bool uac = true, int uacPrompt = 5, bool remoteDesktop = false,
                bool passwordlessAdmin = false, int administrators = 1)
            {
                var accounts = new List<LocalAccountInfo>
                {
                    new LocalAccountInfo
                    {
                        Name = "Utilisateur",
                        Enabled = Ok(true),
                        IsAdministrator = Ok(administrators >= 1),
                        NoPasswordRequired = Ok(passwordlessAdmin),
                        PasswordNeverExpires = Ok(false),
                    },
                };

                for (var i = 1; i < administrators; i++)
                    accounts.Add(new LocalAccountInfo
                    {
                        Name = "Admin" + i,
                        Enabled = Ok(true),
                        IsAdministrator = Ok(true),
                        NoPasswordRequired = Ok(false),
                        PasswordNeverExpires = Ok(false),
                    });

                // Compte intégré désactivé, présent sur toute machine Windows : il ne doit
                // jamais être compté comme un compte exposé.
                accounts.Add(new LocalAccountInfo
                {
                    Name = "Invité",
                    Enabled = Ok(false),
                    IsAdministrator = Ok(false),
                    NoPasswordRequired = Ok(true),
                    PasswordNeverExpires = Ok(true),
                });

                var profiles = new List<FirewallProfileState>();
                foreach (var name in new[] { "Domaine", "Privé", "Public" })
                    profiles.Add(new FirewallProfileState { Profile = name, Enabled = Ok(firewall) });

                _security = new SecuritySnapshot
                {
                    Products = new[]
                    {
                        new SecurityProductInfo
                        {
                            Name = "Microsoft Defender",
                            Kind = SecurityProductKind.Antivirus,
                            State = Ok(antivirusActive ? ProtectionState.Enabled : ProtectionState.Disabled),
                            UpToDate = Ok(signatureAgeDays < 7),
                            IsBuiltIn = true,
                        },
                    },
                    Defender = new DefenderStatus
                    {
                        AntivirusEnabled = Ok(antivirusActive),
                        RealTimeProtectionEnabled = Ok(antivirusActive),
                        SignatureDate = Ok(DateTimeOffset.Now.AddDays(-signatureAgeDays)),
                        SignatureAgeDays = Ok(signatureAgeDays),
                        LastScan = Ok(DateTimeOffset.Now.AddDays(-1)),
                        TamperProtectionEnabled = Ok(true),
                    },
                    Firewall = profiles,
                    Uac = new UacState
                    {
                        Enabled = Ok(uac),
                        AdminPromptBehavior = Ok(uacPrompt),
                        SecureDesktop = Ok(true),
                    },
                    Accounts = accounts,
                    RemoteAccess = new RemoteAccessState
                    {
                        RemoteDesktopEnabled = Ok(remoteDesktop),
                        NetworkLevelAuthentication = Ok(true),
                        Port = Ok(3389),
                    },
                    SmartScreenEnabled = Ok(true),
                };
            }

            public void Performance(
                double cpuPercent = 20, double commitRatio = 60, int bootSeconds = 25,
                int degradedBoots = 0, long topProcessBytes = 512L * 1024 * 1024)
            {
                _performance = new PerformanceSnapshot
                {
                    Responsiveness = new ResponsivenessInfo
                    {
                        CpuUsagePercent = Ok(cpuPercent),
                        MemoryUsagePercent = Ok(55d),
                        CommittedBytes = Ok(8L * 1024 * 1024 * 1024),
                        CommitLimitBytes = Ok(32L * 1024 * 1024 * 1024),
                        CommitRatioPercent = Ok(commitRatio),
                    },
                    Boot = new BootPerformance
                    {
                        Duration = Ok(TimeSpan.FromSeconds(bootSeconds)),
                        MainPathDuration = Ok(TimeSpan.FromSeconds(bootSeconds * 0.6)),
                        MeasuredAt = Ok(DateTimeOffset.Now.AddHours(-3)),
                        DegradedBootCount = Ok(degradedBoots),
                        SampleCount = Ok(10),
                    },
                    TopByMemory = new[]
                    {
                        new ProcessUsage
                        {
                            Name = "navigateur",
                            ProcessId = 4242,
                            WorkingSetBytes = Ok(topProcessBytes),
                            CpuPercent = Ok(3d),
                        },
                    },
                    TopByCpu = Array.Empty<ProcessUsage>(),
                    ProcessCount = Ok(180),
                    Uptime = Ok(TimeSpan.FromHours(6)),
                };
            }

            public void NetworkQuality(
                double lossPercent = 0, double jitterMs = 3, int pathMtu = 1500, int hops = 4)
            {
                var route = new List<RouteHop>();
                for (var i = 1; i <= hops; i++)
                    route.Add(new RouteHop
                    {
                        Distance = i,
                        Address = Ok(i == 1 ? "192.168.1.1" : "10.0.0." + i),
                        RoundTripMs = Ok((double)i * 4),
                        IsLocal = i == 1,
                    });

                _network = new NetworkSnapshot
                {
                    Adapters = _network.Adapters,
                    Tests = new NetworkTests
                    {
                        Gateway = _network.Tests.Gateway,
                        Internet = _network.Tests.Internet,
                        DnsResolutions = _network.Tests.DnsResolutions,
                        HttpChecks = _network.Tests.HttpChecks,
                        Route = route,
                        PathMtu = Ok(pathMtu),
                        Quality = new PathQuality
                        {
                            Target = "1.1.1.1",
                            Sent = Ok(15),
                            Received = Ok((int)Math.Round(15 * (1 - (lossPercent / 100d)))),
                            LossPercent = Ok(lossPercent),
                            AverageMs = Ok(18d),
                            JitterMs = Ok(jitterMs),
                            MinMs = Ok(12d),
                            MaxMs = Ok(30d),
                        },
                    },
                };
            }

            public SystemSnapshot ToSnapshot() => new SystemSnapshot
            {
                Metadata = new SnapshotMetadata { ToolVersion = "test", RunMode = RunMode.Full, CreatedAt = _createdAt },
                Platform = new PlatformSnapshot { Windows = _windows },
                Hardware = new HardwareSnapshot
                {
                    Cpu = _cpu,
                    Memory = _memory,
                    Gpus = _gpus,
                    Motherboard = _motherboard,
                    Thermal = _thermal,
                    Displays = _displays,
                    Batteries = _batteries,
                    Errors = _hardwareErrors,
                },
                Storage = new StorageSnapshot
                {
                    PhysicalDisks = _disks, Volumes = _volumes, Maintenance = _maintenance,
                    SystemSpace = new SystemSpaceSnapshot { Consumers = _space, Volume = @"C:\" },
                },
                Windows = new WindowsSnapshot
                {
                    Install = _install,
                    Updates = _updates,
                    Events = _events,
                    Startup = _startup,
                    Tasks = _tasks,
                    Software = _software,
                    Stability = _stability,
                    Profiles = _profileInventory,
                    Time = _time,
                    SerialPorts = _serialPorts,
                    Services = _services,
                    Devices = _extraDevices,
                    SafetyNet = _safetyNet,
                    Printing = _printing,
                    Audio = _audio,
                },
                Network = new NetworkSnapshot
                {
                    Adapters = _network.Adapters,
                    Tests = _network.Tests,
                    Environment = _network.Environment,
                    Listening = _listeningInfo,
                    Paths = new NetworkPathsInfo
                    {
                        Routing = _routes.Count == 0
                            ? new RoutingTable()
                            : new RoutingTable { Routes = _routes, Count = Ok(_routes.Count) },
                        Hosts = _hosts,
                        Winsock = _winsock,
                        DnsServers = _dnsServers,
                        Sharing = _sharing,
                    },
                },
                Security = _firewallProfiles.Count == 0
                    ? _security
                    : new SecuritySnapshot
                    {
                        Products = _security.Products,
                        Defender = _security.Defender,
                        Firewall = _firewallProfiles,
                        Uac = _security.Uac,
                        RemoteAccess = _security.RemoteAccess,
                        SmartScreenEnabled = _security.SmartScreenEnabled,
                        Accounts = _security.Accounts,
                    },
                // Les réglages d'alimentation se posent après coup plutôt que dans le
                // constructeur de performances : les tests qui les règlent n'ont pas à décrire
                // une charge machine dont ils ne se servent pas.
                Performance = new PerformanceSnapshot
                {
                    Responsiveness = _performance.Responsiveness,
                    Boot = _performance.Boot,
                    TopByMemory = _performance.TopByMemory,
                    TopByCpu = _performance.TopByCpu,
                    ProcessCount = _performance.ProcessCount,
                    Uptime = _performance.Uptime,
                    Power = _power,
                },
            };
        }
    }
}
