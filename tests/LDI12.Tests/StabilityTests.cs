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
    /// « Depuis quand ? » : la question à laquelle un relevé instantané ne sait pas répondre.
    /// </summary>
    public class StabilityTests
    {
        // ============================================================ ce qui plante

        [Fact]
        public void Un_programme_qui_plante_a_repetition_est_signale()
        {
            var finding = Single(Evaluate(b =>
                b.Stability("navigateur.exe", IncidentKind.ProgramCrash, new[] { 1, 2, 3, 5, 8, 11 })), "STA-001");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("navigateur.exe", finding.TechnicalDetail);
        }

        [Fact]
        public void Un_programme_qui_ne_plante_plus_n_est_plus_un_probleme()
        {
            // Trente plantages d'un jeu désinstallé il y a deux mois ne sont plus une panne : le
            // client ne les vit plus. Le constat reste, sa gravité tombe.
            var finding = Single(Evaluate(b =>
                b.Stability("jeu.exe", IncidentKind.ProgramCrash, new[] { 60, 61, 62, 63, 64, 70 })), "STA-001");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("Plus rien depuis", finding.TechnicalDetail);
        }

        [Fact]
        public void Un_plantage_isole_ne_declenche_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.Stability("outil.exe", IncidentKind.ProgramCrash, new[] { 2, 4 })),
                finding => finding.RuleId == "STA-001");
        }

        [Fact]
        public void Un_service_qui_tombe_a_repetition_est_signale()
        {
            var finding = Single(Evaluate(b =>
                b.Stability("Spouleur d'impression", IncidentKind.ServiceCrash, new[] { 1, 2, 3, 4, 9 })), "STA-002");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("Spouleur", finding.TechnicalDetail);
        }

        [Fact]
        public void Un_composant_commun_a_tous_les_plantages_est_nomme()
        {
            // Un même module derrière plusieurs programmes désigne un pilote ou une bibliothèque
            // partagée, pas les programmes : c'est la piste la plus utile du relevé.
            var finding = Single(Evaluate(b =>
            {
                b.Stability("a.exe", IncidentKind.ProgramCrash, new[] { 1, 2, 3, 4, 5 }, module: "pilote3d.dll");
                b.Stability("b.exe", IncidentKind.ProgramCrash, new[] { 1, 2, 3, 4, 6 }, module: "pilote3d.dll");
            }), "STA-001");

            Assert.Contains("pilote3d.dll", finding.TechnicalDetail);
        }

        [Fact]
        public void Des_composants_differents_ne_designent_rien()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Stability("a.exe", IncidentKind.ProgramCrash, new[] { 1, 2, 3, 4, 5 }, module: "a.dll");
                b.Stability("b.exe", IncidentKind.ProgramCrash, new[] { 1, 2, 3, 4, 6 }, module: "b.dll");
            }), "STA-001");

            Assert.DoesNotContain("Composant mis en cause", finding.TechnicalDetail);
        }

        [Fact]
        public void Le_module_que_Windows_dit_ignorer_n_est_pas_un_composant()
        {
            // Windows écrit « unknown » dans le champ du module quand il ne l'a pas identifié.
            // Recopié tel quel, ce mot se lirait comme le nom d'un composant.
            Assert.Null(StabilityProbe.NormalizeModule("unknown"));
            Assert.Null(StabilityProbe.NormalizeModule("  "));
            Assert.Equal("nvwgf2umx.dll", StabilityProbe.NormalizeModule(" nvwgf2umx.dll "));
        }

        // ============================================================ depuis quand

        [Fact]
        public void Une_degradation_est_datee_du_jour_ou_le_rythme_a_change()
        {
            // Calme pendant deux mois, puis une panne presque tous les jours depuis le douzième
            // jour. La date attendue est celle-là, et non la borne de la fenêtre d'analyse : une
            // borne serait sortie identique sur n'importe quelle machine.
            var finding = Single(Evaluate(Degraded), "STA-003");

            Assert.Contains(ValueFormat.Date(DateTimeOffset.Now.AddDays(-12)), finding.Title);
        }

        [Fact]
        public void Une_machine_qui_a_toujours_plante_n_est_pas_une_degradation()
        {
            // Un rythme régulier depuis trois mois est peut-être un problème (les autres règles
            // le disent) mais ce n'est pas une dégradation, et l'annoncer comme telle enverrait
            // le technicien chercher un changement qui n'existe pas.
            var findings = Evaluate(b =>
            {
                var days = new List<int>();
                for (var day = 2; day < 84; day += 4) days.Add(day);
                b.Stability("navigateur.exe", IncidentKind.ProgramCrash, days);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "STA-003");
        }

        [Fact]
        public void Une_degradation_terminee_n_est_plus_annoncee()
        {
            // Une mauvaise semaine en juin ne doit pas être annoncée en septembre comme si la
            // machine était en train de se dégrader.
            var findings = Evaluate(b =>
                b.Stability("navigateur.exe", IncidentKind.ProgramCrash,
                    new[] { 40, 41, 42, 43, 44, 45, 46, 47, 48, 49 }));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "STA-003");
        }

        [Fact]
        public void Un_journal_trop_court_interdit_de_comparer()
        {
            // Sur une machine réinstallée la semaine dernière, tout est récent par construction.
            // La règle ne conclut pas, et surtout ne pénalise pas.
            var score = Analyze(b => b.Stability(
                "navigateur.exe", IncidentKind.ProgramCrash,
                new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, coveredDays: 12));

            Assert.DoesNotContain("STA-003", score.EvaluatedRuleIds);
        }

        [Fact]
        public void Une_machine_qui_se_degrade_a_bien_ete_evaluee()
        {
            // Le pendant du test précédent : sans lui, « aucun constat » et « pas évalué » se
            // confondraient, et la règle pourrait ne jamais s'exécuter sans que rien ne le dise.
            Assert.Contains("STA-003", Analyze(Degraded).EvaluatedRuleIds);
        }

        // ============================================================ ce qui a changé avant

        [Fact]
        public void Ce_qui_a_change_avant_la_degradation_est_montre_sans_etre_accuse()
        {
            var finding = Single(Evaluate(b =>
            {
                Degraded(b);
                b.Software("Pilote imprimante", installedDaysAgo: 15);
            }), "STA-003");

            Assert.Contains("Pilote imprimante", finding.TechnicalDetail);

            // La phrase pour le client dit que c'est une coïncidence de dates, et comment la
            // vérifier. C'est la seule chose qui sépare une piste d'une accusation.
            Assert.Contains("coïncidence", finding.PlainExplanation);
            Assert.Contains("non une cause démontrée", finding.PlainExplanation);
        }

        [Fact]
        public void Un_changement_trop_ancien_n_est_pas_rapproche()
        {
            // Un logiciel installé un mois avant le début des pannes n'a rien à voir avec elles.
            var finding = Single(Evaluate(b =>
            {
                Degraded(b);
                b.Software("Traitement de texte", installedDaysAgo: 45);
            }), "STA-003");

            Assert.DoesNotContain("Traitement de texte", finding.TechnicalDetail);
        }

        // ============================================================ chronologie

        [Fact]
        public void La_chronologie_distingue_ce_qu_on_subit_de_ce_qu_on_fait()
        {
            var timeline = TimelineBuilder.Build(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Stability("navigateur.exe", IncidentKind.ProgramCrash, new[] { 3 });
                b.Software("Un logiciel", installedDaysAgo: 4);
            }));

            var incident = Assert.Single(timeline.Entries.Where(entry => entry.IsIncident));
            var change = Assert.Single(timeline.Entries.Where(entry => !entry.IsIncident));

            Assert.Equal(TimelineKind.ProgramCrash, incident.Kind);
            Assert.Equal(TimelineKind.SoftwareInstalled, change.Kind);

            // Du plus récent au plus ancien : l'ordre dans lequel la question se pose.
            Assert.True(timeline.Entries[0].Date >= timeline.Entries[1].Date);
        }

        [Fact]
        public void Une_date_posterieure_au_releve_est_ecartee()
        {
            // Horloge remise à l'heure, fuseau, ou installeur qui écrit n'importe quoi : une date
            // dans le futur de la chronologie ne dit rien, et n'est pas corrigée en silence.
            var timeline = TimelineBuilder.Build(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Software("Logiciel venu du futur", installedDaysAgo: -30);
            }));

            Assert.Empty(timeline.Entries);
        }

        [Fact]
        public void La_periode_reellement_couverte_est_annoncee()
        {
            // Un journal purgé ne se lit pas comme une période calme : ce que la chronologie
            // couvre est dit, et c'est cette borne que la règle de tendance utilise.
            var timeline = TimelineBuilder.Build(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Stability("navigateur.exe", IncidentKind.ProgramCrash, new[] { 3 }, coveredDays: 20);
            }));

            Assert.NotNull(timeline.CoveredSince);
            Assert.InRange((timeline.ReferenceDate - timeline.CoveredSince!.Value).TotalDays, 19, 21);
        }

        // ============================================================ absence de mesure

        [Fact]
        public void Un_historique_illisible_ne_penalise_pas_la_machine()
        {
            var score = Analyze(b => b.StabilityUnreadable("Le journal « Application » n'a pas pu être lu."));

            foreach (var id in new[] { "STA-001", "STA-002", "STA-003" })
                Assert.DoesNotContain(id, score.EvaluatedRuleIds);
        }

        /// <summary>Deux mois calmes, puis une panne presque chaque jour depuis douze jours.</summary>
        private static void Degraded(Fixtures.Builder b)
        {
            b.Stability("ancien.exe", IncidentKind.ProgramCrash, new[] { 80, 68, 55, 41, 30 });
            b.Stability("navigateur.exe", IncidentKind.ProgramCrash,
                new[] { 12, 11, 10, 9, 7, 6, 4, 3, 2, 1 }, coveredDays: 90);
        }

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Snapshot(configure)).Findings;

        private static DiagnosticScore Analyze(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Snapshot(configure)).Score!;

        private static SystemSnapshot Snapshot(Action<Fixtures.Builder> configure)
            => Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));
    }
}
