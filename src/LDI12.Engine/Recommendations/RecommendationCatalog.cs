using System;
using System.Collections.Generic;
using LDI12.Core.Model;

namespace LDI12.Engine.Recommendations
{
    /// <summary>Identifiants des recommandations, cités par les règles.</summary>
    public static class Rec
    {
        public const string BackupNow = "BACKUP-NOW";
        public const string ReplaceDisk = "REPLACE-DISK";
        public const string MonitorDisk = "MONITOR-DISK";
        public const string CheckDisk = "CHECK-DISK";
        public const string FreeDiskSpace = "FREE-DISK-SPACE";
        public const string ImproveCooling = "IMPROVE-COOLING";
        public const string UpgradeToSsd = "UPGRADE-TO-SSD";
        public const string PlanDiskReplacement = "PLAN-DISK-REPLACEMENT";

        public const string AddMemory = "ADD-MEMORY";
        public const string EnableDualChannel = "ENABLE-DUAL-CHANNEL";
        public const string CheckMemory = "CHECK-MEMORY";
        public const string EnableVirtualization = "ENABLE-VIRTUALIZATION";
        public const string PlanHardwareUpgrade = "PLAN-HARDWARE-UPGRADE";

        public const string InstallGpuDriver = "INSTALL-GPU-DRIVER";
        public const string UpdateGpuDriver = "UPDATE-GPU-DRIVER";
        public const string InstallMissingDrivers = "INSTALL-MISSING-DRIVERS";
        public const string InspectDeviceError = "INSPECT-DEVICE-ERROR";

        public const string UpdateBios = "UPDATE-BIOS";
        public const string EnableSecureBoot = "ENABLE-SECURE-BOOT";
        public const string EnableTpm = "ENABLE-TPM";
        public const string ReplaceBattery = "REPLACE-BATTERY";

        public const string RepairSystemFiles = "REPAIR-SYSTEM-FILES";
        public const string RepairComponentStore = "REPAIR-COMPONENT-STORE";
        public const string RestartMachine = "RESTART-MACHINE";
        public const string ActivateWindows = "ACTIVATE-WINDOWS";
        public const string RunWindowsUpdate = "RUN-WINDOWS-UPDATE";
        public const string StartUpdateService = "START-UPDATE-SERVICE";
        public const string StartEssentialService = "START-ESSENTIAL-SERVICE";
        public const string EnableSystemRestore = "ENABLE-SYSTEM-RESTORE";
        public const string DisableFastStartup = "DISABLE-FAST-STARTUP";
        public const string MigrateSupportedWindows = "MIGRATE-SUPPORTED-WINDOWS";

        public const string AnalyzeCrashDumps = "ANALYZE-CRASH-DUMPS";
        public const string EnableCrashDumps = "ENABLE-CRASH-DUMPS";
        public const string RestorePowerPlan = "RESTORE-POWER-PLAN";
        public const string RestorePageFile = "RESTORE-PAGE-FILE";
        public const string RestoreTrim = "RESTORE-TRIM";
        public const string FixSystemClock = "FIX-SYSTEM-CLOCK";

        public const string SetNativeResolution = "SET-NATIVE-RESOLUTION";

        public const string RepairUserProfile = "REPAIR-USER-PROFILE";

        public const string CheckNetworkCabling = "CHECK-NETWORK-CABLING";
        public const string UpdateNetworkDriver = "UPDATE-NETWORK-DRIVER";
        public const string ReviewProxySettings = "REVIEW-PROXY-SETTINGS";
        public const string ReviewDomainDns = "REVIEW-DOMAIN-DNS";
        public const string CleanHostsFile = "CLEAN-HOSTS-FILE";
        public const string ResetWinsockCatalog = "RESET-WINSOCK-CATALOG";
        public const string ReviewStaticRoutes = "REVIEW-STATIC-ROUTES";
        public const string RemoveLegacySharing = "REMOVE-LEGACY-SHARING";
        public const string StartDiscoveryServices = "START-DISCOVERY-SERVICES";

        public const string InvestigateEventErrors = "INVESTIGATE-EVENT-ERRORS";
        public const string RepairFailingProgram = "REPAIR-FAILING-PROGRAM";
        public const string CheckPowerSupply = "CHECK-POWER-SUPPLY";

        public const string ReduceStartupItems = "REDUCE-STARTUP-ITEMS";
        public const string CleanStartupOrphans = "CLEAN-STARTUP-ORPHANS";
        public const string InvestigateCpuLoad = "INVESTIGATE-CPU-LOAD";

        public const string CheckNetworkLink = "CHECK-NETWORK-LINK";
        public const string RestartRouter = "RESTART-ROUTER";
        public const string FixDnsConfiguration = "FIX-DNS-CONFIGURATION";
        public const string ImproveWifiSignal = "IMPROVE-WIFI-SIGNAL";
        public const string SecureWifi = "SECURE-WIFI";
        public const string ChangeWifiChannel = "CHANGE-WIFI-CHANNEL";
        public const string EnableWifiRadio = "ENABLE-WIFI-RADIO";
        public const string EnableDiskEncryption = "ENABLE-DISK-ENCRYPTION";
        public const string EnableFirewall = "ENABLE-FIREWALL";

        public const string RemoveUnsupportedSoftware = "REMOVE-UNSUPPORTED-SOFTWARE";
    }

    /// <summary>
    /// Catalogue des actions recommandées.
    /// </summary>
    /// <remarks>
    /// Les recommandations vivent ici et non dans les règles : dix constats « disque plein » ne
    /// doivent produire qu'une seule ligne d'action, et le tri par priorité puis par rapport
    /// impact/effort doit être cohérent d'un domaine à l'autre.
    /// </remarks>
    public static class RecommendationCatalog
    {
        private static readonly Dictionary<string, Recommendation> Entries =
            new Dictionary<string, Recommendation>(StringComparer.OrdinalIgnoreCase);

        static RecommendationCatalog()
        {
            Add(Rec.BackupNow, "Sauvegarder les données sans attendre",
                "Le disque montre des signes de défaillance : une sauvegarde faite maintenant coûte quelques minutes, une récupération après panne coûte beaucoup plus.",
                RecommendationPriority.Immediate, EffortLevel.Extended, ImpactLevel.High);

            Add(Rec.ReplaceDisk, "Remplacer le disque",
                "Le disque annonce lui-même sa défaillance. Le remplacer avant la panne évite une perte de données et une immobilisation.",
                RecommendationPriority.Immediate, EffortLevel.Intervention, ImpactLevel.High, hardware: true);

            Add(Rec.PlanDiskReplacement, "Prévoir le remplacement du disque",
                "Le disque fonctionne encore mais montre des signes d'usure : il vaut mieux planifier son remplacement que le subir.",
                RecommendationPriority.High, EffortLevel.Intervention, ImpactLevel.High, hardware: true);

            Add(Rec.MonitorDisk, "Surveiller l'état du disque",
                "Quelques indicateurs se dégradent sans être alarmants : un nouveau contrôle dans quelques semaines dira si la tendance se confirme.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.CheckDisk, "Vérifier le système de fichiers",
                "Des erreurs de lecture ont été enregistrées : une vérification du disque permet de savoir si le support ou seulement le système de fichiers est en cause.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High, action: "CHKDSK");

            Add(Rec.FreeDiskSpace, "Libérer de l'espace disque",
                "Windows a besoin d'espace libre pour fonctionner normalement : sans lui, les mises à jour échouent et la machine ralentit fortement.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High, action: "MAINTENANCE-CLEANUP");

            Add(Rec.ImproveCooling, "Améliorer le refroidissement",
                "La température relevée raccourcit la durée de vie du matériel. Un dépoussiérage et une meilleure ventilation suffisent souvent.",
                RecommendationPriority.Normal, EffortLevel.Intervention, ImpactLevel.Moderate);

            Add(Rec.UpgradeToSsd, "Remplacer le disque système par un SSD",
                "C'est l'amélioration la plus visible sur une machine ancienne : démarrage et ouverture des logiciels plusieurs fois plus rapides.",
                RecommendationPriority.Normal, EffortLevel.Intervention, ImpactLevel.High, hardware: true);

            Add(Rec.AddMemory, "Augmenter la mémoire vive",
                "La mémoire installée est le facteur qui limite cette machine : l'augmenter apporte davantage que n'importe quel nettoyage logiciel.",
                RecommendationPriority.High, EffortLevel.Intervention, ImpactLevel.High, hardware: true);

            Add(Rec.EnableDualChannel, "Installer la mémoire en double canal",
                "Un second module identique double la bande passante mémoire, ce qui profite particulièrement aux machines à carte graphique intégrée.",
                RecommendationPriority.Optional, EffortLevel.Intervention, ImpactLevel.Moderate, hardware: true);

            Add(Rec.CheckMemory, "Tester la mémoire vive",
                "Les erreurs relevées peuvent venir d'un module défaillant : un test mémoire permet de le confirmer ou de l'écarter.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High,
                action: "TOOL-MEMORY-DIAGNOSTIC");

            Add(Rec.EnableVirtualization, "Activer la virtualisation dans le firmware",
                "Cette option est nécessaire aux machines virtuelles, au sous-système Linux et à certaines protections de sécurité de Windows.",
                RecommendationPriority.Optional, EffortLevel.Minutes, ImpactLevel.Low);

            Add(Rec.PlanHardwareUpgrade, "Envisager un renouvellement de la machine",
                "Le matériel lui-même limite les performances : au-delà d'un certain point, aucune intervention logicielle n'y changera grand-chose.",
                RecommendationPriority.Normal, EffortLevel.Intervention, ImpactLevel.High, hardware: true);

            Add(Rec.InstallGpuDriver, "Installer le pilote de la carte graphique",
                "L'affichage fonctionne en mode dégradé avec le pilote générique de Windows : résolutions limitées, animations saccadées, pas d'accélération.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.UpdateGpuDriver, "Mettre à jour le pilote graphique",
                "Le pilote installé est ancien : les mises à jour corrigent des blocages d'affichage et améliorent la compatibilité.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.InstallMissingDrivers, "Installer les pilotes manquants",
                "Certains matériels ne sont pas reconnus par Windows et restent donc inutilisables.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High);

            Add(Rec.InspectDeviceError, "Examiner les périphériques en erreur",
                "Un ou plusieurs matériels signalent une erreur : chacun a une cause distincte, à traiter individuellement.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.Moderate, action: "TOOL-DEVICE-MANAGER");

            Add(Rec.UpdateBios, "Mettre à jour le firmware de la carte mère",
                "Le firmware installé est ancien : les mises à jour corrigent des problèmes de stabilité et de compatibilité matérielle.",
                RecommendationPriority.Optional, EffortLevel.Intervention, ImpactLevel.Moderate);

            Add(Rec.EnableSecureBoot, "Activer le démarrage sécurisé",
                "Cette protection empêche un logiciel malveillant de se charger avant Windows.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.EnableTpm, "Activer le module de sécurité TPM",
                "Ce composant est nécessaire au chiffrement du disque et exigé par Windows 11.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.ReplaceBattery, "Remplacer la batterie",
                "La batterie a perdu une part importante de sa capacité d'origine : l'autonomie ne reviendra pas sans remplacement.",
                RecommendationPriority.Normal, EffortLevel.Intervention, ImpactLevel.Moderate, hardware: true);

            Add(Rec.RepairSystemFiles, "Réparer les fichiers système de Windows",
                "Des fichiers de Windows sont endommagés : cela provoque des dysfonctionnements et empêche souvent les mises à jour de s'installer.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High, action: "REPAIR-SFC");

            Add(Rec.RepairComponentStore, "Réparer l'image système de Windows",
                "Les fichiers de référence qui permettent à Windows de se réparer lui-même sont endommagés. Ils doivent être restaurés en premier.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High, action: "REPAIR-DISM");

            Add(Rec.RemoveUnsupportedSoftware, "Retirer les logiciels qui ne sont plus corrigés",
                "Un logiciel que son éditeur ne corrige plus garde ses failles connues. Vérifier d'abord qu'aucun usage de la maison n'en dépend, puis le désinstaller depuis Programmes et fonctionnalités.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.RestartMachine, "Redémarrer la machine",
                "Un redémarrage est en attente : tant qu'il n'a pas eu lieu, les mises à jour restent bloquées et certains diagnostics sont faussés.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.ActivateWindows, "Régulariser l'activation de Windows",
                "Windows n'est pas activé : certaines fonctions de personnalisation sont désactivées et des rappels s'affichent.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Low);

            Add(Rec.RunWindowsUpdate, "Installer les mises à jour de Windows",
                "La machine a pris du retard sur les correctifs : cela expose à des failles déjà corrigées ailleurs.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High);

            Add(Rec.StartUpdateService, "Réactiver le service Windows Update",
                "Le service chargé des mises à jour est arrêté : aucun correctif ne peut être installé tant qu'il le reste.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.StartEssentialService, "Redémarrer les services essentiels arrêtés",
                "Un service nécessaire au fonctionnement normal de Windows est arrêté ou désactivé.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High, action: "TOOL-SERVICES");

            Add(Rec.EnableSystemRestore, "Activer la restauration système",
                "Sans point de restauration, aucun retour en arrière n'est possible si une installation se passe mal.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.DisableFastStartup, "Désactiver le démarrage rapide",
                "Avec cette option, « éteindre puis rallumer » ne redémarre pas réellement Windows, ce qui laisse persister des problèmes qu'un vrai redémarrage réglerait.",
                RecommendationPriority.Optional, EffortLevel.Minutes, ImpactLevel.Low);

            Add(Rec.MigrateSupportedWindows, "Passer à une version de Windows encore suivie",
                "Cette version ne reçoit plus de correctifs de sécurité : les failles découvertes depuis la fin du support ne seront jamais corrigées.",
                RecommendationPriority.High, EffortLevel.Intervention, ImpactLevel.High);

            Add(Rec.AnalyzeCrashDumps, "Analyser les rapports de plantage",
                "Les écrans bleus enregistrés contiennent le nom du composant fautif : leur analyse évite de remplacer du matériel au hasard.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High);

            Add(Rec.EnableCrashDumps, "Réactiver l'enregistrement des plantages",
                "Sans lui, le prochain écran bleu ne laissera aucune trace : la recherche de la panne repartira de zéro.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate,
                action: "TOOL-SYSTEM-PROPERTIES");

            Add(Rec.RestorePowerPlan, "Rendre au processeur toute sa fréquence",
                "Le plan d'alimentation retient la machine : rétablir le mode équilibré lui rend sa vitesse sans rien changer d'autre.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High,
                action: "TOOL-POWER-OPTIONS");

            Add(Rec.RestorePageFile, "Rétablir le fichier d'échange",
                "Sans lui, les logiciels se ferment sans prévenir dès que la mémoire vive est pleine. Le laisser géré par Windows suffit.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High,
                action: "TOOL-SYSTEM-PROPERTIES");

            Add(Rec.RestoreTrim, "Réactiver la notification de suppression",
                "Sans elle, le disque à mémoire flash écrit de plus en plus lentement à mesure qu'il se remplit.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.RepairFailingProgram, "Réparer ou réinstaller le programme qui plante",
                "Un programme qui se ferme tout seul à répétition a presque toujours une installation abîmée, ou un composant partagé (pilote, bibliothèque) qui ne lui convient plus.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.EnableFirewall, "Réactiver le pare-feu de Windows",
                "Sur un réseau qu'on ne maîtrise pas, c'est lui qui ferme les portes que Windows laisse ouvertes par construction.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.FixSystemClock, "Remettre l'horloge à l'heure et la maintenir",
                "Une horloge fausse fait échouer toutes les connexions sécurisées à la fois, bloque les mises à jour et peut empêcher l'ouverture de session sur un domaine. Sur un poste fixe, penser à la pile de la carte mère.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.SetNativeResolution, "Remettre l'écran à sa définition d'origine",
                "Une dalle est nette à sa définition et à aucune autre : en dessous, elle étire chaque point sur plusieurs et le texte perd ses contours.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.RepairUserProfile, "Rétablir le profil de l'utilisateur",
                "Le dossier d'origine est en général intact à côté du nouveau : l'essentiel est de sauvegarder la session en cours avant de la fermer.",
                RecommendationPriority.High, EffortLevel.Extended, ImpactLevel.High);

            Add(Rec.CheckNetworkCabling, "Changer le câble réseau et le port",
                "Des trames arrivent abîmées : la cause est presque toujours entre la machine et la prise. Deux minutes de test évitent des heures de recherche ailleurs.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High, hardware: true);

            Add(Rec.UpdateNetworkDriver, "Mettre à jour le pilote de la carte réseau",
                "Des données valides sont écartées faute d'être traitées à temps, ce qui relève du pilote plutôt que du câble.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.ReviewProxySettings, "Vérifier la configuration du serveur mandataire",
                "Un mandataire injoignable coupe le web sans couper le réseau, et Windows en garde deux configurations distinctes : celle de la session et celle de la machine.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.ReviewDomainDns, "Ne laisser que les serveurs DNS du domaine",
                "Un serveur extérieur ne connaît aucun nom interne : ouverture de session, stratégies de groupe et partages en dépendent tous.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.CleanHostsFile, "Rendre au fichier hosts son contenu d'origine",
                "Une ligne de ce fichier l'emporte sur tous les serveurs DNS, ne se voit dans aucune fenêtre de Windows et survit à toute réinitialisation du réseau.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High, action: "REPAIR-NETWORK-HOSTS");

            Add(Rec.ResetWinsockCatalog, "Réinitialiser le catalogue Winsock",
                "Une bibliothèque restée dans la pile après une désinstallation arrête tout le trafic, alors que l'adresse et la passerelle sont correctes.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High, action: "REPAIR-NETWORK-STACK");

            Add(Rec.ReviewStaticRoutes, "Revoir les routes posées à la main",
                "Une route rendue permanente survit aux redémarrages et aux changements de box : elle détourne un sous-réseau entier sans que rien ne le signale.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate, action: "REPAIR-NETWORK-ROUTE");

            Add(Rec.RemoveLegacySharing, "Retirer le partage de fichiers de première génération",
                "Ce protocole de 1983 ne chiffre rien et n'authentifie pas le serveur ; Windows ne l'installe plus, et ce poste l'a conservé d'une installation plus ancienne.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.StartDiscoveryServices, "Relancer la découverte du voisinage réseau",
                "Sans ces services, la machine ne voit plus les NAS ni les imprimantes du réseau, et rien n'indique que la panne vient de là.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate, action: "REPAIR-NETWORK-DISCOVERY");

            Add(Rec.InvestigateEventErrors, "Examiner les erreurs récurrentes des journaux",
                "Une même erreur revient régulièrement : elle a une cause précise, qui se traite une fois pour toutes.",
                RecommendationPriority.Normal, EffortLevel.Extended, ImpactLevel.Moderate, action: "TOOL-EVENT-VIEWER");

            Add(Rec.CheckPowerSupply, "Vérifier l'alimentation électrique",
                "Les arrêts brutaux répétés viennent souvent d'une alimentation défaillante, d'une surchauffe ou d'une prise mal contactée.",
                RecommendationPriority.High, EffortLevel.Intervention, ImpactLevel.High);

            Add(Rec.ReduceStartupItems, "Réduire les programmes lancés au démarrage",
                "Chaque programme lancé automatiquement allonge le démarrage et consomme des ressources en continu.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.High, action: "TOOL-TASK-MANAGER");

            Add(Rec.CleanStartupOrphans, "Supprimer les entrées de démarrage orphelines",
                "Des programmes désinstallés sont encore appelés au démarrage : Windows perd du temps à les chercher.",
                RecommendationPriority.Optional, EffortLevel.Minutes, ImpactLevel.Low);

            Add(Rec.InvestigateCpuLoad, "Identifier ce qui occupe le processeur",
                "Le processeur est fortement sollicité : il faut identifier le programme responsable avant de conclure à un manque de puissance.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate, action: "TOOL-TASK-MANAGER");

            Add(Rec.CheckNetworkLink, "Vérifier le raccordement réseau",
                "La machine n'obtient pas d'adresse valide : câble, prise, ou point d'accès sont à contrôler en premier.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.RestartRouter, "Redémarrer la box ou le routeur",
                "La machine communique correctement mais rien ne passe au-delà : le problème se situe côté box ou opérateur.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.FixDnsConfiguration, "Corriger la configuration DNS",
                "La connexion fonctionne mais les noms de sites ne se traduisent plus en adresses : les sites semblent inaccessibles alors que la ligne est bonne.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.ImproveWifiSignal, "Améliorer la réception Wi-Fi",
                "Le signal reçu est faible : débit réduit et coupures intermittentes en découlent.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.SecureWifi, "Sécuriser le réseau sans fil",
                "Le réseau utilisé n'est pas chiffré : tout ce qui y transite peut être lu par un tiers à portée.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.ChangeWifiChannel, "Changer de canal Wi-Fi, ou passer en 5 GHz",
                "Le canal utilisé est partagé avec les réseaux voisins : le débit se divise entre tous les émetteurs de la fréquence, signal excellent compris.",
                RecommendationPriority.Normal, EffortLevel.Minutes, ImpactLevel.Moderate);

            Add(Rec.EnableWifiRadio, "Rallumer l'émetteur Wi-Fi",
                "La carte est présente et son émetteur est coupé : la machine ne voit aucun réseau, ce qui ressemble à une panne de matériel.",
                RecommendationPriority.High, EffortLevel.Minutes, ImpactLevel.High);

            Add(Rec.EnableDiskEncryption, "Activer le chiffrement du disque",
                "Sur une machine transportée, le chiffrement est ce qui protège les données en cas de perte ou de vol.",
                RecommendationPriority.Normal, EffortLevel.Extended, ImpactLevel.Moderate);
        }

        public static bool TryGet(string id, out Recommendation recommendation)
            => Entries.TryGetValue(id, out recommendation!);

        public static IReadOnlyCollection<string> AllIds => Entries.Keys;

        private static void Add(
            string id, string title, string rationale,
            RecommendationPriority priority, EffortLevel effort, ImpactLevel impact,
            bool hardware = false, string? action = null)
            => Entries[id] = new Recommendation
            {
                Id = id,
                Title = title,
                Rationale = rationale,
                Priority = priority,
                Effort = effort,
                ExpectedImpact = impact,
                RequiresHardwarePurchase = hardware,
                LinkedAction = action,
            };
    }
}
