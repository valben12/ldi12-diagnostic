using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Journaux d'événements : erreurs critiques, erreurs récurrentes, erreurs disque, écrans
    /// bleus et arrêts inattendus.
    /// </summary>
    /// <remarks>
    /// Le regroupement par (source, identifiant) est ce qui rend ce module exploitable : une même
    /// erreur répétée quatre cents fois est <b>un</b> signal, pas quatre cents. Sans lui, la liste
    /// serait illisible et le technicien passerait à côté de l'anomalie rare qui compte.
    /// <para>
    /// La lecture se fait par EventLogReader et non par <c>wevtutil</c> : format XML stable, pas
    /// de processus à lancer, et surtout aucune sortie traduite à analyser.
    /// </para>
    /// </remarks>
    public sealed class EventsProbe : IDiagnosticProbe
    {
        private const int WindowDays = 14;
        private const int BsodWindowDays = 90;

        /// <summary>Plafond de lecture : sur une machine très bavarde, tout lire prendrait des minutes.</summary>
        private const int MaxEventsPerLog = 4000;

        /// <summary>Sources dont les erreurs annoncent une défaillance matérielle du stockage.</summary>
        private static readonly HashSet<string> DiskSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "disk", "Disk", "Ntfs", "volmgr", "storahci", "stornvme", "iaStorA", "nvraid", "atapi",
        };

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Events,
            DisplayName = "Journaux d'événements",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(5),
            HardTimeout = TimeSpan.FromSeconds(60),
            Isolation = IsolationMode.SeparateProcess,
            FullScanOnly = true,
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var system = ReadErrors("System", WindowDays, cancellationToken);
            var application = ReadErrors("Application", WindowDays, cancellationToken);

            if (system.Failed && application.Failed)
            {
                context.Draft.SetEvents(new EventsInfo
                {
                    WindowDays = Measured.Missing<int>(system.Reason ?? "Journaux illisibles."),
                    UnexpectedShutdowns = Measured.Missing<int>(system.Reason ?? "Journaux illisibles."),
                });
                return Task.FromResult(ProbeOutcome.Failed(
                    "Les journaux d'événements n'ont pas pu être lus : " + system.Reason));
            }

            var critical = new List<EventSummary>();
            var recurring = new List<EventSummary>();
            var diskErrors = new List<EventSummary>();

            foreach (var summary in system.Summaries)
            {
                if (DiskSources.Contains(summary.Source)) diskErrors.Add(summary);
                if (string.Equals(summary.Level, "Critique", StringComparison.Ordinal)) critical.Add(summary);
                else if (summary.Count >= 5) recurring.Add(summary);
            }

            foreach (var summary in application.Summaries)
            {
                if (string.Equals(summary.Level, "Critique", StringComparison.Ordinal)) critical.Add(summary);
                else if (summary.Count >= 5) recurring.Add(summary);
            }

            critical.Sort((a, b) => b.Count.CompareTo(a.Count));
            recurring.Sort((a, b) => b.Count.CompareTo(a.Count));
            diskErrors.Sort((a, b) => b.Count.CompareTo(a.Count));

            var bsods = ReadBsods(cancellationToken);

            context.Draft.SetEvents(new EventsInfo
            {
                WindowDays = Measured.Ok(WindowDays, DataSource.EventLog),
                CriticalErrors = critical,
                RecurringErrors = recurring,
                DiskErrors = diskErrors,
                Bsods = bsods.Items,
                UnexpectedShutdowns = system.Failed
                    ? Measured.Missing<int>(system.Reason ?? "Journal système illisible.")
                    : Measured.Ok(system.UnexpectedShutdowns, DataSource.EventLog),
            });

            var parts = new List<string>();
            if (critical.Count > 0) parts.Add(critical.Count + " erreur(s) critique(s)");
            if (diskErrors.Count > 0) parts.Add(diskErrors.Count + " type(s) d'erreur disque");
            if (bsods.Items.Count > 0) parts.Add(bsods.Items.Count + " écran(s) bleu(s)");
            if (system.UnexpectedShutdowns > 0) parts.Add(system.UnexpectedShutdowns + " arrêt(s) inattendu(s)");

            var message = parts.Count == 0
                ? "Aucune anomalie notable sur les " + WindowDays + " derniers jours."
                : string.Join(", ", parts.ToArray()) + " sur " + WindowDays + " jours.";

            if (application.Failed || system.Failed) return Task.FromResult(ProbeOutcome.Partial(message + " Un journal n'a pas pu être lu."));
            return Task.FromResult(ProbeOutcome.Ok(message));
        }

        private static (List<EventSummary> Summaries, int UnexpectedShutdowns, bool Failed, string? Reason)
            ReadErrors(string logName, int days, CancellationToken cancellationToken)
        {
            var grouped = new Dictionary<string, MutableSummary>(StringComparer.Ordinal);
            var unexpectedShutdowns = 0;

            // Filtrage côté journal : demander à Windows de ne renvoyer que les niveaux Critique
            // et Erreur de la fenêtre voulue est incomparablement plus rapide que tout lire.
            var xpath =
                "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= " +
                ((long)days * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]]]";

            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < MaxEventsPerLog; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record)
                    {
                        var source = record.ProviderName ?? "(source inconnue)";
                        var id = record.Id;
                        var when = record.TimeCreated ?? DateTime.Now;

                        // 6008 (arrêt inattendu) et Kernel-Power 41 sont les deux traces d'une
                        // machine qui s'est éteinte sans passer par l'arrêt de Windows.
                        if (id == 6008 || (id == 41 && source.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0))
                            unexpectedShutdowns++;

                        var key = source + "|" + id;
                        if (!grouped.TryGetValue(key, out var summary))
                        {
                            grouped[key] = new MutableSummary
                            {
                                LogName = logName,
                                Source = source,
                                EventId = id,
                                Level = record.Level == 1 ? "Critique" : "Erreur",
                                First = when,
                                Last = when,
                                Count = 1,
                                Sample = SafeDescription(record),
                            };
                        }
                        else
                        {
                            summary.Count++;
                            if (when < summary.First) summary.First = when;
                            if (when > summary.Last) summary.Last = when;
                        }
                    }
                }
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return (new List<EventSummary>(), 0, true,
                    "Le journal « " + logName + " » n'a pas pu être lu (" + ex.GetType().Name + ").");
            }

            var result = new List<EventSummary>(grouped.Count);
            foreach (var summary in grouped.Values) result.Add(summary.ToSummary());
            return (result, unexpectedShutdowns, false, null);
        }

        private static (List<BsodInfo> Items, bool Failed) ReadBsods(CancellationToken cancellationToken)
        {
            var bsods = new List<BsodInfo>();
            var xpath =
                "*[System[Provider[@Name='BugCheck'] and TimeCreated[timediff(@SystemTime) <= " +
                ((long)BsodWindowDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]]]";

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < 50; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = reader.ReadEvent();
                    if (record == null) break;

                    using (record)
                    {
                        var description = SafeDescription(record);
                        bsods.Add(new BsodInfo
                        {
                            Date = record.TimeCreated ?? DateTime.Now,
                            BugCheckCode = ExtractBugCheckCode(description),
                            Parameters = description,
                            DumpPath = ExtractDumpPath(description),
                        });
                    }
                }
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return (bsods, true);
            }

            return (bsods, false);
        }

        /// <summary>
        /// Le code d'arrêt est le premier motif hexadécimal de la description. Le reste de la
        /// ligne est traduit et ne doit pas être analysé.
        /// </summary>
        private static string? ExtractBugCheckCode(string? description)
        {
            if (description == null) return null;
            var start = description.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            var end = start + 2;
            while (end < description.Length && Uri.IsHexDigit(description[end])) end++;
            return end > start + 2 ? description.Substring(start, end - start) : null;
        }

        /// <summary>
        /// Le chemin du rapport de plantage, quand Windows dit l'avoir enregistré.
        /// </summary>
        /// <remarks>
        /// Même technique que le code d'arrêt, et pour la même raison : un chemin de fichier ne
        /// se traduit pas. La phrase qui l'entoure, si : elle n'est donc pas lue. Sans cela, la
        /// colonne « Fichier de vidage » du rapport restait vide sur toutes les machines, alors
        /// que la recommandation d'analyser les rapports, elle, était bien émise.
        /// </remarks>
        internal static string? ExtractDumpPath(string? description)
        {
            if (description == null) return null;

            var end = description.IndexOf(".dmp", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            end += 4;

            // Remonter jusqu'à la lettre de lecteur la plus proche : « C:\ ».
            for (var index = end - 5; index >= 1; index--)
            {
                if (description[index] != ':' || index + 1 >= description.Length) continue;
                if (description[index + 1] != '\\' || !char.IsLetter(description[index - 1])) continue;

                return description.Substring(index - 1, end - index + 1);
            }

            return null;
        }

        private static string? SafeDescription(EventRecord record)
        {
            // FormatDescription lève si le fournisseur a été désinstallé : cas fréquent après
            // la suppression d'un antivirus ou d'un pilote.
            try
            {
                var description = record.FormatDescription();
                if (string.IsNullOrWhiteSpace(description)) return null;
                return description!.Length > 400 ? description.Substring(0, 400) + "…" : description;
            }
            catch (Exception ex) when (ex is EventLogException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed class MutableSummary
        {
            public string LogName = string.Empty;
            public string Source = string.Empty;
            public int EventId;
            public string Level = string.Empty;
            public int Count;
            public DateTime First;
            public DateTime Last;
            public string? Sample;

            public EventSummary ToSummary() => new EventSummary
            {
                LogName = LogName,
                Source = Source,
                EventId = EventId,
                Level = Level,
                Count = Count,
                FirstSeen = First,
                LastSeen = Last,
                SampleMessage = Sample,
            };
        }
    }
}
