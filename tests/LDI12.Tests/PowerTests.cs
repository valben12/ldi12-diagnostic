using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Collectors.Performance;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les réglages qui brident une machine que rien ne casse.
    /// </summary>
    public class PowerTests
    {
        // ============================================================ plans d'alimentation

        [Theory]
        [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", PowerPlanKind.Balanced)]
        [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", PowerPlanKind.HighPerformance)]
        [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a", PowerPlanKind.PowerSaver)]
        [InlineData("381B4222-F694-41F0-9685-FF5BB260DF2E", PowerPlanKind.Balanced)]
        public void Un_plan_de_Windows_se_reconnait_a_son_identifiant(string id, PowerPlanKind expected)
        {
            // Par l'identifiant et non par le nom affiché : celui-ci est une chaîne indirecte
            // pointant dans une ressource traduite, illisible sans analyser du texte localisé.
            Assert.Equal(expected, PowerProbe.Classify(id));
        }

        [Fact]
        public void Un_plan_inconnu_reste_personnalise_plutot_que_range_de_force()
        {
            // Constructeurs et techniciens créent leurs propres plans : les ranger dans une case
            // connue reviendrait à affirmer ce qu'on n'a pas mesuré.
            Assert.Equal(PowerPlanKind.Custom, PowerProbe.Classify("11111111-2222-3333-4444-555555555555"));
        }

        // ============================================================ bridage du processeur

        [Fact]
        public void Un_processeur_bride_a_la_moitie_est_un_probleme()
        {
            var finding = Single(Evaluate(b => b.Power(maximumOnAc: 50)), "PWR-001");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Un_bridage_leger_reste_un_avertissement()
        {
            var finding = Single(Evaluate(b => b.Power(maximumOnAc: 90)), "PWR-001");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        [Fact]
        public void Un_processeur_libre_ne_produit_aucun_constat()
        {
            var findings = Evaluate(b => b.Power(maximumOnAc: 100));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "PWR-001");
        }

        // ============================================================ plan d'économie

        [Fact]
        public void L_economie_d_energie_sur_un_poste_fixe_est_un_avertissement()
        {
            var finding = Single(Evaluate(b => b.Power(PowerPlanKind.PowerSaver)), "PWR-002");

            Assert.Equal(Severity.Warning, finding.Severity);
        }

        [Fact]
        public void L_economie_d_energie_sur_un_portable_reste_une_information()
        {
            // Le client a peut-être besoin de son autonomie : c'est un choix, pas un défaut.
            var finding = Single(Evaluate(b =>
            {
                b.Battery();
                b.Power(PowerPlanKind.PowerSaver);
            }), "PWR-002");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        // ============================================================ fichier d'échange

        [Fact]
        public void Une_machine_modeste_sans_fichier_d_echange_est_un_probleme()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Memory(totalGb: 4, usagePercent: 60, modules: 1, slots: 2);
                b.Power(pageFile: PageFileMode.Disabled);
            }), "PWR-003");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Un_fichier_d_echange_gere_par_Windows_ne_dit_rien()
        {
            var findings = Evaluate(b => b.Power(pageFile: PageFileMode.SystemManaged));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "PWR-003");
        }

        // ============================================================ notification de suppression

        [Fact]
        public void La_notification_coupee_ne_compte_qu_en_presence_de_memoire_flash()
        {
            // Sur un disque à plateaux, le réglage ne change rigoureusement rien : le constat
            // serait un reproche sans conséquence.
            var mechanical = Evaluate(b =>
            {
                b.Disk(0, StorageMediaType.Hdd, SmartOverallStatus.Ok, isSystem: true);
                b.Maintenance(trim: false);
            });
            Assert.DoesNotContain(mechanical, finding => finding.RuleId == "PWR-004");

            var flash = Evaluate(b =>
            {
                b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
                b.Maintenance(trim: false);
            });
            Assert.Contains(flash, finding => finding.RuleId == "PWR-004");
        }

        [Fact]
        public void Une_notification_active_ne_dit_rien()
        {
            var findings = Evaluate(b =>
            {
                b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
                b.Maintenance(trim: true);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "PWR-004");
        }

        // ============================================================ absence de mesure

        [Fact]
        public void Sans_relevé_d_alimentation_aucune_regle_ne_conclut()
        {
            // Un registre illisible n'est pas une machine bien réglée : les quatre règles se
            // taisent plutôt que d'annoncer qu'il n'y a rien à signaler.
            var findings = new AnalysisEngine().Analyze(Fixtures.Healthy()).Findings;

            foreach (var id in new[] { "PWR-001", "PWR-002", "PWR-003", "PWR-004" })
                Assert.DoesNotContain(findings, finding => finding.RuleId == id);
        }

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
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
