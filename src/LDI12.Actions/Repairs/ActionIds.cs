namespace LDI12.Actions
{
    /// <summary>
    /// Identifiants d'action, cités par les recommandations du moteur.
    /// </summary>
    /// <remarks>
    /// Ce sont eux que porte <c>Recommendation.LinkedAction</c>. Un test vérifie que chaque
    /// recommandation qui prétend proposer une action désigne bien quelque chose d'exécutable :
    /// une recommandation qui renvoie vers un bouton inexistant est pire qu'une recommandation
    /// sans bouton.
    /// </remarks>
    public static class ActionIds
    {
        public const string Sfc = "REPAIR-SFC";
        public const string Dism = "REPAIR-DISM";
        public const string CheckDisk = "CHKDSK";
        public const string FlushDns = "REPAIR-DNS-FLUSH";
        public const string NetworkReset = "REPAIR-NETWORK-STACK";
        public const string RenewAddress = "REPAIR-NETWORK-RENEW";
        public const string ResetMachineProxy = "REPAIR-NETWORK-PROXY";
        public const string FlushArpCache = "REPAIR-NETWORK-ARP";
        public const string RestartAdapter = "REPAIR-NETWORK-ADAPTER";
        public const string ResetFirewall = "REPAIR-NETWORK-FIREWALL";
        public const string RestoreHostsFile = "REPAIR-NETWORK-HOSTS";
        public const string RestartDiscovery = "REPAIR-NETWORK-DISCOVERY";
        public const string RemoveRoute = "REPAIR-NETWORK-ROUTE";
        public const string RestartService = "REPAIR-RESTART-SERVICE";
        public const string Cleanup = "MAINTENANCE-CLEANUP";

        /// <summary>La seule action qui ne touche pas à la machine du client, mais à nous.</summary>
        public const string RemoveFootprint = "MAINTENANCE-FOOTPRINT";

        /// <summary>Surveillance en direct. Ne modifie rien : elle est consignée en prévisualisation.</summary>
        public const string Watch = "WATCH-SESSION";

        /// <summary>Mesures de performance. Seule action du logiciel qui sollicite volontairement la machine.</summary>
        public const string Benchmark = "MEASURE-PERFORMANCE";

        /// <summary>Relevé des données à sauvegarder. Lecture pure : ni copie, ni suppression.</summary>
        public const string UserData = "SURVEY-USER-DATA";

        /// <summary>
        /// Copie vérifiée des données du client. La seule action qui écrit des fichiers du client.
        /// </summary>
        public const string BackupUserData = "BACKUP-USER-DATA";

        /// <summary>Restauration d'une sauvegarde LDI12. Écrit dans le profil, ne supprime rien.</summary>
        public const string RestoreUserData = "RESTORE-USER-DATA";

        /// <summary>Export des pilotes tiers dans une sauvegarde. Lit le magasin de pilotes, n'y touche pas.</summary>
        public const string ExportDrivers = "BACKUP-DRIVERS";

        /// <summary>Réinstallation des pilotes d'une sauvegarde, par pnputil.</summary>
        public const string RestoreDrivers = "RESTORE-DRIVERS";

        /// <summary>Réinstallation des applications d'une sauvegarde, par winget.</summary>
        public const string RestoreApplications = "RESTORE-APPLICATIONS";

        /// <summary>Réinstallation des imprimantes relevées avec une sauvegarde.</summary>
        public const string RestorePrinters = "RESTORE-PRINTERS";

        /// <summary>Relecture d'une sauvegarde contre les empreintes de son manifeste. N'écrit rien.</summary>
        public const string VerifyBackup = "VERIFY-BACKUP";

        /// <summary>Copie, depuis un cliché instantané, des fichiers qu'un programme tenait ouverts.</summary>
        public const string CopyOpenFiles = "BACKUP-OPEN-FILES";

        /// <summary>« Rechercher les modifications sur le matériel ».</summary>
        public const string RescanDevices = "DEVICE-RESCAN";

        /// <summary>Redémarrer le pilote d'un périphérique (paramètre « device »).</summary>
        public const string RestartDevice = "DEVICE-RESTART";

        /// <summary>Réactiver un périphérique désactivé (paramètre « device »).</summary>
        public const string EnableDevice = "DEVICE-ENABLE";

        /// <summary>Désinstaller puis redétecter un périphérique (paramètre « device »).</summary>
        public const string ReinstallDevice = "DEVICE-REINSTALL";

        /// <summary>Installer les pilotes que Windows Update propose (paramètre facultatif « filter »).</summary>
        public const string DriverUpdates = "DRIVERS-WINDOWS-UPDATE";

        /// <summary>Vider la file d'impression bloquée.</summary>
        public const string PrintQueueReset = "PRINT-QUEUE-RESET";

        /// <summary>Remettre à zéro le cache de téléchargement de Windows Update.</summary>
        public const string WindowsUpdateReset = "WINDOWS-UPDATE-RESET";

        /// <summary>Désactiver un programme au démarrage (paramètre « entry »), comme le Gestionnaire des tâches.</summary>
        public const string StartupDisable = "STARTUP-DISABLE";

        /// <summary>Désactiver le démarrage rapide de Windows.</summary>
        public const string FastStartupOff = "FAST-STARTUP-OFF";

        /// <summary>Choisir le mode d'alimentation (paramètre « plan » : equilibre ou performances).</summary>
        public const string PowerPlan = "POWER-PLAN";

        /// <summary>Remettre l'horloge à l'heure.</summary>
        public const string TimeResync = "TIME-RESYNC";

        /// <summary>Faire d'une sortie audio la sortie par défaut (paramètre « endpoint »).</summary>
        public const string AudioDefaultOutput = "AUDIO-SET-DEFAULT";

        /// <summary>Lever la coupure du son et remonter un volume nul (paramètre facultatif « endpoint »).</summary>
        public const string AudioRestoreVolume = "AUDIO-RESTORE-VOLUME";

        /// <summary>Jouer une note sur la sortie par défaut.</summary>
        public const string AudioTestSound = "AUDIO-TEST-SOUND";

        /// <summary>Relancer les services audio de Windows.</summary>
        public const string AudioServicesRestart = "AUDIO-RESTART-SERVICES";

        /// <summary>Lire les minividages des écrans bleus (écran Plantages).</summary>
        public const string CrashDumpsRead = "CRASH-DUMPS-READ";

        /// <summary>Régler Windows pour qu'un écran bleu laisse un minividage.</summary>
        public const string CrashDumpsEnable = "CRASH-DUMPS-ENABLE";

        /// <summary>Désactiver ou réactiver une entrée de démarrage (écran Programmes), sans privilèges.</summary>
        public const string AutorunToggle = "AUTORUN-TOGGLE";

        /// <summary>La même, pour ce qui demande d'être administrateur.</summary>
        public const string AutorunToggleAdmin = "AUTORUN-TOGGLE-ADMIN";

        /// <summary>Terminer un processus (écran Programmes), sans privilèges.</summary>
        public const string ProcessEnd = "PROCESS-END";

        /// <summary>Le même, pour un processus du système ou d'un autre compte.</summary>
        public const string ProcessEndAdmin = "PROCESS-END-ADMIN";

        /// <summary>Retirer des paquets du magasin de pilotes (écran Magasin de pilotes).</summary>
        public const string DriverPackagesDelete = "DRIVER-PACKAGES-DELETE";

        /// <summary>Capturer le trafic réseau quelques secondes (écran Réseau local).</summary>
        public const string TrafficCapture = "TRAFFIC-CAPTURE";

        /// <summary>Lancer ou relire l'autotest SMART d'un disque (écran Disques).</summary>
        public const string DiskSelfTest = "DISK-SELF-TEST";

        /// <summary>Lire la surface d'un disque en chronométrant chaque bloc (écran Disques).</summary>
        public const string DiskSurfaceTest = "DISK-SURFACE-TEST";

        /// <summary>Optimiser un volume : TRIM pour un SSD, défragmentation pour un disque dur.</summary>
        public const string DiskOptimize = "DISK-OPTIMIZE";

        /// <summary>
        /// Les opérations qui appartiennent à un écran dédié : elles n'ont de sens qu'avec ce qu'il
        /// montre, et ne figurent pas dans la liste des Réparations.
        /// </summary>
        public static readonly System.Collections.Generic.ISet<string> OwnedByScreens =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                CrashDumpsRead,
                AutorunToggle,
                AutorunToggleAdmin,
                ProcessEnd,
                ProcessEndAdmin,
                DriverPackagesDelete,
                TrafficCapture,
                DiskSelfTest,
                DiskSurfaceTest,
                DiskOptimize,
            };

        /// <summary>Préfixe des lanceurs de consoles Windows, voir <c>WindowsToolCatalog</c>.</summary>
        public const string ToolPrefix = "TOOL-";
    }
}
