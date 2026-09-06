using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur les réglages qui brident la machine.
    /// </summary>
    /// <remarks>
    /// Ce sont les seules pannes de ce logiciel qui n'en sont pas : rien n'est cassé, tout
    /// fonctionne, et la machine va deux fois moins vite qu'elle ne le devrait. Elles se
    /// corrigent en trois clics, ce qui les rend d'autant plus frustrantes à ne pas voir, et
    /// d'autant plus satisfaisantes à montrer au client.
    /// </remarks>
    internal static class PowerRules
    {
        private const string Family = "power.settings";

        /// <summary>Réglage d'origine de Windows : le processeur peut monter au maximum.</summary>
        private const int FullSpeed = 100;

        /// <summary>En deçà, une machine sans mémoire d'échange devient instable, pas seulement lente.</summary>
        private const long LowMemoryBytes = 8L * 1024 * 1024 * 1024;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("PWR-001", "Processeur bridé par le plan d'alimentation",
                DiagnosticCategory.Performance, Throttled, Family);

            yield return new Rule("PWR-002", "Plan d'économie d'énergie sur secteur",
                DiagnosticCategory.Performance, PowerSaver, Family);

            yield return new Rule("PWR-003", "Fichier d'échange désactivé",
                DiagnosticCategory.Performance, PageFile, Family);

            yield return new Rule("PWR-004", "Notification de suppression désactivée",
                DiagnosticCategory.Storage, Trim, "storage.maintenance");
        }

        /// <summary>
        /// Un processeur que le plan d'alimentation empêche de monter en fréquence.
        /// </summary>
        /// <remarks>
        /// Le constat le plus rentable de ce fichier. Une machine bridée à cinquante pour cent
        /// passe tous les contrôles matériels et met deux fois plus de temps à tout faire ; le
        /// client la croit usée et la remplace. Le réglage vient presque toujours d'un utilitaire
        /// d'« optimisation » ou d'une tentative de faire moins chauffer un portable.
        /// </remarks>
        private static RuleResult Throttled(RuleContext c)
        {
            var maximum = c.Snapshot.Performance.Power.ProcessorMaximumOnAc;
            if (!maximum.IsReliable)
                return RuleResult.NotEvaluated(
                    maximum.Reason ?? "Le bridage du processeur n'a pas pu être lu.");

            if (maximum.Value >= FullSpeed) return RuleResult.Clean;

            var severity = maximum.Value <= c.T.ProcessorThrottleProblemPercent
                ? Severity.Problem
                : Severity.Warning;

            return RuleResult.Of(c.Finding(severity,
                "Le processeur ne peut pas dépasser " + maximum.Value + " % de sa fréquence",
                "Plan d'alimentation actif, réglage sur secteur : " + maximum.Value + " % (seuil " +
                c.T.ProcessorThrottleProblemPercent + " %, réglage d'origine " + FullSpeed + " %).",
                "Le plan d'alimentation interdit au processeur de monter en fréquence. Rien n'est " +
                "défaillant : la machine est simplement retenue, et le restera tant que ce réglage " +
                "sera là. C'est une cause de lenteur qu'aucun contrôle matériel ne peut trouver.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fréquence maximale autorisée", maximum.Value + " %",
                        maximum.Source, FullSpeed + " %")),
                recommendations: RuleContext.Rec(Rec.RestorePowerPlan)));
        }

        /// <summary>
        /// Le plan « économie d'énergie », qui n'économise rien sur une machine branchée.
        /// </summary>
        /// <remarks>
        /// Sur un portable, c'est un choix défendable et le constat reste une information : le
        /// client a peut-être besoin de son autonomie. Sur un poste fixe, il n'y a pas de
        /// batterie à ménager, et le réglage ne fait que ralentir la machine.
        /// </remarks>
        private static RuleResult PowerSaver(RuleContext c)
        {
            var plan = c.Snapshot.Performance.Power.Plan;
            if (!plan.IsReliable)
                return RuleResult.NotEvaluated(plan.Reason ?? "Le plan d'alimentation n'a pas pu être lu.");

            if (plan.Value != PowerPlanKind.PowerSaver) return RuleResult.Clean;

            var portable = c.Hardware.Batteries.Count > 0;

            return RuleResult.Of(c.Finding(portable ? Severity.Info : Severity.Warning,
                portable
                    ? "La machine tourne en mode économie d'énergie"
                    : "Un poste fixe tourne en mode économie d'énergie",
                "Plan d'alimentation actif : économie d'énergie.",
                portable
                    ? "Ce mode ménage la batterie en limitant les performances. C'est un choix, pas un " +
                      "défaut : il explique en revanche une partie de la lenteur ressentie."
                    : "Ce mode limite les performances pour ménager une batterie que cette machine n'a pas. " +
                      "Il n'apporte donc rien ici, et se paie sur tout ce que fait la machine.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Plan d'alimentation", "Économie d'énergie", plan.Source, "Équilibré")),
                recommendations: RuleContext.Rec(Rec.RestorePowerPlan)));
        }

        /// <summary>
        /// Une machine sans fichier d'échange.
        /// </summary>
        /// <remarks>
        /// Le réglage que laissent derrière elles les « astuces pour accélérer Windows ». Sur une
        /// machine bien pourvue en mémoire, il ne se voit pas ; sur une machine modeste, il fait
        /// fermer les logiciels sans prévenir dès que la mémoire vive est pleine, et le client
        /// décrit alors des plantages aléatoires que rien n'explique.
        /// </remarks>
        private static RuleResult PageFile(RuleContext c)
        {
            var mode = c.Snapshot.Performance.Power.PageFile;
            if (!mode.IsReliable)
                return RuleResult.NotEvaluated(
                    mode.Reason ?? "Le réglage du fichier d'échange n'a pas pu être lu.");

            if (mode.Value != PageFileMode.Disabled) return RuleResult.Clean;

            var memory = c.Hardware.Memory.TotalBytes;
            var modest = memory.IsReliable && memory.Value < LowMemoryBytes;

            return RuleResult.Of(c.Finding(modest ? Severity.Problem : Severity.Warning,
                "Cette machine n'a aucun fichier d'échange",
                "Aucun fichier d'échange configuré" +
                (memory.IsReliable ? ", pour " + Fmt.Bytes(memory.Value) + " de mémoire vive" : string.Empty) + ".",
                "Windows n'a plus où déposer ce qui ne tient pas en mémoire vive. Quand elle se remplit, " +
                "les logiciels se ferment sans prévenir plutôt que de ralentir, ce qui ressemble à des " +
                "plantages aléatoires et n'en est pas.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fichier d'échange", "Aucun", mode.Source, "Géré par Windows")),
                recommendations: RuleContext.Rec(Rec.RestorePageFile)));
        }

        /// <summary>
        /// La notification de suppression, coupée sur une machine à mémoire flash.
        /// </summary>
        /// <remarks>
        /// Sans elle, le disque ne sait pas quels blocs sont libres : il finit par écrire de plus
        /// en plus lentement, et rien ne le rattrape sinon un effacement complet. Le constat n'a
        /// de sens qu'en présence d'un support flash, sur un disque à plateaux, le réglage ne
        /// change rien du tout.
        /// </remarks>
        private static RuleResult Trim(RuleContext c)
        {
            var trim = c.Storage.Maintenance.TrimEnabled;
            if (!trim.IsReliable)
                return RuleResult.NotEvaluated(
                    trim.Reason ?? "Le réglage de notification de suppression n'a pas pu être lu.");

            if (trim.Value) return RuleResult.Clean;

            var flash = false;
            foreach (var disk in c.Storage.PhysicalDisks)
            {
                if (!disk.MediaType.IsReliable) continue;
                if (disk.MediaType.Value == StorageMediaType.Ssd || disk.MediaType.Value == StorageMediaType.Nvme)
                    flash = true;
            }

            if (!flash) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "La notification de suppression est désactivée",
                "Réglage désactivé alors que cette machine porte au moins un support à mémoire flash.",
                "Le disque n'est plus informé des fichiers effacés : il continue de croire ses blocs " +
                "occupés et écrit de plus en plus lentement à mesure qu'il se remplit. Le ralentissement " +
                "est progressif, définitif tant que le réglage est là, et invisible au SMART.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Notification de suppression", "Désactivée", trim.Source, "Activée")),
                recommendations: RuleContext.Rec(Rec.RestoreTrim)));
        }
    }
}
