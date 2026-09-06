using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public enum ScoreBand
    {
        Excellent = 0,
        Good = 1,
        Attention = 2,
        Degraded = 3,
        Critical = 4,
    }

    public enum ScoreConfidence
    {
        /// <summary>Tous les contrôles de la dimension ont pu être évalués.</summary>
        Full = 0,

        /// <summary>Une partie des contrôles n'a pas pu être évaluée, faute de données.</summary>
        Partial = 1,

        /// <summary>Moins de la moitié des contrôles évalués : le score est indicatif.</summary>
        Low = 2,
    }

    /// <summary>
    /// Une ligne du grand livre : un point perdu, sa règle, sa raison.
    /// </summary>
    /// <remarks>
    /// Aucun chiffre ne peut apparaître dans un rapport sans sa ligne de justification. Un test
    /// vérifie sur chaque cas de référence que la somme des pénalités et le score final se
    /// recomposent exactement.
    /// </remarks>
    public sealed class ScorePenalty
    {
        public string FindingKey { get; init; } = string.Empty;
        public string RuleId { get; init; } = string.Empty;
        public DiagnosticCategory Dimension { get; init; }
        public Severity Severity { get; init; }

        /// <summary>Points effectivement retirés, après plafonnement de famille.</summary>
        public int Points { get; init; }

        /// <summary>Points qu'aurait coûtés le constat sans plafonnement.</summary>
        public int PointsBeforeCap { get; init; }

        /// <summary>Famille de plafonnement appliquée, quand elle a réduit la pénalité.</summary>
        public string? CapApplied { get; init; }

        /// <summary>Justification affichée telle quelle dans le rapport.</summary>
        public string Reason { get; init; } = string.Empty;
    }

    public sealed class DimensionScore
    {
        public DiagnosticCategory Dimension { get; init; }

        /// <summary>
        /// Score sur 100, ou <c>null</c> quand aucun contrôle n'a pu être évalué. La distinction
        /// est capitale : une dimension non mesurable est exclue du calcul, jamais notée zéro.
        /// </summary>
        public int? Score { get; init; }

        /// <summary>
        /// Score avant application du plafond de sévérité. C'est lui qui se recompose exactement
        /// avec le grand livre : <c>somme des pénalités + score brut = 100</c>.
        /// </summary>
        public int? RawScore { get; init; }

        /// <summary>Poids nominal issu du profil, avant renormalisation.</summary>
        public double Weight { get; init; }

        /// <summary>Poids effectif après exclusion des dimensions non évaluées.</summary>
        public double EffectiveWeight { get; init; }

        public ScoreConfidence Confidence { get; init; }

        public int EvaluatedRules { get; init; }
        public int SkippedRules { get; init; }

        /// <summary>Motif du plafond de sévérité, quand il s'est appliqué.</summary>
        public string? CapApplied { get; init; }

        public IReadOnlyList<ScorePenalty> Ledger { get; init; } = Array.Empty<ScorePenalty>();

        public bool IsEvaluated => Score.HasValue;
    }

    public sealed class DiagnosticScore
    {
        /// <summary>Version du barème. Sans elle, comparer deux diagnostics serait mensonger.</summary>
        public string ProfileVersion { get; init; } = string.Empty;

        public int Global { get; init; }
        public ScoreBand Band { get; init; }

        /// <summary>Motif du plafond global, quand un constat critique l'a déclenché.</summary>
        public string? CapApplied { get; init; }

        public ScoreConfidence Confidence { get; init; }

        public IReadOnlyList<DimensionScore> Dimensions { get; init; } = Array.Empty<DimensionScore>();

        /// <summary>
        /// Identifiants des règles qui ont réellement conclu, quel qu'ait été leur verdict.
        /// </summary>
        /// <remarks>
        /// Le compte des contrôles évalués ne suffit pas à comparer deux diagnostics du même PC :
        /// pour affirmer qu'un problème est <b>résolu</b>, il faut savoir que la règle qui le
        /// détectait a bien été rejouée. Sans cette liste, une analyse rapide qui n'exécute pas
        /// les tests réseau ferait disparaître les constats de réseau, et le rapport
        /// « avant / après » annoncerait une réparation qui n'a pas eu lieu.
        ///
        /// Vide sur un diagnostic archivé avant l'existence de ce champ : la comparaison le dit
        /// alors au lieu de conclure.
        /// </remarks>
        public IReadOnlyList<string> EvaluatedRuleIds { get; init; } = Array.Empty<string>();

        public static ScoreBand BandOf(int score) => score switch
        {
            >= 90 => ScoreBand.Excellent,
            >= 75 => ScoreBand.Good,
            >= 60 => ScoreBand.Attention,
            >= 40 => ScoreBand.Degraded,
            _ => ScoreBand.Critical,
        };

        /// <summary>Libellé affiché à côté du chiffre. Un « 72 » seul ne veut rien dire pour un client.</summary>
        public static string BandLabel(ScoreBand band) => band switch
        {
            ScoreBand.Excellent => "Excellent",
            ScoreBand.Good => "Bon",
            ScoreBand.Attention => "Attention",
            ScoreBand.Degraded => "Dégradé",
            _ => "Critique",
        };
    }
}
