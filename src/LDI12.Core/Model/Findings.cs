using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Valeur qui justifie un constat, avec sa provenance et le seuil franchi.
    /// </summary>
    /// <remarks>
    /// C'est ce qui rend un diagnostic défendable : le technicien peut montrer au client la
    /// mesure, sa source et la limite retenue, plutôt qu'une conclusion sans appui.
    /// </remarks>
    public sealed class Evidence
    {
        public string Label { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public DataSource Source { get; init; }

        /// <summary>Seuil déclenchant, formaté pour l'affichage. Absent si le constat n'en a pas.</summary>
        public string? Threshold { get; init; }

        public static Evidence Of(string label, string value, DataSource source, string? threshold = null)
            => new Evidence { Label = label, Value = value, Source = source, Threshold = threshold };
    }

    public enum ConfidenceLevel
    {
        /// <summary>Mesure directe et fiable.</summary>
        High = 0,

        /// <summary>Mesure partielle, ou interprétation dépendant du constructeur.</summary>
        Medium = 1,

        /// <summary>Déduction indirecte : à confirmer avant d'agir.</summary>
        Low = 2,
    }

    /// <summary>
    /// Constat produit par une règle.
    /// </summary>
    /// <remarks>
    /// Le double libellé n'est pas une commodité : c'est lui qui produit mécaniquement le rapport
    /// technicien et le rapport client à partir d'une seule analyse. Une règle qui n'aurait qu'un
    /// libellé technique obligerait à réécrire tout le rapport client à la main.
    /// </remarks>
    public sealed class Finding
    {
        /// <summary>Identifiant stable de la règle (« STO-004 »), citable dans un rapport archivé.</summary>
        public string RuleId { get; init; } = string.Empty;

        /// <summary>
        /// Objet concerné quand une règle peut se déclencher plusieurs fois : « C: », « Disque 1 ».
        /// Distingue deux constats issus de la même règle.
        /// </summary>
        public string? Subject { get; init; }

        public Severity Severity { get; init; }

        public DiagnosticCategory Category { get; init; }

        public string Title { get; init; } = string.Empty;

        /// <summary>Formulation technicien : chiffres, seuils, noms d'objets.</summary>
        public string TechnicalDetail { get; init; } = string.Empty;

        /// <summary>Formulation client : ce que cela signifie, sans jargon ni code d'erreur.</summary>
        public string PlainExplanation { get; init; } = string.Empty;

        public IReadOnlyList<Evidence> Evidence { get; init; } = Array.Empty<Evidence>();

        /// <summary>Identifiants des recommandations déclenchées par ce constat.</summary>
        public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Famille de plafonnement : plusieurs constats de la même famille ne peuvent pas coûter
        /// indéfiniment de points. « Cinq volumes pleins » reste un seul problème d'espace disque.
        /// </summary>
        public string? Family { get; init; }

        public ConfidenceLevel Confidence { get; init; }

        /// <summary>Clé unique du constat, règle et objet confondus.</summary>
        public string Key => Subject == null ? RuleId : RuleId + "/" + Subject;

        public override string ToString() => Key + " : " + Title;
    }

    /// <summary>
    /// Conclusion de second niveau, construite à partir de plusieurs constats.
    /// </summary>
    /// <remarks>
    /// Une règle voit un symptôme, une corrélation raconte une histoire. C'est ce qui transforme
    /// « voici quatorze problèmes » en « voici pourquoi ce PC est lent, et dans quel ordre agir ».
    /// </remarks>
    public sealed class Correlation
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;

        /// <summary>Le raisonnement, rédigé en français et lisible par le client.</summary>
        public string Narrative { get; init; } = string.Empty;

        public Severity Severity { get; init; }

        /// <summary>Clés des constats qui la composent.</summary>
        public IReadOnlyList<string> Contributors { get; init; } = Array.Empty<string>();

        /// <summary>Recommandations, dans l'ordre où il faut les traiter.</summary>
        public IReadOnlyList<string> OrderedActions { get; init; } = Array.Empty<string>();
    }

    public enum RecommendationPriority
    {
        /// <summary>Risque de perte de données : à traiter avant tout le reste.</summary>
        Immediate = 0,
        High = 1,
        Normal = 2,
        Optional = 3,
    }

    public enum EffortLevel
    {
        /// <summary>Quelques minutes, sans redémarrage.</summary>
        Minutes = 0,

        /// <summary>Plusieurs dizaines de minutes, éventuellement avec redémarrage.</summary>
        Extended = 1,

        /// <summary>Intervention complète : démontage, réinstallation, remplacement.</summary>
        Intervention = 2,
    }

    public enum ImpactLevel
    {
        Low = 0,
        Moderate = 1,
        High = 2,
    }

    public sealed class Recommendation
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;

        /// <summary>Pourquoi cette action est recommandée, en une phrase destinée au client.</summary>
        public string Rationale { get; init; } = string.Empty;

        public RecommendationPriority Priority { get; init; }
        public EffortLevel Effort { get; init; }
        public ImpactLevel ExpectedImpact { get; init; }

        /// <summary>Action exécutable depuis l'application, quand elle existe (phase 5).</summary>
        public string? LinkedAction { get; init; }

        /// <summary>Vrai si la recommandation implique un achat de matériel.</summary>
        public bool RequiresHardwarePurchase { get; init; }

        /// <summary>Constats qui ont déclenché cette recommandation.</summary>
        public IReadOnlyList<string> TriggeredBy { get; init; } = Array.Empty<string>();
    }
}
