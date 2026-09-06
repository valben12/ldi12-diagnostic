using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Actions.Journal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Projections d'affichage.
    /// </summary>
    /// <remarks>
    /// Les vues ne lient jamais directement les modèles du diagnostic : elles lient ces objets,
    /// qui portent déjà le texte formaté et les libellés en français. Le formatage n'a ainsi
    /// aucune raison de se retrouver dispersé dans le XAML, où il serait invisible aux tests.
    /// </remarks>
    public sealed class FindingItem
    {
        public FindingItem(Finding finding)
        {
            Severity = finding.Severity;
            RuleId = finding.RuleId;
            Title = finding.Title;
            TechnicalDetail = finding.TechnicalDetail;
            PlainExplanation = finding.PlainExplanation;
            Subject = finding.Subject;

            var lines = new List<string>();
            foreach (var evidence in finding.Evidence)
            {
                var line = evidence.Label + " : " + evidence.Value;
                if (evidence.Threshold != null) line += "   (seuil " + evidence.Threshold + ")";
                line += " : source " + Describe(evidence.Source);
                lines.Add(line);
            }
            Evidence = lines;
            HasEvidence = lines.Count > 0;
        }

        public Severity Severity { get; }
        public string RuleId { get; }
        public string Title { get; }
        public string TechnicalDetail { get; }
        public string PlainExplanation { get; }
        public string? Subject { get; }
        public IReadOnlyList<string> Evidence { get; }
        public bool HasEvidence { get; }

        public string SeverityLabel => Labels.Describe(Severity);

        private static string Describe(DataSource source) => source switch
        {
            DataSource.NativeApi => "API système",
            DataSource.Registry => "registre",
            DataSource.Wmi => "WMI",
            DataSource.Cli => "outil système",
            DataSource.PerformanceCounter => "compteur de performance",
            DataSource.EventLog => "journal d'événements",
            DataSource.FileSystem => "fichier système",
            DataSource.Inferred => "déduction",
            _ => "indéterminée",
        };
    }

    public sealed class DimensionItem
    {
        public DimensionItem(DimensionScore dimension)
        {
            Dimension = dimension.Dimension;
            Name = Labels.Describe(dimension.Dimension);
            IsEvaluated = dimension.IsEvaluated;
            Score = dimension.Score ?? 0;
            ScoreText = dimension.IsEvaluated
                ? dimension.Score!.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            Band = dimension.IsEvaluated ? DiagnosticScore.BandOf(dimension.Score!.Value) : ScoreBand.Attention;

            var total = dimension.EvaluatedRules + dimension.SkippedRules;
            Coverage = dimension.IsEvaluated
                ? dimension.EvaluatedRules + " contrôle" + (dimension.EvaluatedRules > 1 ? "s" : "") +
                  " sur " + total + " · confiance " + Labels.Describe(dimension.Confidence)
                : "aucun contrôle exploitable sur " + total;
            CapApplied = dimension.CapApplied;
            HasCap = dimension.CapApplied != null;
        }

        public DiagnosticCategory Dimension { get; }
        public string Name { get; }
        public bool IsEvaluated { get; }
        public int Score { get; }
        public string ScoreText { get; }
        public ScoreBand Band { get; }
        public string Coverage { get; }
        public string? CapApplied { get; }
        public bool HasCap { get; }
    }

    public sealed class RecommendationItem
    {
        public RecommendationItem(Recommendation recommendation, int rank)
        {
            Rank = rank.ToString(CultureInfo.InvariantCulture);
            Title = recommendation.Title;
            Rationale = recommendation.Rationale;
            Priority = recommendation.Priority;
            PriorityLabel = Labels.Describe(recommendation.Priority);
            EffortLabel = Labels.Describe(recommendation.Effort);
            RequiresHardware = recommendation.RequiresHardwarePurchase;
        }

        public string Rank { get; }
        public string Title { get; }
        public string Rationale { get; }
        public RecommendationPriority Priority { get; }
        public string PriorityLabel { get; }
        public string EffortLabel { get; }
        public bool RequiresHardware { get; }
    }

    public sealed class CorrelationItem
    {
        public CorrelationItem(Core.Model.Correlation correlation)
        {
            Title = correlation.Title;
            Narrative = correlation.Narrative;
            Severity = correlation.Severity;
        }

        public string Title { get; }
        public string Narrative { get; }
        public Severity Severity { get; }
    }

    public sealed class ModuleItem
    {
        public ModuleItem(ModuleReport report)
        {
            Name = report.DisplayName;
            Status = report.Status;
            StatusLabel = Labels.Describe(report.Status);
            Message = report.Message;
            HasMessage = !string.IsNullOrWhiteSpace(report.Message);
            Duration = report.DurationMs + " ms";
            IsHealthy = report.Status == Core.Probes.ProbeStatus.Ok;
        }

        public string Name { get; }
        public Core.Probes.ProbeStatus Status { get; }
        public string StatusLabel { get; }
        public string? Message { get; }
        public bool HasMessage { get; }
        public string Duration { get; }
        public bool IsHealthy { get; }
    }

    /// <summary>Libellés français des énumérations du diagnostic, en un seul endroit.</summary>
    internal static class Labels
    {
        public static string Describe(Severity severity) => severity switch
        {
            Severity.Critical => "Critique",
            Severity.Problem => "Problème",
            Severity.Warning => "À surveiller",
            _ => "Information",
        };

        public static string Describe(DiagnosticCategory category) => category switch
        {
            DiagnosticCategory.Hardware => "Matériel",
            DiagnosticCategory.Storage => "Stockage",
            DiagnosticCategory.Windows => "Windows",
            DiagnosticCategory.Security => "Sécurité",
            DiagnosticCategory.Network => "Réseau",
            DiagnosticCategory.Performance => "Performances",
            _ => "Plateforme",
        };

        public static string Describe(ScoreConfidence confidence) => confidence switch
        {
            ScoreConfidence.Full => "complète",
            ScoreConfidence.Partial => "partielle",
            _ => "faible",
        };

        public static string Describe(RecommendationPriority priority) => priority switch
        {
            RecommendationPriority.Immediate => "Immédiat",
            RecommendationPriority.High => "Prioritaire",
            RecommendationPriority.Normal => "Normal",
            _ => "Optionnel",
        };

        public static string Describe(EffortLevel effort) => effort switch
        {
            EffortLevel.Minutes => "quelques minutes",
            EffortLevel.Extended => "intervention courte",
            _ => "intervention complète",
        };

        public static string Describe(Core.Probes.ProbeStatus status) => status switch
        {
            Core.Probes.ProbeStatus.Ok => "Terminé",
            Core.Probes.ProbeStatus.Partial => "Partiel",
            Core.Probes.ProbeStatus.Failed => "Échec",
            Core.Probes.ProbeStatus.Skipped => "Écarté",
            Core.Probes.ProbeStatus.Unavailable => "Indisponible",
            Core.Probes.ProbeStatus.TimedOut => "Délai dépassé",
            Core.Probes.ProbeStatus.Cancelled => "Annulé",
            Core.Probes.ProbeStatus.ElevationRequired => "Élévation requise",
            _ => "Non exécuté",
        };
    }

    /// <summary>Une ligne du journal d'intervention, telle qu'elle s'affiche à l'écran.</summary>
    public sealed class JournalItem
    {
        public JournalItem(InterventionEntry entry)
        {
            Time = entry.TimeLabel;
            Kind = entry.KindLabel;
            Title = entry.Title;
            Summary = entry.Summary;
            Elevated = entry.Elevated;
            Detail = entry.Duration > TimeSpan.Zero ? ValueFormat.Duration(entry.Duration) : string.Empty;
            HasDetail = Detail.Length > 0;
        }

        public string Time { get; }
        public string Kind { get; }
        public string Title { get; }
        public string Summary { get; }
        public bool Elevated { get; }
        public string Detail { get; }
        public bool HasDetail { get; }
    }
}
