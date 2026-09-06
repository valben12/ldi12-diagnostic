using System;
using System.Collections.Generic;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur la durée de vie de l'installation : jusqu'à quand elle est suivie, et
    /// ce que la machine pourra recevoir ensuite.
    /// </summary>
    /// <remarks>
    /// <b>Deux questions qui n'en font qu'une en atelier.</b> « Votre Windows n'est plus mis à
    /// jour » appelle immédiatement « et je fais quoi ? », et la réponse ne dépend pas du
    /// logiciel installé mais du matériel présent. Les séparer obligerait le technicien à lire
    /// deux écrans pour tenir une seule conversation.
    /// <para>
    /// Aucune de ces règles ne recommande d'acheter quoi que ce soit. Elles disent ce que la
    /// machine peut recevoir ; ce qu'on en fait est une décision qui appartient au client, et
    /// qu'un logiciel de diagnostic n'a pas à prendre à sa place.
    /// </para>
    /// </remarks>
    internal static class UpgradeRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;

        /// <summary>Partagée avec WIN-006 : la fin de support et son approche décrivent le même fait.</summary>
        private const string SupportFamily = "windows.support";

        private const string UpgradeFamily = "windows.upgrade";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SUP-001", "Fin de support proche", Cat, SupportEndingSoon, SupportFamily);
            yield return new Rule("UPG-001", "Passage à Windows 11", Cat, Windows11Reach, UpgradeFamily);
            yield return new Rule("UPG-002", "Réglages bloquant la mise à niveau", Cat, BlockingSettings, UpgradeFamily);
        }

        /// <summary>
        /// La version installée est encore suivie, mais plus pour longtemps.
        /// </summary>
        /// <remarks>
        /// Le seul constat de cette famille qui laisse le temps d'agir. Une fois la date passée,
        /// WIN-006 prend le relais et la réparation devient urgente ; avant elle, il s'agit
        /// simplement de prévoir, et c'est précisément ce qu'un client apprécie d'apprendre en
        /// venant pour autre chose.
        /// </remarks>
        private static RuleResult SupportEndingSoon(RuleContext c)
        {
            var support = WindowsLifecycle.For(c.Windows, c.Snapshot.Metadata.CreatedAt);

            if (support.State == WindowsSupportState.Unknown)
                return RuleResult.NotEvaluated(
                    support.Reason ?? "La date de fin de support de cette version n'est pas connue.");

            // La version déjà hors support est l'affaire de WIN-006 : la signaler ici aussi
            // ferait deux constats pour un seul fait.
            if (support.State != WindowsSupportState.Supported) return RuleResult.Clean;

            var days = support.DaysRemaining ?? int.MaxValue;
            if (days > c.T.WindowsSupportEndingSoonDays) return RuleResult.Clean;

            var date = support.EndOfSupport.HasValue ? Fmt.Date(support.EndOfSupport.Value) : "une date proche";

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Le support de cette version se termine bientôt",
                support.VersionLabel + " cesse d'être suivi le " + date + ", dans " + days + " jour" +
                (days > 1 ? "s" : "") + " (seuil " + c.T.WindowsSupportEndingSoonDays + " jours).",
                "Cette version de Windows reçoit encore des correctifs de sécurité, mais plus pour longtemps. " +
                "Il reste du temps pour préparer la suite sans précipitation : c'est justement le bon moment " +
                "pour savoir ce que cette machine pourra recevoir.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fin de support", date, DataSource.Inferred,
                        c.T.WindowsSupportEndingSoonDays + " jours")),
                recommendations: RuleContext.Rec(Rec.MigrateSupportedWindows)));
        }

        /// <summary>
        /// Ce que le matériel permet, quand il ne permet pas Windows 11.
        /// </summary>
        /// <remarks>
        /// <b>Severity.Info, et c'est délibéré.</b> Une machine inéligible n'est pas en panne :
        /// elle marche, souvent très bien, et la pénaliser au score reviendrait à noter son âge
        /// plutôt que son état. Le constat existe pour être lu, pas pour retirer des points.
        /// </remarks>
        private static RuleResult Windows11Reach(RuleContext c)
        {
            var assessment = UpgradeAdvisor.Assess(c.Snapshot);

            switch (assessment.Verdict)
            {
                case UpgradeVerdict.Unknown:
                case UpgradeVerdict.NotApplicable:
                    return RuleResult.NotEvaluated(
                        assessment.Note ?? "L'éligibilité à Windows 11 n'a pas pu être établie.");

                case UpgradeVerdict.AlreadyThere:
                case UpgradeVerdict.Eligible:
                case UpgradeVerdict.EligibleAfterSetting:
                    return RuleResult.Clean;

                case UpgradeVerdict.Undetermined:
                    return RuleResult.NotEvaluated(Missing(assessment));
            }

            var blocking = assessment.With(RequirementOutcome.NotMet);
            var support = WindowsLifecycle.For(c.Windows, c.Snapshot.Metadata.CreatedAt);

            var evidence = new List<Evidence>();
            foreach (var requirement in blocking)
                evidence.Add(Evidence.Of(requirement.Label, requirement.Observation, DataSource.Inferred,
                    requirement.Expectation));

            var plain = "Cette machine ne remplit pas toutes les conditions matérielles de Windows 11. " +
                (support.State == WindowsSupportState.Ended
                    ? "Comme la version installée n'est plus mise à jour, la question se pose dès maintenant : " +
                      "la machine reste utilisable, mais elle ne recevra plus de correctifs de sécurité."
                    : "Ce n'est pas un défaut : la machine fonctionne et continuera de fonctionner. C'est une " +
                      "information à garder pour le jour où la version installée cessera d'être suivie.");

            return RuleResult.Of(c.Finding(Severity.Info,
                "Windows 11 est hors de portée sans changer de matériel",
                Summary(blocking),
                plain,
                evidence: evidence,
                recommendations: RuleContext.Rec(Rec.PlanHardwareUpgrade),
                confidence: ConfidenceLevel.Medium));
        }

        /// <summary>
        /// Le cas qui vaut la peine d'être cherché : rien à changer, seulement à régler.
        /// </summary>
        /// <remarks>
        /// La gravité dépend de ce qui est installé, pas de ce qui est bloqué. Sur une version
        /// encore suivie, un module TPM éteint n'empêche rien aujourd'hui. Sur une version qui ne
        /// reçoit plus de correctifs, le même réglage sépare une machine à jour d'une machine
        /// exposée, et il se change en quelques minutes dans le micrologiciel.
        /// </remarks>
        private static RuleResult BlockingSettings(RuleContext c)
        {
            var assessment = UpgradeAdvisor.Assess(c.Snapshot);

            if (assessment.Verdict == UpgradeVerdict.Unknown || assessment.Verdict == UpgradeVerdict.NotApplicable ||
                assessment.Verdict == UpgradeVerdict.AlreadyThere)
                return RuleResult.NotEvaluated(
                    assessment.Note ?? "L'éligibilité à Windows 11 n'a pas pu être établie.");

            var settings = assessment.With(RequirementOutcome.Fixable);
            if (settings.Count == 0) return RuleResult.Clean;

            // Un blocage matériel rend les réglages sans objet : les régler ne débloquerait rien,
            // et le dire ferait espérer pour rien.
            if (assessment.Verdict == UpgradeVerdict.Ineligible) return RuleResult.Clean;

            var support = WindowsLifecycle.For(c.Windows, c.Snapshot.Metadata.CreatedAt);
            var urgent = support.State == WindowsSupportState.Ended;

            var evidence = new List<Evidence>();
            var recommendations = new List<string>();

            foreach (var requirement in settings)
            {
                evidence.Add(Evidence.Of(requirement.Label, requirement.Observation, DataSource.Inferred,
                    requirement.Expectation));

                if (requirement.Id == "tpm") recommendations.Add(Rec.EnableTpm);
                if (requirement.Id == "secureboot") recommendations.Add(Rec.EnableSecureBoot);
            }

            var plain = urgent
                ? "Cette machine peut recevoir Windows 11 : ce qui l'en empêche tient à des réglages du " +
                  "micrologiciel, pas à son matériel. La version installée ne recevant plus de correctifs, " +
                  "c'est la piste à examiner en premier : elle ne coûte rien d'autre qu'une intervention."
                : "Cette machine pourrait recevoir Windows 11 : ce qui l'en empêche tient à des réglages du " +
                  "micrologiciel, pas à son matériel. Rien ne presse tant que la version installée est suivie, " +
                  "mais il est utile de le savoir avant d'envisager un remplacement.";

            return RuleResult.Of(c.Finding(urgent ? Severity.Warning : Severity.Info,
                "Seuls des réglages empêchent le passage à Windows 11",
                Summary(settings),
                plain,
                evidence: evidence,
                recommendations: recommendations,
                confidence: ConfidenceLevel.Medium));
        }

        /// <summary>Les exigences concernées, nommées avec ce qui a été relevé.</summary>
        private static string Summary(IReadOnlyList<UpgradeRequirement> requirements)
        {
            var text = new StringBuilder();
            foreach (var requirement in requirements)
            {
                if (text.Length > 0) text.Append(" ");
                text.Append(requirement.Label).Append(" : ").Append(requirement.Observation);
            }

            return text.ToString();
        }

        /// <summary>Le motif de non-évaluation : ce qui manque, et non le fait qu'il manque quelque chose.</summary>
        private static string Missing(UpgradeAssessment assessment)
        {
            var unknown = assessment.With(RequirementOutcome.Unknown);
            var text = new StringBuilder("L'éligibilité à Windows 11 ne peut pas être établie : ");

            for (var i = 0; i < unknown.Count; i++)
            {
                if (i > 0) text.Append(i == unknown.Count - 1 ? " et " : ", ");
                text.Append(Uncapitalize(unknown[i].Observation.TrimEnd('.')));
            }

            return text.Append(".").ToString();
        }

        /// <summary>
        /// Minuscule sur la première lettre seulement.
        /// </summary>
        /// <remarks>
        /// Passer toute la phrase en minuscules écrirait « module tpm » et « uefi » : les sigles
        /// sont ce que le technicien cherche des yeux dans un motif.
        /// </remarks>
        private static string Uncapitalize(string text)
            => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);
    }
}
