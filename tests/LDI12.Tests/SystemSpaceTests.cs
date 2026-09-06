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
    /// De quoi le disque est plein, et pas seulement qu'il l'est.
    /// </summary>
    public class SystemSpaceTests
    {
        private const long Gigabyte = 1024L * 1024 * 1024;

        // ============================================================ part du disque

        [Fact]
        public void Sur_un_petit_disque_les_fichiers_caches_de_Windows_sont_nommes()
        {
            // Cinquante giga-octets sont un détail sur un téraoctet et un cinquième d'un portable
            // à deux cent cinquante : c'est le rapport qui décide.
            var finding = Single(Evaluate(b =>
            {
                b.Volume("C", totalGb: 250, freeGb: 100, isSystem: true);
                b.SystemFile("hiberfil.sys", SpaceKind.Hibernation, 12 * Gigabyte);
                b.SystemFile("pagefile.sys", SpaceKind.PageFile, 24 * Gigabyte);
            }), "SPA-001");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("hiberfil.sys", finding.TechnicalDetail);
        }

        [Fact]
        public void Les_memes_fichiers_sur_un_grand_disque_ne_disent_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Volume("C", totalGb: 1000, freeGb: 400, isSystem: true);
                    b.SystemFile("hiberfil.sys", SpaceKind.Hibernation, 12 * Gigabyte);
                    b.SystemFile("pagefile.sys", SpaceKind.PageFile, 24 * Gigabyte);
                }),
                finding => finding.RuleId == "SPA-001");
        }

        [Fact]
        public void Quand_la_place_manque_deja_le_meme_constat_devient_un_avertissement()
        {
            // Le même fait se lit autrement selon la place qui reste : une curiosité quand il en
            // reste la moitié, le premier levier quand il n'en reste plus rien.
            var finding = Single(Evaluate(b =>
            {
                b.Volume("C", totalGb: 250, freeGb: 20, isSystem: true);
                b.SystemFile("hiberfil.sys", SpaceKind.Hibernation, 12 * Gigabyte);
                b.SystemFile("pagefile.sys", SpaceKind.PageFile, 24 * Gigabyte);
            }), "SPA-001");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("le plus à récupérer", finding.PlainExplanation);
        }

        // ============================================================ fichier d'échange

        [Fact]
        public void Un_fichier_d_echange_fixe_a_la_main_est_signale()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Volume("C", totalGb: 500, freeGb: 200, isSystem: true);
                b.Memory(8, 40, 2, 4);
                b.SystemFile("pagefile.sys", SpaceKind.PageFile, 64 * Gigabyte);
            }), "SPA-002");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("saisie à la main", finding.PlainExplanation);
            Assert.Contains("RESTORE-PAGE-FILE", finding.Recommendations);
        }

        [Fact]
        public void Un_fichier_d_echange_dimensionne_par_Windows_ne_dit_rien()
        {
            // Windows va jusqu'à trois fois la mémoire installée : en deçà, il fait son travail.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Volume("C", totalGb: 1000, freeGb: 400, isSystem: true);
                    b.Memory(16, 40, 2, 4);
                    b.SystemFile("pagefile.sys", SpaceKind.PageFile, 44 * Gigabyte);
                }),
                finding => finding.RuleId == "SPA-002");
        }

        // ============================================================ Windows.old

        [Fact]
        public void Une_ancienne_installation_restee_un_mois_est_un_avertissement()
        {
            // Windows la supprime seul au bout de dix jours : au-delà, la suppression a échoué.
            var finding = Single(Evaluate(b =>
                b.PreviousWindows(daysAgo: 90)), "SPA-003");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("dix jours", finding.PlainExplanation);
        }

        [Fact]
        public void Une_installation_recente_laisse_le_temps_du_retour_en_arriere()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.PreviousWindows(daysAgo: 3)),
                finding => finding.RuleId == "SPA-003");
        }

        // ============================================================ vidage mémoire

        [Fact]
        public void Un_vidage_memoire_date_le_dernier_ecran_bleu()
        {
            var finding = Single(Evaluate(b =>
                b.SystemFile("MEMORY.DMP", SpaceKind.CrashDump, 16 * Gigabyte, daysAgo: 12)), "SPA-004");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("écran bleu", finding.PlainExplanation);
        }

        // ============================================================ fichiers oubliés

        [Fact]
        public void Un_fichier_volumineux_a_la_racine_est_montre_sans_rien_proposer()
        {
            // Seul le client sait s'il compte encore : le constat le nomme et s'arrête là.
            var finding = Single(Evaluate(b =>
                b.SystemFile("sauvegarde.iso", SpaceKind.LooseFile, 8 * Gigabyte)), "SPA-005");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("sauvegarde.iso", finding.TechnicalDetail);
            Assert.Empty(finding.Recommendations);
        }

        [Fact]
        public void Les_fichiers_de_la_racine_ne_comptent_pas_dans_la_part_des_fichiers_systeme()
        {
            // Ce ne sont pas des fichiers de Windows : les mêler ferait porter à Windows une
            // place que quelqu'un a occupée lui-même.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Volume("C", totalGb: 250, freeGb: 100, isSystem: true);
                    b.SystemFile("machine.vhdx", SpaceKind.LooseFile, 60 * Gigabyte);
                }),
                finding => finding.RuleId == "SPA-001");
        }

        // ============================================================ sans relevé

        [Fact]
        public void Sans_releve_rien_n_est_conclu()
        {
            var score = new AnalysisEngine().Analyze(Fixtures.Build(b => b.Windows11())).Score!;

            Assert.DoesNotContain("SPA-001", score.EvaluatedRuleIds);
            Assert.DoesNotContain("SPA-002", score.EvaluatedRuleIds);
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_dit_ce_que_chaque_element_coute_a_recuperer()
        {
            // Une taille seule invite à supprimer ; une taille accompagnée de sa contrepartie
            // permet de décider.
            var facts = Group(b =>
            {
                b.Volume("C", totalGb: 500, freeGb: 200, isSystem: true);
                b.SystemFile("hiberfil.sys", SpaceKind.Hibernation, 12 * Gigabyte);
            });

            Assert.Equal("C:\\", Value(facts, "Relevé sur"));
            Assert.Contains(facts, fact => fact.Label == "Part du volume");
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
                    if (group.Title == "Occupation du disque système") return group.Facts;

            throw new InvalidOperationException("Le cadre « Occupation du disque système » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;
    }
}
