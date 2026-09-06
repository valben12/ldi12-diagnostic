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

        /// <summary>Préfixe des lanceurs de consoles Windows, voir <c>WindowsToolCatalog</c>.</summary>
        public const string ToolPrefix = "TOOL-";
    }
}
