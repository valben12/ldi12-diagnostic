using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur ce qui est installé.
    /// </summary>
    /// <remarks>
    /// <b>Trois règles, et pas une de plus.</b> Un inventaire logiciel invite à juger de tout 
    /// (ce navigateur, ce jeu, cette barre d'outils) et c'est exactement ce qu'un outil de
    /// diagnostic ne doit pas faire : le cahier des charges dit qu'il n'est pas un antivirus.
    /// Ne sont donc retenus que les constats qu'on peut défendre devant l'éditeur concerné : un
    /// logiciel que son éditeur ne corrige plus, plusieurs versions d'un même environnement
    /// d'exécution, et les utilitaires d'optimisation : ces derniers en simple information,
    /// comme une conversation à ouvrir avec le client et non comme un verdict.
    /// <para>
    /// Tout ce qui est affirmé ici vient de <see cref="SoftwareCatalog"/>, une table qu'on relit,
    /// plutôt que de conditions dispersées dans le code.
    /// </para>
    /// </remarks>
    internal static class SoftwareRules
    {
        /// <summary>Au-delà, une famille d'exécution a manifestement gardé ses anciennes versions.</summary>
        private const int TooManyRuntimeVersions = 2;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SFT-001", "Logiciels sans support", DiagnosticCategory.Security,
                EndOfSupport, "software.support");

            yield return new Rule("SFT-002", "Versions d'exécution accumulées", DiagnosticCategory.Security,
                RuntimeVersions, "software.support");

            yield return new Rule("SFT-003", "Utilitaires d'optimisation", DiagnosticCategory.Windows,
                Optimizers, "software.utilities");
        }

        /// <summary>
        /// Un logiciel que son éditeur ne corrige plus.
        /// </summary>
        /// <remarks>
        /// Le constat n'est pas « ce logiciel est dangereux » mais « une faille connue de ce
        /// logiciel le restera ». La nuance compte : le client a le droit de garder un vieux
        /// tableur qui lui convient, il n'a pas le droit d'ignorer qu'il ouvre avec des pièces
        /// jointes venues de l'extérieur.
        /// </remarks>
        private static RuleResult EndOfSupport(RuleContext c)
        {
            var inventory = c.System.Software;
            if (inventory.Programs.Count == 0)
                return RuleResult.NotEvaluated(
                    inventory.Limitation ?? "La liste des logiciels installés n'a pas pu être établie.");

            var found = new List<string>();
            var oldest = int.MaxValue;
            string? explanation = null;

            foreach (var program in inventory.Programs)
            {
                var note = SoftwareCatalog.Describe(program.Name);
                if (note == null || note.Concern != SoftwareConcern.EndOfSupport) continue;

                found.Add(note.Label + (note.SupportEndedYear.HasValue
                    ? " (plus de correctif depuis " + note.SupportEndedYear.Value.ToString(CultureInfo.CurrentCulture) + ")"
                    : string.Empty));

                if (note.SupportEndedYear.HasValue && note.SupportEndedYear.Value < oldest)
                {
                    oldest = note.SupportEndedYear.Value;
                    explanation = note.Explanation;
                }
            }

            if (found.Count == 0) return RuleResult.Clean;

            // Un logiciel abandonné depuis plus de cinq ans a eu le temps de voir publier tout
            // ce qui le concerne ; en deçà, le risque est réel mais moins documenté.
            var severity = oldest <= DateTime.Now.Year - 5 ? Severity.Problem : Severity.Warning;

            return RuleResult.Of(c.Finding(severity,
                found.Count == 1
                    ? "Un logiciel installé n'est plus corrigé par son éditeur"
                    : found.Count + " logiciels installés ne sont plus corrigés par leur éditeur",
                string.Join(" · ", found.ToArray()) + ".",
                (explanation ?? string.Empty) + (found.Count > 1
                    ? " Les autres logiciels signalés sont dans le même cas."
                    : string.Empty),
                evidence: RuleContext.Ev(
                    Evidence.Of("Logiciels sans support", string.Join(", ", found.ToArray()),
                        DataSource.Registry, "aucun")),
                recommendations: RuleContext.Rec(Rec.RemoveUnsupportedSoftware)));
        }

        /// <summary>
        /// Plusieurs versions d'un même environnement d'exécution.
        /// </summary>
        /// <remarks>
        /// Cohabiter n'est pas une faute : un logiciel métier peut exiger une version précise, et
        /// le dire ainsi évite d'envoyer le technicien désinstaller ce qui fait tourner la
        /// comptabilité du client. Ce qui se constate est le nombre : trois versions signifient
        /// presque toujours que les deux anciennes n'ont jamais été retirées, et chacune garde
        /// ses propres failles.
        /// </remarks>
        private static RuleResult RuntimeVersions(RuleContext c)
        {
            var inventory = c.System.Software;
            if (inventory.Programs.Count == 0)
                return RuleResult.NotEvaluated(
                    inventory.Limitation ?? "La liste des logiciels installés n'a pas pu être établie.");

            var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var program in inventory.Programs)
            {
                var family = SoftwareCatalog.RuntimeFamily(program.Name);
                if (family == null || string.IsNullOrWhiteSpace(program.Version)) continue;

                if (!versions.TryGetValue(family, out var set))
                    versions[family] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                set.Add(program.Version!);
            }

            foreach (var pair in versions)
            {
                if (pair.Value.Count <= TooManyRuntimeVersions) continue;

                var list = new List<string>(pair.Value);
                list.Sort(StringComparer.OrdinalIgnoreCase);

                return RuleResult.Of(c.Finding(Severity.Warning,
                    pair.Value.Count + " versions de " + Family(pair.Key) + " cohabitent",
                    "Versions installées : " + string.Join(", ", list.ToArray()) + ".",
                    "Chaque version installée garde ses propres failles, y compris celles que les " +
                    "suivantes ont corrigées. Une seule est en général utilisée ; les autres sont " +
                    "restées là parce qu'aucune mise à jour ne retire la précédente. Il faut " +
                    "vérifier laquelle un logiciel de la maison réclame avant de retirer les autres.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Versions de " + Family(pair.Key), string.Join(", ", list.ToArray()),
                            DataSource.Registry, "une seule")),
                    recommendations: RuleContext.Rec(Rec.RemoveUnsupportedSoftware)));
            }

            return RuleResult.Clean;
        }

        /// <summary>
        /// Les utilitaires d'optimisation.
        /// </summary>
        /// <remarks>
        /// En information et jamais au-delà. Ce ne sont pas des logiciels malveillants, le
        /// diagnostic ne le dit pas, et il ne propose pas de les retirer : il constate leur
        /// présence, explique ce qu'ils promettent, et laisse la décision au client. C'est la
        /// limite exacte entre l'audit et l'antivirus, et elle est tenue ici.
        /// </remarks>
        private static RuleResult Optimizers(RuleContext c)
        {
            var inventory = c.System.Software;
            if (inventory.Programs.Count == 0)
                return RuleResult.NotEvaluated(
                    inventory.Limitation ?? "La liste des logiciels installés n'a pas pu être établie.");

            var found = new List<string>();
            string? explanation = null;

            foreach (var program in inventory.Programs)
            {
                var note = SoftwareCatalog.Describe(program.Name);
                if (note == null || note.Concern != SoftwareConcern.Optimizer) continue;

                found.Add(note.Label);
                explanation ??= note.Explanation;
            }

            if (found.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                found.Count == 1
                    ? "Un utilitaire d'optimisation est installé"
                    : found.Count + " utilitaires d'optimisation sont installés",
                string.Join(" · ", found.ToArray()) + ".",
                explanation ?? string.Empty,
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Utilitaires d'optimisation", string.Join(", ", found.ToArray()),
                        DataSource.Registry))));
        }

        private static string Family(string key)
            => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key.Substring(1);
    }
}
