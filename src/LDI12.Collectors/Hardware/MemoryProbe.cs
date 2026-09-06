using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Occupation mémoire et inventaire des modules installés.
    /// </summary>
    /// <remarks>
    /// Les totaux viennent de l'API native : instantanés, fiables, insensibles à l'état de WMI.
    /// Seul l'inventaire physique (référence, fréquence, emplacement) passe par WMI, faute
    /// d'alternative, et son échec ne prive donc pas du taux d'occupation, qui est ce qui
    /// explique une lenteur.
    /// </remarks>
    public sealed class MemoryProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Memory,
            DisplayName = "Mémoire vive",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(0.8),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var status = context.Native.ReadMemoryStatus();

            var modulesQuery = await context.Wmi.QueryAsync(
                WmiNamespaces.CimV2,
                "SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, " +
                "PartNumber, SerialNumber, MemoryType, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory",
                TimeSpan.FromSeconds(12),
                cancellationToken).ConfigureAwait(false);

            var arrayQuery = await context.Wmi.QueryAsync(
                WmiNamespaces.CimV2,
                "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray",
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);

            var modules = new List<MemoryModule>();
            if (modulesQuery.Succeeded)
            {
                foreach (var record in modulesQuery.Records) modules.Add(ToModule(record, context));
            }

            var slotsTotal = arrayQuery.Succeeded && arrayQuery.First != null
                ? Measure.Int32(arrayQuery.First, "MemoryDevices", "Le nombre total d'emplacements mémoire")
                : Measure.FromFailure<int>(arrayQuery, "Le nombre total d'emplacements mémoire");

            context.Draft.SetMemory(new MemoryInfo
            {
                TotalBytes = status.Map(s => s.TotalBytes),
                AvailableBytes = status.Map(s => s.AvailableBytes),
                UsagePercent = status.Map(s => s.UsagePercent),
                CommittedBytes = status.Map(s => s.CommittedBytes),
                CommitLimitBytes = status.Map(s => s.CommitLimitBytes),
                SlotsTotal = slotsTotal,
                SlotsUsed = modulesQuery.Succeeded
                    ? Measured.Ok(modules.Count, DataSource.Wmi)
                    : Measure.FromFailure<int>(modulesQuery, "Le nombre d'emplacements occupés"),
                Modules = modules,
            });

            if (!status.HasValue)
                return ProbeOutcome.Failed(status.Reason ?? "L'état de la mémoire n'a pas pu être lu.");

            if (!modulesQuery.Succeeded)
            {
                return ProbeOutcome.Partial(
                    "Occupation mémoire relevée, mais l'inventaire des modules est indisponible : " +
                    modulesQuery.Reason);
            }

            var totalGb = Math.Round(status.Value.TotalBytes / (1024d * 1024 * 1024), 1);
            return ProbeOutcome.Ok(
                totalGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " Go : " +
                modules.Count + " module(s)");
        }

        private static MemoryModule ToModule(WmiRecord record, ProbeContext context)
        {
            // SMBIOSMemoryType n'existe qu'à partir de Windows 8 ; MemoryType, plus ancien,
            // ne connaît pas la DDR4 et renvoie 0. On préfère donc le premier quand il est là.
            var smbiosType = record.GetInt32("SMBIOSMemoryType");
            var legacyType = record.GetInt32("MemoryType");
            var typeCode = smbiosType.HasValue && smbiosType.Value > 0 ? smbiosType : legacyType;

            var configured = record.GetInt32("ConfiguredClockSpeed");

            return new MemoryModule
            {
                BankLabel = Measure.Text(record, "BankLabel", "La banque mémoire"),
                DeviceLocator = Measure.Text(record, "DeviceLocator", "L'emplacement"),
                CapacityBytes = Measure.Bytes(record, "Capacity", "La capacité du module"),
                SpeedMhz = Measure.Int32(record, "Speed", "La fréquence nominale"),
                ConfiguredSpeedMhz = configured.HasValue && configured.Value > 0
                    ? Measured.Ok(configured.Value, DataSource.Wmi)
                    : Measured.Missing<int>(
                        context.Platform.Profile.Build < 9200
                            ? "La fréquence réellement appliquée n'est pas exposée avant Windows 8."
                            : "La fréquence réellement appliquée n'est pas renseignée par cette carte mère."),
                Manufacturer = Measure.Text(record, "Manufacturer", "Le fabricant du module"),
                PartNumber = Measure.Text(record, "PartNumber", "La référence du module"),
                SerialNumber = Measure.Text(record, "SerialNumber", "Le numéro de série du module"),
                MemoryType = typeCode.HasValue && typeCode.Value > 0
                    ? Measured.Ok(DescribeMemoryType(typeCode.Value), DataSource.Wmi)
                    : Measured.Missing<string>("Le type de mémoire n'est pas renseigné par cette carte mère."),
                FormFactor = record.GetInt32("FormFactor") is int form && form > 0
                    ? Measured.Ok(DescribeFormFactor(form), DataSource.Wmi)
                    : Measured.Missing<string>("Le format du module n'est pas renseigné."),
            };
        }

        private static string DescribeMemoryType(int code) => code switch
        {
            20 => "DDR",
            21 => "DDR2",
            22 => "DDR2 FB-DIMM",
            24 => "DDR3",
            26 => "DDR4",
            30 => "LPDDR",
            31 => "LPDDR2",
            32 => "LPDDR3",
            33 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => "Type " + code,
        };

        private static string DescribeFormFactor(int code) => code switch
        {
            8 => "DIMM",
            12 => "SODIMM",
            13 => "SRIMM",
            _ => "Format " + code,
        };
    }
}
