using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Performance
{
    /// <summary>
    /// Les réglages qui brident la machine sans rien casser.
    /// </summary>
    /// <remarks>
    /// <b>Tout se lit dans le registre, jamais par <c>powercfg</c>.</b> La commande rend des noms
    /// de plans et des libellés de réglages traduits, qu'il faudrait analyser pour les
    /// comprendre ; le registre rend des identifiants et des entiers, qui ne dépendent d'aucune
    /// langue. C'est aussi instantané, là où lancer un processus coûte le double de tout le
    /// reste de ce module.
    /// <para>
    /// <b>Une valeur absente n'est pas une valeur manquante.</b> Windows n'écrit un réglage de
    /// bridage que si quelqu'un l'a changé : son absence signifie « cent pour cent », et le dire
    /// « non mesuré » ferait chercher une panne là où il n'y a qu'un réglage d'origine. Ces
    /// valeurs sont donc rendues comme <see cref="DataSource.Inferred"/>, connues, mais déduites
    /// plutôt que lues.
    /// </para>
    /// </remarks>
    public sealed class PowerProbe : IDiagnosticProbe
    {
        private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        /// <summary>Sous-groupe « gestion de l'alimentation du processeur ».</summary>
        private const string ProcessorGroup = "54533251-82be-4824-96c1-47b60b740d00";

        /// <summary>Fréquence maximale autorisée, en pourcentage.</summary>
        private const string ThrottleMaximum = "bc5038f7-23e0-4960-96da-33abaf5935ec";

        /// <summary>Fréquence minimale autorisée, en pourcentage.</summary>
        private const string ThrottleMinimum = "893dee8e-2bef-41e0-89c6-b55d0929964c";

        private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        /// <summary>Réglages d'origine de Windows, appliqués tant que rien n'est écrit.</summary>
        private const int DefaultMaximum = 100;

        private const int DefaultMinimum = 5;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Power,
            DisplayName = "Alimentation et bridage",
            Category = DiagnosticCategory.Performance,
            EstimatedDuration = TimeSpan.FromMilliseconds(150),
            HardTimeout = TimeSpan.FromSeconds(15),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var planId = context.Registry.ReadString(RegistryHive.LocalMachine, SchemesKey, "ActivePowerScheme");

            var configuration = new PowerConfiguration
            {
                PlanId = planId == null
                    ? Measured.Missing<string>("Le plan d'alimentation actif n'a pas pu être lu.")
                    : Measured.Ok(planId, DataSource.Registry),
                Plan = planId == null
                    ? Measured.Missing<PowerPlanKind>("Le plan d'alimentation actif n'a pas pu être lu.")
                    : Measured.Ok(Classify(planId), DataSource.Registry),
                ProcessorMaximumOnAc = Throttle(context, planId, ThrottleMaximum, "ACSettingIndex", DefaultMaximum),
                ProcessorMinimumOnAc = Throttle(context, planId, ThrottleMinimum, "ACSettingIndex", DefaultMinimum),
                ProcessorMaximumOnBattery = Throttle(context, planId, ThrottleMaximum, "DCSettingIndex", DefaultMaximum),
                PageFile = PageFile(context, out var setting),
                PageFileSetting = setting,
            };

            context.Draft.SetPower(configuration);

            if (planId == null)
                return Task.FromResult(ProbeOutcome.Partial("Le plan d'alimentation actif n'a pas pu être lu."));

            var summary = Describe(configuration.Plan.Or(PowerPlanKind.Unknown));
            var maximum = configuration.ProcessorMaximumOnAc.Or(DefaultMaximum);
            if (maximum < DefaultMaximum)
                summary += " : processeur limité à " + maximum.ToString(CultureInfo.CurrentCulture) + " %";

            return Task.FromResult(ProbeOutcome.Ok(summary + "."));
        }

        /// <summary>
        /// Plans livrés avec Windows, reconnus par leur identifiant.
        /// </summary>
        /// <remarks>
        /// Un constructeur peut livrer ses propres plans, et le technicien en créer. Ceux-là ne
        /// sont pas rangés de force dans une case : ils sont « personnalisés », et leur
        /// identifiant reste affiché pour qu'on puisse les retrouver.
        /// </remarks>
        internal static PowerPlanKind Classify(string id) => id.ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => PowerPlanKind.Balanced,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => PowerPlanKind.HighPerformance,
            "a1841308-3541-4fab-bc81-f71556f20b4a" => PowerPlanKind.PowerSaver,
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => PowerPlanKind.Ultimate,
            _ => PowerPlanKind.Custom,
        };

        private static string Describe(PowerPlanKind plan) => plan switch
        {
            PowerPlanKind.Balanced => "Plan équilibré",
            PowerPlanKind.HighPerformance => "Plan performances élevées",
            PowerPlanKind.PowerSaver => "Plan économie d'énergie",
            PowerPlanKind.Ultimate => "Plan performances ultimes",
            PowerPlanKind.Custom => "Plan personnalisé",
            _ => "Plan d'alimentation indéterminé",
        };

        private static Measured<int> Throttle(
            ProbeContext context, string? planId, string setting, string valueName, int fallback)
        {
            if (planId == null)
                return Measured.Missing<int>("Le plan d'alimentation actif n'a pas pu être lu.");

            var key = SchemesKey + "\\" + planId + "\\" + ProcessorGroup + "\\" + setting;
            var value = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, valueName);

            return value.HasValue
                ? Measured.Ok(value.Value, DataSource.Registry)
                : Measured.Ok(fallback, DataSource.Inferred);
        }

        /// <summary>
        /// Configuration du fichier d'échange.
        /// </summary>
        /// <remarks>
        /// La valeur est une liste de lignes « chemin taille-mini taille-maxi ». Sans taille, la
        /// gestion est laissée à Windows ; avec, quelqu'un l'a imposée. Une liste vide signifie
        /// qu'il n'y a pas de fichier d'échange du tout : le réglage que laissent derrière eux
        /// les « astuces pour accélérer Windows », et qui fait planter les machines à faible
        /// mémoire dès qu'un logiciel demande un peu plus que ce qu'elles ont.
        /// </remarks>
        private static Measured<PageFileMode> PageFile(ProbeContext context, out Measured<string> setting)
        {
            var lines = context.Registry.ReadMultiString(
                RegistryHive.LocalMachine, MemoryManagementKey, "PagingFiles");

            if (lines == null)
            {
                setting = Measured.Missing<string>("Le réglage du fichier d'échange n'a pas pu être lu.");
                return Measured.Missing<PageFileMode>("Le réglage du fichier d'échange n'a pas pu être lu.");
            }

            var joined = string.Join(" · ", lines);

            var configured = 0;
            var fixedSize = false;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                configured++;

                // « C:\pagefile.sys 4096 8192 » : deux tailles écrites, donc imposées.
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1], out var maximum) && maximum > 0)
                    fixedSize = true;
            }

            setting = configured == 0
                ? Measured.Ok("Aucun fichier d'échange", DataSource.Registry)
                : Measured.Ok(joined, DataSource.Registry);

            return Measured.Ok(
                configured == 0 ? PageFileMode.Disabled
                    : fixedSize ? PageFileMode.FixedSize
                    : PageFileMode.SystemManaged,
                DataSource.Registry);
        }
    }
}
