using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>Règles Windows : intégrité, mises à jour, services, événements, périphériques.</summary>
    internal static class WindowsRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("WIN-001", "Intégrité des fichiers système", Cat, SystemFileIntegrity, "windows.integrity");
            yield return new Rule("WIN-002", "Magasin de composants", Cat, ComponentStore, "windows.integrity");
            yield return new Rule("WIN-003", "Redémarrage en attente", Cat, RebootPending);
            yield return new Rule("WIN-004", "Activation de Windows", Cat, Activation);
            yield return new Rule("WIN-005", "Temps de fonctionnement", Cat, Uptime);
            yield return new Rule("WIN-006", "Version de Windows suivie", Cat, SupportedVersion, "windows.support");
            yield return new Rule("WIN-007", "Restauration système", Cat, SystemRestore);
            yield return new Rule("WIN-008", "Démarrage rapide", Cat, FastStartup);

            yield return new Rule("UPD-001", "Retard de mises à jour", Cat, UpdateDelay);
            yield return new Rule("UPD-002", "Service Windows Update", Cat, UpdateService);

            yield return new Rule("SVC-001", "Services essentiels", Cat, EssentialServices, "services.essential");

            yield return new Rule("EVT-001", "Écrans bleus", Cat, BlueScreens, "events.errors");
            yield return new Rule("EVT-002", "Arrêts inattendus", Cat, UnexpectedShutdowns, "events.errors");
            yield return new Rule("EVT-003", "Erreurs disque dans les journaux", Cat, DiskEvents, "events.errors");
            yield return new Rule("EVT-004", "Erreurs critiques", Cat, CriticalEvents, "events.errors");
            yield return new Rule("EVT-005", "Erreurs récurrentes", Cat, RecurringEvents, "events.errors");

            yield return new Rule("DRV-001", "Périphériques en erreur", Cat, DeviceErrors, "devices.problems");
            yield return new Rule("DRV-002", "Périphériques sans pilote", Cat, MissingDrivers, "devices.problems");
            yield return new Rule("DRV-003", "Périphériques désactivés", Cat, DisabledDevices, "devices.problems");
        }

        private static RuleResult SystemFileIntegrity(RuleContext c)
        {
            var sfc = c.System.SystemFiles.Sfc;
            if (!sfc.IsReliable)
                return RuleResult.NotEvaluated(sfc.Reason ?? "L'état d'intégrité des fichiers système n'a pas pu être lu.");

            if (sfc.Value == SfcStatus.Clean || sfc.Value == SfcStatus.NotRun) return RuleResult.Clean;

            var severity = sfc.Value == SfcStatus.RepairFailed ? Severity.Problem
                : sfc.Value == SfcStatus.CorruptionFound ? Severity.Problem
                : Severity.Info;

            var title = sfc.Value == SfcStatus.RepairedSuccessfully
                ? "Des fichiers système ont été réparés par le passé"
                : "Des fichiers système sont endommagés";

            var plain = sfc.Value == SfcStatus.RepairedSuccessfully
                ? "Windows a déjà réparé des fichiers endommagés lors d'un contrôle précédent. Aucune action n'est " +
                  "requise, mais une corruption répétée mérite qu'on en cherche la cause."
                : "Des fichiers de Windows sont endommagés. Cela provoque des dysfonctionnements imprévisibles et " +
                  "empêche souvent les mises à jour de s'installer correctement.";

            return RuleResult.Of(c.Finding(severity,
                title,
                "Dernier contrôle d'intégrité : " + Describe(sfc.Value) + "." +
                (c.System.SystemFiles.Evidence.HasValue
                    ? " Trace du journal CBS : " + c.System.SystemFiles.Evidence.Value
                    : string.Empty),
                plain,
                evidence: RuleContext.Ev(
                    Evidence.Of("Verdict SFC", Describe(sfc.Value), sfc.Source)),
                recommendations: severity == Severity.Problem
                    ? RuleContext.Rec(Rec.RepairSystemFiles)
                    : Array.Empty<string>()));
        }

        private static RuleResult ComponentStore(RuleContext c)
        {
            var store = c.System.SystemFiles.ComponentStore;
            if (!store.HasValue)
                return RuleResult.NotEvaluated(store.Reason ?? "L'état du magasin de composants n'a pas pu être contrôlé.");

            if (store.Value == ComponentStoreState.Healthy) return RuleResult.Clean;

            var severity = store.Value == ComponentStoreState.NonRepairable ? Severity.Problem : Severity.Problem;

            return RuleResult.Of(c.Finding(severity,
                "Le magasin de composants de Windows est endommagé",
                "État du magasin de composants : " +
                (store.Value == ComponentStoreState.NonRepairable
                    ? "endommagé, sources de réparation introuvables"
                    : "endommagé mais réparable") + ".",
                "Les fichiers de référence qui permettent à Windows de se réparer lui-même sont endommagés. " +
                "Tant qu'ils ne sont pas restaurés, les autres réparations échoueront et les mises à jour " +
                "resteront bloquées. C'est par là qu'il faut commencer.",
                confidence: store.IsReliable ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Magasin de composants", store.Value.ToString(), store.Source)),
                recommendations: RuleContext.Rec(Rec.RepairComponentStore)));
        }

        private static RuleResult RebootPending(RuleContext c)
        {
            var pending = c.System.Install.RebootPending;
            if (!pending.IsReliable)
                return RuleResult.NotEvaluated("L'existence d'un redémarrage en attente n'a pas pu être déterminée.");

            if (!pending.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Un redémarrage est en attente",
                "Windows signale une opération en attente de redémarrage (composants, mises à jour ou renommage de fichiers).",
                "Une installation ou une mise à jour attend un redémarrage pour se terminer. Tant qu'il n'a pas eu lieu, " +
                "d'autres mises à jour resteront bloquées et certains contrôles de ce diagnostic peuvent être faussés.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Redémarrage en attente", "Oui", pending.Source)),
                recommendations: RuleContext.Rec(Rec.RestartMachine)));
        }

        private static RuleResult Activation(RuleContext c)
        {
            var activation = c.System.Install.ActivationStatus;
            if (!activation.IsReliable)
                return RuleResult.NotEvaluated(activation.Reason ?? "L'état d'activation n'a pas pu être lu.");

            if (activation.Value == "Activé") return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Windows n'est pas activé",
                "État de la licence : " + activation.Value + ".",
                "Windows n'est pas activé sur cette machine. Certaines options de personnalisation sont désactivées " +
                "et des rappels s'affichent régulièrement. La sécurité et les mises à jour ne sont pas affectées.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Licence", activation.Value, activation.Source)),
                recommendations: RuleContext.Rec(Rec.ActivateWindows)));
        }

        private static RuleResult Uptime(RuleContext c)
        {
            var uptime = c.System.Install.Uptime;
            if (!uptime.IsReliable)
                return RuleResult.NotEvaluated("Le temps de fonctionnement n'a pas pu être déterminé.");

            if (uptime.Value.TotalDays < c.T.UptimeDaysWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "La machine n'a pas redémarré depuis longtemps",
                "Allumée depuis " + Fmt.Duration(uptime.Value) + " (seuil " + c.T.UptimeDaysWarning + " jours).",
                "Cette machine fonctionne sans interruption depuis longtemps. Un redémarrage régulier permet " +
                "d'appliquer les mises à jour et de libérer la mémoire accumulée.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Temps de fonctionnement", Fmt.Duration(uptime.Value), uptime.Source,
                        c.T.UptimeDaysWarning + " jours")),
                recommendations: RuleContext.Rec(Rec.RestartMachine)));
        }

        /// <summary>
        /// La version installée reçoit-elle encore des correctifs.
        /// </summary>
        /// <remarks>
        /// <b>Par la date, et non par la famille.</b> La règle a longtemps listé Windows 7, 8 et
        /// 8.1, ce qui revenait à décider une fois pour toutes quelles versions étaient périmées.
        /// Windows 10 a montré la limite de ce raisonnement : le 14 octobre 2025, la règle
        /// déclarait encore « propre » sur la moitié du parc. Le calendrier embarqué répond
        /// maintenant à la question, branche par branche, édition par édition, et la version
        /// qu'il ne connaît pas devient une non-évaluation avec son motif, jamais un blanc-seing.
        /// </remarks>
        private static RuleResult SupportedVersion(RuleContext c)
        {
            var support = WindowsLifecycle.For(c.Windows, c.Snapshot.Metadata.CreatedAt);

            if (support.State == WindowsSupportState.Unknown)
                return RuleResult.NotEvaluated(
                    support.Reason ?? "La date de fin de support de cette version n'est pas connue.");

            if (support.State != WindowsSupportState.Ended) return RuleResult.Clean;

            var since = support.EndOfSupport.HasValue
                ? "depuis le " + Fmt.Date(support.EndOfSupport.Value)
                : "depuis plusieurs années";

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Cette version de Windows ne reçoit plus de correctifs",
                support.VersionLabel + " (build " + c.Windows.BuildString + ") : support terminé " + since +
                ", plus aucune mise à jour de sécurité n'est publiée." +
                (support.Reason == null ? string.Empty : " " + support.Reason),
                "La version de Windows installée n'est plus suivie par Microsoft. Les failles de sécurité " +
                "découvertes depuis la fin du support ne seront jamais corrigées sur cette machine. " +
                "C'est le point le plus important à traiter à moyen terme.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Version", support.VersionLabel, DataSource.NativeApi),
                    Evidence.Of("Fin de support",
                        support.EndOfSupport.HasValue ? Fmt.Date(support.EndOfSupport.Value) : "antérieure à 2020",
                        DataSource.Inferred)),
                recommendations: RuleContext.Rec(Rec.MigrateSupportedWindows)));
        }

        /// <summary>
        /// La protection du système est-elle activée.
        /// </summary>
        /// <remarks>
        /// La valeur du relevé d'installation est une déduction : elle vient du registre des
        /// fonctions disponibles et vaut « probablement active » faute de mieux. Le module du
        /// filet de sécurité, lui, lit les deux valeurs qui la commandent réellement. Quand il a
        /// pu les lire, c'est sa mesure qui décide : une déduction ne doit pas l'emporter sur une
        /// mesure lorsqu'on dispose des deux.
        /// </remarks>
        private static RuleResult SystemRestore(RuleContext c)
        {
            var measured = c.System.SafetyNet.Restore.Enabled;
            var enabled = measured.IsReliable ? measured : c.System.Install.SystemRestoreEnabled;
            if (!enabled.HasValue)
                return RuleResult.NotEvaluated("L'état de la restauration système n'a pas pu être déterminé.");

            if (enabled.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "La restauration système est désactivée",
                "Aucun point de restauration ne peut être créé sur cette machine.",
                "Le filet de sécurité de Windows est désactivé : en cas d'installation qui se passe mal, " +
                "aucun retour en arrière ne sera possible. À réactiver avant toute intervention modifiant le système.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Restauration système", "Désactivée", enabled.Source)),
                recommendations: RuleContext.Rec(Rec.EnableSystemRestore)));
        }

        private static RuleResult FastStartup(RuleContext c)
        {
            var fastStartup = c.System.Install.FastStartupEnabled;
            if (!fastStartup.IsReliable)
                return RuleResult.NotEvaluated("Le démarrage rapide n'existe pas sur cette version de Windows.");

            if (!fastStartup.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Le démarrage rapide est activé",
                "HiberbootEnabled est actif : un arrêt suivi d'un allumage ne réinitialise pas le noyau.",
                "Avec cette option, « éteindre puis rallumer » ne redémarre pas réellement Windows. " +
                "C'est pourquoi certains problèmes persistent alors que le client affirme avoir redémarré, " +
                "seul le menu « Redémarrer » relance vraiment la machine.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Démarrage rapide", "Activé", fastStartup.Source)),
                recommendations: RuleContext.Rec(Rec.DisableFastStartup)));
        }

        private static RuleResult UpdateDelay(RuleContext c)
        {
            var days = c.System.Updates.DaysSinceLastUpdate;
            if (!days.IsReliable)
                return RuleResult.NotEvaluated(days.Reason ?? "Le retard de mise à jour n'a pas pu être calculé.");

            if (days.Value < c.T.UpdateDelayDaysWarning) return RuleResult.Clean;

            var severity = days.Value >= c.T.UpdateDelayDaysProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem ? c.T.UpdateDelayDaysProblem : c.T.UpdateDelayDaysWarning;

            return RuleResult.Of(c.Finding(severity,
                "Windows a pris du retard sur les mises à jour",
                "Dernier correctif installé il y a " + days.Value + " jours (seuil " + threshold + " jours).",
                "Cette machine n'a pas reçu de correctif depuis longtemps. Un tel retard vient rarement d'un choix : " +
                "il a presque toujours une cause mécanique : service arrêté, corruption système ou espace disque insuffisant.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Dernier correctif", days.Value + " jours", days.Source, threshold + " jours")),
                recommendations: RuleContext.Rec(Rec.RunWindowsUpdate)));
        }

        private static RuleResult UpdateService(RuleContext c)
        {
            var state = c.System.Updates.ServiceState;
            if (!state.IsReliable)
                return RuleResult.NotEvaluated(state.Reason ?? "L'état du service Windows Update n'a pas pu être lu.");

            if (state.Value == "Running" || state.Value == "StartPending") return RuleResult.Clean;

            // Un service à l'arrêt est le comportement normal de Windows Update entre deux
            // recherches : ce n'est un problème que combiné à un retard, traité par UPD-001.
            if (!c.System.Updates.DaysSinceLastUpdate.IsReliable ||
                c.System.Updates.DaysSinceLastUpdate.Value < c.T.UpdateDelayDaysWarning)
            {
                return RuleResult.Clean;
            }

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Le service Windows Update est arrêté alors que la machine a du retard",
                "Service wuauserv à l'état « " + state.Value + " » et " +
                c.System.Updates.DaysSinceLastUpdate.Value + " jours sans correctif.",
                "Le service chargé des mises à jour ne fonctionne pas, et la machine a effectivement pris du retard. " +
                "Aucun correctif ne s'installera tant que ce service restera arrêté.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Service wuauserv", state.Value, state.Source, "Running")),
                recommendations: RuleContext.Rec(Rec.StartUpdateService)));
        }

        private static RuleResult EssentialServices(RuleContext c)
        {
            if (c.System.Services.Count == 0)
                return RuleResult.NotEvaluated("La liste des services n'a pas pu être obtenue.");

            var findings = new List<Finding>();
            foreach (var service in c.System.Services)
            {
                if (service.Deviation == null) continue;

                findings.Add(c.Finding(Severity.Problem,
                    "Un service essentiel de Windows ne fonctionne pas",
                    service.DisplayName + " (" + service.Name + ") : état « " + service.State +
                    " », démarrage « " + service.StartMode + " ». " + service.Deviation,
                    "Un composant nécessaire au fonctionnement normal de Windows est arrêté ou désactivé. " +
                    "Cela se traduit par une fonction qui ne marche plus, souvent sans message d'erreur explicite.",
                    subject: service.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("État", service.State, DataSource.NativeApi, "Running"),
                        Evidence.Of("Démarrage", service.StartMode, DataSource.Registry)),
                    recommendations: RuleContext.Rec(Rec.StartEssentialService)));
            }

            return RuleResult.Of(findings);
        }

        private static RuleResult BlueScreens(RuleContext c)
        {
            var events = c.System.Events;
            if (!events.WindowDays.IsReliable)
                return RuleResult.NotEvaluated(events.WindowDays.Reason ?? "Les journaux d'événements n'ont pas pu être lus.");

            if (events.Bsods.Count == 0) return RuleResult.Clean;

            var severity = events.Bsods.Count >= 3 ? Severity.Problem : Severity.Warning;
            var codes = new List<string>();
            foreach (var bsod in events.Bsods)
                if (bsod.BugCheckCode != null && !codes.Contains(bsod.BugCheckCode)) codes.Add(bsod.BugCheckCode);

            return RuleResult.Of(c.Finding(severity,
                "La machine a subi des écrans bleus",
                events.Bsods.Count + " arrêt(s) sur écran bleu enregistré(s)" +
                (codes.Count > 0 ? ", code(s) : " + string.Join(", ", codes.ToArray()) : string.Empty) +
                ". Dernier le " + Fmt.Date(events.Bsods[0].Date) + ".",
                "Windows s'est arrêté brutalement à " + events.Bsods.Count + " reprise(s) sur un écran bleu. " +
                "Les rapports enregistrés désignent le composant fautif : leur analyse évite de remplacer " +
                "du matériel au hasard.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Écrans bleus", events.Bsods.Count.ToString(), DataSource.EventLog)),
                recommendations: RuleContext.Rec(Rec.AnalyzeCrashDumps, Rec.CheckMemory)));
        }

        private static RuleResult UnexpectedShutdowns(RuleContext c)
        {
            var shutdowns = c.System.Events.UnexpectedShutdowns;
            if (!shutdowns.IsReliable)
                return RuleResult.NotEvaluated(shutdowns.Reason ?? "Les arrêts inattendus n'ont pas pu être comptés.");

            if (shutdowns.Value < c.T.UnexpectedShutdownsWarning) return RuleResult.Clean;

            var window = c.System.Events.WindowDays.Or(14);
            return RuleResult.Of(c.Finding(Severity.Warning,
                "La machine s'est arrêtée brutalement à plusieurs reprises",
                shutdowns.Value + " arrêt(s) inattendu(s) sur " + window + " jours (seuil " +
                c.T.UnexpectedShutdownsWarning + ").",
                "La machine s'est éteinte sans passer par l'arrêt de Windows. Répété, cela vient le plus souvent " +
                "d'une alimentation défaillante, d'une surchauffe ou d'une prise mal contactée, et cela finit " +
                "par endommager le système de fichiers.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Arrêts inattendus", shutdowns.Value.ToString(), shutdowns.Source,
                        c.T.UnexpectedShutdownsWarning.ToString())),
                recommendations: RuleContext.Rec(Rec.CheckPowerSupply)));
        }

        private static RuleResult DiskEvents(RuleContext c)
        {
            var events = c.System.Events;
            if (!events.WindowDays.IsReliable)
                return RuleResult.NotEvaluated("Les journaux d'événements n'ont pas pu être lus.");

            if (events.DiskErrors.Count < c.T.DiskEventsProblem) return RuleResult.Clean;

            var total = 0;
            foreach (var summary in events.DiskErrors) total += summary.Count;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Le système enregistre des erreurs disque",
                events.DiskErrors.Count + " type(s) d'erreur disque, " + total + " occurrence(s) sur " +
                events.WindowDays.Value + " jours. Source principale : " + events.DiskErrors[0].Source +
                " (événement " + events.DiskErrors[0].EventId + ").",
                "Windows a rencontré des erreurs en lisant ou en écrivant sur un disque. Associées à des " +
                "indicateurs SMART dégradés, elles confirment une défaillance matérielle ; seules, elles " +
                "justifient une vérification du système de fichiers.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Occurrences", total.ToString(), DataSource.EventLog),
                    Evidence.Of("Source", events.DiskErrors[0].Source, DataSource.EventLog)),
                recommendations: RuleContext.Rec(Rec.CheckDisk, Rec.BackupNow)));
        }

        private static RuleResult CriticalEvents(RuleContext c)
        {
            var events = c.System.Events;
            if (!events.WindowDays.IsReliable)
                return RuleResult.NotEvaluated("Les journaux d'événements n'ont pas pu être lus.");

            var total = 0;
            foreach (var summary in events.CriticalErrors) total += summary.Count;
            if (total < c.T.CriticalEventsWarning) return RuleResult.Clean;

            var severity = total >= c.T.CriticalEventsProblem ? Severity.Warning : Severity.Info;
            var threshold = severity == Severity.Warning ? c.T.CriticalEventsProblem : c.T.CriticalEventsWarning;

            return RuleResult.Of(c.Finding(severity,
                "Des erreurs critiques figurent dans les journaux",
                total + " erreur(s) critique(s) réparties sur " + events.CriticalErrors.Count +
                " source(s), sur " + events.WindowDays.Value + " jours (seuil " + threshold + ").",
                "Windows a enregistré des erreurs de niveau critique. Elles n'empêchent pas toujours la machine " +
                "de fonctionner, mais elles désignent des composants qui posent problème.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Erreurs critiques", total.ToString(), DataSource.EventLog, threshold.ToString())),
                recommendations: RuleContext.Rec(Rec.InvestigateEventErrors)));
        }

        private static RuleResult RecurringEvents(RuleContext c)
        {
            var events = c.System.Events;
            if (!events.WindowDays.IsReliable)
                return RuleResult.NotEvaluated("Les journaux d'événements n'ont pas pu être lus.");

            var findings = new List<Finding>();
            foreach (var summary in events.RecurringErrors)
            {
                if (summary.Count < c.T.RecurringEventOccurrencesWarning) continue;

                var subject = summary.Source + "/" + summary.EventId;
                findings.Add(c.Finding(Severity.Info,
                    "Une erreur se répète dans les journaux",
                    summary.Source + " : événement " + summary.EventId + " : " + summary.Count +
                    " occurrences entre le " + Fmt.Date(summary.FirstSeen) + " et le " + Fmt.Date(summary.LastSeen) +
                    " (seuil " + c.T.RecurringEventOccurrencesWarning + ").",
                    "La même erreur revient très régulièrement. Elle a une cause précise, qui se traite une fois " +
                    "pour toutes plutôt que de continuer à remplir les journaux.",
                    subject: subject,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Occurrences", summary.Count.ToString(), DataSource.EventLog,
                            c.T.RecurringEventOccurrencesWarning.ToString()),
                        Evidence.Of("Source", summary.Source, DataSource.EventLog)),
                    recommendations: RuleContext.Rec(Rec.InvestigateEventErrors)));
            }

            return RuleResult.Of(findings);
        }

        private static RuleResult DeviceErrors(RuleContext c)
        {
            if (c.System.Devices.Count == 0)
                return RuleResult.NotEvaluated("Les périphériques n'ont pas pu être énumérés.");

            var findings = new List<Finding>();
            foreach (var device in c.System.Devices)
            {
                // Le pilote absent et le périphérique désactivé ont leurs propres règles.
                if (device.ProblemCode == 0 || device.ProblemCode == 22 || device.ProblemCode == 28) continue;

                findings.Add(c.Finding(Severity.Warning,
                    "Un périphérique signale une erreur",
                    device.Name + " (" + (device.DeviceClass ?? "classe inconnue") + ") : code " +
                    device.ProblemCode + " : " + device.ProblemLabel,
                    "Un matériel de cette machine ne fonctionne pas correctement : " +
                    (device.ProblemLabel ?? "Windows signale une erreur sur ce périphérique."),
                    subject: device.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Code d'erreur", device.ProblemCode.ToString(), DataSource.Wmi, "0"),
                        Evidence.Of("Périphérique", device.Name, DataSource.Wmi)),
                    recommendations: RuleContext.Rec(Rec.InspectDeviceError)));
            }

            return RuleResult.Of(findings);
        }

        private static RuleResult MissingDrivers(RuleContext c)
        {
            if (c.System.Devices.Count == 0)
                return RuleResult.NotEvaluated("Les périphériques n'ont pas pu être énumérés.");

            var missing = new List<DeviceInfo>();
            foreach (var device in c.System.Devices)
                if (device.ProblemCode == 28) missing.Add(device);

            if (missing.Count == 0) return RuleResult.Clean;

            var names = new List<string>();
            foreach (var device in missing) names.Add(device.Name);

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Des périphériques n'ont pas de pilote installé",
                missing.Count + " périphérique(s) sans pilote : " + string.Join(", ", names.ToArray()) + ".",
                "Windows ne sait pas se servir de " + missing.Count + " matériel(s) de cette machine, faute de pilote. " +
                "Ces équipements sont purement et simplement inutilisables tant que le pilote n'est pas installé.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Périphériques sans pilote", missing.Count.ToString(), DataSource.Wmi, "0")),
                recommendations: RuleContext.Rec(Rec.InstallMissingDrivers)));
        }

        private static RuleResult DisabledDevices(RuleContext c)
        {
            if (c.System.Devices.Count == 0)
                return RuleResult.NotEvaluated("Les périphériques n'ont pas pu être énumérés.");

            var disabled = new List<string>();
            foreach (var device in c.System.Devices)
                if (device.IsDisabled) disabled.Add(device.Name);

            if (disabled.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des périphériques ont été désactivés",
                disabled.Count + " périphérique(s) désactivé(s) : " + string.Join(", ", disabled.ToArray()) + ".",
                "Certains matériels ont été volontairement désactivés. C'est parfois intentionnel, parfois " +
                "l'origine oubliée d'un « ça ne marche plus depuis longtemps ».",
                evidence: RuleContext.Ev(
                    Evidence.Of("Périphériques désactivés", disabled.Count.ToString(), DataSource.Wmi))));
        }

        private static string Describe(SfcStatus status) => status switch
        {
            SfcStatus.Clean => "aucun fichier endommagé",
            SfcStatus.NotRun => "jamais exécuté",
            SfcStatus.RepairedSuccessfully => "des fichiers ont été réparés",
            SfcStatus.CorruptionFound => "corruption détectée",
            SfcStatus.RepairFailed => "corruption non réparable",
            _ => "état inconnu",
        };
    }
}
