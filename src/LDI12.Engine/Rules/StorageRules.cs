using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles du stockage : la dimension la plus lourde du barème, parce que c'est la première
    /// cause d'intervention et la seule où l'inaction fait perdre des données.
    /// </summary>
    internal static class StorageRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Storage;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("STO-001", "Espace libre du volume système", Cat, SystemVolumeSpace, "storage.space");
            yield return new Rule("STO-002", "Espace libre des volumes de données", Cat, DataVolumeSpace, "storage.space");
            yield return new Rule("STO-003", "État SMART global", Cat, SmartStatus, "storage.smart");
            yield return new Rule("STO-004", "Secteurs réalloués", Cat, ReallocatedSectors, "storage.smart");
            yield return new Rule("STO-005", "Secteurs instables en attente", Cat, PendingSectors, "storage.smart");
            yield return new Rule("STO-006", "Erreurs non corrigeables", Cat, UncorrectableErrors, "storage.smart");
            yield return new Rule("STO-007", "Température des disques", Cat, DiskTemperature, "storage.temperature");
            yield return new Rule("STO-008", "Usure des disques à mémoire flash", Cat, SsdWear, "storage.smart");
            yield return new Rule("STO-009", "Disque système mécanique", Cat, MechanicalSystemDisk);
            yield return new Rule("STO-010", "Couverture SMART", Cat, SmartCoverage);
            yield return new Rule("STO-011", "Ancienneté des disques", Cat, DiskAge);
            yield return new Rule("STO-012", "Liaison bridée par le port", Cat, ThrottledLink);
        }

        private static RuleResult SystemVolumeSpace(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var volume in c.Storage.Volumes)
            {
                if (!volume.IsSystemVolume.Or(false)) continue;
                if (!volume.UsagePercent.IsReliable || !volume.TotalBytes.IsReliable) continue;

                evaluated = true;
                var free = 100d - volume.UsagePercent.Value;
                var letter = volume.DriveLetter.Or("C:");
                if (free > c.T.SystemVolumeFreePercentWarning) continue;

                var severity = free <= c.T.SystemVolumeFreePercentCritical ? Severity.Critical
                    : free <= c.T.SystemVolumeFreePercentProblem ? Severity.Problem
                    : Severity.Warning;

                var threshold = severity == Severity.Critical ? c.T.SystemVolumeFreePercentCritical
                    : severity == Severity.Problem ? c.T.SystemVolumeFreePercentProblem
                    : c.T.SystemVolumeFreePercentWarning;

                findings.Add(c.Finding(severity,
                    "Le disque système est presque plein",
                    "Volume " + letter + " : " + Fmt.Bytes(volume.FreeBytes.Or(0)) + " libres sur " +
                    Fmt.Bytes(volume.TotalBytes.Value) + " (" + Fmt.Percent(free) + " libres, seuil " +
                    Fmt.Percent(threshold) + ").",
                    "Il ne reste presque plus de place sur le disque où Windows est installé. " +
                    "C'est l'une des causes les plus fréquentes de lenteur, et cela empêche les mises à jour de s'installer.",
                    subject: letter,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Espace libre", Fmt.Bytes(volume.FreeBytes.Or(0)), volume.FreeBytes.Source, Fmt.Percent(threshold)),
                        Evidence.Of("Capacité totale", Fmt.Bytes(volume.TotalBytes.Value), volume.TotalBytes.Source)),
                    recommendations: RuleContext.Rec(Rec.FreeDiskSpace)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun volume système exploitable n'a été relevé.");
        }

        private static RuleResult DataVolumeSpace(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var volume in c.Storage.Volumes)
            {
                if (volume.IsSystemVolume.Or(false)) continue;
                if (!volume.UsagePercent.IsReliable || !volume.TotalBytes.IsReliable) continue;

                evaluated = true;
                var free = 100d - volume.UsagePercent.Value;
                if (free > c.T.DataVolumeFreePercentWarning) continue;

                var letter = volume.DriveLetter.Or("?");
                findings.Add(c.Finding(Severity.Warning,
                    "Un volume de données est presque plein",
                    "Volume " + letter + " : " + Fmt.Bytes(volume.FreeBytes.Or(0)) + " libres sur " +
                    Fmt.Bytes(volume.TotalBytes.Value) + " (" + Fmt.Percent(free) + " libres, seuil " +
                    Fmt.Percent(c.T.DataVolumeFreePercentWarning) + ").",
                    "Le disque « " + letter + " » arrive à saturation. Il faudra bientôt libérer de la place ou prévoir un stockage supplémentaire.",
                    subject: letter,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Espace libre", Fmt.Bytes(volume.FreeBytes.Or(0)), volume.FreeBytes.Source,
                            Fmt.Percent(c.T.DataVolumeFreePercentWarning))),
                    recommendations: RuleContext.Rec(Rec.FreeDiskSpace)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun volume de données exploitable.");
        }

        private static RuleResult SmartStatus(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var status = disk.Smart.OverallStatus;
                if (!status.IsReliable) continue;
                evaluated = true;

                if (status.Value == SmartOverallStatus.Failing)
                {
                    findings.Add(c.Finding(Severity.Critical,
                        "Un disque annonce sa défaillance",
                        Label(disk) + " : l'auto-diagnostic SMART signale le franchissement d'un seuil constructeur.",
                        "Le disque signale lui-même qu'il est en train de tomber en panne. " +
                        "Les données doivent être sauvegardées immédiatement et le disque remplacé.",
                        subject: Label(disk),
                        evidence: RuleContext.Ev(
                            Evidence.Of("État SMART", "Défaillance annoncée", status.Source),
                            Evidence.Of("Modèle", disk.Model.Or("inconnu"), disk.Model.Source)),
                        recommendations: RuleContext.Rec(Rec.BackupNow, Rec.ReplaceDisk)));
                }
                else if (status.Value == SmartOverallStatus.Warning)
                {
                    findings.Add(c.Finding(Severity.Warning,
                        "Un disque montre des signes de fatigue",
                        Label(disk) + " : des indicateurs SMART critiques sont non nuls sans avoir atteint leur seuil.",
                        "Le disque fonctionne encore normalement, mais présente les premiers signes d'usure. " +
                        "Il mérite d'être surveillé.",
                        subject: Label(disk),
                        evidence: RuleContext.Ev(Evidence.Of("État SMART", "Indicateurs dégradés", status.Source)),
                        recommendations: RuleContext.Rec(Rec.MonitorDisk, Rec.BackupNow)));
                }
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun disque n'a exposé son état SMART.");
        }

        private static RuleResult ReallocatedSectors(RuleContext c) => SmartCounter(c,
            disk => disk.Smart.ReallocatedSectors,
            c.T.ReallocatedSectorsWarning, c.T.ReallocatedSectorsProblem, c.T.ReallocatedSectorsCritical,
            "Secteurs réalloués",
            "Des secteurs défectueux ont été remplacés par des secteurs de réserve",
            "Le disque a dû mettre de côté des zones devenues illisibles et les remplacer par des zones de secours. " +
            "C'est un signe d'usure du support : au-delà d'un certain nombre, la panne devient probable.",
            "Les secteurs réalloués sont l'indicateur d'usure le plus lu par les techniciens.");

        private static RuleResult PendingSectors(RuleContext c) => SmartCounter(c,
            disk => disk.Smart.PendingSectors,
            c.T.PendingSectorsWarning, c.T.PendingSectorsWarning, c.T.PendingSectorsCritical,
            "Secteurs instables en attente",
            "Des secteurs ne se lisent plus de façon fiable",
            "Le disque rencontre des zones qu'il n'arrive plus à lire correctement et n'a pas encore décidé de les remplacer. " +
            "C'est le signal d'alerte le plus précoce d'un disque qui commence à lâcher.",
            "Les secteurs instables précèdent généralement les secteurs réalloués.");

        private static RuleResult UncorrectableErrors(RuleContext c) => SmartCounter(c,
            disk => disk.Smart.UncorrectableErrors,
            c.T.UncorrectableErrorsWarning, c.T.UncorrectableErrorsWarning * 5, c.T.UncorrectableErrorsWarning * 20,
            "Erreurs non corrigeables",
            "Des données n'ont pas pu être relues",
            "Le disque a rencontré des erreurs qu'il n'a pas su corriger : des fichiers peuvent déjà être endommagés.",
            "Une erreur non corrigeable signifie une perte de données déjà effective sur le secteur concerné.");

        /// <summary>
        /// Motif commun aux compteurs SMART : même structure de seuils, même formulation, seul le
        /// compteur change. Les écrire une fois évite que trois règles divergent avec le temps.
        /// </summary>
        private static RuleResult SmartCounter(
            RuleContext c,
            Func<PhysicalDiskInfo, Measured<long>> selector,
            long warning, long problem, long critical,
            string counterLabel, string title, string plain, string note)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var counter = selector(disk);
                if (!counter.IsReliable) continue;
                evaluated = true;

                var value = counter.Value;
                if (value < warning) continue;

                var severity = value >= critical ? Severity.Critical
                    : value >= problem ? Severity.Problem
                    : Severity.Warning;

                var recommendations = severity == Severity.Critical
                    ? RuleContext.Rec(Rec.BackupNow, Rec.ReplaceDisk)
                    : severity == Severity.Problem
                        ? RuleContext.Rec(Rec.BackupNow, Rec.PlanDiskReplacement, Rec.CheckDisk)
                        : RuleContext.Rec(Rec.MonitorDisk, Rec.BackupNow);

                findings.Add(c.Finding(severity,
                    title,
                    Label(disk) + " : " + Fmt.Number(value) + " : " + counterLabel.ToLowerInvariant() +
                    " (seuil d'alerte " + Fmt.Number(warning) + "). " + note,
                    plain,
                    subject: Label(disk),
                    evidence: RuleContext.Ev(
                        Evidence.Of(counterLabel, Fmt.Number(value), counter.Source, Fmt.Number(warning)),
                        Evidence.Of("Modèle", disk.Model.Or("inconnu"), disk.Model.Source)),
                    recommendations: recommendations));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Ce compteur SMART n'est exposé par aucun disque de cette machine.");
        }

        private static RuleResult DiskTemperature(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var temperature = disk.Smart.TemperatureCelsius;
                if (!temperature.IsReliable) continue;
                evaluated = true;

                if (temperature.Value < c.T.DiskTemperatureWarning) continue;

                var severity = temperature.Value >= c.T.DiskTemperatureCritical ? Severity.Problem
                    : temperature.Value >= c.T.DiskTemperatureProblem ? Severity.Warning
                    : Severity.Warning;

                findings.Add(c.Finding(severity,
                    "Un disque fonctionne à température élevée",
                    Label(disk) + " : " + Fmt.Celsius(temperature.Value) + " (seuil d'alerte " +
                    Fmt.Celsius(c.T.DiskTemperatureWarning) + ").",
                    "Ce disque chauffe plus que la normale. La chaleur raccourcit sensiblement la durée de vie d'un disque : " +
                    "un dépoussiérage ou une meilleure ventilation du boîtier prolongerait sa durée de service.",
                    subject: Label(disk),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Température", Fmt.Celsius(temperature.Value), temperature.Source,
                            Fmt.Celsius(c.T.DiskTemperatureWarning))),
                    recommendations: RuleContext.Rec(Rec.ImproveCooling)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun disque de cette machine n'expose de capteur de température.");
        }

        private static RuleResult SsdWear(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var wear = disk.Smart.WearPercent;
                if (!wear.HasValue) continue;
                evaluated = true;

                if (wear.Value < c.T.SsdWearWarning) continue;

                var severity = wear.Value >= c.T.SsdWearCritical ? Severity.Problem
                    : wear.Value >= c.T.SsdWearProblem ? Severity.Warning
                    : Severity.Warning;

                findings.Add(c.Finding(severity,
                    "Un disque à mémoire flash approche de sa fin de vie",
                    Label(disk) + " : durée de vie consommée à " + Fmt.Percent(wear.Value) +
                    " (seuil d'alerte " + Fmt.Percent(c.T.SsdWearWarning) + ").",
                    "Ce type de disque supporte un nombre limité d'écritures. Celui-ci a consommé l'essentiel de sa réserve : " +
                    "il fonctionne encore, mais son remplacement doit être planifié.",
                    subject: Label(disk),
                    // L'échelle exacte dépend du constructeur : la mesure est fiable, son interprétation l'est moins.
                    confidence: wear.IsReliable ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Durée de vie consommée", Fmt.Percent(wear.Value), wear.Source,
                            Fmt.Percent(c.T.SsdWearWarning))),
                    recommendations: RuleContext.Rec(Rec.PlanDiskReplacement, Rec.BackupNow)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun disque n'expose d'indicateur d'usure.");
        }

        private static RuleResult MechanicalSystemDisk(RuleContext c)
        {
            foreach (var disk in c.Storage.PhysicalDisks)
            {
                if (!disk.IsSystemDisk.Or(false)) continue;
                if (!disk.MediaType.IsReliable) return RuleResult.NotEvaluated(
                    "Le type de support du disque système n'a pas pu être déterminé.");

                if (disk.MediaType.Value != StorageMediaType.Hdd) return RuleResult.Clean;
                if (!c.IsModernWindows) return RuleResult.Clean;

                return RuleResult.Of(c.Finding(Severity.Warning,
                    "Windows est installé sur un disque mécanique",
                    Label(disk) + " : disque à plateaux portant le système sous " +
                    Core.Platform.WindowsProfile.FamilyLabel(c.Windows.Family) + ".",
                    "Windows est installé sur un disque à plateaux, beaucoup plus lent qu'un disque à mémoire flash. " +
                    "C'est très souvent la principale explication d'une machine qui « rame » malgré un matériel correct.",
                    subject: Label(disk),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Type de support", "Disque à plateaux", disk.MediaType.Source)),
                    recommendations: RuleContext.Rec(Rec.UpgradeToSsd)));
            }

            return RuleResult.NotEvaluated("Le disque système n'a pas été identifié.");
        }

        private static RuleResult SmartCoverage(RuleContext c)
        {
            if (c.Storage.PhysicalDisks.Count == 0)
                return RuleResult.NotEvaluated("Aucun disque physique n'a été énuméré.");

            var withoutSmart = new List<string>();
            var elevationNeeded = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var status = disk.Smart.OverallStatus;
                if (status.IsReliable) continue;
                if (status.Availability == Availability.RequiresElevation) elevationNeeded = true;
                withoutSmart.Add(Label(disk));
            }

            if (withoutSmart.Count == 0) return RuleResult.Clean;

            // Constat d'information, pas de reproche à la machine : il sert à dire au technicien
            // sur quoi le verdict « stockage » ne repose pas.
            return RuleResult.Of(c.Finding(Severity.Info,
                "L'état de santé n'a pas pu être lu sur tous les disques",
                withoutSmart.Count + " disque(s) sur " + c.Storage.PhysicalDisks.Count +
                " n'ont pas fourni leurs données SMART : " + string.Join(", ", withoutSmart.ToArray()) + "." +
                (elevationNeeded
                    // Formulation prudente, corrigée après vérification : une relance élevée sur
                    // la machine de développement n'a rien débloqué. Sans privilèges, la commande
                    // est refusée avant même qu'on sache si le support l'accepte : l'élévation
                    // lève l'obstacle constaté, elle ne garantit pas le résultat.
                    ? " Une relance en tant qu'administrateur permettrait d'essayer de les lire ; " +
                      "certains supports, notamment en USB, ne les exposent de toute façon pas."
                    : " Les disques externes et les contrôleurs RAID masquent souvent ces données."),
                // Le libellé client ne doit pas être une consigne adressée au technicien : le
                // client ne peut rien faire de « relancer en tant qu'administrateur », et une
                // instruction qui ne lui est pas destinée le laisse penser qu'on attend
                // quelque chose de lui.
                elevationNeeded
                    ? "Nous n'avons pas pu contrôler l'état de santé de tous les disques de cet ordinateur. " +
                      "Cette vérification demande des droits d'administrateur ; nous pourrons la refaire " +
                      "lors d'un prochain passage."
                    : "Certains disques ne communiquent pas leur état de santé : c'est courant pour les disques externes. " +
                      "Le diagnostic reste valable pour les autres.",
                confidence: ConfidenceLevel.High));
        }

        private static RuleResult DiskAge(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Storage.PhysicalDisks)
            {
                var hours = disk.Smart.PowerOnHours;
                if (!hours.IsReliable) continue;
                evaluated = true;

                if (hours.Value < c.T.DiskPowerOnHoursWarning) continue;

                var years = Math.Round(hours.Value / 8760d, 1);
                findings.Add(c.Finding(Severity.Info,
                    "Un disque a beaucoup d'heures de fonctionnement",
                    Label(disk) + " : " + Fmt.Number(hours.Value) + " heures, soit environ " +
                    years.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " an(s) de fonctionnement continu " +
                    "(seuil " + Fmt.Number(c.T.DiskPowerOnHoursWarning) + " h).",
                    "Ce disque a beaucoup servi. Il ne présente pas d'anomalie, mais son âge justifie de vérifier " +
                    "que les sauvegardes sont bien en place.",
                    subject: Label(disk),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Heures de fonctionnement", Fmt.Number(hours.Value), hours.Source,
                            Fmt.Number(c.T.DiskPowerOnHoursWarning))),
                    recommendations: RuleContext.Rec(Rec.BackupNow)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun disque n'expose son compteur d'heures de fonctionnement.");
        }

        /// <summary>
        /// Un disque relié à une génération inférieure à celle qu'il sait tenir.
        /// </summary>
        /// <remarks>
        /// Le cas d'atelier par excellence : un disque à mémoire flash de sixième génération
        /// branché sur un port de troisième tient exactement la moitié de son débit, sans
        /// qu'aucun symptôme ne le désigne. Le client conclut que son disque neuf est décevant,
        /// et le technicien cherche du côté du logiciel.
        /// <para>
        /// Le constat n'est possible qu'en session administrateur : c'est la commande ATA brute
        /// qui rend les deux générations. Sans elle, la règle ne conclut rien plutôt que de
        /// laisser croire que tout va bien.
        /// </para>
        /// </remarks>
        private static RuleResult ThrottledLink(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var disk in c.Snapshot.Storage.PhysicalDisks)
            {
                var current = disk.Identity.LinkGigabitsPerSecond;
                var maximum = disk.Identity.MaximumGigabitsPerSecond;
                if (!current.HasValue || !maximum.HasValue) continue;

                evaluated = true;
                if (current.Value >= maximum.Value) continue;

                // Le rapport dit tout : un facteur deux se ressent, un facteur quatre se voit.
                var ratio = maximum.Value / current.Value;

                findings.Add(c.Finding(Severity.Warning,
                    "Un disque est bridé par le port sur lequel il est branché",
                    Label(disk) + " : liaison négociée à " + Fmt.Speed(current.Value) +
                    " alors que le disque sait tenir " + Fmt.Speed(maximum.Value) + ".",
                    "Ce disque est capable d'aller " + Fmt.Ratio(ratio) + " fois plus vite que ce que " +
                    "son branchement lui permet. Le changer de port sur la carte mère, ou activer le " +
                    "mode le plus récent dans le firmware, rendrait la différence sans rien acheter.",
                    subject: Label(disk),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Liaison négociée", Fmt.Speed(current.Value), current.Source,
                            Fmt.Speed(maximum.Value)))));
            }

            if (findings.Count > 0) return RuleResult.Of(findings);

            return evaluated
                ? RuleResult.Clean
                : RuleResult.NotEvaluated(
                    "La génération de liaison des disques n'a pas pu être lue : elle exige une " +
                    "session administrateur.");
        }

        private static string Label(PhysicalDiskInfo disk)
            => "Disque " + disk.Index + (disk.Model.HasValue ? " (" + disk.Model.Value + ")" : string.Empty);
    }
}
