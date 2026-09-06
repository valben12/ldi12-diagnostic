using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Engine.Recommendations
{
    /// <summary>
    /// Assemble le plan d'action à partir des constats et des corrélations.
    /// </summary>
    /// <remarks>
    /// Deux exigences : <b>dédoublonner</b> (dix constats « disque plein » ne doivent produire
    /// qu'une seule ligne) et <b>ordonner</b> par priorité puis par rapport impact/effort, pour
    /// que le technicien lise en tête ce qui change le plus pour le moins de travail.
    /// </remarks>
    public static class RecommendationBuilder
    {
        public static IReadOnlyList<Recommendation> Build(
            IReadOnlyList<Finding> findings,
            IReadOnlyList<Core.Model.Correlation> correlations)
        {
            var triggers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
                foreach (var id in finding.Recommendations)
                    Track(triggers, id, finding.Key);

            // Les corrélations ne créent pas de recommandation nouvelle : elles confirment celles
            // que les constats ont déjà déclenchées, et en fixent l'ordre de traitement.
            foreach (var correlation in correlations)
                foreach (var id in correlation.OrderedActions)
                    Track(triggers, id, correlation.Id);

            var result = new List<Recommendation>(triggers.Count);
            foreach (var pair in triggers)
            {
                if (!RecommendationCatalog.TryGet(pair.Key, out var template)) continue;

                result.Add(new Recommendation
                {
                    Id = template.Id,
                    Title = template.Title,
                    Rationale = template.Rationale,
                    Priority = template.Priority,
                    Effort = template.Effort,
                    ExpectedImpact = template.ExpectedImpact,
                    LinkedAction = template.LinkedAction,
                    RequiresHardwarePurchase = template.RequiresHardwarePurchase,
                    TriggeredBy = pair.Value,
                });
            }

            result.Sort(Compare);
            return result;
        }

        private static void Track(Dictionary<string, List<string>> triggers, string id, string source)
        {
            if (!triggers.TryGetValue(id, out var sources))
            {
                sources = new List<string>();
                triggers[id] = sources;
            }
            if (!sources.Contains(source)) sources.Add(source);
        }

        private static int Compare(Recommendation a, Recommendation b)
        {
            var byPriority = a.Priority.CompareTo(b.Priority);
            if (byPriority != 0) return byPriority;

            // À priorité égale, ce qui apporte le plus passe devant.
            var byImpact = b.ExpectedImpact.CompareTo(a.ExpectedImpact);
            if (byImpact != 0) return byImpact;

            // Puis ce qui coûte le moins d'effort : le technicien veut un résultat rapide d'abord.
            var byEffort = a.Effort.CompareTo(b.Effort);
            if (byEffort != 0) return byEffort;

            return string.CompareOrdinal(a.Id, b.Id);
        }
    }
}
