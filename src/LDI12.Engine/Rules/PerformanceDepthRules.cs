using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles de performance appuyées sur les mesures de la phase 6 : durée de démarrage,
    /// pression mémoire réelle, processus dominant.
    /// </summary>
    /// <remarks>
    /// La durée de démarrage est la seule mesure de cette dimension qui échappe à l'instantané :
    /// Windows l'a chronométrée lui-même, à froid, sans nous. Les autres décrivent la machine
    /// telle qu'elle est pendant l'analyse : elles servent à expliquer une lenteur constatée, pas
    /// à la constater, et le déclarent par une confiance moyenne.
    /// </remarks>
    internal static class PerformanceDepthRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Performance;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("PRF-005", "Durée de démarrage", Cat, BootDuration);
            yield return new Rule("PRF-006", "Mémoire réclamée", Cat, CommitPressure);
            yield return new Rule("PRF-007", "Processus dominant en mémoire", Cat, DominantProcess);
        }

        /// <summary>
        /// Durée du dernier démarrage mesuré par Windows.
        /// </summary>
        /// <remarks>
        /// Un démarrage lent isolé n'est pas un symptôme, après une mise à jour majeure, trois
        /// minutes sont normales. Le nombre de démarrages dégradés parmi les derniers relevés
        /// dit si le cas est isolé, et il est cité dans le constat plutôt que gardé pour soi.
        /// </remarks>
        private static RuleResult BootDuration(RuleContext c)
        {
            var boot = c.Snapshot.Performance.Boot;
            if (!boot.Duration.IsReliable)
                return RuleResult.NotEvaluated(
                    boot.Duration.Reason ?? "La durée de démarrage n'a pas été mesurée.");

            // Le journal de Windows peut n'avoir qu'un relevé, et vieux de plusieurs mois : le
            // démarrage rapide fait repartir la machine d'une mise en veille du noyau, sans
            // démarrage complet à chronométrer. Conclure sur une mesure d'il y a onze mois (cas
            // rencontré en session élevée sur la machine de développement) reviendrait à décrire
            // une machine qui n'existe plus.
            if (boot.MeasuredAt.IsReliable)
            {
                var age = DateTimeOffset.Now - boot.MeasuredAt.Value;
                if (age.TotalDays > c.T.BootMeasurementMaxAgeDays)
                    return RuleResult.NotEvaluated(
                        "Le dernier démarrage mesuré par Windows date du " +
                        Fmt.Date(boot.MeasuredAt.Value) + ", soit " + (int)age.TotalDays +
                        " jours : trop ancien pour décrire la machine d'aujourd'hui. Le démarrage " +
                        "rapide, quand il est actif, empêche Windows de chronométrer un vrai démarrage.");
            }

            var seconds = boot.Duration.Value.TotalSeconds;
            if (seconds < c.T.BootDurationSecondsWarning) return RuleResult.Clean;

            var severity = seconds >= c.T.BootDurationSecondsProblem ? Severity.Warning : Severity.Info;
            var threshold = severity == Severity.Warning
                ? c.T.BootDurationSecondsProblem
                : c.T.BootDurationSecondsWarning;

            var degraded = boot.DegradedBootCount.Or(0);
            var samples = boot.SampleCount.Or(0);

            // Un seul démarrage lent sur dix relevés est un accident, pas une tendance.
            var isolated = samples >= 3 && degraded <= 1;
            if (isolated) severity = Severity.Info;

            return RuleResult.Of(c.Finding(severity,
                isolated ? "Un démarrage lent isolé a été relevé" : "Le démarrage est long",
                "Dernier démarrage en " + Fmt.Duration(boot.Duration.Value) + " (seuil " + threshold + " s)" +
                (samples > 0 ? " : " + degraded + " démarrage(s) dégradé(s) sur les " + samples + " derniers." : "."),
                isolated
                    ? "Un démarrage a été plus long que d'habitude, mais les autres sont normaux. C'est ce qui " +
                      "arrive après une mise à jour de Windows : il n'y a rien à en conclure."
                    : "L'ordinateur met " + Fmt.Duration(boot.Duration.Value) + " à devenir utilisable après " +
                      "l'allumage. Réduire le nombre de programmes lancés au démarrage est l'action la plus " +
                      "rentable sur ce point.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Durée de démarrage", Fmt.Duration(boot.Duration.Value),
                        boot.Duration.Source, threshold + " s"),
                    Evidence.Of("Démarrages dégradés", degraded + " sur " + samples,
                        boot.DegradedBootCount.Source)),
                recommendations: isolated ? null : RuleContext.Rec(Rec.ReduceStartupItems)));
        }

        /// <summary>
        /// Mémoire validée rapportée à la mémoire installée.
        /// </summary>
        /// <remarks>
        /// Complémentaire de PRF-003, qui mesure l'occupation instantanée. Celle-ci dit ce que la
        /// machine <b>réclame</b> : au-dessus de 100 %, elle tient sa charge courante grâce au
        /// fichier d'échange, donc en écrivant sur le disque ce qui aurait dû tenir en mémoire.
        /// C'est l'explication la plus fréquente d'une machine lente dont le processeur n'est
        /// pourtant pas saturé.
        /// </remarks>
        private static RuleResult CommitPressure(RuleContext c)
        {
            var ratio = c.Snapshot.Performance.Responsiveness.CommitRatioPercent;
            if (!ratio.IsReliable)
                return RuleResult.NotEvaluated(
                    ratio.Reason ?? "Le rapport entre mémoire réclamée et mémoire installée n'a pas pu être calculé.");

            if (ratio.Value < c.T.CommitRatioWarning) return RuleResult.Clean;

            var severity = ratio.Value >= c.T.CommitRatioProblem ? Severity.Warning : Severity.Info;
            var threshold = severity == Severity.Warning ? c.T.CommitRatioProblem : c.T.CommitRatioWarning;

            var committed = c.Snapshot.Performance.Responsiveness.CommittedBytes;
            var installed = c.Hardware.Memory.TotalBytes;

            return RuleResult.Of(c.Finding(severity,
                "La machine réclame plus de mémoire qu'elle n'en possède",
                Fmt.Percent(ratio.Value) + " de la mémoire installée réclamée" +
                (committed.HasValue && installed.HasValue
                    ? " : " + Fmt.Bytes(committed.Value) + " demandés pour " + Fmt.Bytes(installed.Value) + " installés"
                    : string.Empty) + " (seuil " + Fmt.Percent(threshold) + ").",
                "Les programmes ouverts demandent plus de mémoire que l'ordinateur n'en possède. Windows compense " +
                "en écrivant la différence sur le disque, ce qui est plusieurs centaines de fois plus lent. " +
                "C'est souvent la vraie raison d'une machine qui « rame » alors que son processeur n'est pas chargé.",
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Mémoire réclamée", Fmt.Percent(ratio.Value), ratio.Source, Fmt.Percent(threshold))),
                recommendations: RuleContext.Rec(Rec.AddMemory)));
        }

        /// <summary>
        /// Un processus occupe-t-il à lui seul une part notable de la mémoire ?
        /// </summary>
        /// <remarks>
        /// Constat volontairement neutre : un navigateur à 3 Go est banal, et le signaler comme
        /// une anomalie serait faux. Ce que le technicien veut savoir, c'est <b>lequel</b> : pour
        /// pouvoir le montrer au client et lui demander s'il s'en sert. La règle nomme donc, et
        /// ne juge pas.
        /// </remarks>
        private static RuleResult DominantProcess(RuleContext c)
        {
            var processes = c.Snapshot.Performance.TopByMemory;
            if (processes.Count == 0)
                return RuleResult.NotEvaluated("Les processus n'ont pas pu être énumérés.");

            var installed = c.Hardware.Memory.TotalBytes;
            if (!installed.IsReliable || installed.Value <= 0)
                return RuleResult.NotEvaluated("La mémoire installée n'est pas connue : aucune part n'est calculable.");

            var top = processes[0];
            if (!top.WorkingSetBytes.HasValue)
                return RuleResult.NotEvaluated("La mémoire du processus principal n'a pas pu être lue.");

            var share = 100d * top.WorkingSetBytes.Value / installed.Value;
            if (share < c.T.SingleProcessMemoryShareWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Un programme occupe une part importante de la mémoire",
                top.Name + " (PID " + top.ProcessId + ") occupe " + Fmt.Bytes(top.WorkingSetBytes.Value) +
                ", soit " + Fmt.Percent(share) + " de la mémoire installée (seuil " +
                Fmt.Percent(c.T.SingleProcessMemoryShareWarning) + ").",
                "Le programme « " + top.Name + " » utilise à lui seul " + Fmt.Percent(share) +
                " de la mémoire de l'ordinateur. Ce n'est pas anormal pour un navigateur ou un logiciel de " +
                "montage ; ça l'est davantage pour un programme dont vous ne vous servez pas.",
                subject: top.Name,
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Mémoire du processus", Fmt.Bytes(top.WorkingSetBytes.Value),
                        top.WorkingSetBytes.Source),
                    Evidence.Of("Part de la mémoire installée", Fmt.Percent(share), installed.Source,
                        Fmt.Percent(c.T.SingleProcessMemoryShareWarning))),
                recommendations: RuleContext.Rec(Rec.InvestigateCpuLoad)));
        }
    }
}
