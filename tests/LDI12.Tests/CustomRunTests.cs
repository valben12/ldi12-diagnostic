using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Engine.Orchestration;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'analyse personnalisée : ce qu'elle exécute, ce qu'elle écarte, et ce qu'elle doit dire
    /// de la note qu'elle produit.
    /// </summary>
    public class CustomRunTests
    {
        [Fact]
        public async Task Un_module_ecarte_est_rapporte_avec_la_raison_de_son_absence()
        {
            // « Pourquoi cette information manque » est une information : c'est le contrat du
            // compte rendu de module depuis la phase 0. Un module simplement absent de la liste
            // laisserait le rapport muet sur ce qui n'a pas été regardé.
            var result = await Run(new[] { "A" }, "A", "B", "C");

            Assert.Equal(3, result.Reports.Count);

            var skipped = result.Reports.Where(report => report.Status == ProbeStatus.Skipped).ToList();
            Assert.Equal(2, skipped.Count);
            Assert.All(skipped, report => Assert.Contains("non retenu", report.Message));
        }

        [Fact]
        public async Task Seuls_les_modules_retenus_s_executent()
        {
            var executed = new List<string>();
            await Run(new[] { "B" }, executed, "A", "B", "C");

            Assert.Equal(new[] { "B" }, executed);
        }

        [Fact]
        public async Task Une_selection_vide_n_execute_rien_et_le_dit_module_par_module()
        {
            var executed = new List<string>();
            var result = await Run(Array.Empty<string>(), executed, "A", "B");

            Assert.Empty(executed);
            Assert.Equal(2, result.Reports.Count);
            Assert.All(result.Reports, report => Assert.Equal(ProbeStatus.Skipped, report.Status));
        }

        [Fact]
        public async Task Sans_selection_le_mode_rapide_ecarte_les_modules_lents_en_le_disant()
        {
            // Le mode rapide écartait ses modules sans laisser de trace : un rapport d'analyse
            // rapide ne disait pas ce qu'il n'avait pas regardé.
            var probes = new IDiagnosticProbe[]
            {
                new FakeProbe("RAPIDE", fullScanOnly: false),
                new FakeProbe("LENT", fullScanOnly: true),
            };

            var result = await new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance)
                .RunAsync(probes, Context(), RunMode.Quick, null, CancellationToken.None);

            var skipped = Assert.Single(result.Reports.Where(report => report.Status == ProbeStatus.Skipped));
            Assert.Equal("LENT", skipped.ProbeId);
            Assert.Contains("diagnostic complet", skipped.Message);
        }

        [Fact]
        public async Task Une_dependance_ecartee_n_empeche_pas_la_sonde_qui_en_depend()
        {
            // Une sonde sait déjà s'adapter à l'absence d'une donnée : la bloquer parce que sa
            // dépendance n'a pas été retenue transformerait un choix du technicien en panne.
            var probes = new IDiagnosticProbe[]
            {
                new FakeProbe("BASE"),
                new FakeProbe("DEPEND", dependsOn: "BASE"),
            };

            var result = await new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance)
                .RunAsync(probes, Context(), RunMode.Full, null, CancellationToken.None, new[] { "DEPEND" });

            var executed = Assert.Single(result.Reports.Where(report => report.Status == ProbeStatus.Ok));
            Assert.Equal("DEPEND", executed.ProbeId);
        }

        private static ProbeContext Context()
            => new ProbeContext(
                new FakePlatformInfo(), new FakeProcessRunner(), new FakeWmiGateway(),
                new FakeRegistryGateway(), NullLogger.Instance, null!, null!, null!, null!, null!, null!, null!,
                new SnapshotDraft(), RunMode.Custom);

        private static Task<CollectionResult> Run(IReadOnlyCollection<string> selection, params string[] ids)
            => Run(selection, new List<string>(), ids);

        private static Task<CollectionResult> Run(
            IReadOnlyCollection<string> selection, List<string> executed, params string[] ids)
        {
            var probes = new List<IDiagnosticProbe>();
            foreach (var id in ids) probes.Add(new FakeProbe(id, executed: executed));

            return new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance)
                .RunAsync(probes, Context(), RunMode.Custom, null, CancellationToken.None, selection);
        }

        private sealed class FakeProbe : IDiagnosticProbe
        {
            private readonly List<string>? _executed;

            public FakeProbe(
                string id, bool fullScanOnly = false, string? dependsOn = null, List<string>? executed = null)
            {
                _executed = executed;
                Descriptor = new ProbeDescriptor
                {
                    Id = id,
                    DisplayName = "Module " + id,
                    Category = DiagnosticCategory.Windows,
                    FullScanOnly = fullScanOnly,
                    DependsOn = dependsOn == null ? Array.Empty<string>() : new[] { dependsOn },
                    EstimatedDuration = TimeSpan.FromMilliseconds(1),
                    HardTimeout = TimeSpan.FromSeconds(5),
                };
            }

            public ProbeDescriptor Descriptor { get; }

            public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
            {
                _executed?.Add(Descriptor.Id);
                return Task.FromResult(ProbeOutcome.Ok("fait"));
            }
        }
    }
}
