using System;
using System.Collections.Generic;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Catalogue complet des règles de diagnostic.
    /// </summary>
    /// <remarks>
    /// Point d'entrée unique, comme le catalogue de sondes : ajouter une règle consiste à
    /// l'écrire dans le fichier de son domaine et à l'exposer dans le <c>All()</c> correspondant.
    /// </remarks>
    public static class RuleCatalog
    {
        public static IReadOnlyList<IRule> All()
        {
            var rules = new List<IRule>();
            rules.AddRange(HardwareRules.All());
            rules.AddRange(HardwareErrorRules.All());
            rules.AddRange(DisplayRules.All());
            rules.AddRange(SerialPortRules.All());
            rules.AddRange(StorageRules.All());
            rules.AddRange(SystemSpaceRules.All());
            rules.AddRange(WindowsRules.All());
            rules.AddRange(UpgradeRules.All());
            rules.AddRange(NetworkRules.All());
            rules.AddRange(NetworkQualityRules.All());
            rules.AddRange(NetworkEnvironmentRules.All());
            rules.AddRange(ListeningPortRules.All());
            rules.AddRange(NetworkPathRules.All());
            rules.AddRange(WirelessRules.All());
            rules.AddRange(SecurityRules.All());
            rules.AddRange(SecurityAuditRules.All());
            rules.AddRange(SoftwareRules.All());
            rules.AddRange(ScheduledTaskRules.All());
            rules.AddRange(StabilityRules.All());
            rules.AddRange(SafetyNetRules.All());
            rules.AddRange(ProfileRules.All());
            rules.AddRange(SystemTimeRules.All());
            rules.AddRange(PrintingRules.All());
            rules.AddRange(AudioRules.All());
            rules.AddRange(PerformanceRules.All());
            rules.AddRange(PerformanceDepthRules.All());
            rules.AddRange(PowerRules.All());
            return rules;
        }
    }
}
