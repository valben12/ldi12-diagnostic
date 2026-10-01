using System;
using System.Collections.Generic;
using LDI12.Actions.Maintenance;
using LDI12.Actions.Repairs;
using LDI12.Actions.Tools;
using LDI12.Core.Logging;

namespace LDI12.Actions
{
    /// <summary>
    /// Les actions disponibles, construites sous protection.
    /// </summary>
    /// <remarks>
    /// Même leçon que pour le catalogue de sondes : une exception dans un constructeur survient
    /// avant toute politique d'exécution et emporterait l'application entière. Une action qui ne
    /// se construit pas est écartée et journalisée ; les autres restent utilisables.
    /// </remarks>
    public static class ActionCatalog
    {
        private const string Category = "Actions";

        public static IReadOnlyList<IRepairAction> CreateAll(ILdiLogger? logger = null)
        {
            var factories = new Func<IRepairAction>[]
            {
                () => new SfcScanAction(),
                () => new DismRestoreHealthAction(),
                () => new CheckDiskAction(),
                () => new CleanupAction(),
                () => new Backup.BackupUserDataAction(),
                () => new Backup.RestoreUserDataAction(),
                () => new Backup.ExportDriversAction(),
                () => new Backup.RestoreDriversAction(),
                () => new Backup.RestoreApplicationsAction(),
                () => new Backup.RestorePrintersAction(),
                () => new Backup.OpenFilesAction(),
                () => new Footprint.RemoveFootprintAction(),
                () => new FlushDnsAction(),
                () => new NetworkStackResetAction(),
                () => new RenewAddressAction(),
                () => new ResetMachineProxyAction(),
                () => new FlushArpCacheAction(),
                () => new RestartAdapterAction(),
                () => new ResetFirewallAction(),
                () => new RestoreHostsFileAction(),
                () => new RestartDiscoveryAction(),
                () => new RemoveRouteAction(),
                () => new RestartServiceAction(),
            };

            var actions = new List<IRepairAction>(factories.Length);
            foreach (var factory in factories)
            {
                try
                {
                    actions.Add(factory());
                }
                catch (Exception ex)
                {
                    logger?.Error(Category, "Une action n'a pas pu être construite et a été écartée.", ex);
                }
            }

            return actions;
        }

        public static IRepairAction? Find(string id, ILdiLogger? logger = null)
        {
            foreach (var action in CreateAll(logger))
                if (string.Equals(action.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase)) return action;
            return null;
        }

        /// <summary>
        /// Vrai si l'identifiant désigne quelque chose que l'application sait faire : une action
        /// ou une console Windows.
        /// </summary>
        /// <remarks>
        /// C'est ce que vérifie le test sur <c>Recommendation.LinkedAction</c>. Une recommandation
        /// qui annonce un bouton inexistant est pire qu'une recommandation sans bouton : elle fait
        /// chercher au technicien quelque chose qui n'a jamais existé.
        /// </remarks>
        public static bool IsKnown(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            if (id!.StartsWith(ActionIds.ToolPrefix, StringComparison.OrdinalIgnoreCase))
                return WindowsToolCatalog.Find(id) != null;

            return Find(id) != null;
        }
    }
}
