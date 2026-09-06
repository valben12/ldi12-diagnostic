using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Sens d'évolution d'une mesure entre deux diagnostics.</summary>
    public enum ChangeDirection
    {
        /// <summary>Les deux mesures existent et sont identiques.</summary>
        Unchanged = 0,

        Improved = 1,
        Worsened = 2,

        /// <summary>
        /// L'une des deux mesures manque : le sens d'évolution ne peut pas être établi.
        /// </summary>
        /// <remarks>
        /// Distinct d'<see cref="Unchanged"/> à dessein. Une mesure absente d'un côté ne prouve
        /// pas que rien n'a bougé : c'est exactement la confusion qu'un rapport « avant / après »
        /// ne peut pas se permettre.
        /// </remarks>
        Unknown = 3,
    }

    /// <summary>Évolution d'une mesure clé entre deux diagnostics de la même machine.</summary>
    public sealed class MeasureChange
    {
        public string Label { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;

        public Measured<double> Before { get; init; }
        public Measured<double> After { get; init; }

        /// <summary>Vrai quand une valeur qui monte est une bonne nouvelle (espace libre).</summary>
        public bool HigherIsBetter { get; init; }

        public ChangeDirection Direction { get; init; }

        /// <summary>Écart, quand les deux mesures existent.</summary>
        public double? Delta { get; init; }

        /// <summary>Pourquoi l'évolution ne peut pas être établie, le cas échéant.</summary>
        public string? Note { get; init; }

        public bool IsComparable => Direction != ChangeDirection.Unknown;
    }

    public enum FindingChangeKind
    {
        /// <summary>Le constat était là, la règle a été rejouée, elle ne le produit plus.</summary>
        Resolved = 0,

        /// <summary>Le constat n'existait pas au premier diagnostic.</summary>
        Appeared = 1,

        /// <summary>Le constat est toujours là.</summary>
        Persisting = 2,

        /// <summary>
        /// Le constat n'apparaît plus, mais la règle qui le produisait n'a pas conclu.
        /// </summary>
        /// <remarks>
        /// Le cas le plus dangereux du rapport « avant / après » : une analyse rapide n'exécute
        /// pas les tests réseau, les constats de réseau disparaissent, et l'outil annoncerait une
        /// réparation qui n'a pas eu lieu. Ils sont donc rangés à part et jamais comptés comme
        /// résolus.
        /// </remarks>
        NotRechecked = 3,
    }

    public sealed class FindingChange
    {
        public Finding Finding { get; init; } = new Finding();
        public FindingChangeKind Kind { get; init; }

        /// <summary>Gravité au premier diagnostic, quand elle a changé.</summary>
        public Severity? PreviousSeverity { get; init; }

        public string? Note { get; init; }
    }

    public sealed class DimensionDelta
    {
        public DiagnosticCategory Dimension { get; init; }
        public int? Before { get; init; }
        public int? After { get; init; }

        /// <summary>Écart, seulement quand les deux dimensions ont été notées.</summary>
        public int? Delta => Before.HasValue && After.HasValue ? After - Before : null;
    }

    /// <summary>Ce qu'il faut savoir d'un diagnostic pour le désigner dans une comparaison.</summary>
    public sealed class SnapshotSummary
    {
        public Guid SnapshotId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public RunMode RunMode { get; init; }
        public string ToolVersion { get; init; } = string.Empty;
        public string ProfileVersion { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public string MachineName { get; init; } = string.Empty;
        public int? Score { get; init; }
        public string? ClientReference { get; init; }
    }

    /// <summary>
    /// Ce qui a changé entre deux diagnostics de la même machine.
    /// </summary>
    /// <remarks>
    /// C'est le document qui répond à « qu'est-ce que votre intervention a changé ? », et c'est
    /// pour cette raison qu'il est le plus exposé à la complaisance : tout ce qui a disparu
    /// ressemble à une réussite. Trois distinctions le tiennent honnête et se retrouvent dans
    /// le modèle plutôt que dans l'affichage : un constat non réévalué n'est pas un constat
    /// résolu, deux scores calculés sur des barèmes différents ne se soustraient pas, et deux
    /// diagnostics dont rien ne prouve qu'ils viennent de la même machine ne se comparent pas
    /// sans le dire.
    /// </remarks>
    public sealed class SnapshotDelta
    {
        public SnapshotSummary Before { get; init; } = new SnapshotSummary();
        public SnapshotSummary After { get; init; } = new SnapshotSummary();

        /// <summary>
        /// Les deux diagnostics viennent-ils de la même machine ?
        /// </summary>
        /// <remarks>
        /// Mesurée, et non supposée : sur une machine dont l'empreinte n'a pas pu être lue, la
        /// réponse est « on ne sait pas », pas « oui ».
        /// </remarks>
        public Measured<bool> SameMachine { get; init; }

        /// <summary>
        /// Les scores se soustraient-ils ? Faux dès que le barème a changé entre les deux.
        /// </summary>
        public bool ScoreComparable { get; init; }

        public int? ScoreDelta => ScoreComparable && Before.Score.HasValue && After.Score.HasValue
            ? After.Score - Before.Score
            : null;

        /// <summary>Réserves à afficher avant toute conclusion : barème, schéma, mode d'analyse.</summary>
        public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();

        public IReadOnlyList<DimensionDelta> Dimensions { get; init; } = Array.Empty<DimensionDelta>();
        public IReadOnlyList<FindingChange> Findings { get; init; } = Array.Empty<FindingChange>();
        public IReadOnlyList<MeasureChange> Measures { get; init; } = Array.Empty<MeasureChange>();

        public int Count(FindingChangeKind kind)
        {
            var total = 0;
            foreach (var change in Findings)
                if (change.Kind == kind) total++;
            return total;
        }

        /// <summary>
        /// Rien n'a changé, et la comparaison était en mesure de le voir.
        /// </summary>
        /// <remarks>
        /// À dire explicitement plutôt qu'à laisser deviner d'un écran vide : « rien n'a changé »
        /// est une conclusion, et sur une machine qu'on vient de réparer, c'en est une importante.
        ///
        /// D'où la seconde condition, qui compte autant que la première : un constat non
        /// revérifié ou une mesure qu'on ne sait plus lire interdisent cette conclusion. Sur un
        /// second diagnostic qui n'a rien pu mesurer, tout est « inchangé », et l'annoncer
        /// serait le pire mensonge que ce document puisse produire.
        /// </remarks>
        public bool NothingChanged
        {
            get
            {
                if (!SawEverything) return false;
                if (Count(FindingChangeKind.Resolved) > 0 || Count(FindingChangeKind.Appeared) > 0) return false;

                foreach (var measure in Measures)
                    if (measure.Direction == ChangeDirection.Improved || measure.Direction == ChangeDirection.Worsened)
                        return false;

                return true;
            }
        }

        /// <summary>La comparaison a pu conclure sur tout ce qu'elle a regardé.</summary>
        public bool SawEverything
        {
            get
            {
                if (Count(FindingChangeKind.NotRechecked) > 0) return false;
                foreach (var measure in Measures)
                    if (!measure.IsComparable) return false;
                return true;
            }
        }
    }
}
