using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles de sécurité exploitables avec les données actuellement collectées.
    /// </summary>
    /// <remarks>
    /// Trois règles portant sur le socle matériel de la sécurité : démarrage sécurisé, TPM,
    /// chiffrement du disque. L'audit de configuration proprement dit (antivirus, pare-feu,
    /// contrôle de compte, comptes et bureau à distance) vit dans <c>SecurityAuditRules</c>,
    /// séparément, parce qu'il s'appuie sur une tout autre collecte et se relit mieux ainsi.
    /// </remarks>
    internal static class SecurityRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Security;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SEC-001", "Démarrage sécurisé", Cat, SecureBoot);
            yield return new Rule("SEC-002", "Module de sécurité TPM", Cat, Tpm);
            yield return new Rule("SEC-003", "Chiffrement du disque système", Cat, DiskEncryption);
        }

        private static RuleResult SecureBoot(RuleContext c)
        {
            var motherboard = c.Hardware.Motherboard;
            var secureBoot = motherboard.SecureBootEnabled;

            if (!secureBoot.IsReliable)
            {
                // Une machine en BIOS hérité ne peut pas activer le démarrage sécurisé : ce n'est
                // pas un défaut de configuration, c'est une caractéristique de l'installation.
                if (motherboard.Bios.Mode.Or(FirmwareMode.Unknown) == FirmwareMode.LegacyBios)
                {
                    return RuleResult.Of(c.Finding(Severity.Info,
                        "La machine démarre en mode BIOS hérité",
                        "Mode de démarrage : BIOS hérité. Le démarrage sécurisé ne s'applique pas dans cette configuration.",
                        "Cette machine utilise l'ancien mode de démarrage. Les protections modernes du démarrage " +
                        "ne peuvent pas être activées sans réinstaller Windows en mode UEFI.",
                        evidence: RuleContext.Ev(
                            Evidence.Of("Mode de démarrage", "BIOS hérité", motherboard.Bios.Mode.Source))));
                }

                return RuleResult.NotEvaluated(secureBoot.Reason ?? "L'état du démarrage sécurisé n'a pas pu être lu.");
            }

            if (secureBoot.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Le démarrage sécurisé est désactivé",
                "La machine démarre en UEFI mais le démarrage sécurisé est désactivé dans le firmware.",
                "Cette protection empêche un logiciel malveillant de se charger avant Windows. " +
                "Elle est désactivée sur cette machine.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Démarrage sécurisé", "Désactivé", secureBoot.Source, "Activé")),
                recommendations: RuleContext.Rec(Rec.EnableSecureBoot)));
        }

        private static RuleResult Tpm(RuleContext c)
        {
            var tpm = c.Hardware.Motherboard.Tpm;

            if (!tpm.Present.HasValue)
                return RuleResult.NotEvaluated(tpm.Present.Reason ?? "L'état du module TPM n'a pas pu être lu.");

            if (!tpm.Present.Value)
            {
                // Sur Windows 11, l'absence de TPM signale une installation contournant les
                // prérequis : c'est une information dont le technicien doit disposer.
                var severity = c.Windows.Family == WindowsFamily.Windows11 ? Severity.Warning : Severity.Info;
                return RuleResult.Of(c.Finding(severity,
                    "Aucun module de sécurité TPM n'est disponible",
                    "Aucun TPM exposé à Windows" +
                    (c.Windows.Family == WindowsFamily.Windows11
                        ? ", alors que Windows 11 l'exige normalement : installation probablement réalisée en contournant les prérequis."
                        : "."),
                    "Cette machine ne dispose pas du composant de sécurité qui protège les clés de chiffrement. " +
                    (c.Windows.Family == WindowsFamily.Windows11
                        ? "Windows 11 le demande normalement : cette installation risque de ne plus recevoir certaines mises à jour."
                        : "Le chiffrement du disque n'est pas possible dans cette configuration."),
                    evidence: RuleContext.Ev(
                        Evidence.Of("TPM", "Absent", tpm.Present.Source)),
                    recommendations: RuleContext.Rec(Rec.EnableTpm)));
            }

            if (!tpm.Ready.HasValue)
                return RuleResult.NotEvaluated("L'état de préparation du TPM n'est pas exposé.");

            if (tpm.Ready.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Le module TPM est présent mais inactif",
                "TPM " + tpm.SpecVersion.Or("de version inconnue") + " détecté, mais non activé ou non initialisé.",
                "Le composant de sécurité est physiquement présent mais désactivé dans le firmware. " +
                "Il suffit de l'activer pour permettre le chiffrement du disque.",
                evidence: RuleContext.Ev(
                    Evidence.Of("TPM", "Présent mais inactif", tpm.Ready.Source, "Actif")),
                recommendations: RuleContext.Rec(Rec.EnableTpm)));
        }

        private static RuleResult DiskEncryption(RuleContext c)
        {
            // Le chiffrement ne se justifie vraiment que sur une machine transportée : le
            // signaler sur un poste fixe serait un conseil hors sujet.
            var chassis = c.Hardware.Motherboard.ChassisType;
            if (!chassis.IsReliable)
                return RuleResult.NotEvaluated("Le type de machine n'a pas pu être déterminé.");

            var isPortable = chassis.Value.IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0
                             || chassis.Value.IndexOf("tablette", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isPortable) return RuleResult.Clean;

            foreach (var volume in c.Storage.Volumes)
            {
                if (!volume.IsSystemVolume.Or(false)) continue;
                if (!volume.BitLockerStatus.IsReliable)
                    return RuleResult.NotEvaluated(volume.BitLockerStatus.Reason ?? "L'état de chiffrement n'a pas pu être lu.");

                if (volume.BitLockerStatus.Value.IndexOf("Chiffré", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RuleResult.Clean;

                return RuleResult.Of(c.Finding(Severity.Warning,
                    "Le disque système d'une machine portable n'est pas chiffré",
                    "Volume " + volume.DriveLetter.Or("système") + " : " + volume.BitLockerStatus.Value +
                    ", sur un châssis de type « " + chassis.Value + " ».",
                    "Cette machine se transporte et son disque n'est pas chiffré. En cas de perte ou de vol, " +
                    "toutes les données sont lisibles en retirant simplement le disque.",
                    subject: volume.DriveLetter.Or("système"),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Chiffrement", volume.BitLockerStatus.Value, volume.BitLockerStatus.Source),
                        Evidence.Of("Type de machine", chassis.Value, chassis.Source)),
                    recommendations: RuleContext.Rec(Rec.EnableDiskEncryption)));
            }

            return RuleResult.NotEvaluated("Le volume système n'a pas été identifié.");
        }
    }

    /// <summary>Règles de performance : ce qui explique concrètement « mon PC est lent ».</summary>
    internal static class PerformanceRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Performance;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("PRF-001", "Programmes lancés au démarrage", Cat, StartupLoad, "startup.load");
            yield return new Rule("PRF-002", "Entrées de démarrage orphelines", Cat, StartupOrphans, "startup.load");
            yield return new Rule("PRF-003", "Occupation mémoire", Cat, MemoryPressure);
            yield return new Rule("PRF-004", "Charge processeur", Cat, CpuLoad);
        }

        private static RuleResult StartupLoad(RuleContext c)
        {
            var startup = c.System.Startup;
            if (startup.Count == 0 && c.System.Services.Count == 0)
                return RuleResult.NotEvaluated("Les programmes au démarrage n'ont pas pu être énumérés.");

            var enabled = 0;
            foreach (var item in startup) if (item.Enabled) enabled++;

            if (enabled < c.T.StartupItemsWarning) return RuleResult.Clean;

            var severity = enabled >= c.T.StartupItemsProblem ? Severity.Warning : Severity.Info;
            var threshold = severity == Severity.Warning ? c.T.StartupItemsProblem : c.T.StartupItemsWarning;

            return RuleResult.Of(c.Finding(severity,
                "Beaucoup de programmes se lancent au démarrage",
                enabled + " programme(s) lancés automatiquement (seuil " + threshold + ").",
                enabled + " programmes démarrent en même temps que Windows. Chacun allonge le temps de démarrage " +
                "et continue ensuite à consommer des ressources. En désactiver une partie est l'action la plus " +
                "rentable sur une machine qui met longtemps à être utilisable.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Programmes au démarrage", enabled.ToString(), DataSource.Registry, threshold.ToString())),
                recommendations: RuleContext.Rec(Rec.ReduceStartupItems)));
        }

        private static RuleResult StartupOrphans(RuleContext c)
        {
            if (c.System.Startup.Count == 0)
                return RuleResult.NotEvaluated("Les programmes au démarrage n'ont pas pu être énumérés.");

            var orphans = new List<string>();
            foreach (var item in c.System.Startup)
                if (item.TargetMissing) orphans.Add(item.Name);

            if (orphans.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des entrées de démarrage pointent vers des fichiers absents",
                orphans.Count + " entrée(s) orpheline(s) : " + string.Join(", ", orphans.ToArray()) + ".",
                "Des programmes désinstallés sont encore appelés au démarrage. Windows perd du temps à les " +
                "chercher avant d'abandonner. C'est un nettoyage sans risque.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Entrées orphelines", orphans.Count.ToString(), DataSource.Registry, "0")),
                recommendations: RuleContext.Rec(Rec.CleanStartupOrphans)));
        }

        private static RuleResult MemoryPressure(RuleContext c)
        {
            var usage = c.Hardware.Memory.UsagePercent;
            if (!usage.IsReliable)
                return RuleResult.NotEvaluated("L'occupation mémoire n'a pas pu être mesurée.");

            if (usage.Value < c.T.MemoryUsageWarning) return RuleResult.Clean;

            var severity = usage.Value >= c.T.MemoryUsageProblem ? Severity.Warning : Severity.Info;
            var threshold = severity == Severity.Warning ? c.T.MemoryUsageProblem : c.T.MemoryUsageWarning;

            return RuleResult.Of(c.Finding(severity,
                "La mémoire est fortement sollicitée",
                Fmt.Percent(usage.Value) + " de la mémoire occupée au moment de l'analyse : " +
                Fmt.Bytes(c.Hardware.Memory.AvailableBytes.Or(0)) + " disponibles sur " +
                Fmt.Bytes(c.Hardware.Memory.TotalBytes.Or(0)) + " (seuil " + Fmt.Percent(threshold) + ").",
                "La mémoire était presque saturée pendant l'analyse. Quand elle manque, Windows compense en " +
                "écrivant sur le disque, ce qui ralentit brutalement toute la machine.",
                // Une mesure instantanée : la conclusion dépend de ce qui tournait à ce moment-là.
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Occupation mémoire", Fmt.Percent(usage.Value), usage.Source, Fmt.Percent(threshold))),
                recommendations: RuleContext.Rec(Rec.AddMemory)));
        }

        private static RuleResult CpuLoad(RuleContext c)
        {
            var usage = c.Hardware.Cpu.UsagePercent;
            if (!usage.IsReliable)
                return RuleResult.NotEvaluated("La charge processeur n'a pas pu être mesurée.");

            if (usage.Value < c.T.CpuUsageWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Le processeur était fortement sollicité pendant l'analyse",
                Fmt.Percent(usage.Value) + " de charge mesurée sur une fenêtre de 600 ms (seuil " +
                Fmt.Percent(c.T.CpuUsageWarning) + ").",
                "Le processeur tournait à plein régime au moment du diagnostic. Il faut identifier le programme " +
                "responsable avant de conclure à un manque de puissance : une mise à jour ou une indexation en " +
                "cours produit le même symptôme.",
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Charge processeur", Fmt.Percent(usage.Value), usage.Source,
                        Fmt.Percent(c.T.CpuUsageWarning))),
                recommendations: RuleContext.Rec(Rec.InvestigateCpuLoad)));
        }
    }
}
