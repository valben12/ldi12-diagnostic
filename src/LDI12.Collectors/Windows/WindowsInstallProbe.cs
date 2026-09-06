using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Installation Windows : ancienneté, dernier démarrage, activation, redémarrage en attente.
    /// </summary>
    /// <remarks>
    /// Deux constats d'apparence anodine expliquent beaucoup d'interventions : un redémarrage en
    /// attente, qui bloque les mises à jour et fausse tous les autres diagnostics ; et le
    /// démarrage rapide, qui fait qu'un « j'ai redémarré » du client n'a en réalité rien
    /// redémarré du tout.
    /// </remarks>
    public sealed class WindowsInstallProbe : IDiagnosticProbe
    {
        private const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        private const string ComponentServicing =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
        private const string WindowsUpdateReboot =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.WindowsInstall,
            DisplayName = "Installation Windows",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var os = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT InstallDate, LastBootUpTime, Locale, SizeStoredInPagingFiles FROM Win32_OperatingSystem",
                TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);

            var activation = await ReadActivationAsync(context, cancellationToken).ConfigureAwait(false);
            var record = os.First;

            var install = new WindowsInstallInfo
            {
                InstallDate = os.Succeeded
                    ? Measure.DmtfDate(record, "InstallDate", "La date d'installation de Windows")
                    : Measure.FromFailure<DateTimeOffset>(os, "La date d'installation de Windows"),
                LastBootTime = os.Succeeded
                    ? Measure.DmtfDate(record, "LastBootUpTime", "La date du dernier démarrage")
                    : Measure.FromFailure<DateTimeOffset>(os, "La date du dernier démarrage"),

                // L'API native ne dépend pas de WMI : le temps de fonctionnement reste connu
                // même sur une machine dont le dépôt est endommagé.
                Uptime = context.Native.ReadUptime(),
                ActivationStatus = activation,
                Locale = Measured.Ok(
                    context.Platform.Profile.SystemLocale ?? System.Globalization.CultureInfo.InstalledUICulture.Name,
                    DataSource.NativeApi),
                TimeZone = ReadTimeZone(),
                RebootPending = ReadRebootPending(context),
                FastStartupEnabled = ReadFastStartup(context),
                SystemRestoreEnabled = ReadSystemRestore(context),
                PageFileSizeBytes = os.Succeeded && record?.GetInt64("SizeStoredInPagingFiles") is long pageFileMb
                    ? Measured.Ok(pageFileMb * 1024 * 1024, DataSource.Wmi)
                    : Measured.Missing<long>("La taille du fichier d'échange n'est pas renseignée."),
            };

            context.Draft.SetInstall(install);

            if (install.RebootPending.Or(false))
                return ProbeOutcome.Ok("Un redémarrage est en attente sur cette machine.");

            return install.Uptime.HasValue
                ? ProbeOutcome.Ok("Allumé depuis " + Describe(install.Uptime.Value) + ".")
                : ProbeOutcome.Partial("Le temps de fonctionnement n'a pas pu être déterminé.");
        }

        /// <summary>
        /// Trois indices indépendants du redémarrage en attente. Windows n'en expose aucun
        /// directement : il faut chercher les traces laissées par les composants qui l'ont demandé.
        /// </summary>
        private static Measured<bool> ReadRebootPending(ProbeContext context)
        {
            var registry = context.Registry;

            var componentServicing = registry.KeyExists(RegistryHive.LocalMachine, ComponentServicing);
            var windowsUpdate = registry.KeyExists(RegistryHive.LocalMachine, WindowsUpdateReboot);
            var pendingRenames = registry.ReadMultiString(
                RegistryHive.LocalMachine, SessionManager, "PendingFileRenameOperations");

            var pending = componentServicing || windowsUpdate || (pendingRenames != null && pendingRenames.Length > 0);
            return Measured.Ok(pending, DataSource.Registry);
        }

        private static Measured<bool> ReadFastStartup(ProbeContext context)
        {
            var value = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, SessionManager + @"\Power", "HiberbootEnabled");

            return value.HasValue
                ? Measured.Ok(value.Value == 1, DataSource.Registry)
                : Measured.Missing<bool>(
                    "Le démarrage rapide n'existe pas sur cette version de Windows.");
        }

        private static Measured<bool> ReadSystemRestore(ProbeContext context)
        {
            var feature = context.Platform.Features.Get(FeatureId.SystemRestore);
            return feature.Availability == Availability.Unavailable
                ? Measured.Ok(false, DataSource.Registry)
                : Measured.Partial(true, DataSource.Registry, feature.Reason);
        }

        private static Measured<string> ReadTimeZone()
        {
            try { return Measured.Ok(TimeZoneInfo.Local.DisplayName, DataSource.NativeApi); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                return Measured.Missing<string>("Le fuseau horaire du système est mal configuré.");
            }
        }

        /// <summary>
        /// L'activation se lit dans SoftwareLicensingProduct, en filtrant sur la clé partielle
        /// pour ne garder que le produit Windows, la classe contient aussi les licences Office.
        /// </summary>
        private static async Task<Measured<string>> ReadActivationAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var query = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT LicenseStatus, Description FROM SoftwareLicensingProduct " +
                "WHERE PartialProductKey IS NOT NULL AND Description LIKE '%Windows%'",
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            if (!query.Succeeded)
                return Measure.FromFailure<string>(query, "L'état d'activation de Windows");

            var record = query.First;
            if (record == null)
                return Measured.Missing<string>("Aucune licence Windows n'est enregistrée sur cette machine.");

            var status = record.GetInt32("LicenseStatus");
            return status.HasValue
                ? Measured.Ok(DescribeLicense(status.Value), DataSource.Wmi)
                : Measured.Missing<string>("L'état de la licence n'est pas renseigné.");
        }

        private static string DescribeLicense(int status) => status switch
        {
            0 => "Non activé",
            1 => "Activé",
            2 => "Période de grâce",
            3 => "Période de grâce hors tolérance",
            4 => "Période de grâce non authentique",
            5 => "Notification : activation requise",
            6 => "Période de grâce étendue",
            _ => "État de licence " + status,
        };

        private static string Describe(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return (int)uptime.TotalDays + " j " + uptime.Hours + " h";
            if (uptime.TotalHours >= 1)
                return (int)uptime.TotalHours + " h " + uptime.Minutes + " min";
            return (int)uptime.TotalMinutes + " min";
        }
    }
}
