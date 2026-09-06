using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur les tâches planifiées.
    /// </summary>
    /// <remarks>
    /// Même ligne que pour l'inventaire logiciel : on constate ce qui a été déposé sur la
    /// machine, on ne juge pas ce que Windows range dans son propre dossier : ces tâches-là ne
    /// sont pas examinées. Trois constats seulement, chacun défendable devant l'éditeur concerné :
    /// une tâche qui échoue, une tâche qui lance un programme absent, une tâche qui se répète si
    /// souvent qu'elle empêche la machine de se reposer.
    /// </remarks>
    internal static class ScheduledTaskRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;
        private const string Family = "startup.tasks";

        /// <summary>Au-delà, la liste cesse d'être lisible dans un constat.</summary>
        private const int MaxNamed = 5;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("TSK-001", "Tâches planifiées en échec", Cat, Failing, Family);
            yield return new Rule("TSK-002", "Tâches planifiées orphelines", Cat, Orphans, Family);
            yield return new Rule("TSK-003", "Tâches planifiées très fréquentes", Cat, Frequent, Family);
        }

        /// <summary>
        /// Une tâche dont la dernière exécution s'est mal passée.
        /// </summary>
        /// <remarks>
        /// La distinction entre les deux gravités n'est pas cosmétique. « Windows n'a pas pu
        /// lancer la tâche » désigne un problème de la machine, fichier absent, compte
        /// supprimé, droits perdus. « Le programme a rendu un code non nul » désigne un problème
        /// du programme, sur lequel le technicien n'a souvent pas la main, et qui peut même être
        /// le fonctionnement normal de certains utilitaires.
        /// </remarks>
        private static RuleResult Failing(RuleContext c)
        {
            var inventory = c.System.Tasks;
            if (!inventory.TotalCount.IsReliable)
                return RuleResult.NotEvaluated(
                    inventory.TotalCount.Reason ?? "Le planificateur de tâches n'a pas pu être lu.");

            var blocked = new List<string>();
            var failed = new List<string>();

            foreach (var task in inventory.Tasks)
            {
                if (!task.Enabled) continue;
                if (task.ResultKind == TaskResultKind.TaskError) blocked.Add(task.Path);
                else if (task.ResultKind == TaskResultKind.ProgramError) failed.Add(task.Path);
            }

            if (blocked.Count == 0 && failed.Count == 0) return RuleResult.Clean;

            var severity = blocked.Count > 0 ? Severity.Warning : Severity.Info;
            var subjects = blocked.Count > 0 ? blocked : failed;

            var detail = blocked.Count > 0
                ? blocked.Count + " tâche(s) que Windows n'a pas pu exécuter : " + Name(blocked) + "."
                : failed.Count + " tâche(s) dont le programme s'est terminé sur une erreur : " + Name(failed) + ".";

            var plain = blocked.Count > 0
                ? "Ces tâches ne s'exécutent plus : Windows n'y arrive pas. Le programme qu'elles lancent a " +
                  "souvent été désinstallé ou déplacé, et la tâche est restée derrière lui."
                : "Ces tâches se lancent bien, mais le programme qu'elles appellent signale une erreur en " +
                  "se terminant. Cela vaut d'être regardé sans être forcément grave : certains utilitaires " +
                  "rendent un code non nul dans leur fonctionnement normal.";

            return RuleResult.Of(c.Finding(severity,
                blocked.Count > 0
                    ? "Des tâches planifiées ne s'exécutent plus"
                    : "Des tâches planifiées se terminent sur une erreur",
                detail, plain,
                subject: subjects[0],
                evidence: RuleContext.Ev(
                    Evidence.Of("Tâches en échec", subjects.Count.ToString(), DataSource.NativeApi, "0")),
                recommendations: RuleContext.Rec(Rec.CleanStartupOrphans)));
        }

        /// <summary>
        /// Une tâche qui lance un programme qui n'existe plus.
        /// </summary>
        /// <remarks>
        /// Les tâches déclenchées à l'ouverture de session comptent déjà parmi les programmes de
        /// démarrage, où la règle des entrées orphelines les voit. Ne restent ici que les autres
        /// (tâches quotidiennes, tâches désactivées), qu'aucune règle ne regardait.
        /// </remarks>
        private static RuleResult Orphans(RuleContext c)
        {
            var inventory = c.System.Tasks;
            if (!inventory.TotalCount.IsReliable)
                return RuleResult.NotEvaluated(
                    inventory.TotalCount.Reason ?? "Le planificateur de tâches n'a pas pu être lu.");

            var orphans = new List<string>();
            foreach (var task in inventory.Tasks)
                if (task.TargetMissing && !task.RunsAtStartup) orphans.Add(task.Path);

            if (orphans.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                orphans.Count == 1
                    ? "Une tâche planifiée appelle un programme absent"
                    : orphans.Count + " tâches planifiées appellent un programme absent",
                Name(orphans) + ".",
                "Un logiciel désinstallé a laissé sa tâche derrière lui. Elle ne fait plus rien, sinon " +
                "encombrer le planificateur et remplir les journaux à chaque tentative.",
                subject: orphans[0],
                evidence: RuleContext.Ev(
                    Evidence.Of("Tâches orphelines", orphans.Count.ToString(), DataSource.NativeApi, "0")),
                recommendations: RuleContext.Rec(Rec.CleanStartupOrphans)));
        }

        /// <summary>
        /// Une tâche qui revient toutes les quelques minutes.
        /// </summary>
        /// <remarks>
        /// Le constat porte sur la fréquence, pas sur l'intention : ce qui se mesure est qu'un
        /// programme se relance sans arrêt, réveille le disque et empêche la machine de se
        /// reposer. Ce qu'il fait pendant ce temps n'est pas l'affaire de cet outil.
        /// </remarks>
        private static RuleResult Frequent(RuleContext c)
        {
            var inventory = c.System.Tasks;
            if (!inventory.TotalCount.IsReliable)
                return RuleResult.NotEvaluated(
                    inventory.TotalCount.Reason ?? "Le planificateur de tâches n'a pas pu être lu.");

            var frequent = new List<string>();
            var shortest = int.MaxValue;

            foreach (var task in inventory.Tasks)
            {
                if (!task.Enabled || !task.RepetitionMinutes.HasValue) continue;
                if (task.RepetitionMinutes.Value > c.T.FrequentTaskMinutes) continue;

                frequent.Add(task.Path + " : toutes les " + task.RepetitionMinutes.Value + " min");
                if (task.RepetitionMinutes.Value < shortest) shortest = task.RepetitionMinutes.Value;
            }

            if (frequent.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                frequent.Count == 1
                    ? "Une tâche planifiée se relance très souvent"
                    : frequent.Count + " tâches planifiées se relancent très souvent",
                Name(frequent) + " (seuil " + c.T.FrequentTaskMinutes + " min).",
                "Un programme qui se relance toutes les quelques minutes réveille le disque, occupe le " +
                "processeur et empêche la machine de se mettre au repos. C'est rarement nécessaire, et " +
                "cela se voit sur l'autonomie d'un portable.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Répétition la plus courte", shortest + " min", DataSource.NativeApi,
                        c.T.FrequentTaskMinutes + " min")),
                recommendations: RuleContext.Rec(Rec.ReduceStartupItems)));
        }

        /// <summary>Cite les premières, compte les autres : un constat doit rester lisible.</summary>
        private static string Name(IReadOnlyList<string> paths)
        {
            if (paths.Count <= MaxNamed) return string.Join(" · ", Copy(paths, paths.Count));

            return string.Join(" · ", Copy(paths, MaxNamed)) +
                   " · et " + (paths.Count - MaxNamed) + " autre(s)";
        }

        private static string[] Copy(IReadOnlyList<string> values, int count)
        {
            var copy = new string[count];
            for (var index = 0; index < count; index++) copy[index] = values[index];
            return copy;
        }
    }
}
