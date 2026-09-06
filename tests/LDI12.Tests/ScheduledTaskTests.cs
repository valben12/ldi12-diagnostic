using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les tâches planifiées : ce qui se lance sans figurer au démarrage, et les codes qu'il ne
    /// faut pas lire comme des échecs.
    /// </summary>
    public class ScheduledTaskTests
    {
        // ============================================================ codes de retour

        [Theory]
        [InlineData(0x00041301)]      // en cours d'exécution
        [InlineData(0x00041303)]      // jamais exécutée
        [InlineData(0x00041300)]      // prête
        [InlineData(0x40010004)]      // interrompue par l'arrêt de la machine
        public void Un_code_non_nul_du_planificateur_n_est_pas_un_echec(int code)
        {
            // Relevé sur la machine de développement : cinq tâches sur quinze portaient un code
            // non nul, et pas une n'avait échoué. Un test sur « différent de zéro » aurait
            // annoncé cinq pannes là où il n'y en avait aucune.
            Assert.Equal(TaskResultKind.Informational, TaskResultCatalog.Classify(code));
        }

        [Fact]
        public void Un_code_de_Windows_est_un_echec_de_la_tache()
        {
            // 0x80070002 : fichier introuvable. Le bit de gravité est ce qui distingue « Windows
            // n'a pas pu lancer la tâche » de « le programme s'est plaint en se terminant ».
            Assert.Equal(TaskResultKind.TaskError, TaskResultCatalog.Classify(unchecked((int)0x80070002)));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(259)]
        public void Un_code_de_sortie_du_programme_se_dit_comme_tel(int code)
        {
            Assert.Equal(TaskResultKind.ProgramError, TaskResultCatalog.Classify(code));
        }

        [Fact]
        public void Un_code_absent_ne_conclut_rien()
        {
            Assert.Equal(TaskResultKind.Unknown, TaskResultCatalog.Classify(null));
            Assert.Equal(TaskResultKind.Success, TaskResultCatalog.Classify(0));
        }

        // ============================================================ tâches de démarrage

        [Theory]
        [InlineData(TaskTriggerKind.Logon, true, true)]
        [InlineData(TaskTriggerKind.Boot, true, true)]
        [InlineData(TaskTriggerKind.Logon, false, false)]      // désactivée : elle ne se lance pas
        [InlineData(TaskTriggerKind.Scheduled, true, false)]
        [InlineData(TaskTriggerKind.Registration, true, false)] // une seule fois, à l'installation
        public void Seules_les_taches_de_session_et_de_demarrage_comptent_comme_programmes_de_demarrage(
            TaskTriggerKind trigger, bool enabled, bool expected)
        {
            var task = Fixtures.Builder.Task(@"\Test", trigger, enabled: enabled);

            Assert.Equal(expected, task.RunsAtStartup);
        }

        // ============================================================ constats

        [Fact]
        public void Une_tache_que_Windows_ne_peut_plus_lancer_est_signalee()
        {
            var finding = Single(
                Evaluate(Fixtures.Builder.Task(@"\Éditeur\Mise à jour",
                    lastResult: unchecked((int)0x80070002))),
                "TSK-001");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        [Fact]
        public void Un_programme_qui_rend_un_code_non_nul_reste_une_information()
        {
            // Le technicien n'a souvent pas la main sur le code de sortie d'un utilitaire tiers,
            // et certains en rendent un dans leur fonctionnement normal.
            var finding = Single(Evaluate(Fixtures.Builder.Task(@"\Éditeur\Contrôle", lastResult: 1)), "TSK-001");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Une_tache_jamais_executee_n_a_pas_echoue()
        {
            // Sur la machine de développement, une tâche jamais lancée portait le code 0x41303 et
            // la date sentinelle de 1999 : lui donner un verdict ferait passer « pas encore
            // essayé » pour un échec.
            var findings = Evaluate(Fixtures.Builder.Task(@"\Éditeur\Neuve", everRun: false, lastResult: 0x00041303));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "TSK-001");
        }

        [Fact]
        public void Une_tache_desactivee_ne_produit_aucun_constat_d_echec()
        {
            var findings = Evaluate(
                Fixtures.Builder.Task(@"\Éditeur\Ancienne", enabled: false,
                    lastResult: unchecked((int)0x80070002)));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "TSK-001");
        }

        [Fact]
        public void Une_tache_orpheline_est_signalee()
        {
            var finding = Single(
                Evaluate(Fixtures.Builder.Task(@"\Éditeur\Quotidienne", targetMissing: true)), "TSK-002");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Une_tache_orpheline_de_demarrage_n_est_pas_comptee_deux_fois()
        {
            // Elle figure déjà parmi les programmes de démarrage, où la règle des entrées
            // orphelines la voit. La compter ici aussi doublerait la pénalité pour un seul
            // logiciel mal désinstallé.
            var findings = Evaluate(
                Fixtures.Builder.Task(@"\Éditeur\Session", TaskTriggerKind.Logon, targetMissing: true));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "TSK-002");
        }

        [Fact]
        public void Une_tache_qui_revient_toutes_les_cinq_minutes_est_signalee()
        {
            var finding = Single(
                Evaluate(Fixtures.Builder.Task(@"\Éditeur\Veille", repetitionMinutes: 5)), "TSK-003");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Une_tache_horaire_ne_derange_personne()
        {
            var findings = Evaluate(Fixtures.Builder.Task(@"\Éditeur\Horaire", repetitionMinutes: 60));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "TSK-003");
        }

        [Fact]
        public void Un_planificateur_illisible_ne_conclut_rien()
        {
            // Un service de planification cassé n'est pas une machine sans tâches : les trois
            // règles doivent se taire plutôt qu'annoncer qu'il n'y a rien à signaler.
            var findings = new AnalysisEngine().Analyze(Fixtures.Healthy()).Findings;

            foreach (var id in new[] { "TSK-001", "TSK-002", "TSK-003" })
                Assert.DoesNotContain(findings, finding => finding.RuleId == id);
        }

        // ============================================================ outillage

        private static IReadOnlyList<Finding> Evaluate(params ScheduledTaskInfo[] tasks)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                builder.Tasks(tasks);
            });

            return new AnalysisEngine().Analyze(snapshot).Findings;
        }

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));
    }
}
