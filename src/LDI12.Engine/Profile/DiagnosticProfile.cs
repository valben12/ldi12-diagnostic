using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Engine.Profile
{
    /// <summary>
    /// Barème et seuils du diagnostic, entièrement externalisés.
    /// </summary>
    /// <remarks>
    /// Rien n'est codé en dur dans les règles : un seuil ou une pénalité se modifie ici, sans
    /// recompiler. Le profil est enregistré dans chaque diagnostic, ce qui rend une analyse
    /// reproductible et une comparaison avant / après honnête : un score qui bouge parce que le
    /// barème a changé n'apprend rien sur la machine.
    /// </remarks>
    public sealed class DiagnosticProfile
    {
        public string Version { get; init; } = "1.0";

        /// <summary>Poids nominal de chaque dimension. La somme fait 100.</summary>
        public IReadOnlyDictionary<DiagnosticCategory, double> Weights { get; init; } =
            new Dictionary<DiagnosticCategory, double>
            {
                { DiagnosticCategory.Hardware, 15 },
                { DiagnosticCategory.Storage, 25 },
                { DiagnosticCategory.Windows, 25 },
                { DiagnosticCategory.Security, 15 },
                { DiagnosticCategory.Network, 10 },
                { DiagnosticCategory.Performance, 10 },
            };

        /// <summary>
        /// Pénalité par défaut selon la gravité. Sans plafond de sévérité, un disque en train de
        /// mourir donnerait encore 82/100 sur une machine par ailleurs saine.
        /// </summary>
        public IReadOnlyDictionary<Severity, int> DefaultPenalties { get; init; } =
            new Dictionary<Severity, int>
            {
                { Severity.Info, 0 },
                { Severity.Warning, 5 },
                { Severity.Problem, 12 },
                { Severity.Critical, 25 },
            };

        /// <summary>
        /// Pénalités spécifiques, par règle <b>et par gravité</b>.
        /// </summary>
        /// <remarks>
        /// La gravité fait partie de la clé, et ce n'était pas le cas au premier jet : une même
        /// règle peut se déclencher à plusieurs niveaux, et un barème indexé sur le seul
        /// identifiant faisait payer à un simple constat d'information le prix d'un problème
        /// avéré. Une passerelle qui ignore les requêtes ICMP (comportement normal) coûtait
        /// ainsi 18 points de réseau.
        /// </remarks>
        public IReadOnlyDictionary<string, PenaltyOverride> PenaltyOverrides { get; init; } =
            new Dictionary<string, PenaltyOverride>(StringComparer.OrdinalIgnoreCase)
            {
                // Stockage : l'inaction fait perdre des données, le barème le reflète.
                { "STO-001", new PenaltyOverride { Warning = 8, Problem = 18, Critical = 25 } },
                { "STO-003", new PenaltyOverride { Warning = 8, Critical = 40 } },
                { "STO-004", new PenaltyOverride { Warning = 8, Problem = 18, Critical = 30 } },
                { "STO-005", new PenaltyOverride { Warning = 12, Critical = 30 } },
                { "STO-006", new PenaltyOverride { Warning = 10, Problem = 20, Critical = 30 } },

                // Windows : la corruption bloque tout le reste, y compris les réparations.
                { "WIN-001", new PenaltyOverride { Problem = 20 } },
                { "WIN-002", new PenaltyOverride { Problem = 18 } },
                { "WIN-006", new PenaltyOverride { Problem = 18 } },
                { "EVT-001", new PenaltyOverride { Warning = 10, Problem = 22 } },
                { "DRV-002", new PenaltyOverride { Problem = 15 } },
                { "SVC-001", new PenaltyOverride { Problem = 10 } },

                // Réseau : seul un étage réellement en panne coûte cher.
                { "NET-003", new PenaltyOverride { Problem = 18 } },
                { "NET-004", new PenaltyOverride { Problem = 20 } },
            };

        /// <summary>
        /// Plafond de points cumulés par famille de constats. Cinq volumes pleins restent un
        /// seul problème d'espace disque, pas cinq fois la même pénalité.
        /// </summary>
        public IReadOnlyDictionary<string, int> FamilyCaps { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "storage.space", 30 },
                { "storage.smart", 45 },
                { "storage.temperature", 12 },
                { "windows.integrity", 30 },
                { "events.errors", 25 },
                { "devices.problems", 20 },
                { "services.essential", 20 },
                { "network.connectivity", 30 },
                { "memory.configuration", 12 },
                { "startup.load", 15 },

                // Une tâche en échec, une tâche orpheline et une tâche trop fréquente décrivent
                // presque toujours le même logiciel mal désinstallé.
                { "startup.tasks", 15 },

                // Un programme qui plante, un service qui tombe et la dégradation qui les date
                // sont trois façons de voir les mêmes pannes : le cumul les compterait trois fois.
                { "stability.programs", 20 },

                // Une protection qui échoue, un environnement de récupération absent et l'absence
                // totale de filet décrivent le même risque : celui de ne pas pouvoir revenir en
                // arrière. Le cumul le compterait trois fois.
                { "safety.net", 20 },

                // Une version qui n'est plus suivie et une fin de support qui approche ne peuvent
                // pas être vraies en même temps : le plafond n'existe que pour garantir qu'aucune
                // évolution du calendrier ne les fasse s'additionner un jour.
                { "windows.support", 30 },

                // Un blocage matériel et un réglage bloquant décrivent la même impossibilité vue
                // de deux côtés, et l'un des deux se tait dès que l'autre parle.
                { "windows.upgrade", 12 },

                // Un profil provisoire est presque toujours accompagné du profil mis de côté qui
                // l'a provoqué : c'est un seul incident, vu de ses deux bouts.
                { "windows.profiles", 30 },

                // Une horloge fausse et un service de temps désactivé sont la cause et l'effet
                // d'un seul problème : le cumul en ferait deux.
                { "windows.clock", 20 },

                // Un port en défaut et une réserve de numéros saturée sont deux façons de ne pas
                // joindre le même appareil.
                { "hardware.serial", 10 },

                // Services exposés et partage ouvert décrivent la même surface visible depuis le
                // réseau : le cumul la compterait deux fois.
                { "network.exposure", 20 },

                // Des routes de sortie concurrentes et des routes posées à la main sont
                // presque toujours le même tunnel mal désinstallé, vu de ses deux bouts.
                { "network.routing", 20 },

                // Fichier hosts, catalogue Winsock et serveur de noms décrivent la même chose :
                // quelqu'un s'est glissé entre cette machine et ce qu'elle cherche à joindre.
                { "network.names", 30 },

                // Un protocole de partage obsolète, une découverte à l'arrêt et des noms courts
                // coupés se répondent : c'est un seul voisinage réseau qui ne fonctionne pas.
                { "network.sharing", 15 },

                // Une définition qui n'est pas la bonne et une fréquence trop basse sont deux
                // symptômes du même réglage d'affichage laissé de côté.
                { "display.mode", 10 },

                // Fichiers cachés, fichier d'échange, ancienne installation et fichiers oubliés
                // décrivent le même disque plein sous quatre angles : le cumul en ferait quatre
                // problèmes distincts là où le technicien n'a qu'un disque à libérer.
                { "storage.occupancy", 20 },

                // Trames abîmées et trames écartées se comptent sur la même liaison : une carte
                // en difficulté produit souvent les deux, pour une seule cause à corriger.
                { "network.link", 25 },

                // Le proxy de la session et celui de la machine décrivent le même réglage vu de
                // deux côtés.
                { "network.proxy", 20 },

                // Le classement d'un réseau et sa portée viennent du même service, et un réseau
                // public sans Internet ne doit pas compter deux fois.
                { "network.location", 20 },

                // Un spouleur arrêté, une imprimante hors connexion et une file qui s'allonge
                // sont le plus souvent la même panne vue à trois endroits.
                { "printing", 15 },

                { "audio", 12 },

                // Un processeur bridé et un plan d'économie d'énergie sont le plus souvent le
                // même réglage vu deux fois : le cumul serait une double peine pour un seul clic.
                { "power.settings", 25 },

                { "storage.maintenance", 12 },

                // Un processeur chaud et une carte mère chaude décrivent le même boîtier
                // encrassé : le cumul serait une double peine pour un seul dépoussiérage.
                { "thermal", 18 },

                // Une liaison qui perd des paquets et qui est irrégulière décrit un seul défaut
                // de ligne vu sous deux angles : le cumul serait une double peine.
                { "network.quality", 25 },

                // Signal faible, canal encombré et débit effondré décrivent la même mauvaise
                // liaison sans fil : un seul déplacement de box les corrige souvent tous.
                { "network.wireless", 20 },

                // « Antivirus expiré » et « signatures anciennes » se disent souvent ensemble et
                // désignent la même protection défaillante.
                { "security.antivirus", 30 },
                { "security.accounts", 25 },

                // Trois logiciels abandonnés et deux versions de Java de trop décrivent la même
                // machine dont personne n'a fait le ménage : le cumul serait une double peine.
                { "software.support", 20 },

                // Les utilitaires d'optimisation ne sont qu'une information : le plafond existe
                // pour que la règle du barème reste vraie sans exception, pas pour retenir une
                // pénalité qui n'est jamais appliquée.
                { "software.utilities", 5 },

                // Erreurs corrigées, erreurs irrécupérables et test mémoire en échec décrivent
                // le même composant qui lâche : le cumul serait une double peine pour une seule
                // barrette à remplacer.
                { "hardware.errors", 40 },

                // Les rapports de plantage ne disent rien de l'état de la machine : ils disent
                // ce qu'on pourra en apprendre. Le plafond reste bas pour cette raison.
                { "hardware.reports", 10 },
            };

        /// <summary>Plafond appliqué à une dimension portant au moins un constat de cette gravité.</summary>
        public IReadOnlyDictionary<Severity, int> SeverityCaps { get; init; } =
            new Dictionary<Severity, int>
            {
                { Severity.Critical, 40 },
                { Severity.Problem, 70 },
            };

        /// <summary>Plafond du score global dès qu'un constat critique existe, toutes dimensions confondues.</summary>
        public int GlobalCriticalCap { get; init; } = 60;

        public Thresholds Limits { get; init; } = new Thresholds();

        /// <summary>
        /// Points retirés par un constat.
        /// </summary>
        /// <remarks>
        /// Règle absolue : un constat d'information ne coûte jamais de point, quel que soit le
        /// barème. La sévérité <c>Info</c> est définie comme « constat notable, aucune action » :
        /// lui attribuer un coût contredirait sa définition et ferait chuter un score sans qu'aucun
        /// problème n'existe.
        /// </remarks>
        public int PenaltyFor(string ruleId, Severity severity)
        {
            if (severity == Severity.Info) return 0;

            if (PenaltyOverrides.TryGetValue(ruleId, out var specific))
            {
                var overridden = specific.For(severity);
                if (overridden.HasValue) return overridden.Value;
            }

            return DefaultPenalties.TryGetValue(severity, out var bySeverity) ? bySeverity : 0;
        }

        public double WeightOf(DiagnosticCategory category)
            => Weights.TryGetValue(category, out var weight) ? weight : 0d;

        public static DiagnosticProfile Default { get; } = new DiagnosticProfile();
    }

    /// <summary>
    /// Pénalités d'une règle, déclarées gravité par gravité. Une gravité laissée à <c>null</c>
    /// retombe sur le barème par défaut.
    /// </summary>
    public sealed class PenaltyOverride
    {
        public int? Warning { get; init; }
        public int? Problem { get; init; }
        public int? Critical { get; init; }

        public int? For(Severity severity) => severity switch
        {
            Severity.Warning => Warning,
            Severity.Problem => Problem,
            Severity.Critical => Critical,
            _ => null,
        };
    }

    /// <summary>Seuils de déclenchement des règles, par domaine.</summary>
    public sealed class Thresholds
    {
        // Stockage
        public double SystemVolumeFreePercentWarning { get; init; } = 15;
        public double SystemVolumeFreePercentProblem { get; init; } = 10;
        public double SystemVolumeFreePercentCritical { get; init; } = 5;
        public double DataVolumeFreePercentWarning { get; init; } = 8;
        public long ReallocatedSectorsWarning { get; init; } = 1;
        public long ReallocatedSectorsProblem { get; init; } = 10;
        public long ReallocatedSectorsCritical { get; init; } = 50;
        public long PendingSectorsWarning { get; init; } = 1;
        public long PendingSectorsCritical { get; init; } = 10;
        public long UncorrectableErrorsWarning { get; init; } = 1;
        public int DiskTemperatureWarning { get; init; } = 50;
        public int DiskTemperatureProblem { get; init; } = 55;
        public int DiskTemperatureCritical { get; init; } = 60;

        /// <summary>
        /// Seuils des zones thermiques ACPI de la carte mère.
        /// </summary>
        /// <remarks>
        /// Plus hauts que ceux des disques, et pour cause : une zone peut suivre l'étage
        /// d'alimentation ou le chipset, qui travaillent normalement au-delà de 60 °C. Reprendre
        /// le seuil d'un disque ici ferait sonner l'alarme sur toutes les machines de bureau.
        /// </remarks>
        public double ThermalZoneWarning { get; init; } = 80;
        public double ThermalZoneProblem { get; init; } = 90;

        /// <summary>
        /// Température du paquet processeur.
        /// </summary>
        /// <remarks>
        /// Plus haute que celle d'une zone de carte mère : un processeur moderne travaille
        /// normalement entre 70 et 85 °C sous charge, et ne bride qu'aux alentours de 95. Un
        /// seuil trop bas ferait sonner l'alarme sur toute machine qui compile ou qui joue.
        /// </remarks>
        public double CpuTemperatureWarning { get; init; } = 88;
        public double CpuTemperatureProblem { get; init; } = 95;
        public double SsdWearWarning { get; init; } = 80;
        public double SsdWearProblem { get; init; } = 90;
        public double SsdWearCritical { get; init; } = 95;
        public long DiskPowerOnHoursWarning { get; init; } = 40000;

        // Mémoire
        public double MemoryUsageWarning { get; init; } = 85;
        public double MemoryUsageProblem { get; init; } = 93;
        public long MinimumMemoryBytesModernWindows { get; init; } = 4L * 1024 * 1024 * 1024;
        public long RecommendedMemoryBytesModernWindows { get; init; } = 8L * 1024 * 1024 * 1024;

        // Processeur
        public double CpuUsageWarning { get; init; } = 85;
        public int MinimumLogicalCoresModernWindows { get; init; } = 4;

        // Windows
        public int UptimeDaysWarning { get; init; } = 30;
        public int UpdateDelayDaysWarning { get; init; } = 45;
        public int UpdateDelayDaysProblem { get; init; } = 90;

        /// <summary>
        /// À combien de jours de la fin de support la version installée est signalée.
        /// </summary>
        /// <remarks>
        /// Six mois : de quoi tomber sur au moins une visite d'atelier, et pas au point de
        /// signaler la même chose pendant deux ans.
        /// </remarks>
        public int WindowsSupportEndingSoonDays { get; init; } = 180;

        /// <summary>
        /// Ancienneté à partir de laquelle un profil inutilisé est signalé.
        /// </summary>
        /// <remarks>
        /// Dix-huit mois : au-delà d'une année pleine, y compris pour un poste qu'on ne ressort
        /// qu'une fois par an. En deçà, le constat sonnerait sur le compte de vacances de tout le
        /// monde.
        /// </remarks>
        public int UnusedProfileMonthsWarning { get; init; } = 18;

        // Horloge
        /// <summary>
        /// Retard, en jours, à partir duquel l'horloge est déclarée fausse.
        /// </summary>
        /// <remarks>
        /// Un jour suffit parce que la comparaison n'est pas une estimation : elle oppose
        /// l'horloge à des dates venues de l'extérieur de la machine. En deçà, seul le décalage
        /// entre l'heure locale d'un fichier et le fuseau pourrait tromper.
        /// </remarks>
        public int ClockBehindDaysProblem { get; init; } = 1;

        /// <summary>Ancienneté de la dernière remise à l'heure au-delà de laquelle plus rien ne surveille l'horloge.</summary>
        public int ClockSyncStaleDaysWarning { get; init; } = 45;

        /// <summary>
        /// Numéros de port série retenus sans appareil, au-delà desquels la dérive est signalée.
        /// </summary>
        /// <remarks>
        /// Quatre : en deçà, le prochain appareil branché restera dans les numéros bas que tous
        /// les logiciels acceptent. Au-delà, il commence à sortir de la plage COM1 à COM9 que les
        /// logiciels anciens sont seuls à proposer.
        /// </remarks>
        public int ReservedSerialPortsWarning { get; init; } = 4;

        // Affichage
        /// <summary>
        /// Part de la fréquence disponible en deçà de laquelle l'écran est signalé bridé.
        /// </summary>
        /// <remarks>
        /// Soixante-quinze pour cent : de quoi ignorer les écarts d'arrondi (une dalle annoncée
        /// à 144 Hz tourne parfois à 143) et signaler le vrai cas, celui d'un écran rapide resté
        /// à soixante.
        /// </remarks>
        public double DisplayRefreshShortfallPercent { get; init; } = 75;

        /// <summary>Densité perçue au-delà de laquelle le texte devient très petit.</summary>
        public double DisplayEffectiveDpiTiny { get; init; } = 140;

        /// <summary>Densité perçue en deçà de laquelle tout paraît grand et l'écran se réduit.</summary>
        public double DisplayEffectiveDpiLarge { get; init; } = 72;

        // Occupation du disque système
        /// <summary>
        /// Part du volume à partir de laquelle les fichiers cachés de Windows sont nommés.
        /// </summary>
        /// <remarks>
        /// La part et non la taille : cinquante giga-octets sont un détail sur un téraoctet et un
        /// cinquième d'un portable à deux cent cinquante. C'est le rapport qui décide s'il y a
        /// quelque chose à dire.
        /// </remarks>
        public double SystemFilesSharePercent { get; init; } = 10;

        /// <summary>Multiple de la mémoire au-delà duquel le fichier d'échange a été fixé à la main.</summary>
        public double PageFileTimesMemory { get; init; } = 3;

        /// <summary>Âge d'un dossier Windows.old au-delà duquel sa suppression automatique a échoué.</summary>
        public int PreviousWindowsDaysWarning { get; init; } = 30;

        // Réseau : qualité de la liaison
        /// <summary>
        /// Trames en erreur pour un million, au-delà desquelles la liaison est signalée.
        /// </summary>
        /// <remarks>
        /// Mille par million (un millième) et non cent. Le compteur de Windows ne distingue pas
        /// une trame abîmée sur le câble d'une trame rejetée par le pilote pour une autre raison,
        /// et son niveau de fond varie d'un pilote à l'autre : la carte Intel de la machine de
        /// développement en compte cent cinquante par million sur une liaison qui fonctionne
        /// parfaitement. Un câble réellement abîmé, lui, se compte en pourcents. Le seuil est
        /// placé là où la mesure sépare vraiment les deux, quitte à laisser passer les cas
        /// limites : un constat qui envoie changer un câble sain ferait perdre plus de temps
        /// qu'il n'en fait gagner.
        /// </remarks>
        public double LinkErrorsPerMillionWarning { get; init; } = 1000;

        public double LinkErrorsPerMillionProblem { get; init; } = 10000;

        /// <summary>Trames écartées pour un million : un défaut de charge, pas de câble.</summary>
        public double LinkDiscardsPerMillionWarning { get; init; } = 5000;

        // Firmware et pilotes
        public int BiosAgeMonthsWarning { get; init; } = 60;
        public int GpuDriverAgeMonthsWarning { get; init; } = 30;

        // Batterie
        public double BatteryWearWarning { get; init; } = 30;
        public double BatteryWearProblem { get; init; } = 50;

        // Événements
        public int CriticalEventsWarning { get; init; } = 3;
        public int CriticalEventsProblem { get; init; } = 10;
        public int UnexpectedShutdownsWarning { get; init; } = 3;
        public int DiskEventsProblem { get; init; } = 1;
        public int RecurringEventOccurrencesWarning { get; init; } = 20;

        // Stabilité dans le temps
        public int RepeatedFailureCount { get; init; } = 5;
        public int RecentInstabilityDays { get; init; } = 14;
        public int ChangeCorrelationDays { get; init; } = 7;

        // Erreurs matérielles signalées
        public int CorrectedHardwareErrorsWarning { get; init; } = 3;
        public int CorrectedHardwareErrorsProblem { get; init; } = 25;

        // Démarrage
        public int StartupItemsWarning { get; init; } = 8;
        public int StartupItemsProblem { get; init; } = 15;
        public int FrequentTaskMinutes { get; init; } = 15;

        // Alimentation
        public int ProcessorThrottleProblemPercent { get; init; } = 80;

        // Réseau
        public double PacketLossWarning { get; init; } = 5;
        public double PacketLossProblem { get; init; } = 25;
        public double GatewayLatencyWarningMs { get; init; } = 20;
        public double InternetLatencyWarningMs { get; init; } = 120;
        public int WifiSignalWarningPercent { get; init; } = 40;
        public int WifiSignalProblemPercent { get; init; } = 25;

        /// <summary>
        /// Réseaux concurrents sur le canal au-delà desquels le partage se voit.
        /// </summary>
        /// <remarks>
        /// En 2,4 GHz, trois canaux seulement ne se recouvrent pas : trois voisins sur le même
        /// canal divisent déjà le temps de parole par quatre.
        /// </remarks>
        public int WifiSameChannelWarning { get; init; } = 3;
        public int WifiSameChannelProblem { get; init; } = 6;

        /// <summary>
        /// Part du débit d'une carte à une antenne en deçà de laquelle la liaison est bridée.
        /// </summary>
        /// <remarks>
        /// Comparée à une carte à une seule antenne, et non au maximum de la norme : sinon la
        /// moitié des portables d'entrée de gamme seraient signalés alors qu'ils tournent à
        /// plein régime.
        /// </remarks>
        public int WifiRateShareWarningPercent { get; init; } = 30;
        public int WifiRateShareProblemPercent { get; init; } = 12;

        /// <summary>Gigue au-delà de laquelle la voix et la visioconférence deviennent hachées.</summary>
        public double JitterWarningMs { get; init; } = 30;

        /// <summary>
        /// Taille de paquet en deçà de laquelle certaines pages se chargent à moitié.
        /// </summary>
        /// <remarks>
        /// 1500 octets est la valeur normale d'un réseau Ethernet. En dessous, un tunnel ou une
        /// box mal configurée fragmente, et le symptôme, des pages incomplètes sans message
        /// d'erreur, ne ressemble à aucune panne réseau connue du client.
        /// </remarks>
        public int PathMtuWarning { get; init; } = 1500;

        // ---------- Sécurité ----------

        /// <summary>Signatures antivirus au-delà de cet âge : la protection ne voit plus le récent.</summary>
        public int SignatureAgeDaysWarning { get; init; } = 7;
        public int SignatureAgeDaysProblem { get; init; } = 30;

        /// <summary>Comptes administrateurs actifs au-delà desquels le poste mérite d'être questionné.</summary>
        public int AdministratorAccountsWarning { get; init; } = 3;

        // ---------- Performances ----------

        /// <summary>Seuil de Windows lui-même : au-delà, il qualifie le démarrage de dégradé.</summary>
        public int BootDurationSecondsWarning { get; init; } = 60;
        public int BootDurationSecondsProblem { get; init; } = 120;

        /// <summary>
        /// Âge au-delà duquel un relevé de démarrage ne décrit plus la machine d'aujourd'hui.
        /// </summary>
        /// <remarks>
        /// Le démarrage rapide fait repartir Windows d'une mise en veille du noyau, sans
        /// démarrage complet à chronométrer : le journal peut donc ne contenir qu'un relevé
        /// vieux de plusieurs mois. Cas rencontré en session élevée sur la machine de
        /// développement, onze mois d'écart.
        /// </remarks>
        public int BootMeasurementMaxAgeDays { get; init; } = 60;

        /// <summary>
        /// Mémoire validée rapportée à la mémoire installée.
        /// </summary>
        /// <remarks>
        /// Au-dessus de 100 %, la machine s'appuie sur le fichier d'échange pour tenir sa charge
        /// courante. C'est la mesure qui explique le mieux « mon PC rame » sur une machine dont
        /// le processeur n'est pourtant pas saturé.
        /// </remarks>
        public double CommitRatioWarning { get; init; } = 100;
        public double CommitRatioProblem { get; init; } = 150;

        /// <summary>Part de la mémoire installée qu'un seul processus peut occuper sans surprendre.</summary>
        public double SingleProcessMemoryShareWarning { get; init; } = 25;
    }
}
