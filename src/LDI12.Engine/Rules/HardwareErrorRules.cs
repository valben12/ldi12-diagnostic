using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur ce que le matériel a signalé de lui-même.
    /// </summary>
    /// <remarks>
    /// Ces constats ne mesurent rien : ils rapportent ce que la machine avait déjà écrit avant
    /// l'arrivée du technicien. C'est leur force (une erreur matérielle survenue il y a trois
    /// semaines ne se reproduira pas pendant l'analyse) et c'est leur limite : leur absence ne
    /// prouve rien. Aucune règle ici ne conclut donc « le matériel va bien », seulement « le
    /// matériel s'est plaint », ou rien du tout.
    /// </remarks>
    internal static class HardwareErrorRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Hardware;

        private const string Family = "hardware.errors";
        private const string Reports = "hardware.reports";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("HWE-001", "Erreurs matérielles irrécupérables", Cat, Uncorrected, Family);
            yield return new Rule("HWE-002", "Erreurs matérielles corrigées", Cat, Corrected, Family);
            yield return new Rule("HWE-003", "Résultat du test mémoire", Cat, MemoryTest, Family);
            yield return new Rule("HWE-004", "Instabilité sans test mémoire", Cat, UntestedMemory, Family);
            yield return new Rule("HWE-005", "Rapports de plantage disponibles", Cat, CrashReports, Reports);
            yield return new Rule("HWE-006", "Enregistrement des plantages désactivé", Cat, CrashReportsDisabled, Reports);
        }

        /// <summary>
        /// Une erreur que le matériel n'a pas su rattraper.
        /// </summary>
        /// <remarks>
        /// Il n'y a pas de faux positif possible ici : ce n'est pas une déduction de ce logiciel,
        /// c'est le processeur qui a signalé lui-même une erreur qu'il n'a pas corrigée, et
        /// Windows qui l'a écrite. La seule question ouverte est de savoir quel composant.
        /// </remarks>
        private static RuleResult Uncorrected(RuleContext c)
        {
            var errors = c.Hardware.Errors;
            if (!errors.UncorrectedCount.IsReliable)
                return RuleResult.NotEvaluated(
                    errors.UncorrectedCount.Reason ?? "Les erreurs matérielles n'ont pas pu être lues.");

            if (errors.UncorrectedCount.Value == 0) return RuleResult.Clean;

            var worst = Worst(errors, HardwareErrorKind.Uncorrected);

            return RuleResult.Of(c.Finding(Severity.Critical,
                "Le matériel a signalé une erreur qu'il n'a pas pu corriger",
                errors.UncorrectedCount.Value + " erreur(s) irrécupérable(s) sur " +
                errors.WindowDays.Or(90) + " jours" +
                (worst != null ? ", dernière le " + Fmt.Date(worst.LastSeen) : string.Empty) + ".",
                "Le matériel lui-même a signalé une erreur qu'il n'a pas su rattraper : c'est Windows qui " +
                "l'a enregistrée, pas ce logiciel qui la déduit. " +
                (worst?.Sample != null
                    ? "Windows la décrit ainsi : « " + worst.Sample + " » "
                    : string.Empty) +
                "Une machine qui plante sans motif apparent tient très souvent à cela.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Erreurs irrécupérables", errors.UncorrectedCount.Value.ToString(),
                        DataSource.EventLog, "0")),
                recommendations: RuleContext.Rec(Rec.CheckMemory, Rec.AnalyzeCrashDumps)));
        }

        /// <summary>
        /// Une erreur rattrapée par le matériel.
        /// </summary>
        /// <remarks>
        /// Rien n'a été perdu, rien n'a planté, et c'est précisément ce qui rend ce constat
        /// difficile à expliquer au client : le code correcteur a fait son travail. Ce qu'il
        /// annonce est un composant qui commence à fatiguer : une erreur isolée arrive, leur
        /// retour régulier annonce la suite.
        /// </remarks>
        private static RuleResult Corrected(RuleContext c)
        {
            var errors = c.Hardware.Errors;
            if (!errors.CorrectedCount.IsReliable)
                return RuleResult.NotEvaluated(
                    errors.CorrectedCount.Reason ?? "Les erreurs matérielles n'ont pas pu être lues.");

            var count = errors.CorrectedCount.Value;
            if (count < c.T.CorrectedHardwareErrorsWarning) return RuleResult.Clean;

            var severity = count >= c.T.CorrectedHardwareErrorsProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem
                ? c.T.CorrectedHardwareErrorsProblem
                : c.T.CorrectedHardwareErrorsWarning;

            var worst = Worst(errors, HardwareErrorKind.Corrected);

            return RuleResult.Of(c.Finding(severity,
                "Le matériel corrige des erreurs à répétition",
                count + " erreur(s) corrigée(s) sur " + errors.WindowDays.Or(90) + " jours (seuil " +
                threshold + ")" + (worst != null ? ", dernière le " + Fmt.Date(worst.LastSeen) : string.Empty) + ".",
                "Le matériel rattrape ces erreurs : rien n'a été perdu et la machine n'a rien montré. " +
                (worst?.Sample != null
                    ? "Windows les décrit ainsi : « " + worst.Sample + " » "
                    : string.Empty) +
                "Leur retour régulier annonce en revanche un composant qui fatigue, le plus souvent une " +
                "barrette de mémoire : cela se vérifie avant que la machine ne commence à planter.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Erreurs corrigées", count.ToString(), DataSource.EventLog, threshold.ToString())),
                recommendations: RuleContext.Rec(Rec.CheckMemory)));
        }

        /// <summary>
        /// Ce que le test mémoire de Windows a répondu la dernière fois qu'on le lui a demandé.
        /// </summary>
        /// <remarks>
        /// Un test annulé ou interrompu ne conclut rien : il ne figure ici ni comme réussite ni
        /// comme échec, mais comme une question restée ouverte. C'est la distinction que la table
        /// des identifiants d'événements sert à tenir.
        /// </remarks>
        private static RuleResult MemoryTest(RuleContext c)
        {
            var test = c.Hardware.Errors.MemoryTest;
            if (!test.IsReliable)
                return RuleResult.NotEvaluated(
                    test.Reason ?? "Le résultat du test mémoire n'a pas pu être lu.");

            var run = c.Hardware.Errors.LastMemoryTest;

            if (test.Value == MemoryTestOutcome.ErrorsFound)
            {
                return RuleResult.Of(c.Finding(Severity.Critical,
                    "Le test mémoire de Windows a trouvé des erreurs",
                    "Test du " + (run != null ? Fmt.Date(run.Date) : "date inconnue") +
                    " : des erreurs matérielles ont été détectées.",
                    "Windows a testé la mémoire de cette machine et y a trouvé des défauts. Une barrette " +
                    "défaillante provoque des plantages sans logique apparente, corrompt les fichiers " +
                    "enregistrés, et aucun nettoyage logiciel n'y changera rien.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Test mémoire", "erreurs détectées", DataSource.EventLog, "aucune erreur")),
                    recommendations: RuleContext.Rec(Rec.CheckMemory)));
            }

            if (test.Value == MemoryTestOutcome.Interrupted)
            {
                return RuleResult.Of(c.Finding(Severity.Info,
                    "Le dernier test mémoire n'est pas allé au bout",
                    "Test du " + (run != null ? Fmt.Date(run.Date) : "date inconnue") +
                    " : interrompu ou incomplet.",
                    "Quelqu'un a lancé le test mémoire sans le laisser finir. Le résultat ne dit donc rien : " +
                    "ni que la mémoire est saine, ni qu'elle est en cause.",
                    recommendations: RuleContext.Rec(Rec.CheckMemory)));
            }

            return RuleResult.Clean;
        }

        /// <summary>
        /// Une machine instable dont personne n'a jamais testé la mémoire.
        /// </summary>
        /// <remarks>
        /// C'est le seul constat de ce fichier qui relie deux domaines : l'instabilité vient des
        /// journaux, l'absence de test du diagnostic mémoire. Le logiciel recommandait déjà de
        /// tester la mémoire après un écran bleu, sans jamais regarder si ça avait été fait :
        /// il pouvait donc le redemander à chaque passage, y compris le lendemain d'un test.
        /// <para>
        /// <b>Les arrêts inattendus n'en font pas partie</b>, et la machine de développement l'a
        /// montré : dix-huit y étaient enregistrés, aucun écran bleu, aucune erreur matérielle.
        /// Une coupure de courant, un bouton maintenu ou une alimentation fatiguée éteignent la
        /// machine sans que la mémoire y soit pour quoi que ce soit : c'est ce que dit déjà la
        /// règle des arrêts inattendus. Une barrette défaillante, elle, produit des écrans bleus
        /// et des erreurs corrigées : ce sont les deux seuls signaux retenus ici.
        /// </para>
        /// </remarks>
        private static RuleResult UntestedMemory(RuleContext c)
        {
            var errors = c.Hardware.Errors;
            if (!errors.MemoryTest.IsReliable) return RuleResult.NotEvaluated(
                errors.MemoryTest.Reason ?? "Le résultat du test mémoire n'a pas pu être lu.");

            if (errors.MemoryTest.Value != MemoryTestOutcome.NeverRun) return RuleResult.Clean;

            var events = c.System.Events;
            if (!events.WindowDays.IsReliable)
                return RuleResult.NotEvaluated("Les journaux d'événements n'ont pas pu être lus.");

            var bsods = events.Bsods.Count;
            var corrected = errors.CorrectedCount.Or(0);
            var uncorrected = errors.UncorrectedCount.Or(0);

            if (bsods == 0 && corrected == 0 && uncorrected == 0) return RuleResult.Clean;

            var symptoms = new List<string>();
            if (bsods > 0) symptoms.Add(bsods + " écran(s) bleu(s)");
            if (uncorrected > 0) symptoms.Add(uncorrected + " erreur(s) matérielle(s) irrécupérable(s)");
            if (corrected > 0) symptoms.Add(corrected + " erreur(s) matérielle(s) corrigée(s)");

            return RuleResult.Of(c.Finding(Severity.Info,
                "La mémoire n'a jamais été testée sur cette machine instable",
                string.Join(", ", symptoms.ToArray()) +
                ", et aucun passage du diagnostic mémoire de Windows n'est enregistré.",
                "Cette machine montre des signes d'instabilité et personne n'a encore écarté la mémoire. " +
                "Le test intégré à Windows demande un redémarrage et une vingtaine de minutes : c'est le " +
                "moyen le plus simple de savoir s'il faut chercher ailleurs.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Test mémoire", "jamais exécuté", DataSource.EventLog)),
                recommendations: RuleContext.Rec(Rec.CheckMemory)));
        }

        /// <summary>
        /// Les rapports de plantage réellement présents sur le disque.
        /// </summary>
        /// <remarks>
        /// « Analyser les rapports enregistrés » était recommandé sans que rien ne vérifie qu'il
        /// y en ait. Ce constat donne les chemins : le technicien sait avant de commencer s'il a
        /// de la matière, et laquelle.
        /// </remarks>
        private static RuleResult CrashReports(RuleContext c)
        {
            var errors = c.Hardware.Errors;
            if (errors.CrashDumps.Count == 0) return RuleResult.Clean;

            var newest = errors.CrashDumps[0];
            var full = 0;
            foreach (var dump in errors.CrashDumps) if (dump.IsFullDump) full++;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des rapports de plantage sont disponibles sur le disque",
                errors.CrashDumps.Count + " fichier(s)" +
                (full > 0 ? " dont " + full + " vidage(s) complet(s)" : string.Empty) +
                ", le plus récent du " + Fmt.Date(newest.Date) + " (" + Fmt.Bytes(newest.SizeBytes) + ").",
                "Windows a enregistré ce qu'il avait en mémoire au moment des plantages. Ces fichiers " +
                "nomment le composant fautif : les analyser évite de remplacer du matériel au hasard.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Rapports", errors.CrashDumps.Count.ToString(), DataSource.FileSystem),
                    Evidence.Of("Plus récent", newest.Path, DataSource.FileSystem)),
                recommendations: RuleContext.Rec(Rec.AnalyzeCrashDumps)));
        }

        /// <summary>
        /// Une machine qui plante et qui n'enregistre rien.
        /// </summary>
        /// <remarks>
        /// Le constat ne se déclenche pas sur une machine stable : le réglage n'y coûte rien.
        /// Sur une machine qui plante, en revanche, il condamne le prochain écran bleu à ne
        /// laisser aucune trace exploitable, et donc la prochaine visite à repartir de zéro.
        /// <para>
        /// <b>Ici, l'arrêt inattendu compte</b>, contrairement à la règle du test mémoire, et
        /// pour une raison précise : quand l'enregistrement est coupé, l'écran bleu ne laisse
        /// justement <i>plus</i> d'événement à compter. L'arrêt brutal est alors la seule trace
        /// qui subsiste, et l'ignorer rendrait ce constat structurellement incapable de se
        /// déclencher sur les machines qu'il vise.
        /// </para>
        /// </remarks>
        private static RuleResult CrashReportsDisabled(RuleContext c)
        {
            var errors = c.Hardware.Errors;
            if (!errors.CrashDumpsEnabled.IsReliable)
                return RuleResult.NotEvaluated(
                    errors.CrashDumpsEnabled.Reason ?? "Le réglage des rapports de plantage n'a pas pu être lu.");

            if (errors.CrashDumpsEnabled.Value) return RuleResult.Clean;

            var events = c.System.Events;
            var bsods = events.Bsods.Count;
            var shutdowns = events.UnexpectedShutdowns.Or(0);
            if (bsods == 0 && shutdowns < c.T.UnexpectedShutdownsWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Les plantages de cette machine ne laissent aucune trace",
                "L'enregistrement des rapports de plantage est désactivé, alors que " +
                (bsods > 0 ? bsods + " écran(s) bleu(s)" : shutdowns + " arrêt(s) inattendu(s)") +
                " figurent dans les journaux.",
                "Quand cette machine s'arrête brutalement, Windows n'écrit rien. Le prochain plantage ne " +
                "laissera donc pas de quoi désigner le composant fautif, et la recherche recommencera à zéro.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Rapports de plantage", "désactivés", DataSource.Registry, "activés")),
                recommendations: RuleContext.Rec(Rec.EnableCrashDumps)));
        }

        /// <summary>Le groupe le plus récent d'un genre donné, ou <c>null</c> s'il n'y en a aucun.</summary>
        private static HardwareErrorGroup? Worst(HardwareErrorsInfo errors, HardwareErrorKind kind)
        {
            HardwareErrorGroup? found = null;
            foreach (var group in errors.Errors)
            {
                if (group.Kind != kind) continue;
                if (found == null || group.LastSeen > found.LastSeen) found = group;
            }
            return found;
        }
    }
}
