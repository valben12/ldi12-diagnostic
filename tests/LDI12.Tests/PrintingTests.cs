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
    /// « Je ne peux plus imprimer », une des trois pannes les plus racontées en atelier.
    /// </summary>
    public class PrintingTests
    {
        // ============================================================ lecture des états

        [Theory]
        [InlineData(3u, PrinterAvailability.Ready)]
        [InlineData(7u, PrinterAvailability.Offline)]
        [InlineData(6u, PrinterAvailability.Paused)]
        public void L_etat_general_se_lit_dans_son_code(uint status, PrinterAvailability expected)
        {
            // Par le code et non par le texte d'état, qui est traduit.
            Assert.Equal(expected, PrintingProbe.Classify(false, status, 0));
        }

        [Fact]
        public void Un_bourrage_prime_sur_un_etat_general_qui_dit_le_contraire()
        {
            // Une imprimante en bourrage papier est parfois annoncée « prête » par son état
            // général : c'est le défaut détecté qui décrit ce qui bloque réellement l'appareil.
            Assert.Equal(PrinterAvailability.Error, PrintingProbe.Classify(false, status: 3, detected: 8));
        }

        [Fact]
        public void Le_mode_hors_connexion_demande_se_lit_meme_sur_une_imprimante_prete()
        {
            Assert.Equal(PrinterAvailability.Offline, PrintingProbe.Classify(true, status: 3, detected: 0));
        }

        // ============================================================ spouleur

        [Fact]
        public void Un_spouleur_arrete_avec_des_imprimantes_est_un_probleme()
        {
            var finding = Single(Evaluate(b =>
                b.Printing(spoolerRunning: false, pendingJobs: 0, Fixtures.Builder.Printer("Laser du bureau"))),
                "PRN-001");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Un_spouleur_arrete_sans_imprimante_n_est_qu_une_information()
        {
            // Couper le spouleur sur une machine sans imprimante est un choix courant : en faire
            // une panne ferait perdre du temps sur une machine qui va bien.
            var finding = Single(Evaluate(b => b.Printing(spoolerRunning: false)), "PRN-001");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        // ============================================================ hors connexion

        [Fact]
        public void Une_imprimante_injoignable_est_signalee()
        {
            var finding = Single(Evaluate(b => b.Printing(true, 0,
                Fixtures.Builder.Printer("Laser du bureau", PrinterAvailability.Offline))), "PRN-002");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("Laser du bureau", finding.Title);
            Assert.Contains("éteinte, débranchée", finding.PlainExplanation);
        }

        [Fact]
        public void Le_mode_hors_connexion_demande_se_dit_autrement()
        {
            // Le client vit les deux de la même façon ; le technicien ne les traite pas pareil.
            // L'un se décoche d'un clic, l'autre demande de regarder le câble ou le réseau.
            var finding = Single(Evaluate(b => b.Printing(true, 0,
                Fixtures.Builder.Printer("Jet d'encre", PrinterAvailability.Offline, offlineByChoice: true))),
                "PRN-002");

            Assert.Contains("mode hors connexion", finding.Title);
            Assert.Contains("décoche", finding.PlainExplanation);
        }

        [Fact]
        public void Les_deux_situations_donnent_deux_constats_distincts()
        {
            var findings = Evaluate(b => b.Printing(true, 0,
                Fixtures.Builder.Printer("Laser", PrinterAvailability.Offline),
                Fixtures.Builder.Printer("Jet d'encre", PrinterAvailability.Offline, offlineByChoice: true,
                    isDefault: false)));

            Assert.Equal(2, findings.Count(finding => finding.RuleId == "PRN-002"));
        }

        // ============================================================ défauts de l'appareil

        [Fact]
        public void Un_manque_de_papier_est_signale()
        {
            var finding = Single(Evaluate(b => b.Printing(true, 0,
                Fixtures.Builder.Printer("Laser", PrinterAvailability.PaperOut))), "PRN-003");

            Assert.Contains("Plus de papier", finding.TechnicalDetail);
            Assert.Contains("devant l'appareil", finding.PlainExplanation);
        }

        // ============================================================ file d'attente

        [Fact]
        public void Des_documents_devant_une_imprimante_indisponible_sont_signales()
        {
            var finding = Single(Evaluate(b => b.Printing(true, 4,
                Fixtures.Builder.Printer("Laser", PrinterAvailability.Offline))), "PRN-004");

            Assert.Contains("4 document(s)", finding.TechnicalDetail);
        }

        [Fact]
        public void Des_documents_devant_une_imprimante_prete_ne_disent_rien()
        {
            // Une file qui avance n'est pas une file bloquée : c'est une impression en cours.
            var findings = Evaluate(b => b.Printing(true, 3, Fixtures.Builder.Printer("Laser")));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "PRN-004");
        }

        // ============================================================ absence d'imprimante

        [Fact]
        public void Sans_imprimante_les_regles_ne_concluent_pas()
        {
            // Une machine sans imprimante n'est pas une machine dont les imprimantes vont bien :
            // le score ne doit pas compter un contrôle qui n'a rien contrôlé.
            var score = new AnalysisEngine().Analyze(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Printing();
            })).Score!;

            Assert.DoesNotContain("PRN-002", score.EvaluatedRuleIds);
            Assert.DoesNotContain("PRN-003", score.EvaluatedRuleIds);
            Assert.Contains("PRN-001", score.EvaluatedRuleIds);
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
