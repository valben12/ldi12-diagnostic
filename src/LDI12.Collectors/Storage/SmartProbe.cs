using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Storage
{
    /// <summary>
    /// État de santé SMART de chaque disque.
    /// </summary>
    /// <remarks>
    /// C'est le module qui justifie à lui seul l'existence de l'outil : un disque qui annonce sa
    /// panne des semaines à l'avance permet de sauvegarder avant de perdre les données. D'où le
    /// soin apporté à ne jamais présenter un « aucun problème » qui viendrait en réalité d'une
    /// lecture impossible.
    /// </remarks>
    public sealed class SmartProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Smart,
            DisplayName = "Santé des disques (SMART)",
            Category = DiagnosticCategory.Storage,
            EstimatedDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(40),
            Isolation = IsolationMode.SeparateProcess,
            DependsOn = new[] { ProbeIds.PhysicalDisks },
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var disks = context.Draft.PhysicalDisks;
            if (disks.Count == 0)
                return Task.FromResult(ProbeOutcome.Skipped("Aucun disque physique à analyser."));

            var nvmeAvailable = context.Platform.Features.IsAvailable(FeatureId.SmartNvme);
            var updated = new List<PhysicalDiskInfo>(disks.Count);

            int read = 0, elevationNeeded = 0, unsupported = 0, failing = 0, warning = 0;

            foreach (var disk in disks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isNvme = disk.MediaType.Or(StorageMediaType.Unknown) == StorageMediaType.Nvme
                             || disk.BusType.Or(StorageBusType.Unknown) == StorageBusType.Nvme;

                var smart = isNvme
                    ? ReadNvme(context, disk, nvmeAvailable)
                    : context.Storage.ReadAtaSmart(disk.Index);

                if (smart.HasValue)
                {
                    read++;
                    var status = smart.Value.OverallStatus.Or(SmartOverallStatus.Unknown);
                    if (status == SmartOverallStatus.Failing) failing++;
                    else if (status == SmartOverallStatus.Warning) warning++;
                }
                else if (smart.Availability == Availability.RequiresElevation) elevationNeeded++;
                else unsupported++;

                updated.Add(WithSmart(disk, smart));
            }

            context.Draft.SetPhysicalDisks(updated);

            if (failing > 0)
            {
                return Task.FromResult(ProbeOutcome.Ok(
                    failing + " disque(s) signalent une défaillance imminente, voir le détail."));
            }

            if (read == 0)
            {
                return Task.FromResult(elevationNeeded > 0
                    ? ProbeOutcome.ElevationRequired(
                        "Aucun disque n'a rendu ses données SMART, et certains ont refusé faute " +
                        "de privilèges administrateur.")
                    : ProbeOutcome.Partial(
                        "Aucun disque n'expose ses données SMART sur cette machine."));
            }

            var summary = read + " disque(s) analysé(s)";
            if (warning > 0) summary += ", " + warning + " à surveiller";
            if (elevationNeeded > 0) summary += ", " + elevationNeeded + " nécessitant une élévation";
            if (unsupported > 0) summary += ", " + unsupported + " sans SMART exposé";

            return Task.FromResult(elevationNeeded > 0 || unsupported > 0
                ? ProbeOutcome.Partial(summary)
                : ProbeOutcome.Ok(summary));
        }

        private static Measured<SmartData> ReadNvme(ProbeContext context, PhysicalDiskInfo disk, bool nvmeAvailable)
        {
            if (nvmeAvailable) return context.Storage.ReadNvmeSmart(disk.Index);

            // Sous Windows 7 et 8.1, aucune API ne donne accès au journal de santé NVMe.
            // On le dit, plutôt que de laisser croire que le disque va bien.
            return Measured.Missing<SmartData>(
                context.Platform.Features.Get(FeatureId.SmartNvme).Reason);
        }

        /// <summary>
        /// Reconstruit le disque avec ses données SMART. Quand la lecture a échoué, chaque
        /// indicateur porte la raison de l'échec plutôt qu'un zéro trompeur.
        /// </summary>
        private static PhysicalDiskInfo WithSmart(PhysicalDiskInfo disk, Measured<SmartData> smart)
        {
            var data = smart.HasValue
                ? smart.Value
                : new SmartData
                {
                    OverallStatus = smart.Availability == Availability.RequiresElevation
                        ? Measured.NeedsElevation<SmartOverallStatus>("lecture SMART")
                        : Measured.Missing<SmartOverallStatus>(smart.Reason ?? "SMART indisponible."),
                    PowerOnHours = Propagate<long>(smart),
                    PowerCycles = Propagate<long>(smart),
                    TemperatureCelsius = Propagate<int>(smart),
                    ReallocatedSectors = Propagate<long>(smart),
                    PendingSectors = Propagate<long>(smart),
                    UncorrectableErrors = Propagate<long>(smart),
                    WearPercent = Propagate<double>(smart),
                    TotalBytesWritten = Propagate<long>(smart),
                };

            // La recopie vit dans le modèle : une sonde qui la referait à la main perdrait le
            // prochain champ ajouté, comme celle-ci a perdu la génération de liaison.
            return disk.WithSmart(data);
        }

        private static Measured<T> Propagate<T>(Measured<SmartData> smart)
            => smart.Availability == Availability.RequiresElevation
                ? Measured.NeedsElevation<T>("lecture SMART")
                : Measured.Missing<T>(smart.Reason ?? "SMART indisponible.");
    }
}
