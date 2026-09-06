using System.Collections.Generic;
using System.Linq;
using LDI12.Collectors.Hardware;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que le matériel a déjà signalé de lui-même, et les contresens qu'il ne faut pas en
    /// tirer.
    /// </summary>
    public class HardwareErrorTests
    {
        // ============================================================ qualification des erreurs

        [Theory]
        [InlineData(1, HardwareErrorKind.Uncorrected)]
        [InlineData(18, HardwareErrorKind.Uncorrected)]
        [InlineData(17, HardwareErrorKind.Corrected)]
        [InlineData(47, HardwareErrorKind.Corrected)]
        [InlineData(49, HardwareErrorKind.Corrected)]
        public void Chaque_identifiant_du_manifeste_est_qualifie_comme_Windows_le_declare(
            int eventId, HardwareErrorKind expected)
        {
            Assert.Equal(expected, WheaCatalog.Classify(eventId, level: null));
        }

        [Fact]
        public void L_evenement_29_est_irrecuperable_malgre_son_niveau_d_avertissement()
        {
            // Microsoft le déclare en Avertissement (niveau 3) avec un texte qui annonce une
            // erreur irrécupérable. Lire le seul niveau ferait passer la panne pour une erreur
            // rattrapée par le matériel : c'est toute la raison d'être de la table.
            Assert.Equal(HardwareErrorKind.Uncorrected, WheaCatalog.Classify(29, level: 3));
        }

        [Theory]
        [InlineData(2, HardwareErrorKind.Uncorrected)]
        [InlineData(3, HardwareErrorKind.Corrected)]
        [InlineData(4, HardwareErrorKind.Informational)]
        public void Un_identifiant_inconnu_se_qualifie_par_son_niveau(byte level, HardwareErrorKind expected)
        {
            // Repli pour une version future de Windows : mieux vaut une qualification déduite du
            // niveau qu'une erreur matérielle silencieusement classée « indéterminée ».
            Assert.Equal(expected, WheaCatalog.Classify(9_999, level));
        }

        [Theory]
        [InlineData(1101, MemoryTestOutcome.NoErrors)]
        [InlineData(1201, MemoryTestOutcome.NoErrors)]
        [InlineData(1102, MemoryTestOutcome.ErrorsFound)]
        [InlineData(1202, MemoryTestOutcome.ErrorsFound)]
        public void Le_verdict_du_test_memoire_vient_de_l_identifiant(int eventId, MemoryTestOutcome expected)
        {
            Assert.Equal(expected, MemoryTestCatalog.Classify(eventId));
        }

        [Theory]
        [InlineData(1103)]
        [InlineData(1104)]
        public void Un_test_memoire_interrompu_ne_vaut_pas_un_test_reussi(int eventId)
        {
            // Les deux sont enregistrés au niveau Information, comme un test sans erreur. Les
            // confondre reviendrait à déclarer saine une mémoire que personne n'a fini de tester.
            Assert.Equal(MemoryTestOutcome.Interrupted, MemoryTestCatalog.Classify(eventId));
        }

        // ============================================================ constats

        [Fact]
        public void Une_erreur_irrecuperable_est_un_constat_critique()
        {
            var finding = Single(Evaluate(b => b.HardwareErrors(uncorrected: 1)), "HWE-001");

            Assert.Equal(Severity.Critical, finding.Severity);
        }

        [Fact]
        public void Une_erreur_corrigee_isolee_ne_produit_aucun_constat()
        {
            // Une erreur rattrapée arrive sur du matériel sain : en faire un constat dès la
            // première remplirait les rapports de bruit et userait l'attention du technicien.
            var findings = Evaluate(b => b.HardwareErrors(corrected: 1));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "HWE-002");
        }

        [Fact]
        public void Des_erreurs_corrigees_a_repetition_finissent_par_se_dire()
        {
            var finding = Single(Evaluate(b => b.HardwareErrors(corrected: 40)), "HWE-002");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Un_test_memoire_en_echec_est_critique_et_un_test_reussi_ne_dit_rien()
        {
            var failed = Single(
                Evaluate(b => b.HardwareErrors(memoryTest: MemoryTestOutcome.ErrorsFound)), "HWE-003");
            Assert.Equal(Severity.Critical, failed.Severity);

            var passed = Evaluate(b => b.HardwareErrors(memoryTest: MemoryTestOutcome.NoErrors));
            Assert.DoesNotContain(passed, finding => finding.RuleId == "HWE-003");
        }

        [Fact]
        public void Le_test_memoire_n_est_reclame_que_sur_une_machine_qui_donne_des_signes()
        {
            // Sur une machine stable, réclamer un redémarrage de vingt minutes serait gratuit.
            var stable = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
                b.HardwareErrors();
            });
            Assert.DoesNotContain(stable, finding => finding.RuleId == "HWE-004");

            var unstable = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 2);
                b.HardwareErrors();
            });
            Assert.Contains(unstable, finding => finding.RuleId == "HWE-004");
        }

        [Fact]
        public void Un_arret_brutal_ne_suffit_pas_a_reclamer_un_test_memoire()
        {
            // Constaté sur la machine de développement : dix-huit arrêts inattendus, aucun écran
            // bleu, aucune erreur matérielle. Une coupure de courant ou un bouton maintenu
            // éteignent la machine sans que la mémoire y soit pour quoi que ce soit, la règle
            // des arrêts inattendus le dit déjà, et mieux.
            var findings = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 18, diskErrors: 0, bsods: 0);
                b.HardwareErrors();
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "HWE-004");
        }

        [Fact]
        public void Un_test_memoire_deja_passe_ne_se_redemande_pas()
        {
            // Le logiciel recommandait de tester la mémoire après un écran bleu sans jamais
            // regarder si ça avait été fait : il pouvait le redemander le lendemain d'un test.
            var findings = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 2);
                b.HardwareErrors(memoryTest: MemoryTestOutcome.NoErrors);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "HWE-004");
        }

        [Fact]
        public void Une_machine_qui_plante_sans_rien_enregistrer_est_signalee()
        {
            var findings = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 3);
                b.HardwareErrors(dumpsEnabled: false);
            });

            Assert.Contains(findings, finding => finding.RuleId == "HWE-006");
        }

        [Fact]
        public void Une_machine_stable_sans_rapports_de_plantage_n_a_rien_a_se_reprocher()
        {
            // Le réglage ne coûte rien tant que la machine ne plante pas : le signaler partout
            // ferait perdre au constat le seul moment où il compte.
            var findings = Evaluate(b =>
            {
                b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
                b.HardwareErrors(dumpsEnabled: false);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "HWE-006");
        }

        [Fact]
        public void Les_rapports_presents_sont_annonces_sans_peser_sur_la_note()
        {
            var finding = Single(Evaluate(b => b.HardwareErrors(crashDumps: 3)), "HWE-005");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Sans_lecture_des_journaux_aucune_regle_ne_conclut()
        {
            // Le journal illisible n'est pas une machine saine : sur une capture où rien n'a été
            // relevé, les six règles doivent se taire plutôt que d'annoncer l'absence d'erreur.
            var findings = new AnalysisEngine().Analyze(Fixtures.Healthy()).Findings;

            foreach (var id in new[] { "HWE-001", "HWE-002", "HWE-003", "HWE-004", "HWE-005", "HWE-006" })
                Assert.DoesNotContain(findings, finding => finding.RuleId == id);
        }

        // ============================================================ chemin du rapport

        [Theory]
        [InlineData(
            "L'ordinateur a redémarré après un bogue. Le bogue était : 0x0000009f. " +
            "Un vidage a été enregistré dans : C:\\WINDOWS\\MEMORY.DMP. ID de rapport : abcd.",
            "C:\\WINDOWS\\MEMORY.DMP")]
        [InlineData(
            "Un vidage a été enregistré dans : D:\\Dumps atelier\\070126-9999-01.dmp.",
            "D:\\Dumps atelier\\070126-9999-01.dmp")]
        public void Le_chemin_du_rapport_se_lit_sans_lire_la_phrase(string description, string expected)
        {
            // Même technique que le code d'arrêt : un chemin de fichier ne se traduit pas, la
            // phrase qui l'entoure si. Sans cela la colonne « Fichier de vidage » du rapport
            // restait vide sur toutes les machines.
            Assert.Equal(expected, LDI12.Collectors.Windows.EventsProbe.ExtractDumpPath(description));
        }

        [Theory]
        [InlineData("Aucun chemin ici.")]
        [InlineData("Le fichier .dmp n'a pas pu être écrit.")]
        public void Une_phrase_sans_chemin_n_en_invente_pas(string description)
        {
            Assert.Null(LDI12.Collectors.Windows.EventsProbe.ExtractDumpPath(description));
        }

        // ============================================================ outillage

        private static IReadOnlyList<Finding> Evaluate(System.Action<Fixtures.Builder> configure)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

            return new AnalysisEngine().Analyze(snapshot).Findings;
        }

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));
    }
}
