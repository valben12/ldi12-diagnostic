using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
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
    /// L'impression : le spouleur, les imprimantes, la file d'attente.
    /// </summary>
    /// <remarks>
    /// <b>Une des trois pannes les plus racontées en atelier, et rien ne la regardait.</b> Les
    /// états viennent de codes numériques de <c>Win32_Printer</c> (jamais du texte d'état, qui
    /// est traduit), et le libellé montré au technicien est écrit ici, en français, à partir de
    /// ces codes.
    /// <para>
    /// Le dossier de spoule est fermé à l'utilisateur courant : le nombre de fichiers restés
    /// dedans est donc déclaré comme demandant une élévation, plutôt que rendu à zéro.
    /// </para>
    /// </remarks>
    public sealed class PrintingProbe : IDiagnosticProbe
    {
        private const int ErrorWindowDays = 30;

        private static readonly string SpoolDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Printing,
            DisplayName = "Impression",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(3),
            HardTimeout = TimeSpan.FromSeconds(40),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var spooler = ReadService("Spooler");

            var printersQuery = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Name, Default, WorkOffline, PrinterStatus, DetectedErrorState, Network, " +
                "DriverName, PortName FROM Win32_Printer",
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            var printers = new List<PrinterInfo>();
            if (printersQuery.Succeeded)
                foreach (var record in printersQuery.Records)
                {
                    var offline = record.GetBoolean("WorkOffline") ?? false;
                    var status = record.GetUInt32("PrinterStatus");
                    var detected = record.GetUInt32("DetectedErrorState");

                    printers.Add(new PrinterInfo
                    {
                        Name = record.GetString("Name") ?? "(sans nom)",
                        IsDefault = record.GetBoolean("Default") ?? false,
                        IsNetwork = record.GetBoolean("Network") ?? false,
                        Availability = Classify(offline, status, detected),
                        OfflineByChoice = offline,
                        Driver = record.GetString("DriverName"),
                        Port = record.GetString("PortName"),
                    });
                }

            var jobsQuery = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Name FROM Win32_PrintJob",
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            var errors = ReadErrors(cancellationToken);

            context.Draft.SetPrinting(new PrintingInfo
            {
                SpoolerState = spooler.State,
                SpoolerRunning = spooler.Running,
                Printers = printers,
                PendingJobs = jobsQuery.Succeeded
                    ? Measured.Ok(jobsQuery.Records.Count, DataSource.Wmi)
                    : Measured.Missing<int>(jobsQuery.Reason ?? "La file d'attente n'a pas pu être lue."),
                SpoolFiles = ReadSpoolFiles(),
                RecentErrors = errors.HasValue
                    ? Measured.Ok(errors.Value, DataSource.EventLog)
                    : Measured.Missing<int>("Le journal d'impression n'est pas disponible sur cette machine."),
                ErrorWindowDays = Measured.Ok(ErrorWindowDays, DataSource.EventLog),
            });

            if (!printersQuery.Succeeded)
                return ProbeOutcome.Partial(
                    "La liste des imprimantes n'a pas pu être lue : " + printersQuery.Reason);

            if (printers.Count == 0)
                return ProbeOutcome.Ok("Aucune imprimante installée sur cette machine.");

            var troubled = 0;
            foreach (var printer in printers)
                if (printer.Availability != PrinterAvailability.Ready && printer.Availability != PrinterAvailability.Unknown)
                    troubled++;

            return ProbeOutcome.Ok(
                printers.Count + " imprimante(s) installée(s)" +
                (troubled > 0 ? ", dont " + troubled + " indisponible(s)" : "") +
                ", spouleur " + (spooler.Running.Or(false) ? "démarré" : "arrêté") + ".");
        }

        /// <summary>
        /// L'état de l'imprimante, à partir des codes et non du texte.
        /// </summary>
        /// <remarks>
        /// <c>DetectedErrorState</c> décrit ce qui bloque physiquement l'appareil et prime sur
        /// tout le reste : une imprimante en bourrage papier est parfois annoncée « prête » par
        /// son état général. Les valeurs proviennent du modèle CIM et ne dépendent d'aucune
        /// traduction.
        /// </remarks>
        internal static PrinterAvailability Classify(bool workOffline, uint? status, uint? detected)
        {
            switch (detected)
            {
                case 3: return PrinterAvailability.PaperOut;   // papier bas
                case 4: return PrinterAvailability.PaperOut;   // plus de papier
                case 5: return PrinterAvailability.InkOut;     // encre basse
                case 6: return PrinterAvailability.InkOut;     // plus d'encre
                case 7: return PrinterAvailability.Error;      // capot ouvert
                case 8: return PrinterAvailability.Error;      // bourrage
                case 9: return PrinterAvailability.Offline;
                case 10: return PrinterAvailability.Error;     // intervention demandée
                case 11: return PrinterAvailability.Error;     // bac de sortie plein
            }

            if (workOffline) return PrinterAvailability.Offline;

            return status switch
            {
                3 => PrinterAvailability.Ready,
                4 => PrinterAvailability.Ready,   // en cours d'impression
                5 => PrinterAvailability.Ready,   // préchauffage
                6 => PrinterAvailability.Paused,  // impression arrêtée
                7 => PrinterAvailability.Offline,
                _ => PrinterAvailability.Unknown,
            };
        }

        private static (Measured<string> State, Measured<bool> Running) ReadService(string name)
        {
            try
            {
                using var service = new ServiceController(name);
                var status = service.Status;
                return (Measured.Ok(Describe(status), DataSource.NativeApi),
                        Measured.Ok(status == ServiceControllerStatus.Running, DataSource.NativeApi));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                var reason = "Le service « " + name + " » est introuvable sur cette machine.";
                return (Measured.Missing<string>(reason), Measured.Missing<bool>(reason));
            }
        }

        internal static string Describe(ServiceControllerStatus status) => status switch
        {
            ServiceControllerStatus.Running => "Démarré",
            ServiceControllerStatus.Stopped => "Arrêté",
            ServiceControllerStatus.Paused => "En pause",
            ServiceControllerStatus.StartPending => "En cours de démarrage",
            ServiceControllerStatus.StopPending => "En cours d'arrêt",
            _ => "Indéterminé",
        };

        /// <summary>
        /// Les fichiers restés dans le dossier de spoule.
        /// </summary>
        /// <remarks>
        /// Le dossier est fermé à l'utilisateur courant, vérifié sur la machine d'essai, où il
        /// rend un refus d'accès. La mesure déclare donc l'élévation nécessaire au lieu de rendre
        /// zéro, qui se lirait comme « aucun fichier bloqué ».
        /// </remarks>
        private static Measured<int> ReadSpoolFiles()
        {
            try
            {
                if (!Directory.Exists(SpoolDirectory))
                    return Measured.Missing<int>("Le dossier de spoule n'existe pas sur cette machine.");

                return Measured.Ok(Directory.GetFiles(SpoolDirectory).Length, DataSource.FileSystem);
            }
            catch (UnauthorizedAccessException)
            {
                return Measured.NeedsElevation<int>(
                    "Le dossier de spoule n'est pas lisible sans privilèges administrateur.");
            }
            catch (IOException)
            {
                return Measured.Missing<int>("Le dossier de spoule n'a pas pu être lu.");
            }
        }

        /// <summary>
        /// Les erreurs d'impression, comptées par niveau et non par identifiant.
        /// </summary>
        /// <remarks>
        /// Le niveau est un nombre que Windows n'a jamais traduit ; la signification de chaque
        /// identifiant, elle, demanderait d'interpréter un manifeste que ce module n'a pas
        /// vérifié. Compter ce qui est marqué « erreur » est ce qu'on peut affirmer.
        /// </remarks>
        private static int? ReadErrors(CancellationToken cancellationToken)
        {
            var xpath =
                "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= " +
                ((long)ErrorWindowDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]]]";

            try
            {
                var query = new EventLogQuery(
                    "Microsoft-Windows-PrintService/Admin", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                var count = 0;
                while (count < 500)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    record.Dispose();
                    count++;
                }

                return count;
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                // Le journal d'impression est désactivé sur beaucoup de machines : ce n'est pas
                // une anomalie, et son absence ne doit pas se lire comme « aucune erreur ».
                return null;
            }
        }
    }
}
