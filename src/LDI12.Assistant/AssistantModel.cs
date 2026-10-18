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

        /// <summary>Ce que dit le client, ou ce que le technicien a observé.</summary>
        public string? Notes { get; init; }

        /// <summary>Un relevé fait à l'instant, joint d'emblée (les sorties audio pour un problème de son…).</summary>
        public string? LiveContext { get; init; }
    }

    /// <summary>Une cause envisagée, avec sa vraisemblance et ce que les étapes en ont établi.</summary>
    public sealed class Hypothesis
    {
        public string Cause { get; init; } = string.Empty;

        /// <summary>De 0 à 100.</summary>
        public int Likelihood { get; init; }

        /// <summary>« à vérifier », « écartée », « confirmée ».</summary>
        public string State { get; init; } = "à vérifier";
    }

    /// <summary>Une vérification que seul le technicien peut faire, et les réponses qu'il peut donner d'un clic.</summary>
    public sealed class CheckQuestion
    {
        public string Question { get; init; } = string.Empty;

        public IReadOnlyList<string> Answers { get; init; } = Array.Empty<string>();
    }

    /// <summary>Ce qui s'est passé pendant une étape, renvoyé à l'analyse pour la suivante.</summary>
    public sealed class StepFeedback
    {
        /// <summary>Les opérations lancées : titre et compte rendu.</summary>
        public IReadOnlyList<(string Title, string Outcome)> Operations { get; init; } = Array.Empty<(string, string)>();

        /// <summary>Les vérifications : question et réponse choisie (ou « sans réponse »).</summary>
        public IReadOnlyList<(string Question, string Answer)> Answers { get; init; } = Array.Empty<(string, string)>();

        /// <summary>Les gestes manuels : titre, fait ou non.</summary>
        public IReadOnlyList<(string Title, bool Done)> Manual { get; init; } = Array.Empty<(string, bool)>();

        /// <summary>Ce que le technicien a constaté, en ses mots.</summary>
        public string? Note { get; init; }

        public string Render(int round)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("Résultats de l'étape " + round + " :");
            foreach (var (title, outcome) in Operations) text.AppendLine("- Opération « " + title + " » : " + outcome);
            foreach (var (question, answer) in Answers) text.AppendLine("- Vérification « " + question + " » : " + answer);
            foreach (var (title, done) in Manual) text.AppendLine("- Geste « " + title + " » : " + (done ? "fait" : "pas fait"));
            if (!string.IsNullOrWhiteSpace(Note)) text.AppendLine("- Constat du technicien : « " + Note!.Trim() + " »");
            if (Operations.Count == 0 && Answers.Count == 0 && Manual.Count == 0 && string.IsNullOrWhiteSpace(Note))
                text.AppendLine("- Rien n'a été fait ni constaté : le technicien demande une autre piste.");
            text.AppendLine("Mets à jour tes hypothèses et rends l'étape suivante avec submit_step.");
            return text.ToString();
        }
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

    /// <summary>Une étape de l'analyse : ce qui est compris à ce point, et ce qu'il faut faire ou vérifier.</summary>
    public sealed class AssistantPlan
    {
        /// <summary>Numéro de l'étape, à partir de 1.</summary>
        public int Round { get; init; } = 1;

        /// <summary>« en_cours », « resolu » (le problème devrait être réglé), « hors_portee » (pièce, atelier, constructeur).</summary>
        public string Status { get; init; } = "en_cours";

        public IReadOnlyList<Hypothesis> Hypotheses { get; init; } = Array.Empty<Hypothesis>();

        public IReadOnlyList<CheckQuestion> Checks { get; init; } = Array.Empty<CheckQuestion>();

        /// <summary>Ce qui se passera après cette étape, en une phrase.</summary>
        public string Next { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        /// <summary>« faible », « moyenne », « forte ».</summary>
        public string Confidence { get; init; } = "moyenne";

        public IReadOnlyList<PlanFinding> Findings { get; init; } = Array.Empty<PlanFinding>();

        public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();

        /// <summary>Jetons consommés depuis le début de la session, entrée et sortie.</summary>
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
