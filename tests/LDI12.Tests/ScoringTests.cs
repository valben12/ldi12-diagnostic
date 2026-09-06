using System;
using System.Collections.Generic;
using System.Reflection;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Profile;
using LDI12.Engine.Recommendations;
using LDI12.Engine.Rules;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le barème est la partie du logiciel la plus facile à casser silencieusement : un seuil
    /// modifié, et tous les diagnostics changent sans que rien ne signale l'écart. Ces tests
    /// tiennent la promesse « chaque perte de points doit pouvoir être expliquée ».
    /// </summary>
    public class ScoringTests
    {
        private static SystemSnapshot Analyze(SystemSnapshot snapshot)
            => new AnalysisEngine().Analyze(snapshot);

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_grand_livre_se_recompose_exactement(string name, SystemSnapshot snapshot)
        {
            // L'invariant central : somme des pénalités + score brut = 100, sur chaque dimension.
            // S'il tombe, c'est qu'un point est retiré sans ligne de justification.
            var score = Analyze(snapshot).Score!;

            foreach (var dimension in score.Dimensions)
            {
                if (!dimension.IsEvaluated) continue;

                var sum = 0;
                foreach (var penalty in dimension.Ledger) sum += penalty.Points;

                Assert.True(sum + dimension.RawScore!.Value == 100 || dimension.RawScore.Value == 0,
                    name + " / " + dimension.Dimension + " : " + sum + " points retirés pour un score brut de " +
                    dimension.RawScore.Value + " : la somme ne se recompose pas.");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Chaque_constat_a_sa_ligne_au_grand_livre(string name, SystemSnapshot snapshot)
        {
            var analyzed = Analyze(snapshot);
            var ledgerKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dimension in analyzed.Score!.Dimensions)
                foreach (var penalty in dimension.Ledger) ledgerKeys.Add(penalty.FindingKey);

            foreach (var finding in analyzed.Findings)
            {
                if (finding.Category == DiagnosticCategory.Platform) continue;
                Assert.True(ledgerKeys.Contains(finding.Key),
                    name + " : le constat " + finding.Key + " n'apparaît pas au grand livre.");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Aucune_ligne_du_grand_livre_n_est_orpheline(string name, SystemSnapshot snapshot)
        {
            var analyzed = Analyze(snapshot);
            var findingKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in analyzed.Findings) findingKeys.Add(finding.Key);

            foreach (var dimension in analyzed.Score!.Dimensions)
                foreach (var penalty in dimension.Ledger)
                    Assert.True(findingKeys.Contains(penalty.FindingKey),
                        name + " : la pénalité " + penalty.FindingKey + " ne correspond à aucun constat.");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_score_reste_dans_les_bornes(string name, SystemSnapshot snapshot)
        {
            var score = Analyze(snapshot).Score!;
            Assert.InRange(score.Global, 0, 100);

            foreach (var dimension in score.Dimensions)
                if (dimension.IsEvaluated) Assert.InRange(dimension.Score!.Value, 0, 100);
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void L_analyse_est_deterministe(string name, SystemSnapshot snapshot)
        {
            // Le plafonnement de famille dépend de l'ordre de traitement : sans tri stable, deux
            // analyses du même diagnostic donneraient deux scores différents.
            var first = Analyze(snapshot).Score!;
            var second = Analyze(snapshot).Score!;

            Assert.Equal(first.Global, second.Global);
            Assert.Equal(first.Dimensions.Count, second.Dimensions.Count);
            for (var i = 0; i < first.Dimensions.Count; i++)
                Assert.Equal(first.Dimensions[i].Score, second.Dimensions[i].Score);
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Un_constat_d_information_ne_coute_jamais_de_point(string name, SystemSnapshot snapshot)
        {
            // Défaut réellement rencontré : le barème était indexé sur le seul identifiant de
            // règle, et une passerelle ignorant l'ICMP (comportement normal, signalé en
            // Information) coûtait 18 points de réseau.
            var analyzed = Analyze(snapshot);
            var infoKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in analyzed.Findings)
                if (finding.Severity == Severity.Info) infoKeys.Add(finding.Key);

            foreach (var dimension in analyzed.Score!.Dimensions)
                foreach (var penalty in dimension.Ledger)
                    if (infoKeys.Contains(penalty.FindingKey))
                        Assert.True(penalty.Points == 0,
                            name + " : le constat d'information " + penalty.FindingKey +
                            " coûte " + penalty.Points + " point(s).");
        }

        [Fact]
        public void Une_machine_saine_obtient_le_score_maximal()
        {
            var analyzed = Analyze(Fixtures.Healthy());

            Assert.Equal(100, analyzed.Score!.Global);
            Assert.Equal(ScoreBand.Excellent, analyzed.Score.Band);
        }

        [Fact]
        public void Un_constat_critique_plafonne_la_dimension_et_le_global()
        {
            // Sans ces plafonds, un disque en train de mourir donnerait encore plus de 80/100 sur
            // une machine par ailleurs saine : un score qui rassure alors que les données sont en
            // danger est pire que pas de score du tout.
            var analyzed = Analyze(Fixtures.DyingDisk());
            var score = analyzed.Score!;

            Assert.True(score.Global <= 60, "Score global " + score.Global + " malgré un constat critique.");
            Assert.NotNull(score.CapApplied);

            var storage = Find(score, DiagnosticCategory.Storage);
            Assert.True(storage.Score <= 40, "Stockage à " + storage.Score + " malgré un constat critique.");
            Assert.NotNull(storage.CapApplied);
        }

        [Fact]
        public void Une_dimension_non_evaluee_est_exclue_et_les_poids_renormalises()
        {
            var analyzed = Analyze(Fixtures.Empty());
            var score = analyzed.Score!;

            var evaluatedWeight = 0d;
            var excluded = 0;
            foreach (var dimension in score.Dimensions)
            {
                if (dimension.IsEvaluated) evaluatedWeight += dimension.EffectiveWeight;
                else
                {
                    excluded++;
                    // Le point capital : non mesurable ≠ nul. La machine n'est pas pénalisée
                    // pour une lacune de l'outil.
                    Assert.Null(dimension.Score);
                    Assert.Equal(0d, dimension.EffectiveWeight);
                }
            }

            Assert.True(excluded > 0, "Le cas « aucune donnée » devrait exclure des dimensions.");
            if (evaluatedWeight > 0) Assert.InRange(evaluatedWeight, 99d, 101d);
        }

        [Fact]
        public void Windows_7_produit_une_confiance_degradee_sans_penaliser_la_machine()
        {
            var analyzed = Analyze(Fixtures.Windows7Minimal());
            var score = analyzed.Score!;

            // La fin de support est un vrai constat ; l'impossibilité de lire le SMART n'en est pas un.
            Assert.NotEqual(ScoreConfidence.Full, score.Confidence);

            var storage = Find(score, DiagnosticCategory.Storage);
            foreach (var penalty in storage.Ledger)
                Assert.True(penalty.Points == 0 || penalty.Severity != Severity.Info,
                    "Une donnée illisible ne doit jamais coûter de points.");
        }

        [Fact]
        public void Le_plafond_de_famille_limite_le_cumul()
        {
            var profile = DiagnosticProfile.Default;
            var cap = profile.FamilyCaps["storage.smart"];

            var analyzed = Analyze(Fixtures.DyingDisk());
            var storage = Find(analyzed.Score!, DiagnosticCategory.Storage);

            var smartPoints = 0;
            foreach (var penalty in storage.Ledger)
            {
                var family = FamilyOf(penalty.RuleId);
                if (family == "storage.smart") smartPoints += penalty.Points;
            }

            Assert.True(smartPoints <= cap,
                "La famille storage.smart a coûté " + smartPoints + " points pour un plafond de " + cap + ".");
        }

        [Fact]
        public void Une_regle_qui_leve_n_interrompt_pas_l_analyse()
        {
            var exploding = new Rule("TEST-BOOM", "Règle défaillante", DiagnosticCategory.Storage,
                _ => throw new InvalidOperationException("défaut de programmation"));

            var rules = new List<IRule> { exploding };
            rules.AddRange(RuleCatalog.All());

            var analyzed = new AnalysisEngine(rules: rules).Analyze(Fixtures.Healthy());

            Assert.NotNull(analyzed.Score);
            // La règle défaillante est écartée, pas comptée comme « rien à signaler » :
            // elle apparaît dans les contrôles non évalués.
            var storage = Find(analyzed.Score!, DiagnosticCategory.Storage);
            Assert.True(storage.SkippedRules >= 1);
        }

        [Fact]
        public void Toutes_les_recommandations_citees_existent_au_catalogue()
        {
            foreach (var field in typeof(Rec).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(string)) continue;
                var id = (string)field.GetValue(null)!;
                Assert.True(RecommendationCatalog.TryGet(id, out _),
                    "La recommandation « " + id + " » est référençable mais absente du catalogue.");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Chaque_recommandation_produite_est_resolue(string name, SystemSnapshot snapshot)
        {
            var analyzed = Analyze(snapshot);
            var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var recommendation in analyzed.Recommendations) produced.Add(recommendation.Id);

            foreach (var finding in analyzed.Findings)
                foreach (var id in finding.Recommendations)
                    Assert.True(produced.Contains(id),
                        name + " : le constat " + finding.Key + " cite « " + id +
                        " », absent du plan d'action.");
        }

        public static IEnumerable<object[]> AllFixtures()
        {
            foreach (var fixture in Fixtures.All())
                yield return new object[] { fixture.Name, fixture.Snapshot };
        }

        private static DimensionScore Find(DiagnosticScore score, DiagnosticCategory category)
        {
            foreach (var dimension in score.Dimensions)
                if (dimension.Dimension == category) return dimension;
            throw new InvalidOperationException("Dimension " + category + " absente du score.");
        }

        private static string? FamilyOf(string ruleId)
        {
            foreach (var rule in RuleCatalog.All())
                if (string.Equals(rule.Descriptor.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                    return rule.Descriptor.Family;
            return null;
        }
    }
}
