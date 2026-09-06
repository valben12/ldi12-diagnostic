using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Identité et charge du processeur.
    /// </summary>
    /// <remarks>
    /// WMI porte l'essentiel, mais le registre sert de filet : sur une machine au dépôt WMI
    /// endommagé (cas courant en dépannage) <c>HARDWARE\DESCRIPTION\System\CentralProcessor</c>
    /// donne toujours le modèle, le fabricant et la fréquence de base. Mieux vaut un module
    /// partiel qu'un écran vide.
    /// </remarks>
    public sealed class CpuProbe : IDiagnosticProbe
    {
        private static readonly TimeSpan UsageSampleWindow = TimeSpan.FromMilliseconds(600);

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Cpu,
            DisplayName = "Processeur",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(1.5),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            // La mesure de charge occupe une fenêtre de 600 ms : on la lance en parallèle de la
            // requête WMI plutôt que de l'ajouter à la durée totale.
            var usageTask = context.Native.ReadCpuUsagePercentAsync(UsageSampleWindow, cancellationToken);

            var query = await context.Wmi.QueryAsync(
                WmiNamespaces.CimV2,
                "SELECT Manufacturer, Name, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, " +
                "MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled " +
                "FROM Win32_Processor",
                TimeSpan.FromSeconds(12),
                cancellationToken).ConfigureAwait(false);

            var usage = await usageTask.ConfigureAwait(false);
            var record = query.First;

            var cpu = query.Succeeded && record != null
                ? FromWmi(record, query, usage, context)
                : FromRegistry(context, usage, query.Reason);

            context.Draft.SetCpu(cpu);

            if (!query.Succeeded)
            {
                return ProbeOutcome.Partial(
                    "Informations lues dans le registre : le service WMI n'a pas répondu. " +
                    "Le nombre de cœurs physiques et la fréquence maximale ne sont pas disponibles.");
            }

            return cpu.Model.IsReliable
                ? ProbeOutcome.Ok(cpu.Model.Value)
                : ProbeOutcome.Partial("Le modèle du processeur n'a pas pu être identifié.");
        }

        private static CpuInfo FromWmi(WmiRecord record, WmiQueryResult query, Measured<double> usage, ProbeContext context)
        {
            var maxClock = Measure.Int32(record, "MaxClockSpeed", "La fréquence maximale");
            var currentClock = Measure.Int32(record, "CurrentClockSpeed", "La fréquence courante");

            return new CpuInfo
            {
                Manufacturer = Measure.Text(record, "Manufacturer", "Le fabricant du processeur"),
                Model = Measure.Text(record, "Name", "Le modèle du processeur"),
                Socket = Measure.Text(record, "SocketDesignation", "Le socket"),
                Architecture = Measured.Ok(
                    Core.Platform.WindowsProfile.ArchitectureLabel(context.Platform.Profile.NativeArchitecture),
                    DataSource.NativeApi),
                PhysicalCores = Measure.Int32(record, "NumberOfCores", "Le nombre de cœurs physiques"),
                LogicalCores = Measure.Int32(record, "NumberOfLogicalProcessors", "Le nombre de threads"),
                BaseClockMhz = maxClock,
                MaxClockMhz = maxClock,
                CurrentClockMhz = currentClock,

                // WMI donne la taille des caches en Ko, contrairement à la plupart des autres tailles.
                L2CacheKb = Measure.Int32(record, "L2CacheSize", "Le cache L2"),
                L3CacheKb = Measure.Int32(record, "L3CacheSize", "Le cache L3"),
                VirtualizationEnabled = Measure.Bool(record, "VirtualizationFirmwareEnabled",
                    "L'état de la virtualisation matérielle"),
                UsagePercent = usage,
                TemperatureCelsius = TemperatureUnavailable(),
                ThrottlingDetected = ThrottlingUnavailable(),
            };
        }

        private static CpuInfo FromRegistry(ProbeContext context, Measured<double> usage, string? wmiReason)
        {
            var reason = wmiReason ?? "Le service WMI n'a pas répondu.";
            var registry = context.Registry;

            var name = registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.SystemProcessor, "ProcessorNameString");
            var vendor = registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.SystemProcessor, "VendorIdentifier");
            var megahertz = registry.ReadInt32(RegistryHive.LocalMachine, RegistryPaths.SystemProcessor, "~MHz");

            return new CpuInfo
            {
                Manufacturer = Measure.Registry(vendor, "Le fabricant du processeur"),
                Model = Measure.Registry(name, "Le modèle du processeur"),
                Socket = Measured.Missing<string>("Le socket n'est lisible que par WMI. " + reason),
                Architecture = Measured.Ok(
                    Core.Platform.WindowsProfile.ArchitectureLabel(context.Platform.Profile.NativeArchitecture),
                    DataSource.NativeApi),
                PhysicalCores = Measured.Missing<int>(
                    "Le nombre de cœurs physiques n'est lisible que par WMI. " + reason),

                // Environment.ProcessorCount vient du noyau, pas de WMI : fiable même ici.
                LogicalCores = Measured.Ok(Environment.ProcessorCount, DataSource.NativeApi),
                BaseClockMhz = megahertz.HasValue
                    ? Measured.Ok(megahertz.Value, DataSource.Registry)
                    : Measured.Missing<int>("La fréquence de base n'est pas renseignée."),
                MaxClockMhz = Measured.Missing<int>("La fréquence maximale n'est lisible que par WMI. " + reason),
                CurrentClockMhz = Measured.Missing<int>("La fréquence courante n'est lisible que par WMI. " + reason),
                L2CacheKb = Measured.Missing<int>("Le cache L2 n'est lisible que par WMI. " + reason),
                L3CacheKb = Measured.Missing<int>("Le cache L3 n'est lisible que par WMI. " + reason),
                VirtualizationEnabled = Measured.Missing<bool>(
                    "L'état de la virtualisation matérielle n'est lisible que par WMI. " + reason),
                UsagePercent = usage,
                TemperatureCelsius = TemperatureUnavailable(),
                ThrottlingDetected = ThrottlingUnavailable(),
            };
        }

        /// <summary>
        /// Position assumée : la température du processeur n'est lisible que par un pilote noyau.
        /// Tant que le technicien n'a pas activé explicitement les capteurs avancés, on préfère
        /// dire « je ne sais pas » plutôt qu'installer un pilote chez le client.
        /// </summary>
        private static Measured<double> TemperatureUnavailable()
            => Measured.Missing<double>(
                "Windows n'expose pas la température du processeur : ni le capteur DTS d'Intel " +
                "ni le Tctl d'AMD ne sont lisibles sans pilote noyau. Les zones thermiques ACPI " +
                "relevées par le module Températures sont ce que cette machine sait donner.");

        private static Measured<bool> ThrottlingUnavailable()
            => Measured.Missing<bool>(
                "Le bridage thermique ne se distingue de la gestion normale d'énergie qu'avec les " +
                "capteurs matériels avancés, désactivés par défaut.");
    }
}
