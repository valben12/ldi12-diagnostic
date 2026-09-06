using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using LDI12.Engine.Orchestration;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ces tests gardent la promesse centrale du logiciel : « aucune fonctionnalité ne doit faire
    /// planter toute l'application ». Ils doivent rester verts quoi qu'il arrive aux modules.
    /// </summary>
    public class ProbeExecutionPolicyTests
    {
        private static ProbeExecutionPolicy Policy() => new ProbeExecutionPolicy(NullLogger.Instance);

        [Fact]
        public async Task Une_sonde_qui_leve_une_exception_ne_remonte_jamais_a_l_appelant()
        {
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(),
                (_, __) => throw new InvalidOperationException("le fournisseur WMI a explosé"));

            var report = await Policy().ExecuteAsync(probe, Fakes.Context(), CancellationToken.None);

            Assert.Equal(ProbeStatus.Failed, report.Status);
            Assert.Contains("InvalidOperationException", report.ExceptionSummary!);
            Assert.Contains("reste du diagnostic", report.Message!);
        }

        [Fact]
        public async Task Une_sonde_qui_leve_de_maniere_asynchrone_est_aussi_absorbee()
        {
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(),
                async (_, __) =>
                {
                    await Task.Yield();
                    throw new System.Runtime.InteropServices.COMException("dépôt WMI corrompu");
                });

            var report = await Policy().ExecuteAsync(probe, Fakes.Context(), CancellationToken.None);

            Assert.Equal(ProbeStatus.Failed, report.Status);
            Assert.Contains("COMException", report.ExceptionSummary!);
        }

        [Fact]
        public async Task Une_sonde_qui_ignore_le_jeton_est_abandonnee_au_delai_maximal()
        {
            // Le cas WMI : un appel bloqué qu'aucune annulation ne libère. La politique doit
            // rendre la main à l'orchestrateur malgré tout.
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(hardTimeout: TimeSpan.FromMilliseconds(150)),
                (_, __) => Task.Delay(Timeout.Infinite, CancellationToken.None)
                               .ContinueWith(_ => ProbeOutcome.Ok()));

            var report = await Policy().ExecuteAsync(probe, Fakes.Context(), CancellationToken.None);

            Assert.Equal(ProbeStatus.TimedOut, report.Status);
        }

        [Fact]
        public async Task Un_depassement_de_delai_n_est_pas_presente_comme_une_annulation_du_technicien()
        {
            // Une sonde coopérative lève OperationCanceledException aussi bien sur un délai
            // dépassé que sur une annulation. Afficher « annulé par le technicien » sur un
            // module qui a calé serait un compte rendu faux.
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(hardTimeout: TimeSpan.FromMilliseconds(150)),
                async (_, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return ProbeOutcome.Ok();
                });

            var report = await Policy().ExecuteAsync(probe, Fakes.Context(), CancellationToken.None);

            Assert.Equal(ProbeStatus.TimedOut, report.Status);
            Assert.DoesNotContain("technicien", report.Message!);
        }

        [Fact]
        public async Task Une_annulation_du_technicien_est_rapportee_comme_telle()
        {
            using var cancellation = new CancellationTokenSource();
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(hardTimeout: TimeSpan.FromSeconds(30)),
                async (_, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return ProbeOutcome.Ok();
                });

            var run = Policy().ExecuteAsync(probe, Fakes.Context(), cancellation.Token);
            cancellation.Cancel();
            var report = await run;

            Assert.Equal(ProbeStatus.Cancelled, report.Status);
            Assert.Contains("technicien", report.Message!);
        }

        [Fact]
        public async Task Une_sonde_trop_recente_pour_la_machine_est_ecartee_avec_son_motif()
        {
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(requirements: new ProbeRequirements { MinimumBuild = 14393 }),
                (_, __) => throw new InvalidOperationException("ne doit jamais s'exécuter"));

            var report = await Policy().ExecuteAsync(
                probe, Fakes.Context(new FakePlatformInfo(build: 7601)), CancellationToken.None);

            Assert.Equal(ProbeStatus.Unavailable, report.Status);
            Assert.Contains("14393", report.Message!);
            Assert.Contains("7601", report.Message!);
        }

        [Fact]
        public async Task Une_sonde_dont_la_fonctionnalite_manque_reprend_le_motif_du_registre()
        {
            var features = new FakeFeatureRegistry().Set(
                FeatureId.SmartNvme, Availability.Unavailable,
                "Requiert Windows 10 1607 (build 14393). Build actuel : 7601.");

            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(requirements: new ProbeRequirements
                {
                    RequiredFeatures = new[] { FeatureId.SmartNvme },
                }),
                (_, __) => Task.FromResult(ProbeOutcome.Ok()));

            var report = await Policy().ExecuteAsync(
                probe, Fakes.Context(new FakePlatformInfo(features: features)), CancellationToken.None);

            Assert.Equal(ProbeStatus.Unavailable, report.Status);
            Assert.Contains("Windows 10 1607", report.Message!);
        }

        [Fact]
        public async Task Une_fonctionnalite_qui_demande_une_elevation_n_ecarte_pas_la_sonde()
        {
            // Élévation requise ≠ indisponible : la sonde reste au plan, c'est à elle de
            // rapporter ce qu'elle peut lire sans privilèges.
            var features = new FakeFeatureRegistry().Set(FeatureId.SmartAta, Availability.RequiresElevation);

            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(requirements: new ProbeRequirements
                {
                    RequiredFeatures = new[] { FeatureId.SmartAta },
                }),
                (_, __) => Task.FromResult(ProbeOutcome.Partial("SMART non lu, reste des données collectées.")));

            var report = await Policy().ExecuteAsync(
                probe, Fakes.Context(new FakePlatformInfo(features: features)), CancellationToken.None);

            Assert.Equal(ProbeStatus.Partial, report.Status);
        }

        [Fact]
        public async Task Une_sonde_exigeant_l_elevation_le_signale_sans_echouer()
        {
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(requirements: new ProbeRequirements { RequiresElevation = true }),
                (_, __) => throw new InvalidOperationException("ne doit jamais s'exécuter"));

            var report = await Policy().ExecuteAsync(
                probe, Fakes.Context(new FakePlatformInfo(elevated: false)), CancellationToken.None);

            Assert.Equal(ProbeStatus.ElevationRequired, report.Status);
            Assert.Contains("administrateur", report.Message!);
        }

        [Fact]
        public async Task Un_espace_de_noms_refuse_est_distingue_d_un_espace_de_noms_absent()
        {
            var requirements = new ProbeRequirements
            {
                RequiredWmiNamespaces = new[] { WmiNamespaces.Tpm },
            };
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(requirements: requirements),
                (_, __) => Task.FromResult(ProbeOutcome.Ok()));

            var refused = await Policy().ExecuteAsync(
                probe,
                Fakes.Context(wmi: new FakeWmiGateway().Set(WmiNamespaces.Tpm, WmiNamespaceState.AccessDenied)),
                CancellationToken.None);

            var absent = await Policy().ExecuteAsync(
                probe,
                Fakes.Context(wmi: new FakeWmiGateway().Set(WmiNamespaces.Tpm, WmiNamespaceState.Absent)),
                CancellationToken.None);

            Assert.Equal(ProbeStatus.ElevationRequired, refused.Status);
            Assert.Equal(ProbeStatus.Unavailable, absent.Status);
        }

        [Fact]
        public async Task Le_compte_rendu_reprend_toujours_l_identite_du_module()
        {
            var probe = new DelegateProbe(
                DelegateProbe.Descriptor_(id: "STO-SMART"),
                (_, __) => Task.FromResult(ProbeOutcome.Ok("3 disques analysés.")));

            var report = await Policy().ExecuteAsync(probe, Fakes.Context(), CancellationToken.None);

            Assert.Equal("STO-SMART", report.ProbeId);
            Assert.Equal("Sonde de test", report.DisplayName);
            Assert.Equal(DiagnosticCategory.Windows, report.Category);
            Assert.Equal(ProbeStatus.Ok, report.Status);
        }
    }
}
