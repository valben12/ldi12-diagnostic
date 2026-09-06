using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce qui permettrait de revenir en arrière, le relevé qu'on lit avant d'intervenir.
    /// </summary>
    public class SafetyNetTests
    {
        // ============================================================ protection en échec

        [Fact]
        public void Une_protection_qui_echoue_parfois_est_un_avertissement()
        {
            var finding = Single(Evaluate(b => b.SafetyNet(pointsCreated: 6, failures: 3)), "SAF-001");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        [Fact]
        public void Une_protection_qui_n_a_jamais_reussi_est_un_probleme()
        {
            // Le pire des cas parce qu'il est invisible : la case reste cochée dans les réglages
            // de Windows, et c'est le point créé avant une réparation qui échouera.
            var finding = Single(Evaluate(b => b.SafetyNet(pointsCreated: 0, failures: 4)), "SAF-001");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("réglages affirment le contraire", finding.PlainExplanation);
        }

        [Fact]
        public void Une_protection_qui_fonctionne_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.SafetyNet(pointsCreated: 6, failures: 0)),
                finding => finding.RuleId == "SAF-001");
        }

        // ============================================================ aucun filet

        [Fact]
        public void Sans_point_sans_sauvegarde_et_donnees_sur_le_disque_systeme_le_constat_est_pose()
        {
            var finding = Single(Evaluate(b =>
            {
                b.SafetyNet(pointsCreated: 0);
                b.PersonalFolders(onSystemVolume: true);
            }), "SAF-002");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("Documents", finding.TechnicalDetail);
            Assert.Contains("BACKUP-NOW", finding.Recommendations);
        }

        [Fact]
        public void Une_sauvegarde_reperee_suffit_a_lever_le_constat()
        {
            // Un seul des trois éléments manquants ne dit rien : c'est la conjonction qui compte.
            var findings = Evaluate(b =>
            {
                b.SafetyNet(pointsCreated: 0);
                b.PersonalFolders(onSystemVolume: true);
                b.BackupDetected(BackupKind.Cloud, "OneDrive");
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "SAF-002");
        }

        [Fact]
        public void Des_donnees_sur_un_autre_disque_levent_le_constat()
        {
            var findings = Evaluate(b =>
            {
                b.SafetyNet(pointsCreated: 0);
                b.PersonalFolders(onSystemVolume: false);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "SAF-002");
        }

        [Fact]
        public void Un_point_de_restauration_recent_leve_le_constat()
        {
            var findings = Evaluate(b =>
            {
                b.SafetyNet(pointsCreated: 4);
                b.PersonalFolders(onSystemVolume: true);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "SAF-002");
        }

        [Fact]
        public void Une_protection_desactivee_annule_les_points_deja_crees()
        {
            // Des points ont été créés avant qu'on ne coupe la protection : ils ne protègent plus
            // ce qui va suivre, et compter dessus reviendrait à annoncer un filet retiré depuis.
            var finding = Single(Evaluate(b =>
            {
                b.SafetyNet(restoreEnabled: false, pointsCreated: 4);
                b.PersonalFolders(onSystemVolume: true);
            }), "SAF-002");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        // ============================================================ dossiers introuvables

        [Fact]
        public void Un_dossier_personnel_qui_pointe_dans_le_vide_est_un_probleme()
        {
            // Le disque secondaire débranché ou tombé en panne : Windows affiche toujours
            // « Documents », et le dossier ne pointe plus sur rien.
            var finding = Single(Evaluate(b =>
            {
                b.SafetyNet();
                b.PersonalFolders(onSystemVolume: false, exists: false);
            }), "SAF-003");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("D:", finding.TechnicalDetail);
        }

        [Fact]
        public void Des_dossiers_accessibles_ne_disent_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => { b.SafetyNet(); b.PersonalFolders(onSystemVolume: false); }),
                finding => finding.RuleId == "SAF-003");
        }

        // ============================================================ environnement de récupération

        [Theory]
        [InlineData(RecoveryEnvironmentState.Disabled, "désactivé")]
        [InlineData(RecoveryEnvironmentState.Missing, "Aucun environnement")]
        public void Une_recuperation_indisponible_est_annoncee(RecoveryEnvironmentState state, string expected)
        {
            var finding = Single(Evaluate(b => b.SafetyNet(recovery: state)), "SAF-004");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains(expected, finding.Title);
        }

        [Fact]
        public void Une_recuperation_installee_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.SafetyNet(recovery: RecoveryEnvironmentState.Installed)),
                finding => finding.RuleId == "SAF-004");
        }

        // ============================================================ mesure contre déduction

        [Fact]
        public void La_mesure_du_registre_l_emporte_sur_la_deduction()
        {
            // Le relevé d'installation déduit « probablement active » de la présence de la
            // fonction ; le filet de sécurité lit les valeurs qui la commandent. Quand les deux
            // se contredisent, c'est la mesure qui décide, sans quoi une protection réellement
            // coupée resterait annoncée active.
            var finding = Single(Evaluate(b =>
            {
                b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 2, restoreEnabled: true);
                b.SafetyNet(restoreEnabled: false);
            }), "WIN-007");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        // ============================================================ absence de mesure

        [Fact]
        public void Sans_releve_du_filet_aucune_regle_ne_conclut()
        {
            // Une machine dont on n'a pas pu lire la protection n'est pas une machine sans filet.
            var score = new AnalysisEngine().Analyze(Fixtures.Healthy()).Score!;

            foreach (var id in new[] { "SAF-001", "SAF-002", "SAF-003", "SAF-004" })
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
