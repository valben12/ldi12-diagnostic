using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Engine.Orchestration
{
    public sealed class DiagnosticProgress
    {
        public int Completed { get; init; }
        public int Total { get; init; }

        /// <summary>Module qui vient de s'achever. Vide sur un rapport de démarrage.</summary>
        public string CurrentProbe { get; init; } = string.Empty;

        /// <summary>
        /// Module qui vient de démarrer. Renseigné sur un rapport de démarrage uniquement.
        /// </summary>
        /// <remarks>
        /// Sans cette information, une interface voulant montrer ce qui travaille devrait le
        /// deviner à partir de l'ordre du catalogue et du plafond de parallélisme : c'est-à-dire
        /// afficher comme certain quelque chose qu'elle infère. Le coût est d'un rapport de plus
        /// par module ; la mesure devient exacte.
        /// </remarks>
        public string? StartedProbe { get; init; }

        public double Fraction => Total == 0 ? 1d : (double)Completed / Total;
    }

    public sealed class CollectionResult
    {
        public IReadOnlyList<ModuleReport> Reports { get; init; } = Array.Empty<ModuleReport>();
        public long DurationMs { get; init; }
    }

    /// <summary>
    /// Exécute un ensemble de sondes avec un parallélisme borné, en respectant les dépendances
    /// qu'elles déclarent.
    /// </summary>
    /// <remarks>
    /// Le plafond de quatre tâches simultanées est délibéré : au-delà, sur un vieux biprocesseur
    /// dont le disque mécanique est déjà saturé, la parallélisation ralentit l'ensemble et fausse
    /// les mesures de performance que l'on cherche justement à relever.
    /// <para>
    /// L'ordonnancement procède par vagues : toutes les sondes dont les dépendances sont
    /// satisfaites démarrent ensemble, puis la vague suivante. C'est ce qui garantit que la
    /// lecture SMART voit bien la liste des disques établie juste avant.
    /// </para>
    /// </remarks>
    public sealed class DiagnosticRunner
    {
        private const string Category = "Engine.Runner";
        private const int MaxConcurrency = 4;

        private readonly ProbeExecutionPolicy _policy;
        private readonly IScopedLogger _log;

        public DiagnosticRunner(ProbeExecutionPolicy policy, ILdiLogger logger)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        /// <param name="selection">
        /// Modules retenus par le technicien. Nul : tout ce que le mode autorise.
        /// </param>
        public async Task<CollectionResult> RunAsync(
            IReadOnlyList<IDiagnosticProbe> probes,
            ProbeContext context,
            RunMode mode,
            IProgress<DiagnosticProgress>? progress,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? selection = null)
        {
            if (probes == null) throw new ArgumentNullException(nameof(probes));

            var stopwatch = Stopwatch.StartNew();

            var chosen = selection == null
                ? null
                : new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase);

            var pending = new List<IDiagnosticProbe>(probes.Count);
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reports = new List<ModuleReport>(probes.Count);

            foreach (var probe in probes)
            {
                var descriptor = probe.Descriptor;

                // Un module écarté est rapporté comme tel, et non passé sous silence : « pourquoi
                // cette information manque » est une information, et c'est le contrat du compte
                // rendu de module depuis la phase 0. Sans cette ligne, un rapport d'analyse
                // rapide ne dirait pas ce qu'il n'a pas regardé.
                if (chosen != null && !chosen.Contains(descriptor.Id))
                {
                    reports.Add(Excluded(descriptor, "Module non retenu par le technicien pour cette analyse."));
                    continue;
                }

                if (mode == RunMode.Quick && descriptor.FullScanOnly)
                {
                    reports.Add(Excluded(descriptor,
                        "Module réservé au diagnostic complet : il lance des outils externes ou " +
                        "des tests réseau, trop longs pour une analyse rapide."));
                    continue;
                }

                pending.Add(probe);
                planned.Add(descriptor.Id);
            }

            _log.Info("Démarrage du diagnostic : " + pending.Count + " module(s) sur " + probes.Count +
                      ", mode " + mode + ".");

            var total = pending.Count;
            var degree = Math.Min(Math.Max(Environment.ProcessorCount, 1), MaxConcurrency);
            var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;

            while (pending.Count > 0)
            {
                var wave = SelectWave(pending, satisfied, planned);
                if (wave.Count == 0)
                {
                    // Dépendance circulaire ou non planifiée : on exécute le reste plutôt que de
                    // bloquer indéfiniment. Le cas relève d'un bug de déclaration, pas de la machine.
                    _log.Warn("Dépendances impossibles à satisfaire : exécution du reste sans ordonnancement.");
                    wave = new List<IDiagnosticProbe>(pending);
                }

                var waveReports = await RunWaveAsync(
                    wave, context, degree, progress, total,
                    () => Interlocked.Increment(ref completed),
                    () => Volatile.Read(ref completed),
                    cancellationToken).ConfigureAwait(false);

                reports.AddRange(waveReports);
                foreach (var probe in wave)
                {
                    satisfied.Add(probe.Descriptor.Id);
                    pending.Remove(probe);
                }
            }

            stopwatch.Stop();
            _log.Info("Diagnostic terminé en " + stopwatch.ElapsedMilliseconds + " ms.");
            return new CollectionResult { Reports = reports, DurationMs = stopwatch.ElapsedMilliseconds };
        }

        /// <summary>Compte rendu d'un module écarté du plan, avec la raison de son absence.</summary>
        private static ModuleReport Excluded(ProbeDescriptor descriptor, string reason)
            => new ModuleReport
            {
                ProbeId = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Category = descriptor.Category,
                Status = ProbeStatus.Skipped,
                Message = reason,
            };

        /// <summary>
        /// Sondes dont toutes les dépendances sont déjà exécutées. Une dépendance absente du plan
        /// (sonde décochée ou retirée du mode rapide) n'empêche pas l'exécution : la sonde
        /// s'adaptera à l'absence de donnée, comme elle sait déjà le faire.
        /// </summary>
        private static List<IDiagnosticProbe> SelectWave(
            List<IDiagnosticProbe> pending, HashSet<string> satisfied, HashSet<string> planned)
        {
            var wave = new List<IDiagnosticProbe>();
            foreach (var probe in pending)
            {
                var ready = true;
                foreach (var dependency in probe.Descriptor.DependsOn)
                {
                    if (!planned.Contains(dependency) || satisfied.Contains(dependency)) continue;
                    ready = false;
                    break;
                }
                if (ready) wave.Add(probe);
            }
            return wave;
        }

        private async Task<ModuleReport[]> RunWaveAsync(
            List<IDiagnosticProbe> wave, ProbeContext context, int degree,
            IProgress<DiagnosticProgress>? progress, int total, Func<int> markCompleted, Func<int> completed,
            CancellationToken cancellationToken)
        {
            using var slots = new SemaphoreSlim(degree, degree);
            var reports = new ModuleReport[wave.Count];
            var tasks = new Task[wave.Count];

            for (var i = 0; i < wave.Count; i++)
            {
                var index = i;
                var probe = wave[index];
                tasks[index] = Task.Run(async () =>
                {
                    await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        progress?.Report(new DiagnosticProgress
                        {
                            Completed = completed(),
                            Total = total,
                            StartedProbe = probe.Descriptor.DisplayName,
                        });

                        reports[index] = await _policy.ExecuteAsync(probe, context, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                        progress?.Report(new DiagnosticProgress
                        {
                            Completed = markCompleted(),
                            Total = total,
                            CurrentProbe = probe.Descriptor.DisplayName,
                        });
                    }
                }, CancellationToken.None);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return reports;
        }
    }
}
