using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Profile;

namespace LDI12.Engine.Scoring
{
    /// <summary>Couverture d'une dimension : combien de contrôles ont pu être évalués.</summary>
    public sealed class DimensionCoverage
    {
        public int Evaluated { get; set; }
        public int Skipped { get; set; }
        public int Total => Evaluated + Skipped;
    }

    /// <summary>
    /// Calcul du score, entièrement reconstructible.
    /// </summary>
    /// <remarks>
    /// Trois mécanismes empêchent le chiffre de mentir :
    /// <list type="number">
    /// <item><b>Plafond de famille</b> : cinq volumes pleins restent un seul problème d'espace disque.</item>
    /// <item><b>Plafond de sévérité</b>, sans lui, un disque en train de mourir donnerait encore
    /// 82/100 sur une machine par ailleurs saine. Un score qui rassure alors que les données sont
    /// en danger est pire que pas de score du tout.</item>
    /// <item><b>Renormalisation</b> : une dimension non mesurable est exclue, jamais notée zéro ;
    /// la machine n'a pas à être pénalisée pour une lacune de l'outil.</item>
    /// </list>
    /// </remarks>
    public static class ScoringEngine
    {
        public static DiagnosticScore Compute(
            IReadOnlyList<Finding> findings,
            IReadOnlyDictionary<DiagnosticCategory, DimensionCoverage> coverage,
            DiagnosticProfile profile,
            IReadOnlyList<string>? evaluatedRuleIds = null)
        {
            if (findings == null) throw new ArgumentNullException(nameof(findings));
            if (coverage == null) throw new ArgumentNullException(nameof(coverage));
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var ordered = Order(findings);
            var familySpent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ledgers = new Dictionary<DiagnosticCategory, List<ScorePenalty>>();
            var worstSeverity = new Dictionary<DiagnosticCategory, Severity>();
            var anyCritical = false;

            foreach (var finding in ordered)
            {
                // La plateforme décrit la machine, elle ne la juge pas : elle n'entre pas au barème.
                if (finding.Category == DiagnosticCategory.Platform) continue;

                var full = profile.PenaltyFor(finding.RuleId, finding.Severity);
                var applied = full;
                string? capApplied = null;

                if (finding.Family != null && profile.FamilyCaps.TryGetValue(finding.Family, out var cap))
                {
                    familySpent.TryGetValue(finding.Family, out var spent);
                    var remaining = Math.Max(0, cap - spent);
                    if (applied > remaining)
                    {
                        applied = remaining;
                        capApplied = finding.Family;
                    }
                    familySpent[finding.Family] = spent + applied;
                }

                if (!ledgers.TryGetValue(finding.Category, out var ledger))
                {
                    ledger = new List<ScorePenalty>();
                    ledgers[finding.Category] = ledger;
                }

                ledger.Add(new ScorePenalty
                {
                    FindingKey = finding.Key,
                    RuleId = finding.RuleId,
                    Dimension = finding.Category,
                    Severity = finding.Severity,
                    Points = applied,
                    PointsBeforeCap = full,
                    CapApplied = capApplied,
                    Reason = BuildReason(finding, full, applied, capApplied),
                });

                if (finding.Severity == Severity.Critical) anyCritical = true;
                if (!worstSeverity.TryGetValue(finding.Category, out var worst) || finding.Severity > worst)
                    worstSeverity[finding.Category] = finding.Severity;
            }

            var dimensions = new List<DimensionScore>();
            var totalEffectiveWeight = 0d;

            foreach (var category in ScoredCategories)
            {
                coverage.TryGetValue(category, out var counts);
                counts ??= new DimensionCoverage();
                ledgers.TryGetValue(category, out var ledger);
                ledger ??= new List<ScorePenalty>();

                var weight = profile.WeightOf(category);

                if (counts.Evaluated == 0)
                {
                    dimensions.Add(new DimensionScore
                    {
                        Dimension = category,
                        Score = null,
                        RawScore = null,
                        Weight = weight,
                        EffectiveWeight = 0,
                        Confidence = ScoreConfidence.Low,
                        EvaluatedRules = 0,
                        SkippedRules = counts.Skipped,
                        Ledger = ledger,
                    });
                    continue;
                }

                var spent = 0;
                foreach (var penalty in ledger) spent += penalty.Points;

                var raw = Math.Max(0, Math.Min(100, 100 - spent));
                var score = raw;
                string? severityCap = null;

                if (worstSeverity.TryGetValue(category, out var worst) &&
                    profile.SeverityCaps.TryGetValue(worst, out var cap) && score > cap)
                {
                    score = cap;
                    severityCap = "Plafond « " + Describe(worst) + " » : " + cap + " points au maximum.";
                }

                totalEffectiveWeight += weight;
                dimensions.Add(new DimensionScore
                {
                    Dimension = category,
                    Score = score,
                    RawScore = raw,
                    Weight = weight,
                    EffectiveWeight = weight,
                    Confidence = ConfidenceOf(counts),
                    EvaluatedRules = counts.Evaluated,
                    SkippedRules = counts.Skipped,
                    CapApplied = severityCap,
                    Ledger = ledger,
                });
            }

            // Renormalisation : les poids des dimensions évaluées sont ramenés à 100 %.
            var normalized = new List<DimensionScore>(dimensions.Count);
            var weighted = 0d;
            foreach (var dimension in dimensions)
            {
                if (!dimension.IsEvaluated || totalEffectiveWeight <= 0)
                {
                    normalized.Add(dimension);
                    continue;
                }

                var effective = dimension.Weight / totalEffectiveWeight * 100d;
                weighted += dimension.Score!.Value * effective;
                normalized.Add(new DimensionScore
                {
                    Dimension = dimension.Dimension,
                    Score = dimension.Score,
                    RawScore = dimension.RawScore,
                    Weight = dimension.Weight,
                    EffectiveWeight = Math.Round(effective, 1),
                    Confidence = dimension.Confidence,
                    EvaluatedRules = dimension.EvaluatedRules,
                    SkippedRules = dimension.SkippedRules,
                    CapApplied = dimension.CapApplied,
                    Ledger = dimension.Ledger,
                });
            }

            var global = totalEffectiveWeight > 0 ? (int)Math.Round(weighted / 100d, MidpointRounding.AwayFromZero) : 0;
            string? globalCap = null;

            if (anyCritical && global > profile.GlobalCriticalCap)
            {
                global = profile.GlobalCriticalCap;
                globalCap = "Au moins un constat critique : le score global est plafonné à " +
                            profile.GlobalCriticalCap + ".";
            }

            return new DiagnosticScore
            {
                ProfileVersion = profile.Version,
                Global = Math.Max(0, Math.Min(100, global)),
                Band = DiagnosticScore.BandOf(global),
                CapApplied = globalCap,
                Confidence = GlobalConfidence(normalized),
                Dimensions = normalized,
                EvaluatedRuleIds = evaluatedRuleIds ?? Array.Empty<string>(),
            };
        }

        private static readonly DiagnosticCategory[] ScoredCategories =
        {
            DiagnosticCategory.Hardware,
            DiagnosticCategory.Storage,
            DiagnosticCategory.Windows,
            DiagnosticCategory.Security,
            DiagnosticCategory.Network,
            DiagnosticCategory.Performance,
        };

        /// <summary>
        /// Ordre stable : gravité décroissante, puis identifiant, puis objet. Le plafonnement de
        /// famille dépend de l'ordre, sans tri déterministe, deux analyses du même diagnostic
        /// pourraient produire deux scores différents.
        /// </summary>
        private static List<Finding> Order(IReadOnlyList<Finding> findings)
        {
            var ordered = new List<Finding>(findings);
            ordered.Sort((a, b) =>
            {
                var bySeverity = b.Severity.CompareTo(a.Severity);
                if (bySeverity != 0) return bySeverity;
                var byRule = string.CompareOrdinal(a.RuleId, b.RuleId);
                if (byRule != 0) return byRule;
                return string.CompareOrdinal(a.Subject ?? string.Empty, b.Subject ?? string.Empty);
            });
            return ordered;
        }

        private static string BuildReason(Finding finding, int full, int applied, string? capApplied)
        {
            var reason = finding.Title;
            if (finding.Subject != null) reason = finding.Subject + " : " + reason;
            if (applied == 0 && full == 0) return reason + " (constat d'information, non pénalisé)";
            if (capApplied != null)
                return reason + " (" + full + " points ramenés à " + applied +
                       " par le plafond de la famille « " + capApplied + " »)";
            return reason;
        }

        private static ScoreConfidence ConfidenceOf(DimensionCoverage coverage)
        {
            if (coverage.Total == 0) return ScoreConfidence.Low;
            var ratio = (double)coverage.Evaluated / coverage.Total;
            if (ratio >= 0.999) return ScoreConfidence.Full;
            return ratio >= 0.5 ? ScoreConfidence.Partial : ScoreConfidence.Low;
        }

        private static ScoreConfidence GlobalConfidence(IReadOnlyList<DimensionScore> dimensions)
        {
            var worst = ScoreConfidence.Full;
            foreach (var dimension in dimensions)
            {
                // Une dimension entièrement exclue dégrade la confiance globale, même si les
                // autres sont parfaitement couvertes : le score ne porte alors que sur une partie.
                var confidence = dimension.IsEvaluated ? dimension.Confidence : ScoreConfidence.Low;
                if (confidence > worst) worst = confidence;
            }
            return worst;
        }

        private static string Describe(Severity severity) => severity switch
        {
            Severity.Critical => "constat critique",
            Severity.Problem => "problème avéré",
            Severity.Warning => "avertissement",
            _ => "information",
        };
    }
}
