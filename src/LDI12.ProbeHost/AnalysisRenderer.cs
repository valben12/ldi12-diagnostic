using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.ProbeHost
{
    /// <summary>
    /// Rendu console du résultat d'analyse.
    /// </summary>
    /// <remarks>
    /// Cette sortie est le livrable de la phase 2 : elle doit permettre de juger la qualité du
    /// moteur avant qu'une seule ligne d'interface ne soit écrite. Elle reprend donc l'ordre de
    /// lecture prévu pour le rapport final, score, conclusions, priorités, puis détail.
    /// </remarks>
    internal static class AnalysisRenderer
    {
        private const int Width = 78;

        public static void Render(SystemSnapshot snapshot, bool detailed)
        {
            if (snapshot.Score == null)
            {
                Console.WriteLine("  Aucune analyse disponible dans ce diagnostic.");
                return;
            }

            RenderScore(snapshot.Score);
            RenderCorrelations(snapshot.Correlations);
            RenderRecommendations(snapshot.Recommendations);
            RenderFindings(snapshot.Findings);
            if (detailed) RenderLedger(snapshot.Score);
        }

        private static void RenderScore(DiagnosticScore score)
        {
            Console.WriteLine();
            Console.WriteLine("  ÉTAT GÉNÉRAL DE LA MACHINE");
            Console.WriteLine();
            Console.WriteLine("      " + score.Global + " / 100 : " + DiagnosticScore.BandLabel(score.Band));
            if (score.CapApplied != null) Console.WriteLine("      " + score.CapApplied);
            Console.WriteLine("      Confiance : " + Describe(score.Confidence) + " · barème v" + score.ProfileVersion);
            Console.WriteLine();

            foreach (var dimension in score.Dimensions)
            {
                var name = Pad(Describe(dimension.Dimension), 16);

                if (!dimension.IsEvaluated)
                {
                    Console.WriteLine("      " + name + "   non évaluée : aucun contrôle exploitable (" +
                                      dimension.SkippedRules + " écarté(s))");
                    continue;
                }

                var line = "      " + name + " " +
                           dimension.Score!.Value.ToString(CultureInfo.InvariantCulture).PadLeft(3) + " / 100  " +
                           Bar(dimension.Score.Value) + "  " +
                           "poids " + dimension.EffectiveWeight.ToString("0.#", CultureInfo.CurrentCulture) + " %";
                Console.WriteLine(line);

                var coverage = "                       " + dimension.EvaluatedRules + " contrôle(s) sur " +
                               (dimension.EvaluatedRules + dimension.SkippedRules) +
                               " : confiance " + Describe(dimension.Confidence).ToLowerInvariant();
                Console.WriteLine(coverage);

                if (dimension.CapApplied != null)
                    Console.WriteLine("                       " + dimension.CapApplied);
            }
        }

        private static void RenderCorrelations(IReadOnlyList<Core.Model.Correlation> correlations)
        {
            if (correlations.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("  CONCLUSIONS");
            foreach (var correlation in correlations)
            {
                Console.WriteLine();
                Console.WriteLine("    " + Marker(correlation.Severity) + " " + correlation.Title);
                foreach (var line in Wrap(correlation.Narrative, Width - 8))
                    Console.WriteLine("        " + line);
            }
        }

        private static void RenderRecommendations(IReadOnlyList<Recommendation> recommendations)
        {
            if (recommendations.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("  À TRAITER, DANS CET ORDRE");
            Console.WriteLine();

            var rank = 1;
            foreach (var recommendation in recommendations)
            {
                Console.WriteLine("    " + rank.ToString(CultureInfo.InvariantCulture).PadLeft(2) + ". " +
                                  recommendation.Title +
                                  "  [" + Describe(recommendation.Priority) + " · " +
                                  Describe(recommendation.Effort) +
                                  (recommendation.RequiresHardwarePurchase ? " · matériel" : string.Empty) + "]");
                rank++;
            }
        }

        private static void RenderFindings(IReadOnlyList<Finding> findings)
        {
            if (findings.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Aucun constat : tous les contrôles évalués sont conformes.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  CONSTATS (" + findings.Count + ")");

            var lastSeverity = (Severity)(-1);
            foreach (var finding in findings)
            {
                if (finding.Severity != lastSeverity)
                {
                    Console.WriteLine();
                    Console.WriteLine("    " + Describe(finding.Severity).ToUpperInvariant());
                    lastSeverity = finding.Severity;
                }

                Console.WriteLine("      " + Marker(finding.Severity) + " " + Pad(finding.RuleId, 9) + finding.Title);
                foreach (var line in Wrap(finding.TechnicalDetail, Width - 12))
                    Console.WriteLine("            " + line);
            }
        }

        private static void RenderLedger(DiagnosticScore score)
        {
            Console.WriteLine();
            Console.WriteLine("  DÉTAIL DU SCORE");

            foreach (var dimension in score.Dimensions)
            {
                if (!dimension.IsEvaluated && dimension.Ledger.Count == 0) continue;

                Console.WriteLine();
                Console.WriteLine("    " + Pad(Describe(dimension.Dimension), 20) +
                                  (dimension.IsEvaluated
                                      ? dimension.Score!.Value + " / 100" +
                                        (dimension.RawScore != dimension.Score ? " (brut " + dimension.RawScore + ")" : string.Empty)
                                      : "non évaluée"));

                foreach (var penalty in dimension.Ledger)
                {
                    Console.WriteLine("      − " + penalty.Points.ToString(CultureInfo.InvariantCulture).PadLeft(2) +
                                      "  " + Pad(penalty.RuleId, 9) + penalty.Reason);
                }

                if (dimension.CapApplied != null)
                    Console.WriteLine("            " + dimension.CapApplied);
            }
        }

        private static string Bar(int score)
        {
            const int length = 20;
            var filled = (int)Math.Round(score / 100d * length);
            return "[" + new string('#', filled) + new string('.', length - filled) + "]";
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var words = text.Split(' ');
            var line = string.Empty;
            foreach (var word in words)
            {
                if (line.Length == 0) { line = word; continue; }
                if (line.Length + 1 + word.Length > width) { yield return line; line = word; }
                else line += " " + word;
            }
            if (line.Length > 0) yield return line;
        }

        private static string Pad(string text, int width)
            => text.Length >= width ? text + " " : text.PadRight(width);

        private static string Marker(Severity severity) => severity switch
        {
            Severity.Critical => "[!!]",
            Severity.Problem => "[! ]",
            Severity.Warning => "[~ ]",
            _ => "[i ]",
        };

        private static string Describe(Severity severity) => severity switch
        {
            Severity.Critical => "Critique",
            Severity.Problem => "Problème",
            Severity.Warning => "Avertissement",
            _ => "Information",
        };

        private static string Describe(DiagnosticCategory category) => category switch
        {
            DiagnosticCategory.Hardware => "Matériel",
            DiagnosticCategory.Storage => "Stockage",
            DiagnosticCategory.Windows => "Windows",
            DiagnosticCategory.Security => "Sécurité",
            DiagnosticCategory.Network => "Réseau",
            DiagnosticCategory.Performance => "Performances",
            _ => "Plateforme",
        };

        private static string Describe(ScoreConfidence confidence) => confidence switch
        {
            ScoreConfidence.Full => "complète",
            ScoreConfidence.Partial => "partielle",
            _ => "faible",
        };

        private static string Describe(RecommendationPriority priority) => priority switch
        {
            RecommendationPriority.Immediate => "immédiat",
            RecommendationPriority.High => "prioritaire",
            RecommendationPriority.Normal => "normal",
            _ => "optionnel",
        };

        private static string Describe(EffortLevel effort) => effort switch
        {
            EffortLevel.Minutes => "quelques minutes",
            EffortLevel.Extended => "intervention courte",
            _ => "intervention complète",
        };
    }
}
