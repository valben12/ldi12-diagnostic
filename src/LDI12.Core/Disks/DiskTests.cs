using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Core.Disks
{
    /// <summary>Un attribut SMART dit en français, et ce qu'il annonce.</summary>
    public sealed class SmartAttributeInfo
    {
        public SmartAttributeInfo(string name, bool critical, string? meaning = null)
        {
            Name = name;
            Critical = critical;
            Meaning = meaning;
        }

        public string Name { get; }
        public bool Critical { get; }
        public string? Meaning { get; }
    }

    /// <summary>
    /// Les attributs SMART que GSmartControl et HDDScan affichent, avec leur nom en français. Les
    /// attributs critiques sont ceux dont une valeur brute non nulle annonce une panne.
    /// </summary>
    public static class SmartCatalog
    {
        private static readonly Dictionary<int, SmartAttributeInfo> Known = new Dictionary<int, SmartAttributeInfo>
        {
            [1] = new SmartAttributeInfo("Taux d'erreurs de lecture", false),
            [2] = new SmartAttributeInfo("Performance de débit", false),
            [3] = new SmartAttributeInfo("Temps de démarrage du moteur", false),
            [4] = new SmartAttributeInfo("Démarrages et arrêts du moteur", false),
            [5] = new SmartAttributeInfo("Secteurs réalloués", true, "Des secteurs défectueux ont été remplacés par la réserve : le disque s'abîme. Sauvegarder."),
            [7] = new SmartAttributeInfo("Taux d'erreurs de positionnement", false),
            [8] = new SmartAttributeInfo("Performance de positionnement", false),
            [9] = new SmartAttributeInfo("Heures de fonctionnement", false),
            [10] = new SmartAttributeInfo("Nouvelles tentatives de démarrage", true, "Le moteur peine à démarrer : mécanique en fin de vie."),
            [11] = new SmartAttributeInfo("Recalibrages", false),
            [12] = new SmartAttributeInfo("Cycles d'alimentation", false),
            [13] = new SmartAttributeInfo("Erreurs de lecture logicielles", false),
            [22] = new SmartAttributeInfo("Niveau d'hélium", false),
            [170] = new SmartAttributeInfo("Blocs de réserve disponibles", false),
            [171] = new SmartAttributeInfo("Échecs de programmation", true, "Des cellules mémoire refusent l'écriture."),
            [172] = new SmartAttributeInfo("Échecs d'effacement", true, "Des cellules mémoire refusent l'effacement."),
            [173] = new SmartAttributeInfo("Usure moyenne des cellules", false),
            [174] = new SmartAttributeInfo("Coupures d'alimentation inattendues", false),
            [175] = new SmartAttributeInfo("Échecs de programmation (pire puce)", false),
            [177] = new SmartAttributeInfo("Nivellement d'usure", false),
            [179] = new SmartAttributeInfo("Blocs de réserve utilisés", false),
            [180] = new SmartAttributeInfo("Blocs de réserve restants", false),
            [181] = new SmartAttributeInfo("Échecs de programmation", true, "Des cellules mémoire refusent l'écriture."),
            [182] = new SmartAttributeInfo("Échecs d'effacement", true, "Des cellules mémoire refusent l'effacement."),
            [183] = new SmartAttributeInfo("Ralentissements de l'interface", false),
            [184] = new SmartAttributeInfo("Erreurs de bout en bout", true, "Des données se sont altérées dans le disque lui-même."),
            [187] = new SmartAttributeInfo("Erreurs non corrigées", true, "Des lectures ont échoué malgré la correction d'erreurs : données perdues possibles."),
            [188] = new SmartAttributeInfo("Commandes expirées", false),
            [189] = new SmartAttributeInfo("Écritures à hauteur anormale", false),
            [190] = new SmartAttributeInfo("Température du flux d'air", false),
            [191] = new SmartAttributeInfo("Chocs détectés", false),
            [192] = new SmartAttributeInfo("Arrêts d'urgence des têtes", false),
            [193] = new SmartAttributeInfo("Cycles de chargement des têtes", false),
            [194] = new SmartAttributeInfo("Température", false),
            [195] = new SmartAttributeInfo("Erreurs corrigées par le disque", false),
            [196] = new SmartAttributeInfo("Événements de réallocation", true, "Le disque a dû remplacer des secteurs défectueux."),
            [197] = new SmartAttributeInfo("Secteurs en attente", true, "Des secteurs illisibles attendent d'être remplacés : le signal le plus précoce d'une panne."),
            [198] = new SmartAttributeInfo("Secteurs irrécupérables", true, "Des secteurs sont définitivement illisibles."),
            [199] = new SmartAttributeInfo("Erreurs de transmission (CRC)", false, "Erreurs sur le câble ou le connecteur, pas sur le disque : changer le câble SATA."),
            [200] = new SmartAttributeInfo("Erreurs d'écriture", false),
            [201] = new SmartAttributeInfo("Erreurs de lecture logicielles", false),
            [220] = new SmartAttributeInfo("Décalage des plateaux", false),
            [222] = new SmartAttributeInfo("Heures de chargement", false),
            [223] = new SmartAttributeInfo("Nouvelles tentatives de chargement", false),
            [224] = new SmartAttributeInfo("Friction de chargement", false),
            [225] = new SmartAttributeInfo("Cycles de chargement", false),
            [226] = new SmartAttributeInfo("Temps de chargement", false),
            [230] = new SmartAttributeInfo("Amplitude des têtes", false),
            [231] = new SmartAttributeInfo("Durée de vie restante", false),
            [232] = new SmartAttributeInfo("Réserve disponible", false),
            [233] = new SmartAttributeInfo("Usure du média", false),
            [234] = new SmartAttributeInfo("Effacements moyens", false),
            [235] = new SmartAttributeInfo("Blocs libres", false),
            [240] = new SmartAttributeInfo("Heures de vol des têtes", false),
            [241] = new SmartAttributeInfo("Données écrites", false),
            [242] = new SmartAttributeInfo("Données lues", false),
            [254] = new SmartAttributeInfo("Chutes libres détectées", false),
        };

        public static SmartAttributeInfo Describe(int id, string? fallback)
            => Known.TryGetValue(id, out var info) ? info : new SmartAttributeInfo(string.IsNullOrWhiteSpace(fallback) ? "Attribut constructeur " + id : fallback!, false);
    }

    /// <summary>Le bilan d'un disque, comme le résumé de GSmartControl.</summary>
    public sealed class DiskHealth
    {
        public string Tone { get; init; } = "Good";
        public string Title { get; init; } = string.Empty;
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

        public static DiskHealth Of(PhysicalDiskInfo disk)
        {
            var smart = disk.Smart;
            if (smart.OverallStatus.ValueOrDefault == SmartOverallStatus.NotSupported || (!smart.OverallStatus.HasValue && smart.Attributes.Count == 0))
                return new DiskHealth
                {
                    Tone = "Unknown",
                    Title = "État SMART non lisible",
                    Reasons = new[] { "Ce disque, ou son boîtier USB, ne transmet pas ses données SMART. Le test de surface reste possible." },
                };

            var bad = new List<string>();
            var warn = new List<string>();
            long Raw(Measured<long> value) => value.ValueOrDefault;

            if (smart.OverallStatus.ValueOrDefault == SmartOverallStatus.Failing) bad.Add("Le disque annonce lui-même sa défaillance (seuil constructeur franchi).");
            foreach (var attribute in smart.Attributes.Where(a => a.ThresholdExceeded))
                bad.Add(SmartCatalog.Describe(attribute.Id, attribute.Name).Name + " : seuil constructeur atteint.");
            if (Raw(smart.PendingSectors) > 0) bad.Add(Raw(smart.PendingSectors) + " secteur(s) illisible(s) en attente de remplacement.");
            if (Raw(smart.UncorrectableErrors) > 0) bad.Add(Raw(smart.UncorrectableErrors) + " secteur(s) irrécupérable(s).");
            if (Raw(smart.ReallocatedSectors) > 0)
                (Raw(smart.ReallocatedSectors) >= 50 ? bad : warn).Add(Raw(smart.ReallocatedSectors) + " secteur(s) déjà remplacé(s) par la réserve.");
            if (smart.WearPercent.HasValue && smart.WearPercent.Value >= 90) bad.Add("Mémoire usée à " + smart.WearPercent.Value.ToString("0") + " %.");
            else if (smart.WearPercent.HasValue && smart.WearPercent.Value >= 70) warn.Add("Mémoire usée à " + smart.WearPercent.Value.ToString("0") + " %.");
            if (smart.TemperatureCelsius.HasValue && smart.TemperatureCelsius.Value >= 60) bad.Add("Température de " + smart.TemperatureCelsius.Value + " °C.");
            else if (smart.TemperatureCelsius.HasValue && smart.TemperatureCelsius.Value >= 50) warn.Add("Température de " + smart.TemperatureCelsius.Value + " °C.");
            var crc = smart.Attributes.FirstOrDefault(a => a.Id == 199);
            if (crc != null && crc.Raw > 0) warn.Add(crc.Raw + " erreur(s) de transmission : câble ou connecteur à reprendre.");

            if (bad.Count > 0) return new DiskHealth { Tone = "Bad", Title = "Disque défaillant : sauvegarder sans attendre", Reasons = bad.Concat(warn).ToList() };
            if (warn.Count > 0) return new DiskHealth { Tone = "Warning", Title = "Disque à surveiller", Reasons = warn };
            return new DiskHealth { Tone = "Good", Title = "Disque en bon état", Reasons = new[] { "Aucun attribut critique dégradé." } };
        }
    }

    /// <summary>Une ligne du journal des autotests du disque.</summary>
    public sealed class SelfTestEntry
    {
        public string Kind { get; init; } = string.Empty;
        public string Result { get; init; } = string.Empty;
        public bool Passed { get; init; }
        public int LifetimeHours { get; init; }
        public long? FailingLba { get; init; }
    }

    /// <summary>L'état de l'autotest d'un disque ATA, et son journal.</summary>
    public sealed class SelfTestState
    {
        public bool Supported { get; init; } = true;
        public bool Running { get; init; }

        /// <summary>Part restante, de 0 à 100, pendant un test.</summary>
        public int RemainingPercent { get; init; }

        public string LastResult { get; init; } = string.Empty;
        public int ShortMinutes { get; init; }
        public int ExtendedMinutes { get; init; }
        public List<SelfTestEntry> Log { get; init; } = new List<SelfTestEntry>();

        /// <summary>Lit l'état dans les 512 octets de « SMART READ DATA ».</summary>
        public static SelfTestState FromSmartData(byte[] data, byte[]? log)
        {
            if (data == null || data.Length < 512) return new SelfTestState { Supported = false, LastResult = "Données SMART illisibles." };
            var status = data[363];
            var code = status >> 4;
            // Au-delà de 254 minutes, la durée de l'autotest long est écrite sur deux octets (375-376).
            var extended = data[373] == 0xFF ? data[375] | (data[376] << 8) : data[373];
            return new SelfTestState
            {
                // Bit 0 de l'octet 367 : autotests pris en charge.
                Supported = (data[367] & 0x01) != 0,
                Running = code == 15,
                RemainingPercent = code == 15 ? (status & 0x0F) * 10 : 0,
                LastResult = Describe(code),
                ShortMinutes = data[372],
                ExtendedMinutes = extended,
                Log = log == null ? new List<SelfTestEntry>() : ParseLog(log),
            };
        }

        /// <summary>Le journal des autotests (journal SMART 06h) : 21 entrées de 24 octets, la plus récente désignée par l'octet 508.</summary>
        public static List<SelfTestEntry> ParseLog(byte[] log)
        {
            var entries = new List<SelfTestEntry>();
            if (log.Length < 512) return entries;
            var newest = log[508];
            if (newest < 1 || newest > 21) return entries;
            for (var step = 0; step < 21; step++)
            {
                var index = ((newest - 1 - step) % 21 + 21) % 21;
                var at = 2 + index * 24;
                var kind = log[at];
                if (kind == 0) break;
                var code = log[at + 1] >> 4;
                entries.Add(new SelfTestEntry
                {
                    Kind = kind switch
                    {
                        1 or 0x81 => "Court",
                        2 or 0x82 => "Long",
                        3 or 0x83 => "Transport",
                        4 or 0x84 => "Sélectif",
                        _ => "Hors ligne",
                    },
                    Result = Describe(code),
                    Passed = code == 0,
                    LifetimeHours = log[at + 2] | (log[at + 3] << 8),
                    FailingLba = code >= 3 && code <= 8 ? (long)BitConverter.ToUInt32(log, at + 5) : (long?)null,
                });
            }
            return entries;
        }

        public static string Describe(int code) => code switch
        {
            0 => "réussi, aucune erreur",
            1 => "interrompu par l'ordinateur",
            2 => "interrompu (réinitialisation)",
            3 => "échec fatal",
            4 => "échec d'un élément inconnu",
            5 => "échec électrique",
            6 => "échec mécanique (asservissement)",
            7 => "échec de lecture",
            8 => "échec : disque endommagé",
            15 => "en cours",
            _ => "état inconnu",
        };
    }

    /// <summary>
    /// Le test de surface, comme HDDScan : chaque bloc lu est classé selon son temps de réponse,
    /// et la carte agrège ces classes par case (la pire l'emporte).
    /// </summary>
    public sealed class SurfaceScan
    {
        /// <summary>Les classes de temps de réponse, en millisecondes, et la dernière pour les erreurs.</summary>
        public static readonly int[] Limits = { 5, 20, 50, 150, 500 };

        public const byte ErrorClass = 6;

        /// <summary>Pour la relecture JSON du résultat transmis par l'hôte élevé.</summary>
        public SurfaceScan() : this(1)
        {
        }

        public SurfaceScan(int cells)
        {
            Cells = new byte[Math.Max(1, cells)];
            Histogram = new long[7];
        }

        public byte[] Cells { get; set; }

        /// <summary>Nombre de blocs par classe : &lt;5, &lt;20, &lt;50, &lt;150, &lt;500, ≥500 ms, erreur.</summary>
        public long[] Histogram { get; set; }

        public List<long> ErrorOffsets { get; set; } = new List<long>();
        public long BytesRead { get; set; }
        public long DiskBytes { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Full { get; set; }
        public bool Cancelled { get; set; }

        public static byte Classify(double milliseconds)
        {
            for (byte index = 0; index < Limits.Length; index++)
                if (milliseconds < Limits[index]) return index;
            return (byte)Limits.Length;
        }

        /// <summary>Note un bloc lu à la position donnée (fraction du disque, 0 à 1).</summary>
        public void Add(double position, double milliseconds, bool error, long offset)
        {
            var category = error ? ErrorClass : Classify(milliseconds);
            Histogram[category]++;
            if (error && ErrorOffsets.Count < 500) ErrorOffsets.Add(offset);
            var cell = (int)Math.Min(Cells.Length - 1, Math.Max(0, position * Cells.Length));
            // Case jamais lue : 0 et « < 5 ms » se confondent ; on code donc la classe + 1, 0 restant « non lu ».
            var coded = (byte)(category + 1);
            if (coded > Cells[cell]) Cells[cell] = coded;
        }

        public long SlowBlocks => Histogram[4] + Histogram[5];
        public long Errors => Histogram[ErrorClass];

        public string Verdict()
        {
            if (Errors > 0) return Errors + " bloc(s) illisible(s) : le disque a des secteurs défectueux. Sauvegarder les données, puis le remplacer.";
            if (Histogram[5] > 0) return Histogram[5] + " bloc(s) très lent(s) (plus d'une demi-seconde) : des secteurs faiblissent. À surveiller de près.";
            if (Histogram[4] > 0) return Histogram[4] + " bloc(s) lent(s), aucun illisible : surface correcte, quelques zones à relire plus tard.";
            return "Surface saine : aucun bloc lent ni illisible.";
        }
    }
}
