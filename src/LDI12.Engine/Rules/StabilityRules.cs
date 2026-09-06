using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur l'histoire récente de la machine.
    /// </summary>
    /// <remarks>
    /// <b>Le reste du moteur juge un état ; ces règles-ci jugent une évolution.</b> Une machine
    /// qui plante trois fois par semaine depuis deux ans et une machine qui s'est mise à planter
    /// mardi dernier présentent le même relevé, et n'appellent pas le même travail. La première
    /// demande qu'on cherche une cause de fond, la seconde qu'on regarde ce qui a changé.
    /// <para>
    /// Aucune de ces règles n'affirme de cause. Une mise à jour installée deux jours avant le
    /// premier plantage est une coïncidence de dates, montrée comme telle à quelqu'un qui saura
    /// la vérifier ; l'écrire autrement serait une conclusion que ce logiciel n'a pas les moyens
    /// de tirer.
    /// </para>
    /// </remarks>
    internal static class StabilityRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;
        private const string Programs = "stability.programs";

        /// <summary>Au-delà, la liste cesse d'être lisible dans un constat.</summary>
        private const int MaxNamed = 4;

        /// <summary>En deçà, il n'y a pas assez de pannes pour parler d'un rythme.</summary>
        private const int MinIncidentDays = 8;

        /// <summary>Une série plus courte est une mauvaise journée, pas une dégradation.</summary>
        private const int MinBurstDays = 3;

        /// <summary>Rapport de rythme en deçà duquel la différence tient au hasard.</summary>
        private const double MinRateFactor = 2;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("STA-001", "Programmes qui plantent à répétition", Cat, FailingPrograms, Programs);
            yield return new Rule("STA-002", "Services qui s'arrêtent seuls", Cat, FailingServices, Programs);
            yield return new Rule("STA-003", "Instabilité récente", Cat, RecentInstability, Programs);
        }

        /// <summary>
        /// Un programme qui plante ou se bloque à répétition.
        /// </summary>
        /// <remarks>
        /// La gravité dépend de la date de la dernière occurrence, pas du nombre. Trente
        /// plantages d'un jeu désinstallé depuis deux mois ne sont plus un problème ; trois
        /// plantages du navigateur cette semaine en sont un, parce que le client les vit encore.
        /// </remarks>
        private static RuleResult FailingPrograms(RuleContext c)
        {
            var stability = c.System.Stability;
            if (!stability.WindowDays.IsReliable)
                return RuleResult.NotEvaluated(
                    stability.WindowDays.Reason ?? "L'historique de stabilité n'a pas pu être lu.");

            var recentStart = c.Snapshot.Metadata.CreatedAt.AddDays(-c.T.RecentInstabilityDays);

            var current = new List<FailingProgram>();
            var past = new List<FailingProgram>();

            foreach (var program in stability.Programs)
            {
                if (program.Kind != IncidentKind.ProgramCrash && program.Kind != IncidentKind.ProgramHang) continue;
                if (program.Count < c.T.RepeatedFailureCount) continue;

                if (program.LastSeen >= recentStart) current.Add(program);
                else past.Add(program);
            }

            if (current.Count == 0 && past.Count == 0) return RuleResult.Clean;

            var subjects = current.Count > 0 ? current : past;
            var severity = current.Count > 0 ? Severity.Warning : Severity.Info;

            var detail = Describe(subjects) +
                         (current.Count > 0
                             ? " Dernière occurrence il y a " + Days(c, subjects[0].LastSeen) + " jour(s)."
                             : " Plus rien depuis " + Days(c, subjects[0].LastSeen) + " jour(s).");

            var modules = NamedModules(subjects);
            if (modules != null) detail += " Composant mis en cause par Windows : " + modules + ".";

            var plain = current.Count > 0
                ? "Ces programmes se ferment tout seuls ou cessent de répondre régulièrement. C'est ce " +
                  "que le client décrit quand il dit que « ça plante » : le problème est réel et il est " +
                  "enregistré, programme par programme."
                : "Ces programmes ont planté à répétition, mais plus récemment. Soit ils ont été " +
                  "réparés ou désinstallés, soit ils ne sont plus utilisés : rien n'indique un problème " +
                  "en cours.";

            return RuleResult.Of(c.Finding(severity,
                current.Count == 1
                    ? "Un programme plante à répétition"
                    : subjects.Count + " programmes plantent à répétition",
                detail, plain,
                subject: subjects[0].Name,
                evidence: RuleContext.Ev(Evidence.Of(
                    "Plantages de " + subjects[0].Name,
                    subjects[0].Count + " en " + stability.WindowDays.Value + " jours",
                    DataSource.EventLog,
                    "moins de " + c.T.RepeatedFailureCount)),
                recommendations: RuleContext.Rec(Rec.RepairFailingProgram)));
        }

        /// <summary>
        /// Un service qui s'arrête sans qu'on le lui ait demandé.
        /// </summary>
        /// <remarks>
        /// Distinct du constat sur les services essentiels arrêtés, qui regarde un état : celui-ci
        /// regarde une répétition. Un service qui tombe et que Windows relance dix fois par jour
        /// est vu « démarré » par l'autre règle, et ne se voit que dans les journaux.
        /// </remarks>
        private static RuleResult FailingServices(RuleContext c)
        {
            var stability = c.System.Stability;
            if (!stability.ServiceCrashes.IsReliable)
                return RuleResult.NotEvaluated(
                    stability.ServiceCrashes.Reason ?? "Le journal système n'a pas pu être lu.");

            var services = new List<FailingProgram>();
            foreach (var program in stability.Programs)
                if (program.Kind == IncidentKind.ServiceCrash && program.Count >= c.T.RepeatedFailureCount)
                    services.Add(program);

            if (services.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                services.Count == 1
                    ? "Un service s'arrête tout seul à répétition"
                    : services.Count + " services s'arrêtent seuls à répétition",
                Describe(services) + " Dernière fois il y a " + Days(c, services[0].LastSeen) + " jour(s).",
                "Ces services s'arrêtent sans qu'on le leur demande, et Windows les relance. Entre les " +
                "deux, la fonction qu'ils rendent (impression, son, réseau, sauvegarde) est " +
                "indisponible, ce qui explique des pannes qui « se réparent toutes seules ».",
                subject: services[0].Name,
                evidence: RuleContext.Ev(Evidence.Of(
                    "Arrêts de " + services[0].Name,
                    services[0].Count + " en " + stability.WindowDays.Value + " jours",
                    DataSource.EventLog,
                    "moins de " + c.T.RepeatedFailureCount)),
                recommendations: RuleContext.Rec(Rec.InvestigateEventErrors)));
        }

        /// <summary>
        /// La machine s'est mise à aller mal.
        /// </summary>
        /// <remarks>
        /// <b>Le seul constat de ce logiciel qui compare la machine à elle-même.</b> Il cherche
        /// la date à partir de laquelle le rythme des pannes a changé, et la donne : « depuis le
        /// 22 août » est une réponse, « au cours des quinze derniers jours » n'en est pas une :
        /// c'était la borne de la fenêtre d'analyse, et elle serait sortie identique sur
        /// n'importe quelle machine.
        /// <para>
        /// Ce sont les <i>jours</i> avec panne qui sont comptés, jamais les pannes. Une
        /// après-midi où un programme se ferme sept fois de suite est un incident, pas sept, et
        /// compter les occurrences ferait passer une seule mauvaise séance pour une dégradation
        /// durable. C'est la même règle que pour les journaux d'événements.
        /// </para>
        /// <para>
        /// La comparaison exige une période antérieure assez longue pour servir de référence :
        /// sur une machine réinstallée la semaine dernière, tout est récent par construction, et
        /// l'annoncer comme une dégradation serait faux. Le cas est dit, pas deviné.
        /// </para>
        /// </remarks>
        private static RuleResult RecentInstability(RuleContext c)
        {
            var stability = c.System.Stability;
            if (!stability.WindowDays.IsReliable)
                return RuleResult.NotEvaluated(
                    stability.WindowDays.Reason ?? "L'historique de stabilité n'a pas pu être lu.");

            var timeline = TimelineBuilder.Build(c.Snapshot);
            var reference = timeline.ReferenceDate;
            var covered = timeline.CoveredSince ?? reference.AddDays(-stability.WindowDays.Value);

            var history = (int)(reference - covered).TotalDays;
            if (history < 2 * c.T.RecentInstabilityDays)
                return RuleResult.NotEvaluated(
                    "Les journaux ne remontent pas assez loin pour comparer la machine à elle-même : " +
                    Math.Max(0, history) + " jour(s) enregistrés.");

            var days = IncidentDays(timeline);
            if (days.Count < MinIncidentDays) return RuleResult.Clean;

            // Une dégradation qui s'est arrêtée n'est plus une dégradation. Sans cette condition,
            // une mauvaise semaine de juin serait encore annoncée en septembre.
            var last = days[days.Count - 1];
            if ((reference.Date - last).TotalDays > c.T.RecentInstabilityDays) return RuleResult.Clean;

            var start = Changepoint(c, days, covered.Date, reference.Date);
            if (start == null) return RuleResult.Clean;

            var since = new DateTimeOffset(start.Value, reference.Offset);
            var afterDays = (int)(reference.Date - start.Value).TotalDays + 1;
            var beforeDays = (int)(start.Value - covered.Date).TotalDays;

            var after = 0;
            foreach (var day in days) if (day >= start.Value) after++;
            var before = days.Count - after;

            var incidents = timeline.Count(true, since, reference.AddDays(1));
            var changes = timeline.ChangesBetween(since.AddDays(-c.T.ChangeCorrelationDays), since);

            var detail =
                "Depuis le " + Fmt.Date(since) + ", " + after + " jour(s) sur " + afterDays +
                " ont connu au moins une panne : " + incidents + " au total : contre " + before +
                " jour(s) sur " + beforeDays + " auparavant.";

            if (changes.Count > 0)
                detail += " Dans les " + c.T.ChangeCorrelationDays + " jours qui ont précédé cette date : " +
                          Changes(changes) + ".";

            var plain =
                "Cette machine ne plantait pas à ce rythme avant cette date : quelque chose a changé " +
                "à ce moment-là. " +
                (changes.Count > 0
                    ? "Ce qui a été installé juste avant est indiqué. C'est une coïncidence de dates et " +
                      "non une cause démontrée : cela se vérifie en retirant ou en remettant l'un de ces " +
                      "éléments, pas en le supposant."
                    : "Rien d'installé n'a été enregistré dans les jours qui ont précédé : la cause est " +
                      "à chercher ailleurs, du côté du matériel ou d'une usure.");

            return RuleResult.Of(c.Finding(Severity.Warning,
                "La machine s'est mise à planter le " + Fmt.Date(since),
                detail, plain,
                evidence: RuleContext.Ev(
                    Evidence.Of("Jours avec panne depuis cette date",
                        after + " sur " + afterDays,
                        DataSource.EventLog,
                        before + " sur " + beforeDays + " auparavant")),
                recommendations: RuleContext.Rec(Rec.InvestigateEventErrors),
                confidence: ConfidenceLevel.Medium));
        }

        /// <summary>
        /// Le jour où le rythme des pannes a le plus nettement changé.
        /// </summary>
        /// <remarks>
        /// Chaque jour avec panne est essayé comme date de rupture, et celui qui sépare le mieux
        /// les deux régimes est retenu, pas le premier qui dépasse le seuil. Sur la machine
        /// d'essai, « le premier qui dépasse » désignait le 5 juillet parce que le début de la
        /// période était creux ; le rythme, lui, avait changé le 22 août, et c'est cette date-là
        /// que le technicien peut rapprocher de quelque chose.
        /// <para>
        /// Les garde-fous empêchent de nommer une rupture qui n'en est pas : une série d'au moins
        /// trois jours, et une période de référence d'au moins la durée considérée comme récente.
        /// </para>
        /// </remarks>
        private static DateTime? Changepoint(
            RuleContext c, IReadOnlyList<DateTime> days, DateTime covered, DateTime reference)
        {
            DateTime? best = null;
            var bestRatio = 0d;

            for (var i = 0; i < days.Count; i++)
            {
                var candidate = days[i];

                var afterDays = (reference - candidate).TotalDays + 1;
                var beforeDays = (candidate - covered).TotalDays;
                if (afterDays < MinBurstDays || beforeDays < c.T.RecentInstabilityDays) continue;

                var after = days.Count - i;
                if (after < MinBurstDays) continue;

                var afterRate = after / afterDays;
                var beforeRate = i / beforeDays;
                if (afterRate < MinRateFactor * beforeRate) continue;

                // Une période antérieure sans aucune panne rend le rapport infini : la rupture
                // est alors la plus nette possible, et c'est bien ce qu'on veut dire.
                var ratio = beforeRate > 0 ? afterRate / beforeRate : double.PositiveInfinity;
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Les jours ayant connu au moins une panne, du plus ancien au plus récent.</summary>
        private static List<DateTime> IncidentDays(Timeline timeline)
        {
            var days = new List<DateTime>();
            var seen = new HashSet<DateTime>();

            foreach (var entry in timeline.Entries)
                if (entry.IsIncident && seen.Add(entry.Date.Date))
                    days.Add(entry.Date.Date);

            days.Sort();
            return days;
        }

        private static string Describe(IReadOnlyList<FailingProgram> programs)
        {
            var parts = new List<string>();
            for (var i = 0; i < programs.Count && i < MaxNamed; i++)
                parts.Add(programs[i].Name + " (" + programs[i].Count + ")");

            var text = string.Join(", ", parts.ToArray());
            if (programs.Count > MaxNamed) text += " et " + (programs.Count - MaxNamed) + " autre(s)";
            return text + ".";
        }

        private static string Changes(IReadOnlyList<TimelineEntry> changes)
        {
            var parts = new List<string>();
            for (var i = 0; i < changes.Count && i < MaxNamed; i++)
                parts.Add(changes[i].Subject + " le " + Fmt.Date(changes[i].Date));

            var text = string.Join(", ", parts.ToArray());
            if (changes.Count > MaxNamed) text += " et " + (changes.Count - MaxNamed) + " autre(s)";
            return text;
        }

        /// <summary>Le composant mis en cause, quand un seul revient sur tous les programmes nommés.</summary>
        /// <remarks>
        /// Un module unique partagé par plusieurs programmes désigne presque toujours un pilote
        /// ou une bibliothèque commune, et non les programmes eux-mêmes. Plusieurs modules
        /// différents ne disent rien : la ligne est alors omise plutôt que remplie.
        /// </remarks>
        private static string? NamedModules(IReadOnlyList<FailingProgram> programs)
        {
            string? single = null;
            foreach (var program in programs)
            {
                if (program.Module == null) continue;
                if (single == null) single = program.Module;
                else if (!string.Equals(single, program.Module, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return single;
        }

        private static int Days(RuleContext c, DateTimeOffset date)
            => Math.Max(0, (int)(c.Snapshot.Metadata.CreatedAt - date).TotalDays);
    }
}
