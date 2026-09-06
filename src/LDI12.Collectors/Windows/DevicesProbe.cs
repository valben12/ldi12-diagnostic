using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Périphériques et pilotes, avec les codes d'erreur du Gestionnaire de périphériques.
    /// </summary>
    /// <remarks>
    /// C'est le module qui répond à « pourquoi ce PC n'a plus de Wi-Fi ». Un code
    /// <c>CM_PROB_*</c> traduit en français dit exactement ce qui bloque : pilote absent,
    /// périphérique désactivé, conflit de ressources, matériel qui ne répond plus.
    /// <para>
    /// L'énumération PnP complète est l'une des requêtes WMI les plus lentes et l'une de celles
    /// qui se bloquent le plus volontiers sur une machine dégradée : la sonde est marquée pour
    /// s'exécuter dans le processus satellite, où elle peut être tuée sans conséquence.
    /// </para>
    /// </remarks>
    public sealed class DevicesProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Devices,
            DisplayName = "Périphériques et pilotes",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(4),
            HardTimeout = TimeSpan.FromSeconds(60),
            Isolation = IsolationMode.SeparateProcess,
            FullScanOnly = true,
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var devicesQuery = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Name, PNPClass, Manufacturer, DeviceID, ConfigManagerErrorCode, Status " +
                "FROM Win32_PnPEntity",
                TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);

            if (!devicesQuery.Succeeded)
            {
                context.Draft.SetDevices(Array.Empty<DeviceInfo>());
                context.Draft.SetDrivers(Array.Empty<DriverInfo>());
                return ProbeOutcome.Failed("Les périphériques n'ont pas pu être énumérés : " + devicesQuery.Reason);
            }

            var devices = new List<DeviceInfo>(devicesQuery.Records.Count);
            var faulty = 0;
            var unknown = 0;

            foreach (var record in devicesQuery.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var errorCode = record.GetInt32("ConfigManagerErrorCode") ?? 0;
                var name = Measure.Clean(record.GetString("Name")) ?? "Périphérique sans nom";
                var isUnknown = name.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0
                                || name.IndexOf("Inconnu", StringComparison.OrdinalIgnoreCase) >= 0
                                || record.GetString("PNPClass") == null && errorCode == 28;

                if (errorCode != 0) faulty++;
                if (isUnknown) unknown++;

                devices.Add(new DeviceInfo
                {
                    Name = name,
                    DeviceClass = Measure.Clean(record.GetString("PNPClass")),
                    Manufacturer = Measure.Clean(record.GetString("Manufacturer")),
                    HardwareId = Measure.Clean(record.GetString("DeviceID")),
                    ProblemCode = errorCode,
                    ProblemLabel = errorCode == 0 ? null : DescribeProblem(errorCode),

                    // 22 est le code du périphérique explicitement désactivé par l'utilisateur.
                    IsDisabled = errorCode == 22,
                    IsUnknownDevice = isUnknown,
                });
            }

            context.Draft.SetDevices(devices);

            var drivers = await ReadDriversAsync(context, cancellationToken).ConfigureAwait(false);
            context.Draft.SetDrivers(drivers.Items);

            var summary = devices.Count + " périphérique(s)";
            if (faulty > 0) summary += ", " + faulty + " en erreur";
            if (unknown > 0) summary += ", " + unknown + " non identifié(s)";
            summary += " : " + drivers.Items.Count + " pilote(s)";

            return drivers.Failed
                ? ProbeOutcome.Partial(summary + ". La liste des pilotes est incomplète : " + drivers.Reason)
                : ProbeOutcome.Ok(summary + ".");
        }

        private static async Task<(IReadOnlyList<DriverInfo> Items, bool Failed, string? Reason)> ReadDriversAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var query = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, " +
                "DriverProviderName, InfName, IsSigned FROM Win32_PnPSignedDriver",
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            if (!query.Succeeded)
                return (Array.Empty<DriverInfo>(), true, query.Reason);

            var drivers = new List<DriverInfo>(query.Records.Count);
            foreach (var record in query.Records)
            {
                var deviceName = Measure.Clean(record.GetString("DeviceName"));
                if (deviceName == null) continue;

                var provider = Measure.Clean(record.GetString("DriverProviderName"));

                drivers.Add(new DriverInfo
                {
                    DeviceName = deviceName,
                    DeviceClass = Measure.Clean(record.GetString("DeviceClass")),
                    Manufacturer = Measure.Clean(record.GetString("Manufacturer")),
                    Version = Measure.Clean(record.GetString("DriverVersion")),
                    Date = record.GetDmtfDate("DriverDate"),
                    InfName = Measure.Clean(record.GetString("InfName")),
                    IsSigned = record.GetBoolean("IsSigned"),

                    // Un pilote fourni par Microsoft là où un pilote constructeur est attendu
                    // signifie que le matériel fonctionne en mode générique, souvent dégradé.
                    IsMicrosoftGeneric = provider != null &&
                        provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }

            return (drivers, false, null);
        }

        /// <summary>
        /// Codes CM_PROB_* du Gestionnaire de périphériques, traduits. Ce sont exactement les
        /// messages que Windows affiche dans les propriétés du périphérique, en plus direct.
        /// </summary>
        private static string DescribeProblem(int code) => code switch
        {
            1 => "Le périphérique n'est pas configuré correctement.",
            3 => "Le pilote est peut-être endommagé, ou la mémoire système est insuffisante.",
            9 => "Windows ne parvient pas à identifier ce matériel.",
            10 => "Le périphérique ne peut pas démarrer.",
            12 => "Ressources système insuffisantes pour ce périphérique.",
            14 => "Un redémarrage est nécessaire pour que ce périphérique fonctionne.",
            16 => "Windows ne peut pas identifier toutes les ressources utilisées par ce périphérique.",
            18 => "Les pilotes doivent être réinstallés.",
            19 => "Le registre est endommagé pour ce périphérique.",
            21 => "Windows est en train de retirer ce périphérique.",
            22 => "Ce périphérique est désactivé.",
            24 => "Ce périphérique est absent, ne fonctionne pas correctement, ou n'a pas tous ses pilotes.",
            28 => "Les pilotes de ce périphérique ne sont pas installés.",
            29 => "Ce périphérique est désactivé dans le firmware (BIOS/UEFI).",
            31 => "Windows ne peut pas charger les pilotes requis pour ce périphérique.",
            32 => "Le service d'installation de ce pilote est désactivé.",
            33 => "Windows ne peut pas déterminer les ressources dont ce périphérique a besoin.",
            35 => "Le firmware ne contient pas les informations nécessaires à ce périphérique.",
            37 => "Le pilote a signalé une erreur lors de l'initialisation.",
            38 => "Une instance précédente du pilote est encore en mémoire.",
            39 => "Le pilote est endommagé ou manquant.",
            40 => "Les informations du pilote dans le registre sont incorrectes.",
            43 => "Windows a arrêté ce périphérique car il a signalé un problème.",
            45 => "Ce périphérique n'est pas connecté à l'ordinateur.",
            47 => "Ce périphérique a été préparé pour un retrait sécurisé.",
            48 => "Le pilote de ce périphérique a été bloqué par Windows.",
            52 => "La signature numérique du pilote n'a pas pu être vérifiée.",
            _ => "Code d'erreur " + code + " du Gestionnaire de périphériques.",
        };
    }
}
