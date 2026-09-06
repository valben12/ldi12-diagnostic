using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Mises à jour Windows : dernière installation, ancienneté, état du service.
    /// </summary>
    /// <remarks>
    /// Une machine qui n'a plus reçu de correctif depuis des mois a presque toujours une cause
    /// mécanique (service arrêté, corruption système, espace disque insuffisant) et non un
    /// choix de l'utilisateur. Le retard est donc le symptôme à mesurer en priorité.
    /// </remarks>
    public sealed class UpdatesProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Updates,
            DisplayName = "Mises à jour Windows",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(35),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var query = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering",
                TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);

            var serviceState = ReadServiceState();

            if (!query.Succeeded)
            {
                context.Draft.SetUpdates(new UpdatesInfo
                {
                    LastInstalled = Measured.Missing<DateTimeOffset>(query.Reason ?? "Historique illisible."),
                    DaysSinceLastUpdate = Measured.Missing<int>(query.Reason ?? "Historique illisible."),
                    InstalledCount = Measured.Missing<int>(query.Reason ?? "Historique illisible."),
                    ServiceState = serviceState,
                    Failures = FailuresUnavailable(),
                });
                return ProbeOutcome.Partial("L'historique des mises à jour n'a pas pu être lu : " + query.Reason);
            }

            DateTimeOffset? latest = null;

            // Les correctifs datés sont conservés un par un, et pas seulement comptés : c'est ce
            // qui permet à la chronologie de montrer ce qui a changé sur la machine juste avant
            // qu'elle ne se mette à planter. Ceux dont la date est illisible n'y figurent pas.
            var dated = new List<InstalledUpdate>();

            foreach (var record in query.Records)
            {
                // InstalledOn est tantôt une date DMTF, tantôt une chaîne au format américain,
                // tantôt vide : Windows n'a jamais été cohérent sur ce champ.
                var installed = record.GetDmtfDate("InstalledOn") ?? ParseLooseDate(record.GetString("InstalledOn"));
                if (!installed.HasValue) continue;

                if (latest == null || installed.Value > latest.Value) latest = installed;

                var identifier = record.GetString("HotFixID");
                if (!string.IsNullOrWhiteSpace(identifier))
                    dated.Add(new InstalledUpdate { Identifier = identifier!, InstalledOn = installed.Value });
            }

            var days = latest.HasValue ? (int)(DateTimeOffset.Now - latest.Value).TotalDays : (int?)null;

            context.Draft.SetUpdates(new UpdatesInfo
            {
                LastInstalled = latest.HasValue
                    ? Measured.Ok(latest.Value, DataSource.Wmi)
                    : Measured.Missing<DateTimeOffset>(
                        "Aucune date d'installation exploitable : Windows ne renseigne pas ce champ de façon fiable."),
                DaysSinceLastUpdate = days.HasValue
                    ? Measured.Ok(days.Value, DataSource.Inferred)
                    : Measured.Missing<int>("Le retard de mise à jour n'a pas pu être calculé."),
                InstalledCount = Measured.Ok(query.Records.Count, DataSource.Wmi),
                ServiceState = serviceState,
                Failures = FailuresUnavailable(),
                Installed = dated,
            });

            if (days.HasValue)
                return ProbeOutcome.Ok("Dernier correctif installé il y a " + days.Value + " jour(s).");

            return ProbeOutcome.Partial(
                query.Records.Count + " correctif(s) recensé(s), sans date d'installation exploitable.");
        }

        private static Measured<string> ReadServiceState()
        {
            try
            {
                using var service = new ServiceController("wuauserv");
                return Measured.Ok(service.Status.ToString(), DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return Measured.Missing<string>(
                    "Le service Windows Update est introuvable sur cette machine.");
            }
        }

        /// <summary>
        /// La liste des échecs de mise à jour n'est accessible que par l'API COM de l'agent
        /// Windows Update. C'est prévu pour la phase consacrée aux mises à jour ; d'ici là,
        /// l'absence est déclarée plutôt que suggérée par une liste vide.
        /// </summary>
        private static IReadOnlyList<FailedUpdate> FailuresUnavailable() => Array.Empty<FailedUpdate>();

        private static DateTimeOffset? ParseLooseDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Le champ est renseigné dans la culture de l'installation : on tente la culture
            // courante puis la culture invariante, sans jamais échouer bruyamment.
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.None, out var local))
                return new DateTimeOffset(local);

            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var invariant))
                return new DateTimeOffset(invariant);

            return null;
        }
    }
}
