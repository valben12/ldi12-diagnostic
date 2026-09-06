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
    /// Batteries et usure.
    /// </summary>
    /// <remarks>
    /// L'usure explique la moitié des « mon portable ne tient plus » : elle se calcule en
    /// comparant la capacité à pleine charge d'aujourd'hui à la capacité d'origine. Ces deux
    /// valeurs ne sont pas dans Win32_Battery mais dans root\WMI, d'où la double requête.
    /// Aucune batterie n'est un constat normal sur un poste fixe, pas une lacune.
    /// </remarks>
    public sealed class BatteryProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Battery,
            DisplayName = "Batterie",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(0.6),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(10);

            var batteries = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Name, Chemistry, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery",
                timeout, cancellationToken).ConfigureAwait(false);

            if (!batteries.Succeeded)
            {
                context.Draft.SetBatteries(Array.Empty<BatteryInfo>());
                return ProbeOutcome.Failed("Les batteries n'ont pas pu être énumérées : " + batteries.Reason);
            }

            if (batteries.Records.Count == 0)
            {
                context.Draft.SetBatteries(Array.Empty<BatteryInfo>());
                return ProbeOutcome.Ok("Aucune batterie, poste fixe.");
            }

            var design = await context.Wmi.QueryAsync(WmiNamespaces.Wmi,
                "SELECT DesignedCapacity FROM BatteryStaticData", timeout, cancellationToken).ConfigureAwait(false);
            var fullCharge = await context.Wmi.QueryAsync(WmiNamespaces.Wmi,
                "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", timeout, cancellationToken).ConfigureAwait(false);
            var cycles = await context.Wmi.QueryAsync(WmiNamespaces.Wmi,
                "SELECT CycleCount FROM BatteryCycleCount", timeout, cancellationToken).ConfigureAwait(false);

            var result = new List<BatteryInfo>(batteries.Records.Count);
            for (var i = 0; i < batteries.Records.Count; i++)
            {
                result.Add(ToBattery(
                    batteries.Records[i],
                    At(design, i), At(fullCharge, i), At(cycles, i),
                    design, fullCharge, cycles));
            }

            context.Draft.SetBatteries(result);

            var wear = result[0].WearPercent;
            return wear.HasValue
                ? ProbeOutcome.Ok(result.Count + " batterie(s) : usure " +
                                  wear.Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " %")
                : ProbeOutcome.Partial(result.Count + " batterie(s), l'usure n'a pas pu être calculée.");
        }

        private static WmiRecord? At(WmiQueryResult result, int index)
            => result.Succeeded && index < result.Records.Count ? result.Records[index] : null;

        private static BatteryInfo ToBattery(
            WmiRecord battery, WmiRecord? design, WmiRecord? full, WmiRecord? cycle,
            WmiQueryResult designQuery, WmiQueryResult fullQuery, WmiQueryResult cycleQuery)
        {
            var designCapacity = design != null
                ? Measure.Int64(design, "DesignedCapacity", "La capacité d'origine de la batterie")
                : Measure.FromFailure<long>(designQuery, "La capacité d'origine de la batterie");

            var fullCapacity = full != null
                ? Measure.Int64(full, "FullChargedCapacity", "La capacité actuelle à pleine charge")
                : Measure.FromFailure<long>(fullQuery, "La capacité actuelle à pleine charge");

            var wear = designCapacity.IsReliable && fullCapacity.IsReliable && designCapacity.Value > 0
                ? Measured.Ok(
                    Math.Round(Math.Max(0d, 100d * (1d - (double)fullCapacity.Value / designCapacity.Value)), 1),
                    DataSource.Inferred)
                : Measured.Missing<double>(
                    "L'usure ne peut être calculée sans la capacité d'origine et la capacité à pleine charge.");

            return new BatteryInfo
            {
                Name = Measure.Text(battery, "Name", "Le modèle de la batterie"),
                Chemistry = battery.GetInt32("Chemistry") is int chemistry
                    ? Measured.Ok(DescribeChemistry(chemistry), DataSource.Wmi)
                    : Measured.Missing<string>("La technologie de la batterie n'est pas renseignée."),
                Status = battery.GetInt32("BatteryStatus") is int status
                    ? Measured.Ok(DescribeStatus(status), DataSource.Wmi)
                    : Measured.Missing<string>("L'état de charge n'est pas renseigné."),
                ChargePercent = Measure.Int32(battery, "EstimatedChargeRemaining", "Le niveau de charge"),
                DesignCapacityMwh = designCapacity,
                FullChargeCapacityMwh = fullCapacity,
                WearPercent = wear,
                CycleCount = cycle != null
                    ? Measure.Int32(cycle, "CycleCount", "Le nombre de cycles")
                    : Measured.Missing<int>(
                        "Le nombre de cycles n'est pas exposé par la plupart des batteries : " +
                        (cycleQuery.Reason ?? "aucune donnée renvoyée.")),
            };
        }

        private static string DescribeChemistry(int code) => code switch
        {
            3 => "Plomb-acide",
            4 => "Nickel-cadmium",
            5 => "Nickel-métal hydrure",
            6 => "Lithium-ion",
            7 => "Zinc-air",
            8 => "Lithium-polymère",
            _ => "Technologie " + code,
        };

        private static string DescribeStatus(int code) => code switch
        {
            1 => "En décharge",
            2 => "Sur secteur",
            3 => "Complètement chargée",
            4 => "Faible",
            5 => "Critique",
            6 => "En charge",
            7 => "En charge (niveau élevé)",
            8 => "En charge (niveau faible)",
            9 => "En charge (niveau critique)",
            11 => "Partiellement chargée",
            _ => "État " + code,
        };
    }
}
