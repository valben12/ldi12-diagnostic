using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Engine.Correlation;
using LDI12.Engine.Profile;
using LDI12.Engine.Recommendations;
using LDI12.Engine.Rules;
using LDI12.Engine.Scoring;

namespace LDI12.Engine
{
    /// <summary>
    /// Transforme un diagnostic collecté en constats, corrélations, score et recommandations.
    /// </summary>
    /// <remarks>
    /// Le moteur ne touche à rien : il prend un <see cref="SystemSnapshot"/> et en rend une copie
    /// enrichie. C'est ce qui permet de rejouer une analyse sur un diagnostic archivé, et donc de
    /// tester le barème sur des cas de référence figés plutôt que sur la machine du moment.
    /// </remarks>
    public sealed class AnalysisEngine
    {
        private const string Category = "Engine.Analysis";

        private readonly IReadOnlyList<IRule> _rules;
        private readonly IScopedLogger _log;

        public AnalysisEngine(ILdiLogger? logger = null, IReadOnlyList<IRule>? rules = null)
        {
            _rules = rules ?? RuleCatalog.All();
            _log = (logger ?? NullLogger.Instance).For(Category);
        }

        public SystemSnapshot Analyze(SystemSnapshot snapshot, DiagnosticProfile? profile = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var active = profile ?? DiagnosticProfile.Default;

            var findings = new List<Finding>();
            var coverage = new Dictionary<DiagnosticCategory, DimensionCoverage>();

            // Quelles règles ont conclu, et pas seulement combien : c'est ce qui permettra plus
            // tard d'affirmer qu'un problème est résolu plutôt que simplement non réévalué.
            var evaluatedRuleIds = new List<string>();

            foreach (var rule in _rules)
            {
                var descriptor = rule.Descriptor;
                if (!coverage.TryGetValue(descriptor.Category, out var counts))
                {
                    counts = new DimensionCoverage();
                    coverage[descriptor.Category] = counts;
                }

                RuleResult result;
                try
                {
                    result = rule.Evaluate(new RuleContext(snapshot, active, descriptor));
                }
                catch (Exception ex)
                {
                    // Une règle est une fonction pure : si elle lève, c'est un défaut de
                    // programmation. Il ne doit pas emporter l'analyse entière, et il ne doit
                    // surtout pas se transformer en « aucun problème détecté ».
                    _log.Error("La règle " + descriptor.Id + " a levé une exception ; elle est écartée du score.", ex);
                    counts.Skipped++;
                    continue;
                }

                if (!result.Evaluated)
                {
                    counts.Skipped++;
                    continue;
                }

                counts.Evaluated++;
                evaluatedRuleIds.Add(descriptor.Id);
                findings.AddRange(result.Findings);
            }

            var correlations = CorrelationRules.Evaluate(findings);
            var score = ScoringEngine.Compute(findings, coverage, active, evaluatedRuleIds);
            var recommendations = RecommendationBuilder.Build(findings, correlations);

            _log.Info("Analyse terminée : " + findings.Count + " constat(s), " + correlations.Count +
                      " corrélation(s), score " + score.Global + "/100 (" +
                      DiagnosticScore.BandLabel(score.Band) + ").");

            // Et non une reconstruction section par section : elle perdrait en silence toute
            // section ajoutée plus tard au modèle. C'est arrivé, voir SystemSnapshot.WithAnalysis.
            return snapshot.WithAnalysis(Sort(findings), correlations, recommendations, score);
        }

        /// <summary>Gravité décroissante, puis identifiant : l'ordre d'affichage du rapport.</summary>
        private static IReadOnlyList<Finding> Sort(List<Finding> findings)
        {
            findings.Sort((a, b) =>
            {
                var bySeverity = b.Severity.CompareTo(a.Severity);
                if (bySeverity != 0) return bySeverity;
                var byRule = string.CompareOrdinal(a.RuleId, b.RuleId);
                return byRule != 0 ? byRule : string.CompareOrdinal(a.Subject ?? string.Empty, b.Subject ?? string.Empty);
            });
            return findings;
        }
    }
}
