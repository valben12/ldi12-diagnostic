using System;
using System.Collections.Generic;

namespace LDI12.Assistant
{
    /// <summary>Ce que Claude doit analyser.</summary>
    public enum AssistantMode
    {
        /// <summary>Les pilotes de cette machine : manquants, en erreur, génériques, périmés.</summary>
        Drivers = 0,

        /// <summary>Un problème décrit par le technicien, en quelques choix.</summary>
        Guided = 1,
    }

    /// <summary>La demande, telle que l'écran l'a recueillie : pas de conversation, tout est dit d'avance.</summary>
    public sealed class AssistantRequest
    {
        public AssistantMode Mode { get; init; }

        /// <summary>Le genre de problème choisi : « Lenteurs », « Écran bleu »…</summary>
        public string? Problem { get; init; }

        /// <summary>Les précisions cochées : « Depuis une mise à jour », « Au démarrage »…</summary>
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

        /// <summary>Ce que le client en dit, s'il y a lieu.</summary>
        public string? Notes { get; init; }
    }

    /// <summary>Une opération de LDI12 que le plan peut proposer.</summary>
    public sealed class AvailableAction
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Purpose { get; init; } = string.Empty;

        /// <summary>« lecture seule », « faible », « modéré », « élevé ».</summary>
        public string Risk { get; init; } = string.Empty;

        /// <summary>Le paramètre attendu, s'il y en a un : « service : nom court du service ».</summary>
        public string? Parameter { get; init; }
    }

    /// <summary>Un constat de Claude.</summary>
    public sealed class PlanFinding
    {
        public string Title { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        /// <summary>« info », « attention », « problème », « critique ».</summary>
        public string Severity { get; init; } = "info";
    }

    /// <summary>Une étape du plan : une opération de LDI12, ou un geste à faire à la main.</summary>
    public sealed class PlanStep
    {
        public string Title { get; init; } = string.Empty;

        public string Why { get; init; } = string.Empty;

        /// <summary>L'opération de LDI12 qui l'exécute ; null pour un geste manuel.</summary>
        public string? ActionId { get; init; }

        public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

        /// <summary>Pour un geste manuel : quoi faire, précisément.</summary>
        public string? Manual { get; init; }

        public bool IsAutomatic => ActionId != null;
    }

    /// <summary>Le résultat d'une analyse : ce que Claude a compris, et ce qu'il propose.</summary>
    public sealed class AssistantPlan
    {
        public string Summary { get; init; } = string.Empty;

        /// <summary>« faible », « moyenne », « forte ».</summary>
        public string Confidence { get; init; } = "moyenne";

        public IReadOnlyList<PlanFinding> Findings { get; init; } = Array.Empty<PlanFinding>();

        public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();

        /// <summary>Jetons consommés, entrée et sortie : pour suivre ce que coûte une analyse.</summary>
        public long InputTokens { get; init; }

        public long OutputTokens { get; init; }

        /// <summary>Recherches web faites en cours d'analyse.</summary>
        public int WebSearches { get; init; }

        /// <summary>Ce que l'analyse a coûté, en dollars, au tarif public.</summary>
        public double CostDollars { get; init; }

        /// <summary>Repris d'une analyse identique déjà faite : rien n'a été consommé.</summary>
        public bool FromCache { get; init; }
    }

    /// <summary>Une analyse qui n'a pas abouti, avec une phrase pour le technicien.</summary>
    public sealed class AssistantException : Exception
    {
        public AssistantException(string message, Exception? inner = null) : base(message, inner)
        {
        }
    }
}
