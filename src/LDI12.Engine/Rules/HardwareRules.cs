using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>Règles matérielles : processeur, mémoire, affichage, firmware, batterie.</summary>
    internal static class HardwareRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Hardware;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("CPU-001", "Virtualisation matérielle", Cat, Virtualization);
            yield return new Rule("CPU-002", "Nombre de cœurs", Cat, CoreCount);
            yield return new Rule("MEM-001", "Quantité de mémoire installée", Cat, MemoryAmount);
            yield return new Rule("MEM-002", "Configuration mono-canal", Cat, SingleChannel, "memory.configuration");
            yield return new Rule("MEM-003", "Mémoire sous-cadencée", Cat, UnderclockedMemory, "memory.configuration");
            yield return new Rule("MEM-004", "Modules mémoire hétérogènes", Cat, MixedMemory, "memory.configuration");
            yield return new Rule("GPU-001", "Pilote graphique générique", Cat, GenericGpuDriver);
            yield return new Rule("GPU-002", "Ancienneté du pilote graphique", Cat, GpuDriverAge);
            yield return new Rule("MB-001", "Ancienneté du firmware", Cat, BiosAge);
            yield return new Rule("BAT-001", "Usure de la batterie", Cat, BatteryWear);
            yield return new Rule("BAT-002", "État de la batterie", Cat, BatteryStatus);
            yield return new Rule("CPU-003", "Température du processeur", Cat, CpuTemperature, "thermal");
            yield return new Rule("THM-001", "Zone thermique élevée", Cat, ThermalZone, "thermal");
        }

        private static RuleResult Virtualization(RuleContext c)
        {
            var virtualization = c.Hardware.Cpu.VirtualizationEnabled;
            if (!virtualization.IsReliable)
                return RuleResult.NotEvaluated("L'état de la virtualisation matérielle n'est pas exposé par cette machine.");

            if (virtualization.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "La virtualisation matérielle est désactivée",
                "Le processeur prend en charge la virtualisation mais elle est désactivée dans le firmware.",
                "Cette option du BIOS est nécessaire aux machines virtuelles, au sous-système Linux de Windows " +
                "et à certaines protections de sécurité. Son absence ne gêne pas un usage courant.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Virtualisation", "Désactivée", virtualization.Source)),
                recommendations: RuleContext.Rec(Rec.EnableVirtualization)));
        }

        private static RuleResult CoreCount(RuleContext c)
        {
            var cores = c.Hardware.Cpu.LogicalCores;
            if (!cores.IsReliable)
                return RuleResult.NotEvaluated("Le nombre de threads du processeur n'a pas pu être déterminé.");

            if (!c.IsModernWindows || cores.Value >= c.T.MinimumLogicalCoresModernWindows) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Le processeur est un facteur limitant",
                "Processeur " + c.Hardware.Cpu.Model.Or("inconnu") + " : " + cores.Value +
                " thread(s) sous " + Core.Platform.WindowsProfile.FamilyLabel(c.Windows.Family) +
                " (seuil " + c.T.MinimumLogicalCoresModernWindows + ").",
                "Le processeur de cette machine est peu puissant pour la version de Windows installée. " +
                "Aucun nettoyage logiciel ne compensera cette limite matérielle.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Threads", cores.Value.ToString(), cores.Source,
                        c.T.MinimumLogicalCoresModernWindows.ToString())),
                recommendations: RuleContext.Rec(Rec.PlanHardwareUpgrade)));
        }

        private static RuleResult MemoryAmount(RuleContext c)
        {
            var total = c.Hardware.Memory.TotalBytes;
            if (!total.IsReliable)
                return RuleResult.NotEvaluated("La quantité de mémoire installée n'a pas pu être lue.");

            if (!c.IsModernWindows || total.Value >= c.T.RecommendedMemoryBytesModernWindows) return RuleResult.Clean;

            var severity = total.Value < c.T.MinimumMemoryBytesModernWindows ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem
                ? c.T.MinimumMemoryBytesModernWindows
                : c.T.RecommendedMemoryBytesModernWindows;

            return RuleResult.Of(c.Finding(severity,
                "La mémoire installée est insuffisante",
                Fmt.Bytes(total.Value) + " installés sous " +
                Core.Platform.WindowsProfile.FamilyLabel(c.Windows.Family) +
                " (seuil " + Fmt.Bytes(threshold) + ").",
                "Cette machine n'a pas assez de mémoire pour la version de Windows installée. " +
                "Elle passe son temps à échanger avec le disque, ce qui explique la lenteur générale. " +
                "Ajouter de la mémoire est ici l'amélioration la plus efficace.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Mémoire installée", Fmt.Bytes(total.Value), total.Source, Fmt.Bytes(threshold))),
                recommendations: RuleContext.Rec(Rec.AddMemory)));
        }

        private static RuleResult SingleChannel(RuleContext c)
        {
            var memory = c.Hardware.Memory;
            if (memory.Modules.Count == 0 || !memory.SlotsTotal.IsReliable)
                return RuleResult.NotEvaluated("L'inventaire des emplacements mémoire n'est pas disponible.");

            if (memory.Modules.Count != 1 || memory.SlotsTotal.Value < 2) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "La mémoire fonctionne en mono-canal",
                "1 module installé sur " + memory.SlotsTotal.Value + " emplacements disponibles.",
                "Un seul barrette de mémoire est installée alors que la carte mère en accepte plusieurs. " +
                "En ajouter une identique augmenterait sensiblement les performances, surtout sur une machine " +
                "à carte graphique intégrée.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Modules installés", "1", DataSource.Wmi,
                        memory.SlotsTotal.Value + " emplacements")),
                recommendations: RuleContext.Rec(Rec.EnableDualChannel)));
        }

        private static RuleResult UnderclockedMemory(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var module in c.Hardware.Memory.Modules)
            {
                if (!module.SpeedMhz.IsReliable || !module.ConfiguredSpeedMhz.IsReliable) continue;
                evaluated = true;

                if (module.ConfiguredSpeedMhz.Value >= module.SpeedMhz.Value) continue;

                var locator = module.DeviceLocator.Or("emplacement inconnu");
                findings.Add(c.Finding(Severity.Info,
                    "Un module mémoire fonctionne sous sa fréquence nominale",
                    locator + " : " + module.ConfiguredSpeedMhz.Value + " MHz appliqués pour " +
                    module.SpeedMhz.Value + " MHz annoncés.",
                    "La mémoire tourne moins vite que ce dont elle est capable. Un réglage du firmware " +
                    "permettrait d'exploiter pleinement les barrettes déjà installées.",
                    subject: locator,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Fréquence appliquée", module.ConfiguredSpeedMhz.Value + " MHz",
                            module.ConfiguredSpeedMhz.Source, module.SpeedMhz.Value + " MHz"))));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("La fréquence réellement appliquée n'est pas exposée par cette carte mère.");
        }

        private static RuleResult MixedMemory(RuleContext c)
        {
            var modules = c.Hardware.Memory.Modules;
            if (modules.Count < 2) return RuleResult.Clean;

            long? capacity = null;
            int? speed = null;
            var mixedCapacity = false;
            var mixedSpeed = false;
            var evaluated = false;

            foreach (var module in modules)
            {
                if (module.CapacityBytes.IsReliable)
                {
                    evaluated = true;
                    if (capacity == null) capacity = module.CapacityBytes.Value;
                    else if (capacity.Value != module.CapacityBytes.Value) mixedCapacity = true;
                }
                if (module.SpeedMhz.IsReliable)
                {
                    if (speed == null) speed = module.SpeedMhz.Value;
                    else if (speed.Value != module.SpeedMhz.Value) mixedSpeed = true;
                }
            }

            if (!evaluated)
                return RuleResult.NotEvaluated("Les caractéristiques des modules mémoire ne sont pas disponibles.");

            if (!mixedCapacity && !mixedSpeed) return RuleResult.Clean;

            var detail = mixedCapacity && mixedSpeed ? "capacités et fréquences différentes"
                : mixedCapacity ? "capacités différentes"
                : "fréquences différentes";

            return RuleResult.Of(c.Finding(Severity.Info,
                "Les modules mémoire ne sont pas identiques",
                modules.Count + " modules installés avec des " + detail + ".",
                "Les barrettes de mémoire installées ne sont pas identiques. La machine fonctionne, mais " +
                "toutes s'alignent sur la plus lente et le double canal peut être partiellement perdu.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Modules", modules.Count.ToString(), DataSource.Wmi, "caractéristiques identiques"))));
        }

        private static RuleResult GenericGpuDriver(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var gpu in c.Hardware.Gpus)
            {
                if (!gpu.UsesGenericMicrosoftDriver.IsReliable) continue;
                evaluated = true;

                if (!gpu.UsesGenericMicrosoftDriver.Value) continue;

                var model = gpu.Model.Or("Carte graphique");
                findings.Add(c.Finding(Severity.Problem,
                    "La carte graphique fonctionne avec le pilote générique de Windows",
                    model + " : pilote d'affichage générique Microsoft actif à la place du pilote constructeur.",
                    "L'affichage fonctionne en mode dégradé : pas d'accélération, résolutions limitées, " +
                    "animations saccadées. C'est souvent ce qui se cache derrière « depuis la réinstallation, " +
                    "l'écran rame ».",
                    subject: model,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Pilote actif", "Générique Microsoft", gpu.UsesGenericMicrosoftDriver.Source)),
                    recommendations: RuleContext.Rec(Rec.InstallGpuDriver)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Le pilote d'affichage n'a pas pu être identifié.");
        }

        private static RuleResult GpuDriverAge(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;
            var now = c.Snapshot.Metadata.CreatedAt;

            foreach (var gpu in c.Hardware.Gpus)
            {
                if (!gpu.DriverDate.IsReliable) continue;
                evaluated = true;

                var months = Fmt.MonthsSince(gpu.DriverDate.Value, now);
                if (months < c.T.GpuDriverAgeMonthsWarning) continue;

                // Le pilote générique est déjà signalé par GPU-001 : ne pas compter deux fois.
                if (gpu.UsesGenericMicrosoftDriver.Or(false)) continue;

                var model = gpu.Model.Or("Carte graphique");
                findings.Add(c.Finding(Severity.Info,
                    "Le pilote graphique est ancien",
                    model + " : pilote du " + Fmt.Date(gpu.DriverDate.Value) + ", soit " + Fmt.Years(months) +
                    " (seuil " + c.T.GpuDriverAgeMonthsWarning + " mois).",
                    "Le pilote de la carte graphique n'a pas été mis à jour depuis longtemps. " +
                    "Les versions récentes corrigent souvent des blocages d'affichage.",
                    subject: model,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Date du pilote", Fmt.Date(gpu.DriverDate.Value), gpu.DriverDate.Source,
                            c.T.GpuDriverAgeMonthsWarning + " mois")),
                    recommendations: RuleContext.Rec(Rec.UpdateGpuDriver)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("La date des pilotes graphiques n'est pas disponible.");
        }

        private static RuleResult BiosAge(RuleContext c)
        {
            var release = c.Hardware.Motherboard.Bios.ReleaseDate;
            if (!release.IsReliable)
                return RuleResult.NotEvaluated("La date du firmware n'est pas renseignée par cette carte mère.");

            var months = Fmt.MonthsSince(release.Value, c.Snapshot.Metadata.CreatedAt);
            if (months < c.T.BiosAgeMonthsWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Le firmware de la carte mère est ancien",
                "Version " + c.Hardware.Motherboard.Bios.Version.Or("inconnue") + " du " +
                Fmt.Date(release.Value) + ", soit " + Fmt.Years(months) +
                " (seuil " + c.T.BiosAgeMonthsWarning + " mois).",
                "Le firmware de la carte mère date de plusieurs années. Une mise à jour peut corriger " +
                "des problèmes de stabilité ou de compatibilité matérielle, mais elle n'est pas urgente " +
                "si la machine fonctionne bien.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Date du firmware", Fmt.Date(release.Value), release.Source,
                        c.T.BiosAgeMonthsWarning + " mois")),
                recommendations: RuleContext.Rec(Rec.UpdateBios)));
        }

        private static RuleResult BatteryWear(RuleContext c)
        {
            if (c.Hardware.Batteries.Count == 0)
                return RuleResult.NotEvaluated("Cette machine ne dispose pas de batterie.");

            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var battery in c.Hardware.Batteries)
            {
                if (!battery.WearPercent.HasValue) continue;
                evaluated = true;

                if (battery.WearPercent.Value < c.T.BatteryWearWarning) continue;

                var severity = battery.WearPercent.Value >= c.T.BatteryWearProblem ? Severity.Warning : Severity.Info;
                var name = battery.Name.Or("Batterie");

                findings.Add(c.Finding(severity,
                    "La batterie est usée",
                    name + " : " + Fmt.Percent(battery.WearPercent.Value) + " de capacité perdue : " +
                    Fmt.Number(battery.FullChargeCapacityMwh.Or(0)) + " mWh restants sur " +
                    Fmt.Number(battery.DesignCapacityMwh.Or(0)) + " mWh d'origine (seuil " +
                    Fmt.Percent(c.T.BatteryWearWarning) + ").",
                    "La batterie a perdu une part importante de sa capacité d'origine. L'autonomie ne reviendra " +
                    "pas sans remplacement : c'est une usure normale, pas une panne.",
                    subject: name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Usure", Fmt.Percent(battery.WearPercent.Value), battery.WearPercent.Source,
                            Fmt.Percent(c.T.BatteryWearWarning))),
                    recommendations: RuleContext.Rec(Rec.ReplaceBattery)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Les capacités de la batterie ne sont pas exposées par cette machine.");
        }

        private static RuleResult BatteryStatus(RuleContext c)
        {
            if (c.Hardware.Batteries.Count == 0)
                return RuleResult.NotEvaluated("Cette machine ne dispose pas de batterie.");

            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var battery in c.Hardware.Batteries)
            {
                if (!battery.Status.IsReliable) continue;
                evaluated = true;

                var status = battery.Status.Value;
                if (status != "Critique" && status != "Faible") continue;

                var name = battery.Name.Or("Batterie");
                findings.Add(c.Finding(Severity.Info,
                    "La batterie est faiblement chargée",
                    name + " : état « " + status + " », charge à " +
                    (battery.ChargePercent.HasValue ? battery.ChargePercent.Value + " %" : "niveau inconnu") + ".",
                    "La batterie était peu chargée au moment de l'analyse. Ce constat reflète l'instant de la mesure " +
                    "et non l'état de santé de la batterie.",
                    subject: name,
                    confidence: ConfidenceLevel.Medium,
                    evidence: RuleContext.Ev(Evidence.Of("État", status, battery.Status.Source))));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("L'état de charge de la batterie n'est pas exposé.");
        }

        /// <summary>
        /// Température du processeur.
        /// </summary>
        /// <remarks>
        /// N'existe que lorsque le technicien a activé les capteurs matériels : c'est la seule
        /// source, sous Windows, qui la donne. Sans eux, la règle ne se déclare pas « propre »
        /// mais <b>non évaluée</b> : dire qu'un processeur ne chauffe pas quand on n'a pas su
        /// prendre sa température serait exactement le mensonge que le reste du logiciel évite.
        /// </remarks>
        private static RuleResult CpuTemperature(RuleContext c)
        {
            var temperature = c.Hardware.Thermal.CpuPackageCelsius;
            if (!temperature.IsReliable)
                return RuleResult.NotEvaluated(
                    temperature.Reason ?? "La température du processeur n'a pas été mesurée.");

            if (temperature.Value < c.T.CpuTemperatureWarning) return RuleResult.Clean;

            var severity = temperature.Value >= c.T.CpuTemperatureProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem ? c.T.CpuTemperatureProblem : c.T.CpuTemperatureWarning;

            // La mesure est un instantané pris pendant l'analyse : un processeur qui compile à
            // 90 °C fait son travail, un processeur au repos à 90 °C ne va pas bien. La charge
            // relevée au même moment est donc citée avec la température.
            var load = c.Hardware.Cpu.UsagePercent;

            return RuleResult.Of(c.Finding(severity,
                "Le processeur est chaud",
                Fmt.Celsius((int)Math.Round(temperature.Value)) + " relevés" +
                (load.IsReliable ? " sous " + Fmt.Percent(load.Value) + " de charge" : string.Empty) +
                " (seuil " + Fmt.Celsius((int)Math.Round(threshold)) + ").",
                "Le processeur travaille à une température élevée. Sous forte charge c'est normal ; " +
                "au repos, cela signale presque toujours un radiateur encrassé ou une pâte thermique " +
                "à refaire. Un processeur trop chaud se ralentit lui-même pour se protéger.",
                confidence: ConfidenceLevel.Medium,
                evidence: RuleContext.Ev(
                    Evidence.Of("Température du processeur", Fmt.Celsius((int)Math.Round(temperature.Value)),
                        temperature.Source, Fmt.Celsius((int)Math.Round(threshold))),
                    Evidence.Of("Charge au même instant",
                        load.IsReliable ? Fmt.Percent(load.Value) : "non mesurée", load.Source)),
                recommendations: RuleContext.Rec(Rec.ImproveCooling)));
        }

        /// <summary>
        /// Zone thermique ACPI anormalement chaude.
        /// </summary>
        /// <remarks>
        /// Seules les zones dont la valeur est physiquement crédible entrent ici. Une carte mère
        /// qui déclare une zone à 17 °C sur une machine allumée depuis huit heures ne mesure pas
        /// un composant : conclure dessus, dans un sens ou dans l'autre, serait inventer.
        /// <para>
        /// Et le constat ne nomme jamais le composant. Le firmware ne dit pas ce que chaque zone
        /// suit ; dire « le processeur chauffe » à partir de <c>\_TZ.TZ00</c> serait une
        /// déduction que rien n'appuie. On dit qu'une zone est chaude, on donne son nom, et on
        /// laisse le technicien ouvrir le boîtier.
        /// </para>
        /// </remarks>
        private static RuleResult ThermalZone(RuleContext c)
        {
            var zones = c.Hardware.Thermal.Zones;
            if (zones.Count == 0)
                return RuleResult.NotEvaluated(
                    "Cette machine n'expose aucune zone thermique lisible sans pilote de capteurs.");

            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var zone in zones)
            {
                if (!zone.Plausible || !zone.Celsius.HasValue) continue;

                // Les capteurs du processeur sont laissés à CPU-003, qui les nomme mieux et
                // applique les seuils qui leur correspondent : sans cela, une seule surchauffe
                // produirait deux constats.
                if (zone.IsProcessor) continue;

                evaluated = true;

                var celsius = zone.Celsius.Value;
                if (celsius < c.T.ThermalZoneWarning) continue;

                var severity = celsius >= c.T.ThermalZoneProblem ? Severity.Warning : Severity.Info;
                var threshold = severity == Severity.Warning ? c.T.ThermalZoneProblem : c.T.ThermalZoneWarning;

                findings.Add(c.Finding(severity,
                    "Une zone thermique de la carte mère est chaude",
                    "Zone « " + zone.Name + " » à " + Fmt.Celsius((int)Math.Round(celsius)) +
                    " (seuil " + Fmt.Celsius((int)Math.Round(threshold)) + "). Le firmware ne dit pas " +
                    "quel composant cette zone suit.",
                    "Un capteur de la carte mère relève une température élevée. L'ordinateur ne dit pas " +
                    "de quelle pièce il s'agit, mais la cause est presque toujours la même : de la " +
                    "poussière accumulée dans les ventilateurs et les radiateurs.",
                    subject: zone.Name,
                    // La zone est mesurée, mais ce qu'elle mesure ne l'est pas.
                    confidence: ConfidenceLevel.Medium,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Zone " + zone.Name, Fmt.Celsius((int)Math.Round(celsius)),
                            zone.Celsius.Source, Fmt.Celsius((int)Math.Round(threshold)))),
                    recommendations: RuleContext.Rec(Rec.ImproveCooling)));
            }

            if (!evaluated)
                return RuleResult.NotEvaluated(
                    "Les zones thermiques déclarées ne rendent aucune valeur crédible : le firmware " +
                    "les expose sans les alimenter.");

            return findings.Count == 0 ? RuleResult.Clean : RuleResult.Of(findings);
        }
    }
}
