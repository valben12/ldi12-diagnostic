using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Jusqu'à quand cette installation est suivie, et ce que la machine pourra recevoir ensuite.
    /// </summary>
    /// <remarks>
    /// Tous les tests qui portent sur une échéance fixent la date du relevé. C'est la même
    /// discipline que pour la chronologie : un test qui lit l'horloge raconte une histoire vraie
    /// jusqu'au jour où elle cesse de l'être, et il échoue alors sans que rien n'ait changé.
    /// </remarks>
    public class UpgradeTests
    {
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.FromHours(1));

        // ============================================================ le calendrier

        [Fact]
        public void Windows_10_n_est_plus_suivi_depuis_le_14_octobre_2025()
        {
            // La raison d'être de cette table : avec l'ancienne règle, qui listait les familles
            // périmées une fois pour toutes, Windows 10 restait « propre » sur tout le parc.
            var support = WindowsLifecycle.For(Ten("22H2"), Today);

            Assert.Equal(WindowsSupportState.Ended, support.State);
            Assert.Equal(new DateTime(2025, 10, 14), support.EndOfSupport!.Value.Date);
            Assert.Equal("Windows 10 22H2", support.VersionLabel);
        }

        [Fact]
        public void Une_branche_plus_ancienne_de_Windows_10_est_finie_sans_avoir_sa_propre_date()
        {
            var support = WindowsLifecycle.For(Ten("21H2"), Today);

            Assert.Equal(WindowsSupportState.Ended, support.State);
            Assert.Contains("avant le 14 octobre 2025", support.Reason);
        }

        [Fact]
        public void Le_jour_de_l_echeance_la_version_est_encore_suivie()
        {
            // Le support se termine à la fin de cette journée-là, pas à son début : annoncer la
            // panne le matin même serait faux d'une journée, et faux dans le mauvais sens.
            var eve = new DateTimeOffset(2026, 10, 13, 23, 0, 0, TimeSpan.FromHours(2));

            var support = WindowsLifecycle.For(Eleven("24H2"), eve);

            Assert.Equal(WindowsSupportState.Supported, support.State);
            Assert.Equal(0, support.DaysRemaining);
        }

        [Fact]
        public void Une_edition_entreprise_dispose_d_une_annee_de_plus()
        {
            var professional = WindowsLifecycle.For(Eleven("23H2"), Today);
            var enterprise = WindowsLifecycle.For(Eleven("23H2", "Enterprise"), Today);

            Assert.Equal(WindowsSupportState.Ended, professional.State);
            Assert.Equal(WindowsSupportState.Supported, enterprise.State);
        }

        [Fact]
        public void Une_version_absente_du_calendrier_le_dit_au_lieu_de_deviner()
        {
            // Le seul cas où ne rien conclure est la bonne réponse : une version postérieure à la
            // relecture de la table. Un « encore suivi » inventé rassurerait à tort.
            var support = WindowsLifecycle.For(Eleven("26H1"), Today);

            Assert.Equal(WindowsSupportState.Unknown, support.State);
            Assert.Null(support.EndOfSupport);
            Assert.Contains("26H1", support.Reason);
            Assert.Contains("calendrier embarqué", support.Reason);
        }

        // ============================================================ la liste de processeurs

        [Theory]
        [InlineData("Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz", CpuListIndication.LikelyListed)]
        [InlineData("Intel(R) Core(TM) i9-13900K", CpuListIndication.LikelyListed)]
        [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuListIndication.LikelyListed)]
        [InlineData("Intel(R) Core(TM) i5-7200U CPU @ 2.50GHz", CpuListIndication.LikelyNotListed)]
        [InlineData("Intel(R) Core(TM) i5-650 @ 3.20GHz", CpuListIndication.LikelyNotListed)]
        [InlineData("AMD Ryzen 5 3600 6-Core Processor", CpuListIndication.LikelyListed)]
        [InlineData("AMD Ryzen 7 1700 Eight-Core Processor", CpuListIndication.LikelyNotListed)]
        [InlineData("Intel(R) Core(TM)2 Duo CPU E8400", CpuListIndication.LikelyNotListed)]
        [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4", CpuListIndication.Unknown)]
        [InlineData("AMD Athlon Gold 3150U with Radeon Graphics", CpuListIndication.Unknown)]
        public void La_generation_du_processeur_se_lit_dans_sa_reference(string model, CpuListIndication expected)
            => Assert.Equal(expected, UpgradeAdvisor.ClassifyCpu(model));

        [Fact]
        public void Le_modele_du_processeur_reste_une_exigence_indecidee()
        {
            // Même favorable, l'indication ne conclut pas : la liste officielle n'est pas
            // embarquée, et une bonne nouvelle mal étayée est celle qu'on retient le mieux.
            var requirement = Requirement(Assess(Modern()), "cpu.model");

            Assert.Equal(RequirementOutcome.Undetermined, requirement.Outcome);
            Assert.Contains("liste officielle", requirement.Note);
        }

        [Fact]
        public void La_liste_de_processeurs_ne_rend_pas_le_verdict_indecis()
        {
            // Elle vaut pour toutes les machines : la faire peser sur le verdict rendrait toutes
            // les réponses indécises, et un outil qui répond « je ne sais pas » partout ne répond
            // nulle part.
            Assert.Equal(UpgradeVerdict.Eligible, Assess(Modern()).Verdict);
        }

        // ============================================================ les exigences

        [Fact]
        public void Un_processeur_32_bits_ferme_la_question()
        {
            var assessment = Assess(Modern(architecture: ProcessorArchitecture.X86));

            Assert.Equal(UpgradeVerdict.Ineligible, assessment.Verdict);
            Assert.Contains(assessment.With(RequirementOutcome.NotMet),
                requirement => requirement.Id == "cpu.architecture");
        }

        [Fact]
        public void Un_module_TPM_absent_est_un_reglage_a_verifier_et_non_une_condamnation()
        {
            // Windows n'expose rien dans les deux cas, module absent, ou module désactivé dans le
            // micrologiciel. Annoncer « pas de TPM » serait faux une fois sur deux, sur des
            // machines pourtant récentes où Intel PTT et AMD fTPM sortent d'usine désactivés.
            var assessment = Assess(Modern(tpmPresent: false));

            Assert.Equal(UpgradeVerdict.EligibleAfterSetting, assessment.Verdict);

            var tpm = Requirement(assessment, "tpm");
            Assert.Equal(RequirementOutcome.Fixable, tpm.Outcome);
            Assert.Contains("fTPM", tpm.Note);
        }

        [Fact]
        public void Un_module_TPM_en_1_2_est_un_blocage_materiel()
        {
            var assessment = Assess(Modern(tpmVersion: "1.2, 0, 1.38"));

            Assert.Equal(UpgradeVerdict.Ineligible, assessment.Verdict);
            Assert.Equal(RequirementOutcome.NotMet, Requirement(assessment, "tpm").Outcome);
        }

        [Fact]
        public void Le_TPM_illisible_faute_de_privileges_rend_le_verdict_indetermine()
        {
            var assessment = UpgradeAdvisor.Assess(Fixtures.Build(b =>
            {
                b.TakenOn(Today);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: true, tpm: true);
                b.Tpm(Measured.NeedsElevation<bool>("lecture de l'état du module TPM"),
                    Measured.NeedsElevation<bool>("lecture de l'activation du TPM"),
                    Measured.NeedsElevation<string>("lecture de la version du TPM"));
            }));

            Assert.Equal(UpgradeVerdict.Undetermined, assessment.Verdict);
            Assert.Contains("administrateur", Requirement(assessment, "tpm").Observation);
        }

        [Fact]
        public void Le_BIOS_herite_entraine_le_demarrage_securise_avec_lui()
        {
            // Le démarrage sécurisé n'existe qu'en mode UEFI : le signaler « désactivé » sans dire
            // qu'il est hors de portée enverrait le technicien le chercher dans un menu absent.
            var assessment = Assess(Modern(uefi: false));

            Assert.Equal(UpgradeVerdict.EligibleAfterSetting, assessment.Verdict);

            var fixable = assessment.With(RequirementOutcome.Fixable).Select(r => r.Id).ToList();
            Assert.Contains("firmware", fixable);
            Assert.Contains("secureboot", fixable);
            Assert.Contains("MBR", Requirement(assessment, "firmware").Observation);
        }

        [Fact]
        public void Trop_peu_de_memoire_est_un_blocage_qui_se_leve_sans_changer_de_machine()
        {
            var assessment = Assess(Modern(memoryBytes: 2L * 1024 * 1024 * 1024));

            Assert.Equal(UpgradeVerdict.Ineligible, assessment.Verdict);
            Assert.Contains("s'ajoute", Requirement(assessment, "memory").Note);
        }

        [Fact]
        public void Les_quatre_gigaoctets_annonces_sont_acceptes_malgre_la_reserve_du_firmware()
        {
            // Une machine à 4 Go en déclare toujours un peu moins : le chipset graphique en prend
            // sa part. Comparer à 4 Go exactement recalerait des machines conformes.
            var assessment = Assess(Modern(memoryBytes: 4_190_000_000L));

            Assert.Equal(RequirementOutcome.Met, Requirement(assessment, "memory").Outcome);
        }

        [Fact]
        public void Sur_Windows_11_il_n_y_a_rien_a_evaluer()
        {
            var assessment = UpgradeAdvisor.Assess(Fixtures.Build(b => b.Windows11()));

            Assert.Equal(UpgradeVerdict.AlreadyThere, assessment.Verdict);
            Assert.Empty(assessment.Requirements);
        }

        // ============================================================ les règles

        [Fact]
        public void Windows_10_est_signale_comme_n_etant_plus_suivi()
        {
            var finding = Single(Evaluate(b => { b.TakenOn(Today); b.Windows10(); }), "WIN-006");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("Windows 10 22H2", finding.TechnicalDetail);
            Assert.Contains("MIGRATE-SUPPORTED-WINDOWS", finding.Recommendations);
        }

        [Fact]
        public void Une_fin_de_support_proche_est_annoncee_avant_l_echeance()
        {
            // Cent jours avant : le constat existe pour laisser le temps de prévoir, pas pour
            // constater les dégâts.
            var summer = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.FromHours(2));
            var finding = Single(Evaluate(b => { b.TakenOn(summer); b.Windows11("24H2"); }), "SUP-001");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("100 jours", finding.TechnicalDetail);
        }

        [Fact]
        public void Une_echeance_lointaine_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => { b.TakenOn(Today); b.Windows11("24H2"); }),
                finding => finding.RuleId == "SUP-001");
        }

        [Fact]
        public void Une_version_hors_calendrier_n_est_pas_evaluee_plutot_que_declaree_saine()
        {
            var score = Analyze(b => { b.TakenOn(Today); b.Windows11("26H1"); }).Score!;

            Assert.DoesNotContain("WIN-006", score.EvaluatedRuleIds);
            Assert.DoesNotContain("SUP-001", score.EvaluatedRuleIds);
        }

        [Fact]
        public void Une_machine_inelligible_ne_perd_aucun_point()
        {
            // Elle n'est pas en panne : elle marche, souvent très bien. La pénaliser reviendrait à
            // noter son âge plutôt que son état.
            var finding = Single(Evaluate(b =>
            {
                b.TakenOn(Today);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: true, tpm: true);
                b.Memory(2, 40, 1, 2);
            }), "UPG-001");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Un_blocage_materiel_fait_taire_les_reglages()
        {
            // Les régler ne débloquerait rien, et le dire ferait espérer pour rien.
            var findings = Evaluate(b =>
            {
                b.TakenOn(Today);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: false, tpm: true);
                b.Memory(2, 40, 1, 2);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "UPG-002");
        }

        [Fact]
        public void Sur_une_version_hors_support_un_reglage_bloquant_devient_un_avertissement()
        {
            var expired = Single(Evaluate(b =>
            {
                b.TakenOn(Today);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: false, tpm: true);
            }), "UPG-002");

            // La même machine, un an plus tôt : Windows 10 était alors encore suivi. Prendre
            // Windows 11 pour le cas « encore suivi » ne dirait rien, la question ne s'y pose pas.
            var stillSupported = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.FromHours(1));
            var supported = Single(Evaluate(b =>
            {
                b.TakenOn(stillSupported);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: false, tpm: true);
            }), "UPG-002");

            Assert.Equal(Severity.Warning, expired.Severity);
            Assert.Equal(Severity.Info, supported.Severity);
            Assert.Contains("ENABLE-SECURE-BOOT", expired.Recommendations);
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_montre_le_verdict_et_la_limite_de_l_indication()
        {
            var facts = Facts(Fixtures.Build(b =>
            {
                b.TakenOn(Today);
                b.Windows10();
                Workstation(b, uefi: true, secureBoot: true, tpm: true);
            }));

            Assert.Equal("Possible", Value(facts, "Passage à Windows 11"));
            Assert.Contains("14 octobre 2025", Value(facts, "Fin de support"));
            Assert.Contains("liste officielle", Note(facts, "Modèle du processeur"));
        }

        [Fact]
        public void Une_fin_de_support_inconnue_n_est_pas_presentee_comme_une_date()
        {
            var facts = Facts(Fixtures.Build(b => { b.TakenOn(Today); b.Windows11("26H1"); }));

            Assert.Equal("Non connue", Value(facts, "Fin de support"));
            Assert.Contains("calendrier embarqué", Note(facts, "Fin de support"));
        }

        // ============================================================ montage

        private static WindowsProfile Ten(string version) => new WindowsProfile
        {
            Family = WindowsFamily.Windows10,
            Build = 19045,
            DisplayVersion = version,
            EditionId = "Professional",
            NativeArchitecture = ProcessorArchitecture.X64,
        };

        private static WindowsProfile Eleven(string version, string edition = "Professional") => new WindowsProfile
        {
            Family = WindowsFamily.Windows11,
            Build = 22631,
            DisplayVersion = version,
            EditionId = edition,
            NativeArchitecture = ProcessorArchitecture.X64,
        };

        /// <summary>Une machine qui remplit tout, et dont chaque test dérègle une seule chose.</summary>
        private static SystemSnapshot Modern(
            ProcessorArchitecture architecture = ProcessorArchitecture.X64,
            long memoryBytes = 16L * 1024 * 1024 * 1024,
            bool uefi = true,
            bool tpmPresent = true,
            string tpmVersion = "2.0, 0, 1.38")
            => Fixtures.Build(b =>
            {
                b.TakenOn(Today);
                b.Windows10(architecture: architecture);
                b.CpuModel("AMD Ryzen 5 3600 6-Core Processor");
                b.MemoryBytes(memoryBytes);
                b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
                b.Motherboard(uefi: uefi, secureBoot: true, tpm: true, biosAgeMonths: 12, chassis: "Tour");
                b.Tpm(Measured.Ok(tpmPresent, DataSource.Wmi), Measured.Ok(tpmPresent, DataSource.Wmi),
                    Measured.Ok(tpmVersion, DataSource.Wmi));
            });

        /// <summary>Le socle matériel commun aux tests de règles : processeur, disque, carte mère.</summary>
        private static void Workstation(Fixtures.Builder b, bool uefi, bool secureBoot, bool tpm)
        {
            b.CpuModel("AMD Ryzen 5 3600 6-Core Processor");
            b.Memory(16, 40, 2, 4);
            b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
            b.Motherboard(uefi: uefi, secureBoot: secureBoot, tpm: tpm, biosAgeMonths: 12, chassis: "Tour");
        }

        private static UpgradeAssessment Assess(SystemSnapshot snapshot) => UpgradeAdvisor.Assess(snapshot);

        private static UpgradeRequirement Requirement(UpgradeAssessment assessment, string id)
            => Assert.Single(assessment.Requirements.Where(requirement => requirement.Id == id));

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => Analyze(configure).Findings;

        private static SystemSnapshot Analyze(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(configure));

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static IReadOnlyList<Fact> Facts(SystemSnapshot snapshot)
        {
            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == "Support et passage à Windows 11") return group.Facts;

            throw new InvalidOperationException("Le cadre « Support et passage à Windows 11 » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;

        private static string Note(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Note ?? string.Empty;
    }
}
