using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Orchestration;
using LDI12.Engine.Profile;
using LDI12.Platform;
using LDI12.Platform.Isolation;
using LDI12.Reports.Json;

namespace LDI12.App.Services
{
    /// <summary>Un module du catalogue, tel qu'il se présente au choix du technicien.</summary>
    public sealed class ModuleChoice
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public DiagnosticCategory Category { get; init; }

        /// <summary>Absent d'une analyse rapide : il lance un outil externe ou un test réseau.</summary>
        public bool FullScanOnly { get; init; }

        public TimeSpan EstimatedDuration { get; init; }
    }

    /// <summary>
    /// Exécute un diagnostic complet (collecte puis analyse) hors du fil d'interface.
    /// </summary>
    /// <remarks>
    /// La couche plateforme est construite une seule fois et réutilisée : sa création coûte le
    /// sondage des seize fonctionnalités système, inutile à refaire à chaque analyse. Tout le
    /// travail part sur le pool de threads, l'interface ne reçoit que la progression.
    /// </remarks>
    public sealed class DiagnosticService : IDisposable
    {
        private readonly ILdiLogger _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private PlatformServices? _platform;

        public DiagnosticService(ILdiLogger logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>
        /// Barème utilisé par l'analyse.
        /// </summary>
        /// <remarks>
        /// Fourni par une fonction et non figé à la construction : le technicien peut ajuster un
        /// seuil dans l'écran de réglages entre deux analyses, et la suivante doit en tenir
        /// compte sans relancer l'application.
        /// </remarks>
        public Func<DiagnosticProfile>? ActiveProfile { get; set; }

        public async Task<PlatformServices> GetPlatformAsync(CancellationToken cancellationToken)
        {
            if (_platform != null) return _platform;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Task.Run avec une lambda asynchrone déballe déjà la tâche interne.
                return _platform ??= await Task
                    .Run(() => PlatformServices.CreateAsync(_logger, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Les modules qui seront exécutés dans ce mode, dans l'ordre du catalogue.
        /// </summary>
        /// <remarks>
        /// Permet à l'écran d'analyse d'afficher le plan complet dès le départ, et non de le
        /// découvrir au fil de l'eau. Le technicien voit ce que l'outil va regarder avant qu'il
        /// l'ait regardé, ce qui est aussi ce qu'il doit pouvoir dire au client.
        /// </remarks>
        public IReadOnlyList<(string Id, string Name, DiagnosticCategory Category)> DescribeModules(RunMode mode)
        {
            var modules = new List<(string, string, DiagnosticCategory)>();

            foreach (var probe in CollectorCatalog.CreateAll(_logger))
            {
                var descriptor = probe.Descriptor;
                if (mode == RunMode.Quick && descriptor.FullScanOnly) continue;
                modules.Add((descriptor.Id, descriptor.DisplayName, descriptor.Category));
            }

            return modules;
        }

        /// <summary>
        /// Tous les modules du catalogue, avec ce qui permet au technicien de choisir.
        /// </summary>
        /// <remarks>
        /// Distincte de <see cref="DescribeModules"/>, qui décrit un plan d'exécution : celle-ci
        /// décrit un catalogue à cocher, et doit donc dire ce qu'un module coûte et pourquoi il
        /// est absent d'une analyse rapide.
        /// </remarks>
        public IReadOnlyList<ModuleChoice> DescribeCatalogue()
        {
            var modules = new List<ModuleChoice>();

            foreach (var probe in CollectorCatalog.CreateAll(_logger))
            {
                var descriptor = probe.Descriptor;
                modules.Add(new ModuleChoice
                {
                    Id = descriptor.Id,
                    Name = descriptor.DisplayName,
                    Category = descriptor.Category,
                    FullScanOnly = descriptor.FullScanOnly,
                    EstimatedDuration = descriptor.EstimatedDuration,
                });
            }

            return modules;
        }

        /// <param name="selection">
        /// Modules retenus par le technicien, pour une analyse personnalisée. Nul : tout ce que
        /// le mode autorise.
        /// </param>
        public async Task<SystemSnapshot> RunAsync(
            RunMode mode, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken,
            IReadOnlyCollection<string>? selection = null)
        {
            var services = await GetPlatformAsync(cancellationToken).ConfigureAwait(false);

            return await Task.Run(async () =>
            {
                var draft = new SnapshotDraft();

                // Un hôte isolé par analyse, et pas un pour la session : le réglage des capteurs
                // peut changer entre deux passages, et un processus enfant qui survivrait à
                // l'analyse serait une trace de plus laissée sur la machine du client.
                using var isolation = new ProbeIsolationHost(
                    services.Launcher, new SnapshotCodec(), services.Sensors.Enabled, _logger);

                var runner = new DiagnosticRunner(new ProbeExecutionPolicy(_logger, isolation), _logger);

                var collection = await runner
                    .RunAsync(
                        CollectorCatalog.CreateAll(_logger),
                        services.CreateProbeContext(draft, mode),
                        mode, progress, cancellationToken, selection)
                    .ConfigureAwait(false);

                var collected = SnapshotComposer.Compose(services, draft, collection, mode);
                return new AnalysisEngine(_logger).Analyze(collected, ActiveProfile?.Invoke());
            }, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _platform?.Dispose();
            _gate.Dispose();
        }
    }
}
