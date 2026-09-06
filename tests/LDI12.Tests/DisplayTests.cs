using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// « C'est flou », « les caractères sont minuscules » : les plaintes qui ne sont pas des pannes.
    /// </summary>
    public class DisplayTests
    {
        // ============================================================ définition native

        [Fact]
        public void Un_ecran_hors_de_sa_definition_explique_le_flou()
        {
            // Une dalle est nette à sa définition et à aucune autre : en dessous, elle étire
            // chaque point sur plusieurs.
            var finding = Single(Evaluate(b =>
                b.Monitor(width: 1280, height: 720, nativeWidth: 1920, nativeHeight: 1080)), "DSP-001");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("c'est flou", finding.PlainExplanation);
            Assert.Contains("SET-NATIVE-RESOLUTION", finding.Recommendations);
        }

        [Fact]
        public void Un_ecran_a_sa_definition_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.Monitor()),
                finding => finding.RuleId == "DSP-001");
        }

        [Fact]
        public void Avec_le_pilote_generique_le_constat_designe_le_pilote_et_non_le_reglage()
        {
            // Proposer de changer la définition ferait tourner en rond : le pilote générique
            // n'en propose que quelques-unes, toutes basses.
            var finding = Single(Evaluate(b =>
            {
                b.Monitor(width: 1024, height: 768, nativeWidth: 1920, nativeHeight: 1080);
                b.Gpu(generic: true, driverAgeMonths: 6);
            }), "DSP-001");

            Assert.Contains("pilote générique", finding.PlainExplanation);
            Assert.Contains("INSTALL-GPU-DRIVER", finding.Recommendations);
            Assert.DoesNotContain("SET-NATIVE-RESOLUTION", finding.Recommendations);
        }

        // ============================================================ fréquence

        [Fact]
        public void Une_dalle_rapide_restee_a_soixante_hertz_est_signalee()
        {
            var finding = Single(Evaluate(b => b.Monitor(refreshHz: 60, maxRefreshHz: 144)), "DSP-002");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("144 Hz", finding.TechnicalDetail);
        }

        [Fact]
        public void Un_ecart_d_arrondi_ne_declenche_rien()
        {
            // Une dalle annoncée à 144 Hz tourne parfois à 143 : le seuil existe pour cela.
            Assert.DoesNotContain(
                Evaluate(b => b.Monitor(refreshHz: 143, maxRefreshHz: 144)),
                finding => finding.RuleId == "DSP-002");
        }

        [Fact]
        public void Une_dalle_ordinaire_a_soixante_hertz_ne_dit_rien()
        {
            // Il n'y a rien à gagner : soixante est son maximum.
            Assert.DoesNotContain(
                Evaluate(b => b.Monitor(refreshHz: 60, maxRefreshHz: 60)),
                finding => finding.RuleId == "DSP-002");
        }

        // ============================================================ taille du texte

        [Fact]
        public void Une_dalle_4K_de_27_pouces_sans_mise_a_l_echelle_rend_le_texte_minuscule()
        {
            var finding = Single(Evaluate(b => b.Monitor(
                width: 3840, height: 2160, nativeWidth: 3840, nativeHeight: 2160,
                diagonalInches: 27, scalingPercent: 100)), "DSP-003");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("minuscule", finding.PlainExplanation);
        }

        [Fact]
        public void La_meme_dalle_mise_a_l_echelle_ne_dit_plus_rien()
        {
            // C'est le rapport corrigé de la mise à l'échelle qui décide, pas la définition.
            Assert.DoesNotContain(
                Evaluate(b => b.Monitor(
                    width: 3840, height: 2160, nativeWidth: 3840, nativeHeight: 2160,
                    diagonalInches: 27, scalingPercent: 150)),
                finding => finding.RuleId == "DSP-003");
        }

        [Fact]
        public void Une_mise_a_l_echelle_excessive_reduit_l_ecran_pour_rien()
        {
            var finding = Single(Evaluate(b => b.Monitor(
                width: 1920, height: 1080, diagonalInches: 24, scalingPercent: 150)), "DSP-003");

            Assert.Contains("espace de travail", finding.PlainExplanation);
        }

        // ============================================================ sans relevé

        [Fact]
        public void Sans_ecran_releve_rien_n_est_conclu()
        {
            var score = new AnalysisEngine().Analyze(Fixtures.Build(b => b.Windows11())).Score!;

            foreach (var id in new[] { "DSP-001", "DSP-002", "DSP-003" })
                Assert.DoesNotContain(id, score.EvaluatedRuleIds);
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_met_les_deux_definitions_cote_a_cote()
        {
            // C'est la comparaison qui informe : une valeur seule n'apprend rien à qui ne connaît
            // pas la dalle.
            var facts = Group(b => b.Monitor(width: 1280, height: 720, scalingPercent: 125));

            Assert.Equal("1", Value(facts, "Écrans branchés"));
            Assert.Equal("125 %", Value(facts, "Mise à l'échelle"));
        }

        // ============================================================ montage

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            })).Findings;

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static IReadOnlyList<Fact> Group(Action<Fixtures.Builder> configure)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == "Écrans") return group.Facts;

            throw new InvalidOperationException("Le cadre « Écrans » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;
    }
}
