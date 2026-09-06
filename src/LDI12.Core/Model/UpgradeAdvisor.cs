using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Platform;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Confronte ce qui a été mesuré aux exigences de Windows 11.
    /// </summary>
    /// <remarks>
    /// <b>Aucune mesure nouvelle : une projection, comme la chronologie.</b> Tout ce qui sert ici
    /// (processeur, mémoire, disque système, micrologiciel, démarrage sécurisé, module TPM) est
    /// relevé depuis la phase 1 par des sondes qui n'ont jamais été écrites pour cette question.
    /// C'est la raison pour laquelle cette classe vit dans le noyau : le moteur de règles et la
    /// fiche de faits en ont besoin tous les deux, et deux assemblages parallèles finiraient par
    /// se contredire à l'écran.
    /// <para>
    /// <b>La liste de processeurs de Microsoft n'y est pas, et ne peut pas y être.</b> Elle
    /// compte plus de mille références et se révise à chaque sortie ; l'embarquer reviendrait à
    /// figer une donnée périssable dans un logiciel qui se veut hors ligne. Le modèle du
    /// processeur est donc rapporté avec l'indication que sa génération permet (utile pour
    /// orienter, jamais suffisant pour conclure) et l'exigence reste explicitement indécidée.
    /// C'est la seule des neuf qui le soit par nature.
    /// </para>
    /// </remarks>
    public static class UpgradeAdvisor
    {
        /// <summary>
        /// 4 Go exigés, moins ce que le micrologiciel se réserve.
        /// </summary>
        /// <remarks>
        /// La mémoire vue par Windows est toujours un peu inférieure à celle qui est installée :
        /// le chipset graphique intégré en prend sa part. Comparer à 4 Go exactement ferait
        /// recaler des machines qui ont bien leurs quatre gigaoctets.
        /// </remarks>
        public const long MinimumMemoryBytes = 3_800_000_000L;

        /// <summary>64 Go exigés, moins l'arrondi que les constructeurs pratiquent sur les capacités.</summary>
        public const long MinimumStorageBytes = 63_000_000_000L;

        public const int MinimumCores = 2;

        public const int MinimumClockMhz = 1000;

        /// <summary>Première génération Intel Core retenue par Microsoft.</summary>
        public const int IntelCutoffGeneration = 8;

        /// <summary>Première série Ryzen retenue par Microsoft, Zen+ et au-delà.</summary>
        public const int RyzenCutoffSeries = 2;

        public static UpgradeAssessment Assess(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var windows = snapshot.Platform.Windows;

            if (windows.IsServer)
                return new UpgradeAssessment
                {
                    Verdict = UpgradeVerdict.NotApplicable,
                    Note = "Cette machine porte une édition serveur de Windows : les exigences de Windows 11 ne " +
                           "la concernent pas.",
                };

            if (windows.Family == WindowsFamily.Windows11)
                return new UpgradeAssessment
                {
                    Verdict = UpgradeVerdict.AlreadyThere,
                    Note = "Windows 11 est déjà installé.",
                };

            if (windows.Family == WindowsFamily.Unknown)
                return new UpgradeAssessment
                {
                    Verdict = UpgradeVerdict.Unknown,
                    Note = "La version de Windows installée n'a pas pu être déterminée.",
                };

            var cpu = snapshot.Hardware.Cpu;
            var board = snapshot.Hardware.Motherboard;

            var requirements = new List<UpgradeRequirement>
            {
                Architecture(windows),
                Cores(cpu),
                Clock(cpu),
                Model(cpu),
                Memory(snapshot.Hardware.Memory),
                Storage(snapshot.Storage),
                Firmware(board),
                SecureBoot(board),
                Tpm(board),
            };

            return new UpgradeAssessment
            {
                Verdict = Conclude(requirements),
                Requirements = requirements,
                CpuIndication = ClassifyCpu(cpu.Model.Or(null!)),
            };
        }

        /// <summary>
        /// Le verdict d'ensemble, à partir des neuf exigences.
        /// </summary>
        /// <remarks>
        /// L'ordre de priorité est celui de l'utilité : un blocage matériel se dit avant tout le
        /// reste, parce qu'il clôt la question ; une mesure manquante vient ensuite, parce qu'elle
        /// se rattrape ; un réglage à changer en dernier, parce que c'est une bonne nouvelle.
        /// <para>
        /// <see cref="RequirementOutcome.Undetermined"/> (la liste de processeurs) n'entre pas
        /// dans le calcul. Elle vaut pour toutes les machines : la faire peser sur le verdict
        /// rendrait toutes les réponses indécises, et un outil qui répond « je ne sais pas »
        /// partout ne répond nulle part.
        /// </para>
        /// </remarks>
        private static UpgradeVerdict Conclude(IReadOnlyList<UpgradeRequirement> requirements)
        {
            var unknown = false;
            var fixable = false;

            foreach (var requirement in requirements)
            {
                if (requirement.Outcome == RequirementOutcome.NotMet) return UpgradeVerdict.Ineligible;
                if (requirement.Outcome == RequirementOutcome.Unknown) unknown = true;
                if (requirement.Outcome == RequirementOutcome.Fixable) fixable = true;
            }

            if (unknown) return UpgradeVerdict.Undetermined;
            return fixable ? UpgradeVerdict.EligibleAfterSetting : UpgradeVerdict.Eligible;
        }

        private static UpgradeRequirement Architecture(WindowsProfile windows)
        {
            const string expectation = "Un processeur 64 bits.";

            switch (windows.NativeArchitecture)
            {
                case ProcessorArchitecture.X64:
                case ProcessorArchitecture.Arm64:
                    return Of("cpu.architecture", "Architecture du processeur", expectation,
                        RequirementOutcome.Met,
                        "Processeur 64 bits (" + ArchitectureLabel(windows.NativeArchitecture) + ").");

                case ProcessorArchitecture.Unknown:
                    return Of("cpu.architecture", "Architecture du processeur", expectation,
                        RequirementOutcome.Unknown,
                        "L'architecture du processeur n'a pas pu être lue.");

                default:
                    return Of("cpu.architecture", "Architecture du processeur", expectation,
                        RequirementOutcome.NotMet,
                        "Processeur 32 bits (" + ArchitectureLabel(windows.NativeArchitecture) + ").",
                        "Windows 11 n'existe qu'en 64 bits : aucune édition ne s'installe ici.");
            }
        }

        private static UpgradeRequirement Cores(CpuInfo cpu)
        {
            const string expectation = "Au moins deux cœurs.";

            // Le compte physique d'abord : c'est celui que Microsoft nomme. Le compte logique le
            // remplace quand la carte mère ne publie pas le premier, et il ne peut alors que
            // surestimer : un processeur à deux cœurs logiques n'en a jamais quatre physiques.
            var cores = cpu.PhysicalCores.HasValue ? cpu.PhysicalCores : cpu.LogicalCores;
            if (!cores.HasValue)
                return Of("cpu.cores", "Nombre de cœurs", expectation, RequirementOutcome.Unknown,
                    cores.Reason ?? "Le nombre de cœurs du processeur n'a pas pu être lu.");

            var count = cores.Value;
            return Of("cpu.cores", "Nombre de cœurs", expectation,
                count >= MinimumCores ? RequirementOutcome.Met : RequirementOutcome.NotMet,
                count + (count > 1 ? " cœurs." : " cœur."));
        }

        private static UpgradeRequirement Clock(CpuInfo cpu)
        {
            const string expectation = "Une fréquence d'au moins 1 GHz.";

            var clock = cpu.MaxClockMhz.HasValue ? cpu.MaxClockMhz : cpu.BaseClockMhz;
            if (!clock.HasValue)
                return Of("cpu.clock", "Fréquence du processeur", expectation, RequirementOutcome.Unknown,
                    clock.Reason ?? "La fréquence du processeur n'a pas pu être lue.");

            return Of("cpu.clock", "Fréquence du processeur", expectation,
                clock.Value >= MinimumClockMhz ? RequirementOutcome.Met : RequirementOutcome.NotMet,
                ValueFormat.Mhz(clock.Value) + ".");
        }

        private static UpgradeRequirement Model(CpuInfo cpu)
        {
            const string expectation = "Un processeur figurant sur la liste publiée par Microsoft.";

            if (!cpu.Model.HasValue)
                return Of("cpu.model", "Modèle du processeur", expectation, RequirementOutcome.Unknown,
                    cpu.Model.Reason ?? "Le modèle du processeur n'a pas pu être lu.");

            var model = cpu.Model.Value;

            // La liste officielle n'est pas embarquée : le motif l'accompagne à chaque fois, y
            // compris quand l'indication est favorable. Une bonne nouvelle mal étayée se retient
            // mieux qu'une mauvaise, et c'est exactement celle qu'il ne faut pas laisser passer
            // pour une certitude.
            const string caveat = "La liste officielle des processeurs n'est pas embarquée : plus de mille " +
                                  "références, révisées à chaque sortie. Elle seule fait foi.";

            switch (ClassifyCpu(model))
            {
                case CpuListIndication.LikelyListed:
                    return Of("cpu.model", "Modèle du processeur", expectation, RequirementOutcome.Undetermined,
                        model + " : génération retenue par Microsoft.", caveat);

                case CpuListIndication.LikelyNotListed:
                    return Of("cpu.model", "Modèle du processeur", expectation, RequirementOutcome.Undetermined,
                        model + " : génération antérieure à celles que Microsoft a retenues.", caveat);

                default:
                    return Of("cpu.model", "Modèle du processeur", expectation, RequirementOutcome.Undetermined,
                        model + " : génération non identifiable depuis la référence.", caveat);
            }
        }

        private static UpgradeRequirement Memory(MemoryInfo memory)
        {
            const string expectation = "4 Go de mémoire vive.";

            if (!memory.TotalBytes.HasValue)
                return Of("memory", "Mémoire vive", expectation, RequirementOutcome.Unknown,
                    memory.TotalBytes.Reason ?? "La quantité de mémoire n'a pas pu être lue.");

            var total = memory.TotalBytes.Value;
            return Of("memory", "Mémoire vive", expectation,
                total >= MinimumMemoryBytes ? RequirementOutcome.Met : RequirementOutcome.NotMet,
                ValueFormat.Bytes(total) + ".",
                total >= MinimumMemoryBytes
                    ? null
                    : "La mémoire s'ajoute sur la plupart des machines : c'est le seul de ces blocages qui se " +
                      "lève sans changer la machine.");
        }

        private static UpgradeRequirement Storage(StorageSnapshot storage)
        {
            const string expectation = "64 Go de stockage.";

            var capacity = SystemCapacity(storage);
            if (capacity == null)
                return Of("storage", "Disque système", expectation, RequirementOutcome.Unknown,
                    "La capacité du disque qui porte Windows n'a pas pu être lue.");

            var bytes = capacity.Value;
            return Of("storage", "Disque système", expectation,
                bytes >= MinimumStorageBytes ? RequirementOutcome.Met : RequirementOutcome.NotMet,
                ValueFormat.Bytes(bytes) + ".");
        }

        /// <summary>Capacité du disque qui porte Windows, à défaut celle de son volume.</summary>
        private static long? SystemCapacity(StorageSnapshot storage)
        {
            foreach (var disk in storage.PhysicalDisks)
                if (disk.IsSystemDisk.Or(false) && disk.CapacityBytes.HasValue) return disk.CapacityBytes.Value;

            // Le volume est plus petit que le disque dès qu'il y a une partition de récupération,
            // ce qui est la règle : le repli ne peut donc que sous-estimer, jamais l'inverse.
            foreach (var volume in storage.Volumes)
                if (volume.IsSystemVolume.Or(false) && volume.TotalBytes.HasValue) return volume.TotalBytes.Value;

            return null;
        }

        /// <summary>
        /// Le mode de démarrage, qui décide aussi de la table de partition.
        /// </summary>
        /// <remarks>
        /// Windows ne se démarre en UEFI que depuis un disque en table GPT, et en BIOS hérité que
        /// depuis un disque en MBR : lire le mode revient donc à lire la table, et c'est pour cela
        /// qu'aucune mesure supplémentaire n'est faite ici.
        /// </remarks>
        private static UpgradeRequirement Firmware(MotherboardInfo board)
        {
            const string expectation = "Un démarrage en mode UEFI, donc un disque système en table GPT.";

            var mode = board.Bios.Mode;
            if (!mode.HasValue)
                return Of("firmware", "Mode de démarrage", expectation, RequirementOutcome.Unknown,
                    mode.Reason ?? "Le mode de démarrage du micrologiciel n'a pas pu être lu.");

            if (mode.Value == FirmwareMode.Uefi)
                return Of("firmware", "Mode de démarrage", expectation, RequirementOutcome.Met,
                    "UEFI, donc disque système en table GPT.");

            return Of("firmware", "Mode de démarrage", expectation, RequirementOutcome.Fixable,
                "BIOS hérité, donc disque système en table MBR.",
                "Convertir le disque en GPT puis basculer le micrologiciel en UEFI. Aucun matériel à changer, " +
                "mais l'opération touche au démarrage et se prépare, et une machine dont le micrologiciel ne " +
                "propose pas l'UEFI ne pourra pas suivre.");
        }

        private static UpgradeRequirement SecureBoot(MotherboardInfo board)
        {
            const string expectation = "Le démarrage sécurisé, disponible et activé.";

            if (board.Bios.Mode.Or(FirmwareMode.Unknown) == FirmwareMode.LegacyBios)
                return Of("secureboot", "Démarrage sécurisé", expectation, RequirementOutcome.Fixable,
                    "Hors de portée tant que la machine démarre en BIOS hérité.",
                    "Le démarrage sécurisé n'existe qu'en mode UEFI : il se règle après la conversion, " +
                    "et pas avant.");

            var secure = board.SecureBootEnabled;
            if (!secure.HasValue)
                return Of("secureboot", "Démarrage sécurisé", expectation, RequirementOutcome.Unknown,
                    secure.Reason ?? "L'état du démarrage sécurisé n'a pas pu être lu.");

            if (secure.Value)
                return Of("secureboot", "Démarrage sécurisé", expectation, RequirementOutcome.Met, "Activé.");

            return Of("secureboot", "Démarrage sécurisé", expectation, RequirementOutcome.Fixable,
                "Disponible mais désactivé.",
                "S'active dans le micrologiciel, sans rien changer au matériel.");
        }

        /// <summary>
        /// Le module TPM, et l'ambiguïté qu'il faut dire au lieu de la masquer.
        /// </summary>
        /// <remarks>
        /// Windows ne distingue pas un module absent d'un module désactivé dans le micrologiciel :
        /// dans les deux cas, il n'expose rien. Or les deux situations n'ont pas la même issue :
        /// l'une se règle en trois clics dans le firmware, l'autre condamne la machine. Annoncer
        /// « pas de TPM » serait faux une fois sur deux, sur des machines pourtant récentes où
        /// Intel PTT et AMD fTPM sortent d'usine désactivés.
        /// </remarks>
        private static UpgradeRequirement Tpm(MotherboardInfo board)
        {
            const string expectation = "Un module TPM en version 2.0, présent et activé.";

            var tpm = board.Tpm;

            if (tpm.Present.Availability == Availability.RequiresElevation)
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.Unknown,
                    "L'état du module TPM ne se lit qu'en session administrateur.");

            if (!tpm.Present.HasValue)
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.Unknown,
                    tpm.Present.Reason ?? "La présence d'un module TPM n'a pas pu être lue.");

            if (!tpm.Present.Value)
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.Fixable,
                    "Aucun module TPM n'est exposé.",
                    "Windows ne voit pas non plus un module désactivé dans le micrologiciel : les deux cas se " +
                    "ressemblent exactement ici. Chercher « Intel PTT », « AMD fTPM » ou « Security Device " +
                    "Support » ; si rien de tel n'y figure, la machine n'en a effectivement pas.");

            var version = SpecVersion(tpm.SpecVersion.Or(null!));
            if (version == null)
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.Unknown,
                    "Un module TPM est présent, mais sa version n'a pas pu être lue.");

            if (version.Value < 2)
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.NotMet,
                    "Module TPM en version " + version.Value.ToString("0.0", CultureInfo.CurrentCulture) +
                    " : Windows 11 en exige un en 2.0.");

            if (!tpm.Enabled.Or(true))
                return Of("tpm", "Module TPM", expectation, RequirementOutcome.Fixable,
                    "Module TPM 2.0 présent, mais désactivé.",
                    "S'active dans le micrologiciel, sans rien changer au matériel.");

            return Of("tpm", "Module TPM", expectation, RequirementOutcome.Met, "Module TPM 2.0 actif.");
        }

        /// <summary>Version majeure et mineure d'une chaîne du genre « 2.0, 0, 1.38 ».</summary>
        private static double? SpecVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw!.Trim();
            var comma = text.IndexOf(',');
            if (comma > 0) text = text.Substring(0, comma).Trim();

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;
        }

        /// <summary>
        /// Situe un processeur par rapport aux générations que Microsoft a retenues.
        /// </summary>
        /// <remarks>
        /// Volontairement prudente : elle ne répond que sur les deux familles dont la référence
        /// commerciale porte sa génération, et se tait partout ailleurs. Un Athlon, un Xeon ou un
        /// Pentium récent reviennent donc <see cref="CpuListIndication.Unknown"/> plutôt que d'être
        /// rangés au jugé.
        /// </remarks>
        public static CpuListIndication ClassifyCpu(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return CpuListIndication.Unknown;

            var text = model!.ToUpperInvariant();

            // La gamme Core Ultra est tout entière postérieure à la coupure, et sa référence ne
            // porte plus de numéro de génération à la même place. Le sigle « (TM) » s'intercale
            // dans la chaîne que publie Windows : les deux écritures sont donc cherchées.
            if (text.Contains("CORE ULTRA") || text.Contains("CORE(TM) ULTRA"))
                return CpuListIndication.LikelyListed;

            var intel = IntelGeneration(text);
            if (intel > 0)
                return intel >= IntelCutoffGeneration ? CpuListIndication.LikelyListed : CpuListIndication.LikelyNotListed;

            var ryzen = RyzenSeries(text);
            if (ryzen > 0)
                return ryzen >= RyzenCutoffSeries ? CpuListIndication.LikelyListed : CpuListIndication.LikelyNotListed;

            // Familles entièrement antérieures à la coupure, et dont la référence ne porte aucun
            // numéro de génération exploitable.
            if (text.Contains("CORE(TM)2") || text.Contains("CORE 2 ") ||
                text.Contains("PHENOM") || text.Contains("ATHLON II") || text.Contains("PENTIUM 4"))
                return CpuListIndication.LikelyNotListed;

            return CpuListIndication.Unknown;
        }

        /// <summary>Génération d'un Intel Core, lue dans sa référence : i7-8550U donne 8.</summary>
        private static int IntelGeneration(string text)
        {
            foreach (var marker in new[] { "I3-", "I5-", "I7-", "I9-" })
            {
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;

                var digits = Digits(text, at + marker.Length);

                // Les trois premières générations se distinguent par la longueur : i5-650 en
                // compte trois, i5-2400 quatre, i9-13900K cinq.
                if (digits.Length == 3) return 1;
                if (digits.Length == 4) return digits[0] - '0';
                if (digits.Length == 5) return int.Parse(digits.Substring(0, 2), CultureInfo.InvariantCulture);
            }

            return 0;
        }

        /// <summary>Série d'un Ryzen, lue dans sa référence : Ryzen 5 3600 donne 3.</summary>
        private static int RyzenSeries(string text)
        {
            var at = text.IndexOf("RYZEN", StringComparison.Ordinal);
            if (at < 0) return 0;

            for (var i = at; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i])) continue;

                // Le premier groupe de quatre chiffres est la référence ; ceux d'un ou deux
                // chiffres qui le précèdent sont le rang commercial (« Ryzen 5 ») ou la mention
                // « PRO ».
                var digits = Digits(text, i);
                if (digits.Length == 4) return digits[0] - '0';
                i += digits.Length;
            }

            return 0;
        }

        private static string Digits(string text, int from)
        {
            var end = from;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            return text.Substring(from, end - from);
        }

        private static string ArchitectureLabel(ProcessorArchitecture architecture)
        {
            switch (architecture)
            {
                case ProcessorArchitecture.X64: return "x64";
                case ProcessorArchitecture.X86: return "x86";
                case ProcessorArchitecture.Arm: return "ARM";
                case ProcessorArchitecture.Arm64: return "ARM64";
                default: return "inconnue";
            }
        }

        private static UpgradeRequirement Of(
            string id, string label, string expectation, RequirementOutcome outcome,
            string observation, string? note = null)
            => new UpgradeRequirement
            {
                Id = id,
                Label = label,
                Expectation = expectation,
                Outcome = outcome,
                Observation = observation,
                Note = note,
            };
    }
}
