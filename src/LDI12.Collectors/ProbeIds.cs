namespace LDI12.Collectors
{
    /// <summary>
    /// Identifiants stables des sondes. Ils apparaissent dans les rapports et les journaux :
    /// ils ne changent jamais, même si le libellé affiché évolue.
    /// </summary>
    public static class ProbeIds
    {
        public const string Cpu = "HW-CPU";
        public const string Memory = "HW-RAM";
        public const string Gpu = "HW-GPU";
        public const string Motherboard = "HW-MB";
        public const string Battery = "HW-BAT";
        public const string Thermal = "HW-THERMAL";
        public const string Displays = "HW-DISPLAYS";
        public const string HardwareErrors = "HW-ERRORS";

        public const string PhysicalDisks = "STO-DISKS";
        public const string Volumes = "STO-VOLUMES";
        public const string Smart = "STO-SMART";
        public const string SystemSpace = "STO-SPACE";

        public const string WindowsInstall = "WIN-INSTALL";
        public const string SystemFiles = "WIN-SFC";
        public const string Updates = "WIN-UPDATES";
        public const string Services = "WIN-SERVICES";
        public const string Events = "WIN-EVENTS";
        public const string Devices = "WIN-DEVICES";
        public const string Drivers = "WIN-DRIVERS";
        public const string Startup = "WIN-STARTUP";
        public const string Software = "WIN-SOFTWARE";
        public const string Stability = "WIN-STABILITY";
        public const string UserProfiles = "WIN-PROFILES";
        public const string SystemTime = "WIN-TIME";
        public const string SerialPorts = "HW-SERIAL";
        public const string SafetyNet = "WIN-SAFETY";
        public const string Printing = "WIN-PRINT";
        public const string Audio = "WIN-AUDIO";

        public const string NetworkAdapters = "NET-ADAPTERS";
        public const string NetworkTests = "NET-TESTS";
        public const string NetworkPath = "NET-PATH";
        public const string NetworkEnvironment = "NET-ENV";
        public const string ListeningPorts = "NET-PORTS";
        public const string NetworkRouting = "NET-ROUTING";

        public const string SecurityProducts = "SEC-PRODUCTS";
        public const string SecurityPolicy = "SEC-POLICY";
        public const string LocalAccounts = "SEC-ACCOUNTS";

        public const string Responsiveness = "PRF-LOAD";
        public const string BootPerformance = "PRF-BOOT";
        public const string Power = "PRF-POWER";
    }
}
