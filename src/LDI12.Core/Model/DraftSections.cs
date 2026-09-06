using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Une section du relevé, avec ce qu'une sonde y a déposé.
    /// </summary>
    /// <remarks>
    /// Sert à faire franchir une frontière de processus au résultat d'une sonde isolée : le
    /// relevé lui-même n'est pas transportable (il est mutable et protégé par un verrou) mais
    /// la liste de ce qu'une sonde y a écrit, si.
    /// </remarks>
    public sealed class DraftSection
    {
        public DraftSection(string name, object value)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Name { get; }

        public object Value { get; }
    }

    /// <summary>
    /// Noms et types des sections du relevé.
    /// </summary>
    /// <remarks>
    /// <b>Une table, et un test qui la parcourt.</b> C'est la troisième fois dans ce projet qu'une
    /// recopie champ par champ perd silencieusement une valeur : la santé SMART à la phase 11,
    /// le champ d'identité ATA ensuite. Ici, une section oubliée ferait disparaître le résultat
    /// d'une sonde isolée sans erreur ni journal. Un test d'aller-retour écrit toutes les
    /// sections, les rejoue dans un relevé neuf et compare : un oubli fait échouer le test au
    /// lieu de vider un écran.
    /// <para>
    /// Les types sont concrets : <c>List&lt;T&gt;</c> et non <c>IReadOnlyList&lt;T&gt;</c> : parce
    /// qu'ils servent à reconstruire la valeur à partir de sa forme sérialisée.
    /// </para>
    /// </remarks>
    public static class DraftSections
    {
        public const string Cpu = "cpu";
        public const string Memory = "memory";
        public const string Gpus = "gpus";
        public const string Motherboard = "motherboard";
        public const string Batteries = "batteries";
        public const string Thermal = "thermal";
        public const string Displays = "displays";
        public const string HardwareErrors = "hardware-errors";

        public const string PhysicalDisks = "physical-disks";
        public const string SystemSpace = "system-space";
        public const string Volumes = "volumes";

        public const string Install = "install";
        public const string SystemFiles = "system-files";
        public const string Updates = "updates";
        public const string Events = "events";
        public const string Services = "services";
        public const string Devices = "devices";
        public const string Drivers = "drivers";
        public const string Startup = "startup";
        public const string Tasks = "tasks";
        public const string Software = "software";
        public const string Stability = "stability";
        public const string SafetyNet = "safety-net";
        public const string Profiles = "profiles";
        public const string SystemTime = "system-time";
        public const string SerialPorts = "serial-ports";
        public const string Printing = "printing";
        public const string Audio = "audio";

        public const string NetworkAdapters = "network-adapters";
        public const string NetworkTests = "network-tests";
        public const string NetworkEnvironment = "network-environment";
        public const string ListeningPorts = "listening-ports";
        public const string NetworkPaths = "network-paths";

        public const string SecurityProducts = "security-products";
        public const string Defender = "defender";
        public const string Firewall = "firewall";
        public const string Uac = "uac";
        public const string RemoteAccess = "remote-access";
        public const string SmartScreen = "smart-screen";
        public const string LocalAccounts = "local-accounts";

        public const string Responsiveness = "responsiveness";
        public const string TopByMemory = "top-by-memory";
        public const string TopByCpu = "top-by-cpu";
        public const string ProcessCount = "process-count";
        public const string Uptime = "uptime";
        public const string BootPerformance = "boot-performance";
        public const string Power = "power";

        public const string StorageMaintenance = "storage-maintenance";

        private static readonly Dictionary<string, Type> Map = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { Cpu, typeof(CpuInfo) },
            { Memory, typeof(MemoryInfo) },
            { Gpus, typeof(List<GpuInfo>) },
            { Motherboard, typeof(MotherboardInfo) },
            { Batteries, typeof(List<BatteryInfo>) },
            { Thermal, typeof(ThermalSnapshot) },
            { Displays, typeof(DisplaySnapshot) },
            { HardwareErrors, typeof(HardwareErrorsInfo) },

            { PhysicalDisks, typeof(List<PhysicalDiskInfo>) },
            { SystemSpace, typeof(SystemSpaceSnapshot) },
            { Volumes, typeof(List<VolumeInfo>) },

            { Install, typeof(WindowsInstallInfo) },
            { SystemFiles, typeof(SystemFilesInfo) },
            { Updates, typeof(UpdatesInfo) },
            { Events, typeof(EventsInfo) },
            { Services, typeof(List<ServiceInfo>) },
            { Devices, typeof(List<DeviceInfo>) },
            { Drivers, typeof(List<DriverInfo>) },
            { Startup, typeof(List<StartupItem>) },
            { Tasks, typeof(ScheduledTaskInventory) },
            { Software, typeof(SoftwareInventory) },
            { Stability, typeof(StabilityInfo) },
            { SafetyNet, typeof(SafetyNet) },
            { Profiles, typeof(ProfileInventory) },
            { SystemTime, typeof(SystemTimeInfo) },
            { SerialPorts, typeof(SerialPortsInfo) },
            { Printing, typeof(PrintingInfo) },
            { Audio, typeof(AudioInfo) },

            { NetworkAdapters, typeof(List<NetworkAdapterInfo>) },
            { NetworkTests, typeof(NetworkTests) },
            { NetworkEnvironment, typeof(NetworkEnvironment) },
            { ListeningPorts, typeof(ListeningPortsInfo) },
            { NetworkPaths, typeof(NetworkPathsInfo) },

            { SecurityProducts, typeof(List<SecurityProductInfo>) },
            { Defender, typeof(DefenderStatus) },
            { Firewall, typeof(List<FirewallProfileState>) },
            { Uac, typeof(UacState) },
            { RemoteAccess, typeof(RemoteAccessState) },
            { SmartScreen, typeof(Measured<bool>) },
            { LocalAccounts, typeof(List<LocalAccountInfo>) },

            { Responsiveness, typeof(ResponsivenessInfo) },
            { TopByMemory, typeof(List<ProcessUsage>) },
            { TopByCpu, typeof(List<ProcessUsage>) },
            { ProcessCount, typeof(Measured<int>) },
            { Uptime, typeof(Measured<TimeSpan>) },
            { BootPerformance, typeof(BootPerformance) },
            { Power, typeof(PowerConfiguration) },
            { StorageMaintenance, typeof(StorageMaintenance) },
        };

        public static IReadOnlyCollection<string> Names => Map.Keys;

        /// <summary>Type sous lequel reconstruire la valeur d'une section, ou <c>null</c> si inconnue.</summary>
        public static Type? TypeOf(string name)
            => name != null && Map.TryGetValue(name, out var type) ? type : null;
    }
}
