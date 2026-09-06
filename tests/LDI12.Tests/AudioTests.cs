using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Collectors.Windows;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// « Je n'ai plus de son », et ce que ce logiciel peut réellement en dire.
    /// </summary>
    public class AudioTests
    {
        // ============================================================ lecture de l'état

        [Theory]
        [InlineData(0x1, AudioEndpointState.Active)]
        [InlineData(0x2, AudioEndpointState.Disabled)]
        [InlineData(0x4, AudioEndpointState.NotPresent)]
        [InlineData(0x8, AudioEndpointState.Unplugged)]
        public void L_etat_se_lit_dans_les_quatre_bits_de_poids_faible(int raw, AudioEndpointState expected)
            => Assert.Equal(expected, AudioProbe.Classify(raw));

        [Theory]
        [InlineData(0x21000004)]
        [InlineData(0x20000004)]
        public void Les_drapeaux_des_bits_hauts_ne_changent_pas_l_etat(int raw)
        {
            // Relevé sur la machine d'essai : la même sortie absente apparaît sous deux valeurs
            // différentes. Comparer la valeur entière à 4 rangerait presque tout dans
            // « indéterminé », et la machine paraîtrait n'avoir aucun périphérique connu.
            Assert.Equal(AudioEndpointState.NotPresent, AudioProbe.Classify(raw));
        }

        // ============================================================ service

        [Fact]
        public void Un_service_audio_arrete_est_un_probleme()
        {
            var finding = Single(Evaluate(b => b.Audio(serviceRunning: false)), "AUD-001");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Le_constructeur_de_peripheriques_arrete_compte_aussi()
        {
            // Sans lui, aucun périphérique n'apparaît, même si le service audio tourne.
            var finding = Single(
                Evaluate(b => b.Audio(serviceRunning: true, builderRunning: false)), "AUD-001");

            Assert.Contains("constructeur de points de terminaison", finding.TechnicalDetail);
        }

        // ============================================================ aucune sortie

        [Fact]
        public void Des_sorties_toutes_desactivees_expliquent_le_silence()
        {
            var finding = Single(Evaluate(b => b.Audio(true, true,
                Fixtures.Builder.Endpoint("Haut-parleurs", AudioDirection.Output, AudioEndpointState.Disabled))),
                "AUD-002");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("désactivée(s)", finding.TechnicalDetail);
            Assert.Contains("deux clics", finding.PlainExplanation);
        }

        [Fact]
        public void Des_sorties_toutes_debranchees_designent_une_prise()
        {
            var finding = Single(Evaluate(b => b.Audio(true, true,
                Fixtures.Builder.Endpoint("Casque", AudioDirection.Output, AudioEndpointState.Unplugged))),
                "AUD-002");

            Assert.Contains("rien n'est branché", finding.TechnicalDetail);
        }

        [Fact]
        public void Aucune_sortie_du_tout_designe_le_pilote()
        {
            var finding = Single(Evaluate(b => b.Audio(true, true)), "AUD-002");

            Assert.Contains("Aucun périphérique de sortie", finding.TechnicalDetail);
            Assert.Contains("pilote", finding.PlainExplanation);
        }

        [Fact]
        public void Une_sortie_active_leve_le_constat()
        {
            var findings = Evaluate(b => b.Audio(true, true,
                Fixtures.Builder.Endpoint("Casque", AudioDirection.Output, AudioEndpointState.Active)));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "AUD-002");
        }

        // ============================================================ sortie désactivée parmi d'autres

        [Fact]
        public void Une_sortie_desactivee_parmi_d_autres_explique_le_mauvais_appareil()
        {
            // « Le son sort de l'écran au lieu des enceintes » vient presque toujours de là.
            var finding = Single(Evaluate(b => b.Audio(true, true,
                Fixtures.Builder.Endpoint("Casque", AudioDirection.Output, AudioEndpointState.Active),
                Fixtures.Builder.Endpoint("Haut-parleurs", AudioDirection.Output, AudioEndpointState.Disabled))),
                "AUD-003");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("Haut-parleurs", finding.TechnicalDetail);
        }

        [Fact]
        public void Un_son_qui_fonctionne_ne_dit_rien()
        {
            var findings = Evaluate(b => b.Audio(true, true,
                Fixtures.Builder.Endpoint("Casque", AudioDirection.Output, AudioEndpointState.Active),
                Fixtures.Builder.Endpoint("Microphone", AudioDirection.Input, AudioEndpointState.Active)));

            foreach (var id in new[] { "AUD-001", "AUD-002", "AUD-003" })
                Assert.DoesNotContain(findings, finding => finding.RuleId == id);
        }

        // ============================================================ absence de mesure

        [Fact]
        public void Sans_releve_audio_aucune_regle_ne_conclut()
        {
            var score = new AnalysisEngine().Analyze(Fixtures.Healthy()).Score!;

            foreach (var id in new[] { "AUD-001", "AUD-002", "AUD-003" })
                Assert.DoesNotContain(id, score.EvaluatedRuleIds);
        }

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            })).Findings;

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));
    }
}
