using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Engine.Orchestration;
using Xunit;

namespace LDI12.Tests
{
    public class CollectionTests
    {
        [Fact]
        public void Toutes_les_sondes_du_catalogue_se_construisent()
        {
            // Une exception dans un constructeur ou un initialiseur de type survient AVANT la
            // politique d'exécution : elle emporterait toute l'application. Ce test l'attrape.
            var probes = CollectorCatalog.CreateAll();

            Assert.NotEmpty(probes);
            foreach (var probe in probes)
            {
                Assert.False(string.IsNullOrWhiteSpace(probe.Descriptor.Id), "Sonde sans identifiant.");
                Assert.False(string.IsNullOrWhiteSpace(probe.Descriptor.DisplayName),
                    probe.Descriptor.Id + " : libellé manquant.");
                Assert.True(probe.Descriptor.HardTimeout > TimeSpan.Zero,
                    probe.Descriptor.Id + " : délai maximal non défini.");
            }
        }

        [Fact]
        public void Les_identifiants_de_sonde_sont_uniques()
        {
            // Les identifiants apparaissent dans les rapports archivés : un doublon rendrait
            // deux modules indiscernables lors d'une comparaison avant / après.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in CollectorCatalog.CreateAll())
                Assert.True(seen.Add(probe.Descriptor.Id), "Identifiant en double : " + probe.Descriptor.Id);
        }

        [Fact]
        public void Chaque_dependance_declaree_existe_dans_le_catalogue()
        {
            var probes = CollectorCatalog.CreateAll();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in probes) ids.Add(probe.Descriptor.Id);

            foreach (var probe in probes)
                foreach (var dependency in probe.Descriptor.DependsOn)
                    Assert.True(ids.Contains(dependency),
                        probe.Descriptor.Id + " dépend de « " + dependency + " », absent du catalogue.");
        }

        [Fact]
        public async Task Une_sonde_s_execute_apres_celle_dont_elle_depend()
        {
            // Garantie dont dépend la lecture SMART : elle a besoin de la liste des disques
            // établie par la sonde d'énumération, et non d'une liste encore vide.
            var order = new List<string>();
            var gate = new object();

            IDiagnosticProbe Make(string id, string[] dependsOn) => new DelegateProbe(
                DelegateProbe.Descriptor_(id: id, dependsOn: dependsOn),
                async (_, __) =>
                {
                    await Task.Delay(20);
                    lock (gate) order.Add(id);
                    return ProbeOutcome.Ok();
                });

            var probes = new[]
            {
                Make("C", new[] { "B" }),
                Make("A", Array.Empty<string>()),
                Make("B", new[] { "A" }),
            };

            var runner = new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance);
            var result = await runner.RunAsync(probes, Fakes.Context(), RunMode.Full, null, CancellationToken.None);

            Assert.Equal(3, result.Reports.Count);
            Assert.Equal(new[] { "A", "B", "C" }, order.ToArray());
        }

        [Fact]
        public async Task Une_dependance_circulaire_n_immobilise_pas_le_diagnostic()
        {
            // Défaut de déclaration, pas défaut de la machine : le diagnostic doit tout de même
            // se terminer plutôt que d'attendre indéfiniment.
            IDiagnosticProbe Make(string id, string dependsOn) => new DelegateProbe(
                DelegateProbe.Descriptor_(id: id, dependsOn: new[] { dependsOn }),
                (_, __) => Task.FromResult(ProbeOutcome.Ok()));

            var probes = new[] { Make("X", "Y"), Make("Y", "X") };

            var runner = new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance);
            var result = await runner.RunAsync(probes, Fakes.Context(), RunMode.Full, null, CancellationToken.None);

            Assert.Equal(2, result.Reports.Count);
        }

        [Fact]
        public async Task Le_mode_rapide_ecarte_les_sondes_longues()
        {
            var quick = new DelegateProbe(
                DelegateProbe.Descriptor_(id: "QUICK"), (_, __) => Task.FromResult(ProbeOutcome.Ok()));
            var slow = new DelegateProbe(
                DelegateProbe.Descriptor_(id: "SLOW", fullScanOnly: true), (_, __) => Task.FromResult(ProbeOutcome.Ok()));

            var runner = new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance);
            var result = await runner.RunAsync(new[] { quick, slow }, Fakes.Context(), RunMode.Quick, null, CancellationToken.None);

            // La sonde longue n'est pas exécutée, mais elle est rapportée : un compte rendu qui
            // l'omettrait laisserait le rapport muet sur ce qu'il n'a pas regardé, et c'est
            // précisément ce que le contrat du compte rendu de module interdit.
            var executed = Assert.Single(result.Reports.Where(report => report.Status == ProbeStatus.Ok));
            Assert.Equal("QUICK", executed.ProbeId);

            var skipped = Assert.Single(result.Reports.Where(report => report.Status == ProbeStatus.Skipped));
            Assert.Equal("SLOW", skipped.ProbeId);
            Assert.Contains("diagnostic complet", skipped.Message);
        }

        [Fact]
        public async Task La_progression_ne_depasse_jamais_le_total_annonce()
        {
            var probes = new List<IDiagnosticProbe>();
            for (var i = 0; i < 6; i++)
            {
                probes.Add(new DelegateProbe(
                    DelegateProbe.Descriptor_(id: "P" + i, dependsOn: i > 0 ? new[] { "P" + (i - 1) } : null),
                    (_, __) => Task.FromResult(ProbeOutcome.Ok())));
            }

            var reports = new List<DiagnosticProgress>();
            var progress = new SynchronousProgress(reports.Add);

            var runner = new DiagnosticRunner(new ProbeExecutionPolicy(NullLogger.Instance), NullLogger.Instance);
            await runner.RunAsync(probes, Fakes.Context(), RunMode.Full, progress, CancellationToken.None);

            Assert.NotEmpty(reports);

            // Deux natures de rapport depuis que l'écran d'analyse montre ce qui travaille : un
            // au démarrage d'un module, un à sa fin. Ils ne se confondent pas : un rapport de
            // démarrage ne prétend jamais qu'un module de plus est terminé.
            var started = new List<string>();
            var finished = new List<string>();

            foreach (var report in reports)
            {
                Assert.Equal(6, report.Total);
                Assert.InRange(report.Completed, 0, 6);

                if (report.StartedProbe != null)
                {
                    Assert.Equal(string.Empty, report.CurrentProbe);
                    started.Add(report.StartedProbe);
                }
                else
                {
                    Assert.NotEqual(string.Empty, report.CurrentProbe);
                    finished.Add(report.CurrentProbe);
                    Assert.InRange(report.Completed, 1, 6);
                }
            }

            Assert.Equal(6, started.Count);
            Assert.Equal(6, finished.Count);

            // Garantie sur laquelle repose l'affichage : un module est toujours annoncé comme
            // démarré avant d'être annoncé comme terminé. Sans elle, une tuile pourrait passer
            // au vert sans jamais s'être allumée.
            foreach (var name in finished)
                Assert.True(
                    started.IndexOf(name) >= 0 && started.IndexOf(name) <= finished.IndexOf(name),
                    "Le module « " + name + " » s'est terminé sans avoir été annoncé comme démarré.");
        }

        private sealed class SynchronousProgress : IProgress<DiagnosticProgress>
        {
            private readonly Action<DiagnosticProgress> _handler;
            private readonly object _gate = new object();

            public SynchronousProgress(Action<DiagnosticProgress> handler) => _handler = handler;

            public void Report(DiagnosticProgress value)
            {
                lock (_gate) _handler(value);
            }
        }
    }
}
