using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Performance
{
    /// <summary>
    /// Charge de la machine au moment de l'analyse : processeur, mémoire, processus les plus
    /// gourmands.
    /// </summary>
    /// <remarks>
    /// Tout ce que cette sonde mesure est un instantané, pris sur une machine où le technicien
    /// vient d'ouvrir un logiciel de diagnostic. Aucune de ces valeurs ne peut donc fonder à elle
    /// seule un constat de lenteur : elles servent à <b>expliquer</b> une lenteur constatée par
    /// ailleurs, ou à écarter une cause. Les règles qui s'en servent le déclarent en confiance
    /// moyenne, et le rapport dit toujours que la mesure est ponctuelle.
    /// <para>
    /// La mesure qui échappe à cette limite est la mémoire validée : elle décrit ce que la
    /// machine réclame, indépendamment de ce qu'elle fait à l'instant.
    /// </para>
    /// </remarks>
    public sealed class ResponsivenessProbe : IDiagnosticProbe
    {
        /// <summary>Fenêtre d'échantillonnage. Assez longue pour être exploitable, assez courte pour ne pas peser.</summary>
        private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(700);

        /// <summary>Au-delà, la liste cesse d'aider et devient un gestionnaire des tâches.</summary>
        private const int TopCount = 8;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Responsiveness,
            DisplayName = "Charge de la machine",
            Category = DiagnosticCategory.Performance,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            // Une seule fenêtre d'échantillonnage pour les deux mesures : les lancer l'une après
            // l'autre coûterait le double de temps pour décrire le même instant.
            var cpuTask = context.Native.ReadCpuUsagePercentAsync(SampleWindow, cancellationToken);
            var processTask = context.Native.ReadProcessUsageAsync(SampleWindow, cancellationToken);

            await Task.WhenAll(cpuTask, processTask).ConfigureAwait(false);

            var cpu = await cpuTask.ConfigureAwait(false);
            var processes = await processTask.ConfigureAwait(false);
            var memory = context.Native.ReadMemoryStatus();
            var uptime = context.Native.ReadUptime();

            var responsiveness = Build(cpu, memory);
            var byMemory = Top(processes, TopCount, ByMemory);
            var byCpu = Top(processes, TopCount, ByCpu);

            context.Draft.SetResponsiveness(
                responsiveness, byMemory, byCpu,
                processes.HasValue
                    ? Measured.Ok(processes.Value.Count, DataSource.NativeApi)
                    : Measured.Missing<int>(
                        processes.Reason ?? "Les processus n'ont pas pu être énumérés.", DataSource.NativeApi),
                uptime);

            return Conclude(responsiveness, processes);
        }

        private static ProbeOutcome Conclude(
            ResponsivenessInfo responsiveness, Measured<IReadOnlyList<ProcessUsage>> processes)
        {
            if (!processes.HasValue)
                return ProbeOutcome.Partial(
                    processes.Reason ?? "La liste des processus n'a pas pu être établie.");

            var commit = responsiveness.CommitRatioPercent;
            if (commit.IsReliable && commit.Value >= 100)
                return ProbeOutcome.Ok(
                    "La machine réclame plus de mémoire qu'elle n'en a : elle s'appuie sur le fichier d'échange.");

            var cpu = responsiveness.CpuUsagePercent;
            if (cpu.IsReliable && cpu.Value >= 80)
                return ProbeOutcome.Ok("Processeur fortement sollicité au moment de l'analyse.");

            return processes.Availability == Availability.Partial
                ? ProbeOutcome.Partial(processes.Reason!)
                : ProbeOutcome.Ok("Charge relevée sur " + processes.Value.Count + " processus.");
        }

        private static ResponsivenessInfo Build(Measured<double> cpu, Measured<Core.Execution.MemoryStatus> memory)
        {
            if (!memory.HasValue)
            {
                var reason = memory.Reason ?? "L'état de la mémoire n'a pas pu être lu.";
                return new ResponsivenessInfo
                {
                    CpuUsagePercent = cpu,
                    MemoryUsagePercent = Measured.Missing<double>(reason, DataSource.NativeApi),
                    CommittedBytes = Measured.Missing<long>(reason, DataSource.NativeApi),
                    CommitLimitBytes = Measured.Missing<long>(reason, DataSource.NativeApi),
                    CommitRatioPercent = Measured.Missing<double>(reason, DataSource.NativeApi),
                };
            }

            var status = memory.Value;

            // Rapport à la mémoire vive installée, et non à la limite de validation : c'est ce
            // qui dit si la machine tient sa charge courante sans fichier d'échange.
            var ratio = status.TotalBytes > 0
                ? Measured.Ok(Math.Round(100d * status.CommittedBytes / status.TotalBytes, 1), DataSource.NativeApi)
                : Measured.Missing<double>(
                    "La mémoire installée n'est pas connue : le rapport de validation est incalculable.",
                    DataSource.NativeApi);

            return new ResponsivenessInfo
            {
                CpuUsagePercent = cpu,
                MemoryUsagePercent = Measured.Ok(status.UsagePercent, DataSource.NativeApi),
                CommittedBytes = Measured.Ok(status.CommittedBytes, DataSource.NativeApi),
                CommitLimitBytes = Measured.Ok(status.CommitLimitBytes, DataSource.NativeApi),
                CommitRatioPercent = ratio,
            };
        }

        /// <summary>
        /// Les <paramref name="count"/> processus en tête du classement demandé.
        /// </summary>
        /// <remarks>
        /// Tri par insertion sur une liste bornée plutôt que tri complet : quelques centaines de
        /// processus pour en garder huit, autant ne pas trier le reste.
        /// </remarks>
        private static IReadOnlyList<ProcessUsage> Top(
            Measured<IReadOnlyList<ProcessUsage>> processes, int count, Comparison<ProcessUsage> comparison)
        {
            if (!processes.HasValue) return Array.Empty<ProcessUsage>();

            var sorted = new List<ProcessUsage>(processes.Value);
            sorted.Sort(comparison);

            if (sorted.Count > count) sorted.RemoveRange(count, sorted.Count - count);
            return sorted;
        }

        private static int ByMemory(ProcessUsage a, ProcessUsage b)
            => b.WorkingSetBytes.Or(0).CompareTo(a.WorkingSetBytes.Or(0));

        /// <summary>
        /// Décroissant sur la charge, la mémoire départageant les ex æquo.
        /// </summary>
        /// <remarks>
        /// Un processus dont la charge n'a pas pu être lue vaut zéro ici : il ne remonte pas dans
        /// le classement faute de mesure, et son absence de valeur reste visible à l'affichage.
        /// </remarks>
        private static int ByCpu(ProcessUsage a, ProcessUsage b)
        {
            var comparison = b.CpuPercent.Or(0d).CompareTo(a.CpuPercent.Or(0d));
            return comparison != 0 ? comparison : ByMemory(a, b);
        }
    }
}
