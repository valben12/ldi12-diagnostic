using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LDI12.Core.Diagnostics;

namespace LDI12.Engine.Profile
{
    /// <summary>
    /// Description d'un seuil : ce qu'il vaut, ce qu'il change, et dans quelles bornes il a un sens.
    /// </summary>
    public sealed class ThresholdDescriptor
    {
        public string Key { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        /// <summary>Ce que déplacer ce seuil change concrètement. Affiché sous le champ.</summary>
        public string Effect { get; init; } = string.Empty;

        public string Unit { get; init; } = string.Empty;

        public DiagnosticCategory Category { get; init; }

        /// <summary>Bornes de saisie. Hors de là, le seuil ne décrirait plus rien de réel.</summary>
        public double Minimum { get; init; }

        public double Maximum { get; init; }

        /// <summary>Valeur du barème d'origine, pour pouvoir y revenir.</summary>
        public double Default { get; init; }
    }

    /// <summary>
    /// Catalogue des seuils modifiables.
    /// </summary>
    /// <remarks>
    /// Chaque seuil de <see cref="Thresholds"/> y figure, avec un libellé français et l'effet de
    /// son déplacement. Un test vérifie qu'aucune propriété n'est absente : un seuil ajouté au
    /// barème et oublié ici serait invisible dans l'écran de réglages, donc impossible à
    /// ajuster, sans que rien ne le signale.
    /// <para>
    /// La lecture et l'écriture passent par la réflexion plutôt que par un interrupteur géant :
    /// c'est le seul moyen d'ajouter un seuil en une ligne, ici, sans toucher au reste.
    /// </para>
    /// </remarks>
    public static class ThresholdCatalog
    {
        private const DiagnosticCategory Sto = DiagnosticCategory.Storage;
        private const DiagnosticCategory Hw = DiagnosticCategory.Hardware;
        private const DiagnosticCategory Win = DiagnosticCategory.Windows;
        private const DiagnosticCategory Net = DiagnosticCategory.Network;
        private const DiagnosticCategory Sec = DiagnosticCategory.Security;
        private const DiagnosticCategory Prf = DiagnosticCategory.Performance;

        private static readonly IReadOnlyList<ThresholdDescriptor> Entries = Build();

        public static IReadOnlyList<ThresholdDescriptor> All => Entries;

        public static IReadOnlyList<ThresholdDescriptor> For(DiagnosticCategory category)
        {
            var kept = new List<ThresholdDescriptor>();
            foreach (var entry in Entries)
                if (entry.Category == category) kept.Add(entry);
            return kept;
        }

        /// <summary>Valeur courante d'un seuil, quel que soit son type numérique.</summary>
        public static double Read(Thresholds thresholds, string key)
        {
            var property = Property(key);
            var value = property.GetValue(thresholds);
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Produit un nouveau jeu de seuils avec une valeur remplacée.
        /// </summary>
        /// <remarks>
        /// Recopie plutôt que mutation : <see cref="Thresholds"/> est immuable comme tout le
        /// modèle, et un barème qui changerait sous les pieds d'une analyse en cours rendrait un
        /// score inexplicable.
        /// </remarks>
        public static Thresholds With(Thresholds thresholds, string key, double value)
        {
            if (thresholds == null) throw new ArgumentNullException(nameof(thresholds));

            var target = Property(key);
            var copy = new Thresholds();

            foreach (var property in typeof(Thresholds).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite) continue;

                var source = property.Name == target.Name
                    ? Convert.ChangeType(Clamp(key, value), property.PropertyType, CultureInfo.InvariantCulture)
                    : property.GetValue(thresholds);

                property.SetValue(copy, source);
            }

            return copy;
        }

        /// <summary>Ramène une valeur dans les bornes du seuil : un champ de saisie accepte tout.</summary>
        public static double Clamp(string key, double value)
        {
            var descriptor = Find(key);
            if (descriptor == null) return value;

            return Math.Max(descriptor.Minimum, Math.Min(descriptor.Maximum, value));
        }

        public static ThresholdDescriptor? Find(string key)
        {
            foreach (var entry in Entries)
                if (string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry;
            return null;
        }

        private static PropertyInfo Property(string key)
            => typeof(Thresholds).GetProperty(key, BindingFlags.Public | BindingFlags.Instance)
               ?? throw new ArgumentException("Seuil inconnu : " + key, nameof(key));

        private static IReadOnlyList<ThresholdDescriptor> Build()
        {
            var defaults = new Thresholds();
            var entries = new List<ThresholdDescriptor>();

            void Add(string key, DiagnosticCategory category, string label, string unit,
                     double minimum, double maximum, string effect)
            {
                var property = typeof(Thresholds).GetProperty(key, BindingFlags.Public | BindingFlags.Instance)
                               ?? throw new InvalidOperationException("Seuil inexistant : " + key);

                entries.Add(new ThresholdDescriptor
                {
                    Key = key,
                    Category = category,
                    Label = label,
                    Unit = unit,
                    Minimum = minimum,
                    Maximum = maximum,
                    Effect = effect,
                    Default = Convert.ToDouble(property.GetValue(defaults), CultureInfo.InvariantCulture),
                });
            }

            // ---------- Stockage ----------
            Add(nameof(Thresholds.SystemVolumeFreePercentWarning), Sto,
                "Espace libre du volume système : avertissement", "%", 2, 50,
                "En deçà, le volume qui porte Windows est signalé à surveiller.");
            Add(nameof(Thresholds.SystemVolumeFreePercentProblem), Sto,
                "Espace libre du volume système : problème", "%", 1, 40,
                "En deçà, les mises à jour de Windows commencent à échouer.");
            Add(nameof(Thresholds.SystemVolumeFreePercentCritical), Sto,
                "Espace libre du volume système : critique", "%", 1, 30,
                "En deçà, Windows ne peut plus fonctionner normalement.");
            Add(nameof(Thresholds.DataVolumeFreePercentWarning), Sto,
                "Espace libre d'un volume de données", "%", 1, 40,
                "Volumes autres que celui de Windows : le seuil peut être plus bas sans conséquence.");
            Add(nameof(Thresholds.ReallocatedSectorsWarning), Sto,
                "Secteurs réalloués : avertissement", "secteurs", 0, 1000,
                "Premier signe d'usure du support. Un seul secteur est déjà notable sur un disque récent.");
            Add(nameof(Thresholds.ReallocatedSectorsProblem), Sto,
                "Secteurs réalloués : problème", "secteurs", 1, 5000,
                "Au-delà, la sauvegarde devient prioritaire sur toute autre intervention.");
            Add(nameof(Thresholds.ReallocatedSectorsCritical), Sto,
                "Secteurs réalloués : critique", "secteurs", 1, 20000,
                "Au-delà, le disque est considéré en fin de vie.");
            Add(nameof(Thresholds.PendingSectorsWarning), Sto,
                "Secteurs en attente : avertissement", "secteurs", 0, 500,
                "Secteurs que le disque n'arrive pas à relire : plus alarmant qu'un secteur déjà réalloué.");
            Add(nameof(Thresholds.PendingSectorsCritical), Sto,
                "Secteurs en attente : critique", "secteurs", 1, 5000,
                "Au-delà, des fichiers sont probablement déjà illisibles.");
            Add(nameof(Thresholds.UncorrectableErrorsWarning), Sto,
                "Erreurs non corrigibles", "erreurs", 0, 500,
                "Données que le disque a renoncé à restituer. Un seul cas suffit à alerter.");
            Add(nameof(Thresholds.DiskTemperatureWarning), Sto,
                "Température de disque : avertissement", "°C", 30, 80,
                "Au-delà, la durée de vie du disque se raccourcit sensiblement.");
            Add(nameof(Thresholds.DiskTemperatureProblem), Sto,
                "Température de disque : problème", "°C", 35, 90,
                "Au-delà, la ventilation du boîtier est en cause.");
            Add(nameof(Thresholds.DiskTemperatureCritical), Sto,
                "Température de disque : critique", "°C", 40, 100,
                "Au-delà, le disque se protège en ralentissant, voire s'arrête.");
            Add(nameof(Thresholds.SystemFilesSharePercent), Sto,
                "Fichiers cachés de Windows : part du volume", "%", 3, 60,
                "Au-delà de cette part du disque système, les fichiers que Windows cache sont nommés avec leur taille.");
            Add(nameof(Thresholds.PageFileTimesMemory), Sto,
                "Fichier d'échange : multiple de la mémoire", "fois", 1, 20,
                "Au-delà, la taille a été fixée à la main : Windows ne dépasse jamais trois fois la mémoire installée.");
            Add(nameof(Thresholds.PreviousWindowsDaysWarning), Sto,
                "Ancienne installation de Windows", "jours", 10, 365,
                "Au-delà, la suppression automatique de Windows.old a échoué et la place reste immobilisée.");

            Add(nameof(Thresholds.SsdWearWarning), Sto,
                "Usure de SSD : avertissement", "%", 50, 99,
                "Pourcentage de la réserve d'écriture consommée.");
            Add(nameof(Thresholds.SsdWearProblem), Sto,
                "Usure de SSD : problème", "%", 60, 99,
                "Au-delà, le remplacement mérite d'être planifié.");
            Add(nameof(Thresholds.SsdWearCritical), Sto,
                "Usure de SSD : critique", "%", 70, 100,
                "Au-delà, le SSD peut passer en lecture seule sans prévenir.");
            Add(nameof(Thresholds.DiskPowerOnHoursWarning), Sto,
                "Heures de fonctionnement d'un disque", "heures", 5000, 100000,
                "Environ 4,5 ans à 40 000 heures. Un disque plus vieux n'est pas défaillant, mais il a vécu.");

            // ---------- Matériel ----------
            Add(nameof(Thresholds.MemoryUsageWarning), Hw,
                "Occupation mémoire : avertissement", "%", 50, 99,
                "Mesure instantanée : un seuil trop bas se déclenche sur toute machine qui travaille.");
            Add(nameof(Thresholds.MemoryUsageProblem), Hw,
                "Occupation mémoire : problème", "%", 60, 100,
                "Au-delà, Windows compense sur le disque et tout ralentit.");
            Add(nameof(Thresholds.MinimumMemoryBytesModernWindows), Hw,
                "Mémoire minimale sous Windows 10 / 11", "octets", 1073741824, 34359738368,
                "En deçà, la machine est déclarée sous-dimensionnée pour son système.");
            Add(nameof(Thresholds.RecommendedMemoryBytesModernWindows), Hw,
                "Mémoire recommandée sous Windows 10 / 11", "octets", 2147483648, 68719476736,
                "En deçà, la mémoire est signalée comme facteur limitant.");
            Add(nameof(Thresholds.CpuUsageWarning), Hw,
                "Charge processeur : avertissement", "%", 40, 100,
                "Mesure prise sur une fenêtre de moins d'une seconde : à interpréter comme telle.");
            Add(nameof(Thresholds.MinimumLogicalCoresModernWindows), Hw,
                "Threads minimaux sous Windows 10 / 11", "threads", 1, 32,
                "En deçà, le processeur est déclaré facteur limitant.");
            Add(nameof(Thresholds.ReservedSerialPortsWarning), Hw,
                "Numéros de port série retenus", "numéros", 1, 200,
                "Au-delà, les nouveaux appareils série sortent de la plage COM1 à COM9 que les logiciels anciens acceptent.");

            Add(nameof(Thresholds.DisplayRefreshShortfallPercent), Hw,
                "Fréquence d'écran : part du maximum", "%", 25, 100,
                "En deçà de cette part de ce que le pilote propose, l'écran est signalé comme bridé.");
            Add(nameof(Thresholds.DisplayEffectiveDpiTiny), Hw,
                "Densité perçue : texte très petit", "points par pouce", 110, 250,
                "Au-delà, le texte devient difficile à lire et une mise à l'échelle est proposée.");
            Add(nameof(Thresholds.DisplayEffectiveDpiLarge), Hw,
                "Densité perçue : texte très grand", "points par pouce", 40, 95,
                "En deçà, tout paraît grand et l'espace de travail se réduit inutilement.");

            Add(nameof(Thresholds.BiosAgeMonthsWarning), Hw,
                "Ancienneté du firmware", "mois", 12, 240,
                "Un firmware ancien n'est pas un défaut ; c'est une piste quand le matériel se comporte mal.");
            Add(nameof(Thresholds.GpuDriverAgeMonthsWarning), Hw,
                "Ancienneté du pilote graphique", "mois", 6, 120,
                "Au-delà, les logiciels récents peuvent mal se comporter.");
            Add(nameof(Thresholds.BatteryWearWarning), Hw,
                "Usure de batterie : avertissement", "%", 10, 90,
                "Capacité perdue par rapport au neuf.");
            Add(nameof(Thresholds.BatteryWearProblem), Hw,
                "Usure de batterie : problème", "%", 20, 95,
                "Au-delà, l'autonomie annoncée n'a plus rien à voir avec la réalité.");
            Add(nameof(Thresholds.CpuTemperatureWarning), Hw,
                "Température du processeur : avertissement", "°C", 50, 110,
                "Un processeur moderne travaille normalement jusqu'à 85 °C sous charge.");
            Add(nameof(Thresholds.CpuTemperatureProblem), Hw,
                "Température du processeur : problème", "°C", 60, 120,
                "Au-delà, le processeur se bride pour se protéger et la machine ralentit.");
            Add(nameof(Thresholds.ThermalZoneWarning), Hw,
                "Zone thermique : avertissement", "°C", 40, 110,
                "Plus haut que pour un disque : une zone peut suivre l'étage d'alimentation, chaud par nature.");
            Add(nameof(Thresholds.ThermalZoneProblem), Hw,
                "Zone thermique : problème", "°C", 50, 120,
                "Au-delà, un dépoussiérage s'impose avant toute autre intervention.");

            Add(nameof(Thresholds.CorrectedHardwareErrorsWarning), Hw,
                "Erreurs matérielles corrigées : avertissement", "erreurs", 1, 500,
                "Sur quatre-vingt-dix jours. Une erreur isolée arrive ; leur retour annonce la suite.");
            Add(nameof(Thresholds.CorrectedHardwareErrorsProblem), Hw,
                "Erreurs matérielles corrigées : problème", "erreurs", 2, 5000,
                "Au-delà, le composant corrige en continu et finira par ne plus y arriver.");

            // ---------- Windows ----------
            Add(nameof(Thresholds.UptimeDaysWarning), Win,
                "Temps de fonctionnement sans redémarrage", "jours", 3, 365,
                "Au-delà, beaucoup de correctifs restent en attente d'un redémarrage.");
            Add(nameof(Thresholds.UpdateDelayDaysWarning), Win,
                "Retard de mise à jour : avertissement", "jours", 7, 365,
                "Délai depuis le dernier correctif installé.");
            Add(nameof(Thresholds.UpdateDelayDaysProblem), Win,
                "Retard de mise à jour : problème", "jours", 14, 730,
                "Au-delà, la machine accumule des failles corrigées ailleurs depuis longtemps.");
            Add(nameof(Thresholds.WindowsSupportEndingSoonDays), Win,
                "Fin de support annoncée à l'avance", "jours", 30, 730,
                "Délai avant la fin de support à partir duquel la version installée est signalée.");
            Add(nameof(Thresholds.ClockBehindDaysProblem), Win,
                "Retard de l'horloge", "jours", 1, 365,
                "Au-delà de ce retard sur les dates venues de l'extérieur de la machine, l'horloge est déclarée fausse.");
            Add(nameof(Thresholds.ClockSyncStaleDaysWarning), Win,
                "Ancienneté de la remise à l'heure", "jours", 7, 730,
                "Au-delà, plus rien ne surveille l'horloge, sans qu'elle soit forcément fausse.");

            Add(nameof(Thresholds.UnusedProfileMonthsWarning), Win,
                "Profil inutilisé", "mois", 3, 120,
                "Au-delà, un profil qui n'a plus ouvert de session est signalé, piste d'espace disque, jamais une suppression proposée.");

            // ---------- Réseau ----------
            Add(nameof(Thresholds.LinkErrorsPerMillionWarning), Net,
                "Trames en erreur : avertissement", "par million", 10, 100000,
                "Au-delà, la liaison perd des trames : câble, connecteur ou port en cause. Le niveau de fond d'une carte saine tourne autour de cent.");
            Add(nameof(Thresholds.LinkErrorsPerMillionProblem), Net,
                "Trames en erreur : problème", "par million", 100, 500000,
                "Au-delà, la liaison travaille deux fois et le débit utile s'effondre.");
            Add(nameof(Thresholds.LinkDiscardsPerMillionWarning), Net,
                "Trames écartées : avertissement", "par million", 100, 500000,
                "Trames valides que la machine n'a pas su traiter à temps : pilote ou charge, pas câble.");
            Add(nameof(Thresholds.CriticalEventsWarning), Win,
                "Erreurs critiques : avertissement", "événements", 1, 100,
                "Sur la fenêtre d'analyse des journaux.");
            Add(nameof(Thresholds.CriticalEventsProblem), Win,
                "Erreurs critiques : problème", "événements", 2, 500,
                "Au-delà, la machine a un défaut récurrent et non un incident isolé.");
            Add(nameof(Thresholds.UnexpectedShutdownsWarning), Win,
                "Arrêts inattendus", "arrêts", 1, 100,
                "Coupures de courant, plantages, arrêts forcés : les trois se ressemblent dans les journaux.");
            Add(nameof(Thresholds.DiskEventsProblem), Win,
                "Erreurs disque dans les journaux", "événements", 1, 50,
                "Windows enregistre rarement ces erreurs à tort : le seuil est volontairement bas.");
            Add(nameof(Thresholds.RecurringEventOccurrencesWarning), Win,
                "Répétitions d'une même erreur", "occurrences", 5, 1000,
                "En deçà, une erreur qui se répète n'est pas encore un motif de constat.");

            Add(nameof(Thresholds.RepeatedFailureCount), Win,
                "Plantages d'un même programme", "occurrences", 2, 100,
                "En deçà, un programme qui plante relève de l'accident et non de la panne.");
            Add(nameof(Thresholds.RecentInstabilityDays), Win,
                "Période considérée comme récente", "jours", 3, 60,
                "Ce qui s'est passé dans cette fenêtre est ce que le client vit encore aujourd'hui.");
            Add(nameof(Thresholds.ChangeCorrelationDays), Win,
                "Rapprochement avec un changement", "jours", 1, 30,
                "Fenêtre dans laquelle un changement précédant les premiers plantages est signalé " +
                "comme coïncidence de dates, jamais comme cause.");

            Add(nameof(Thresholds.FrequentTaskMinutes), Win,
                "Répétition d'une tâche planifiée", "minutes", 1, 240,
                "En deçà, une tâche qui se répète empêche la machine de se mettre au repos.");

            // ---------- Performances ----------
            Add(nameof(Thresholds.StartupItemsWarning), Prf,
                "Programmes au démarrage : avertissement", "programmes", 3, 60,
                "Chacun allonge le démarrage et consomme ensuite des ressources.");
            Add(nameof(Thresholds.StartupItemsProblem), Prf,
                "Programmes au démarrage : problème", "programmes", 5, 100,
                "Au-delà, réduire la liste est l'action la plus rentable sur la lenteur.");
            Add(nameof(Thresholds.BootDurationSecondsWarning), Prf,
                "Durée de démarrage : avertissement", "secondes", 15, 600,
                "Seuil de Windows lui-même : au-delà, il qualifie le démarrage de dégradé.");
            Add(nameof(Thresholds.BootDurationSecondsProblem), Prf,
                "Durée de démarrage : problème", "secondes", 30, 900,
                "Au-delà, la machine met plus de deux minutes à devenir utilisable.");
            Add(nameof(Thresholds.BootMeasurementMaxAgeDays), Prf,
                "Âge maximal d'un relevé de démarrage", "jours", 7, 365,
                "Au-delà, le relevé ne décrit plus la machine d'aujourd'hui et n'est pas exploité.");
            Add(nameof(Thresholds.CommitRatioWarning), Prf,
                "Mémoire réclamée / installée : avertissement", "%", 50, 300,
                "Au-dessus de 100 %, la machine tient sa charge grâce au fichier d'échange.");
            Add(nameof(Thresholds.CommitRatioProblem), Prf,
                "Mémoire réclamée / installée : problème", "%", 80, 500,
                "Au-delà, ajouter de la mémoire est la seule vraie réponse.");
            Add(nameof(Thresholds.SingleProcessMemoryShareWarning), Prf,
                "Part de mémoire d'un seul programme", "%", 10, 90,
                "Sert à nommer le programme dominant, pas à le juger.");

            Add(nameof(Thresholds.ProcessorThrottleProblemPercent), Prf,
                "Bridage du processeur : problème", "%", 10, 99,
                "En deçà, le plan d'alimentation retient la machine plus qu'il ne la ménage.");

            // ---------- Réseau ----------
            Add(nameof(Thresholds.PacketLossWarning), Net,
                "Perte de paquets : avertissement", "%", 1, 50,
                "Une ligne qui perd quelques pour cent hache la visioconférence sans couper la navigation.");
            Add(nameof(Thresholds.PacketLossProblem), Net,
                "Perte de paquets : problème", "%", 5, 100,
                "Au-delà, la liaison est inutilisable pour la voix et la vidéo.");
            Add(nameof(Thresholds.GatewayLatencyWarningMs), Net,
                "Latence vers la box", "ms", 2, 200,
                "Au-delà, le réseau local lui-même est en cause, câble, Wi-Fi, saturation.");
            Add(nameof(Thresholds.InternetLatencyWarningMs), Net,
                "Latence vers Internet", "ms", 20, 1000,
                "Dépend beaucoup du type de raccordement : plus élevé en ADSL ou en 4G qu'en fibre.");
            Add(nameof(Thresholds.JitterWarningMs), Net,
                "Gigue", "ms", 5, 300,
                "Variation d'un temps de réponse au suivant : c'est elle qui hache la voix.");
            Add(nameof(Thresholds.PathMtuWarning), Net,
                "Taille de paquet attendue", "octets par paquet", 576, 9000,
                "1500 sur une liaison Ethernet normale. En deçà, un tunnel ou une box fragmente.");
            Add(nameof(Thresholds.WifiSignalWarningPercent), Net,
                "Signal Wi-Fi : avertissement", "%", 10, 90,
                "En deçà, le débit s'effondre bien avant que la connexion ne tombe.");
            Add(nameof(Thresholds.WifiSignalProblemPercent), Net,
                "Signal Wi-Fi : problème", "%", 5, 80,
                "En deçà, les coupures deviennent régulières.");
            Add(nameof(Thresholds.WifiSameChannelWarning), Net,
                "Réseaux voisins sur le canal : avertissement", "réseaux", 1, 30,
                "Au-delà, le temps de parole se partage : le débit chute sans que le signal bouge.");
            Add(nameof(Thresholds.WifiSameChannelProblem), Net,
                "Réseaux voisins sur le canal : problème", "réseaux", 2, 40,
                "Seuil du canal saturé, typique d'un immeuble en 2,4 GHz.");
            Add(nameof(Thresholds.WifiRateShareWarningPercent), Net,
                "Débit négocié : avertissement", "%", 5, 100,
                "Part du débit qu'atteint une carte à une antenne sur la même norme.");
            Add(nameof(Thresholds.WifiRateShareProblemPercent), Net,
                "Débit négocié : problème", "%", 1, 90,
                "En deçà, la liaison est bridée par des perturbations ou par la box.");

            // ---------- Sécurité ----------
            Add(nameof(Thresholds.SignatureAgeDaysWarning), Sec,
                "Âge des signatures antivirus : avertissement", "jours", 1, 90,
                "Au-delà, la protection ne connaît pas les menaces apparues depuis.");
            Add(nameof(Thresholds.SignatureAgeDaysProblem), Sec,
                "Âge des signatures antivirus : problème", "jours", 3, 365,
                "Cas typique d'une machine restée éteinte plusieurs mois.");
            Add(nameof(Thresholds.AdministratorAccountsWarning), Sec,
                "Comptes administrateurs actifs", "comptes", 1, 20,
                "Au-delà, le constat est informatif : sur un poste familial, c'est courant.");

            return entries;
        }
    }
}
