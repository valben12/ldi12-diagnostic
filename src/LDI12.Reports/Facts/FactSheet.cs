using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Reports.Facts
{
    /// <summary>État d'une donnée affichée. Transposition directe de <see cref="Availability"/>.</summary>
    public enum FactState
    {
        /// <summary>Mesurée et fiable.</summary>
        Known = 0,

        /// <summary>Mesurée, mais incomplète ou approximative. La limite est dans la note.</summary>
        Partial = 1,

        /// <summary>Impossible à obtenir sur cette machine. La raison est dans la note.</summary>
        Missing = 2,

        /// <summary>Lisible uniquement en session administrateur.</summary>
        NeedsElevation = 3,

        /// <summary>
        /// Jamais tentée : le module ne s'est pas exécuté dans ce mode d'analyse.
        /// </summary>
        /// <remarks>
        /// Distinct de <see cref="Missing"/>, et la distinction n'est pas cosmétique. « Impossible
        /// à lire sur cette machine » est un résultat de diagnostic ; « pas encore demandé » n'en
        /// est pas un. Les afficher pareil ferait passer une analyse rapide pour une machine
        /// pleine de lacunes. Ces lignes sont donc comptées et résumées, jamais listées.
        /// </remarks>
        NotCollected = 4,
    }

    /// <summary>
    /// Accent visuel d'une donnée. Distinct de <see cref="FactState"/> : une valeur parfaitement
    /// mesurée peut être mauvaise, et une valeur manquante n'est jamais mauvaise : c'est une
    /// lacune de mesure, pas un défaut de la machine.
    /// </summary>
    public enum FactTone
    {
        Neutral = 0,
        Good = 1,
        Warning = 2,
        Bad = 3,
    }

    public sealed class Fact
    {
        public Fact(string label, string value, FactState state, string? note = null, FactTone tone = FactTone.Neutral)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Value = value ?? string.Empty;
            State = state;
            Note = note;
            Tone = state == FactState.Known || state == FactState.Partial ? tone : FactTone.Neutral;
        }

        public string Label { get; }

        /// <summary>Valeur déjà formatée et en français. Aucun consommateur ne reformate.</summary>
        public string Value { get; }

        public FactState State { get; }

        /// <summary>Provenance quand la valeur existe, raison de l'absence sinon.</summary>
        public string? Note { get; }

        public FactTone Tone { get; }

        public bool IsKnown => State == FactState.Known || State == FactState.Partial;
    }

    public sealed class FactRow
    {
        public FactRow(IReadOnlyList<string> cells, FactTone tone = FactTone.Neutral)
        {
            Cells = cells ?? throw new ArgumentNullException(nameof(cells));
            Tone = tone;
        }

        public IReadOnlyList<string> Cells { get; }
        public FactTone Tone { get; }
    }

    /// <summary>
    /// Tableau technique : attributs SMART, volumes, périphériques en défaut, services déviants.
    /// </summary>
    public sealed class FactTable
    {
        public FactTable(IReadOnlyList<string> columns, IReadOnlyList<FactRow> rows, string emptyMessage)
        {
            Columns = columns ?? throw new ArgumentNullException(nameof(columns));
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
            EmptyMessage = emptyMessage;
        }

        public IReadOnlyList<string> Columns { get; }
        public IReadOnlyList<FactRow> Rows { get; }

        /// <summary>Ce qu'il faut lire quand le tableau est vide : un vide non expliqué est ambigu.</summary>
        public string EmptyMessage { get; }

        public bool HasRows => Rows.Count > 0;
    }

    public sealed class FactGroup
    {
        public FactGroup(string title, string? subtitle, IReadOnlyList<Fact> facts, FactTable? table = null)
        {
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Subtitle = subtitle;
            Facts = facts ?? Array.Empty<Fact>();
            Table = table;

            var visible = new List<Fact>(Facts.Count);
            var pending = 0;
            foreach (var fact in Facts)
            {
                if (fact.State == FactState.NotCollected) pending++;
                else visible.Add(fact);
            }

            Visible = visible;
            NotCollectedCount = pending;
        }

        public string Title { get; }
        public string? Subtitle { get; }

        /// <summary>Toutes les lignes, y compris celles jamais tentées. Sert aux tests et au JSON.</summary>
        public IReadOnlyList<Fact> Facts { get; }

        /// <summary>Les lignes à afficher. Voir <see cref="FactState.NotCollected"/>.</summary>
        public IReadOnlyList<Fact> Visible { get; }

        public int NotCollectedCount { get; }

        public FactTable? Table { get; }

        public bool HasFacts => Visible.Count > 0;
        public bool HasTable => Table != null;
        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public bool HasNotCollected => NotCollectedCount > 0;

        public string NotCollectedNote => NotCollectedCount == 1
            ? "1 caractéristique n'a pas été relevée dans ce mode d'analyse."
            : NotCollectedCount + " caractéristiques n'ont pas été relevées dans ce mode d'analyse.";
    }

    /// <summary>
    /// Ensemble des caractéristiques relevées pour un domaine.
    /// </summary>
    /// <remarks>
    /// Ce type existe pour une raison précise : les écrans de détail de l'application et les
    /// rapports HTML montrent exactement les mêmes faits. Les construire deux fois garantissait
    /// qu'ils divergeraient, et surtout que la discipline « ne jamais affirmer ce qui n'a pas été
    /// mesuré » serait respectée d'un côté et oubliée de l'autre. Elle est appliquée une seule
    /// fois, dans <see cref="FactSheetBuilder"/>.
    /// </remarks>
    public sealed class FactSheet
    {
        public FactSheet(DiagnosticCategory category, string title, IReadOnlyList<FactGroup> groups)
        {
            Category = category;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Groups = groups ?? Array.Empty<FactGroup>();
        }

        public DiagnosticCategory Category { get; }
        public string Title { get; }
        public IReadOnlyList<FactGroup> Groups { get; }

        public bool HasGroups => Groups.Count > 0;
    }
}
