using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Ce que le matériel a déjà signalé de lui-même : erreurs matérielles, test mémoire,
    /// rapports de plantage.
    /// </summary>
    /// <remarks>
    /// <b>Ces événements existent, et personne ne les lit.</b> Le module des journaux ne
    /// remonte que les niveaux Critique et Erreur : c'est ce qui le rend lisible. Or une erreur
    /// matérielle <i>corrigée</i> est enregistrée en Avertissement, et le résultat d'un test
    /// mémoire en Information : les deux passaient donc à travers, alors qu'ils répondent
    /// précisément à la question qu'on se pose devant une machine qui plante sans motif.
    /// <para>
    /// <b>La qualification vient de l'identifiant, pas du libellé.</b> Le manifeste du
    /// fournisseur associe à chaque identifiant un niveau et une phrase ; c'est de là que vient
    /// la table de <see cref="WheaCatalog"/>. Lire le niveau seul aurait suffi presque partout,
    /// mais Microsoft déclare l'événement 29 en Avertissement avec un texte qui annonce une
    /// erreur irrécupérable : la table existe pour cette exception-là, et le niveau ne sert plus
    /// que de repli pour un identifiant inconnu.
    /// </para>
    /// <para>
    /// Rien ici ne demande de privilèges : le journal Système se lit en session normale, et les
    /// dossiers de vidage sont accessibles en lecture.
    /// </para>
    /// </remarks>
    public sealed class HardwareErrorsProbe : IDiagnosticProbe
    {
        /// <summary>
        /// Fenêtre de lecture des erreurs matérielles.
        /// </summary>
        /// <remarks>
        /// Plus large que les quatorze jours des journaux ordinaires : une erreur corrigée est
        /// rare et espacée, et c'est justement son retour sur plusieurs semaines qui la rend
        /// significative.
        /// </remarks>
        private const int WindowDays = 90;

        private const int MaxEvents = 2000;

        private const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
        private const string MemoryTestProvider = "Microsoft-Windows-MemoryDiagnostics-Results";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.HardwareErrors,
            DisplayName = "Erreurs matérielles signalées",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(1.5),
            HardTimeout = TimeSpan.FromSeconds(45),
            Isolation = IsolationMode.SeparateProcess,
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var errors = ReadHardwareErrors(cancellationToken);
            var memoryTest = ReadMemoryTest(cancellationToken);
            var dumps = CrashDumpScanner.Scan(context);

            var corrected = 0;
            var uncorrected = 0;
            foreach (var group in errors.Groups)
            {
                if (group.Kind == HardwareErrorKind.Uncorrected) uncorrected += group.Count;
                else if (group.Kind == HardwareErrorKind.Corrected) corrected += group.Count;
            }

            context.Draft.SetHardwareErrors(new HardwareErrorsInfo
            {
                WindowDays = errors.Failed
                    ? Measured.Missing<int>(errors.Reason ?? "Le journal Système n'a pas pu être lu.")
                    : Measured.Ok(WindowDays, DataSource.EventLog),
                Errors = errors.Groups,
                CorrectedCount = errors.Failed
                    ? Measured.Missing<int>(errors.Reason ?? "Le journal Système n'a pas pu être lu.")
                    : Measured.Ok(corrected, DataSource.EventLog),
                UncorrectedCount = errors.Failed
                    ? Measured.Missing<int>(errors.Reason ?? "Le journal Système n'a pas pu être lu.")
                    : Measured.Ok(uncorrected, DataSource.EventLog),
                LastMemoryTest = memoryTest.Run,
                MemoryTest = memoryTest.Failed
                    ? Measured.Missing<MemoryTestOutcome>(
                        memoryTest.Reason ?? "Le résultat du test mémoire n'a pas pu être lu.")
                    : Measured.Ok(memoryTest.Run?.Outcome ?? MemoryTestOutcome.NeverRun, DataSource.EventLog),
                CrashDumpsEnabled = dumps.Enabled,
                CrashDumpMode = dumps.Mode,
                CrashDumps = dumps.Dumps,
            });

            if (errors.Failed && memoryTest.Failed)
                return Task.FromResult(ProbeOutcome.Failed(errors.Reason ?? "Le journal Système n'a pas pu être lu."));

            var parts = new List<string>();
            if (uncorrected > 0) parts.Add(uncorrected + " erreur(s) matérielle(s) irrécupérable(s)");
            if (corrected > 0) parts.Add(corrected + " erreur(s) matérielle(s) corrigée(s)");
            if (memoryTest.Run?.Outcome == MemoryTestOutcome.ErrorsFound) parts.Add("test mémoire en échec");
            if (dumps.Dumps.Count > 0) parts.Add(dumps.Dumps.Count + " rapport(s) de plantage");

            var message = parts.Count == 0
                ? "Aucune erreur matérielle signalée sur les " + WindowDays + " derniers jours."
                : string.Join(", ", parts.ToArray()) + ".";

            return Task.FromResult(errors.Failed || memoryTest.Failed
                ? ProbeOutcome.Partial(message + " Une partie du journal n'a pas pu être lue.")
                : ProbeOutcome.Ok(message));
        }

        // ------------------------------------------------------------------ erreurs matérielles

        private static (IReadOnlyList<HardwareErrorGroup> Groups, bool Failed, string? Reason)
            ReadHardwareErrors(CancellationToken cancellationToken)
        {
            var grouped = new Dictionary<int, MutableGroup>();

            // Filtrage par fournisseur : sur une machine saine la requête ne renvoie rien, et
            // n'a donc rien à parcourir. Aucun filtre de niveau, contrairement au module des
            // journaux : c'est tout l'objet de cette sonde.
            var xpath =
                "*[System[Provider[@Name='" + WheaProvider + "'] and TimeCreated[timediff(@SystemTime) <= " +
                ((long)WindowDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]]]";

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < MaxEvents; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record)
                    {
                        var when = record.TimeCreated ?? DateTime.Now;
                        if (!grouped.TryGetValue(record.Id, out var group))
                        {
                            grouped[record.Id] = new MutableGroup
                            {
                                EventId = record.Id,
                                Kind = WheaCatalog.Classify(record.Id, record.Level),
                                Count = 1,
                                First = when,
                                Last = when,
                                Sample = SafeDescription(record),
                            };
                        }
                        else
                        {
                            group.Count++;
                            if (when < group.First) group.First = when;
                            if (when > group.Last) group.Last = when;
                        }
                    }
                }
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return (Array.Empty<HardwareErrorGroup>(), true,
                    "Les erreurs matérielles n'ont pas pu être lues (" + ex.GetType().Name + ").");
            }

            var groups = new List<HardwareErrorGroup>(grouped.Count);
            foreach (var group in grouped.Values) groups.Add(group.ToGroup());

            // Les irrécupérables d'abord : c'est ce que le technicien doit voir en premier.
            groups.Sort((a, b) =>
            {
                var byKind = Rank(b.Kind).CompareTo(Rank(a.Kind));
                return byKind != 0 ? byKind : b.Count.CompareTo(a.Count);
            });

            return (groups, false, null);
        }

        private static int Rank(HardwareErrorKind kind) => kind switch
        {
            HardwareErrorKind.Uncorrected => 3,
            HardwareErrorKind.Corrected => 2,
            HardwareErrorKind.Informational => 1,
            _ => 0,
        };

        // ------------------------------------------------------------------ test mémoire

        /// <summary>
        /// Dernier résultat du diagnostic mémoire de Windows, sans limite d'ancienneté.
        /// </summary>
        /// <remarks>
        /// Un test passé il y a deux ans reste une information : il dit que quelqu'un s'est déjà
        /// posé la question, et sa date se compare à celle des plantages. Seuls les identifiants
        /// portent le verdict : un test annulé ou incomplet ne vaut pas un test réussi, et c'est
        /// exactement le contresens qu'une lecture du seul niveau aurait produit.
        /// </remarks>
        private static (MemoryTestRun? Run, bool Failed, string? Reason) ReadMemoryTest(
            CancellationToken cancellationToken)
        {
            var xpath = "*[System[Provider[@Name='" + MemoryTestProvider + "']]]";

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < 20; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record)
                    {
                        var outcome = MemoryTestCatalog.Classify(record.Id);
                        if (outcome == MemoryTestOutcome.Unknown) continue;

                        return (new MemoryTestRun
                        {
                            Date = record.TimeCreated ?? DateTime.Now,
                            EventId = record.Id,
                            Outcome = outcome,
                            Detail = SafeDescription(record),
                        }, false, null);
                    }
                }
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return (null, true, "Le résultat du test mémoire n'a pas pu être lu (" + ex.GetType().Name + ").");
            }

            return (null, false, null);
        }

        private static string? SafeDescription(EventRecord record)
        {
            try
            {
                var description = record.FormatDescription();
                if (string.IsNullOrWhiteSpace(description)) return null;
                var single = description!.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return single.Length > 400 ? single.Substring(0, 400) + "…" : single;
            }
            catch (Exception ex) when (ex is EventLogException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed class MutableGroup
        {
            public int EventId;
            public HardwareErrorKind Kind;
            public int Count;
            public DateTime First;
            public DateTime Last;
            public string? Sample;

            public HardwareErrorGroup ToGroup() => new HardwareErrorGroup
            {
                EventId = EventId,
                Kind = Kind,
                Count = Count,
                FirstSeen = First,
                LastSeen = Last,
                Sample = Sample,
            };
        }
    }

    /// <summary>
    /// Table des identifiants du journal des erreurs matérielles.
    /// </summary>
    /// <remarks>
    /// Relevée sur le manifeste du fournisseur (<c>Get-WinEvent -ListProvider</c>), et non
    /// déduite : chaque identifiant y est déclaré avec son niveau et sa phrase. L'événement 29
    /// est la raison d'être de cette table : Microsoft le déclare en Avertissement alors que son
    /// texte annonce une erreur irrécupérable. Le niveau reste le repli pour tout identifiant
    /// qu'une version future ajouterait.
    /// </remarks>
    internal static class WheaCatalog
    {
        private static readonly HashSet<int> Uncorrected =
            new HashSet<int> { 1, 16, 18, 20, 22, 24, 26, 29, 40, 42, 44, 46, 48 };

        private static readonly HashSet<int> Corrected =
            new HashSet<int> { 2, 17, 19, 21, 23, 25, 27, 28, 41, 43, 45, 47, 49 };

        public static HardwareErrorKind Classify(int eventId, byte? level)
        {
            if (Uncorrected.Contains(eventId)) return HardwareErrorKind.Uncorrected;
            if (Corrected.Contains(eventId)) return HardwareErrorKind.Corrected;

            // 1 = Critique, 2 = Erreur, 3 = Avertissement, 4 = Information.
            return level switch
            {
                1 => HardwareErrorKind.Uncorrected,
                2 => HardwareErrorKind.Uncorrected,
                3 => HardwareErrorKind.Corrected,
                4 => HardwareErrorKind.Informational,
                _ => HardwareErrorKind.Unknown,
            };
        }
    }

    /// <summary>
    /// Table des identifiants du diagnostic mémoire de Windows.
    /// </summary>
    /// <remarks>
    /// Relevée sur le manifeste du fournisseur. Les identifiants 1101 et 1201 annoncent un test
    /// sans erreur, 1102 et 1202 un test qui en a trouvé, 1103 un test annulé et 1104 un test
    /// qui n'a pas pu aller au bout : ces deux derniers ne concluent rien, et les confondre avec
    /// un test réussi reviendrait à déclarer saine une mémoire que personne n'a fini de tester.
    /// </remarks>
    internal static class MemoryTestCatalog
    {
        public static MemoryTestOutcome Classify(int eventId) => eventId switch
        {
            1101 => MemoryTestOutcome.NoErrors,
            1201 => MemoryTestOutcome.NoErrors,
            1102 => MemoryTestOutcome.ErrorsFound,
            1202 => MemoryTestOutcome.ErrorsFound,
            1103 => MemoryTestOutcome.Interrupted,
            1104 => MemoryTestOutcome.Interrupted,
            _ => MemoryTestOutcome.Unknown,
        };
    }

    /// <summary>
    /// Inventaire des rapports de plantage présents sur le disque.
    /// </summary>
    /// <remarks>
    /// Les chemins sont lus dans le registre plutôt que devinés : ils sont configurables, et un
    /// technicien qui les a déplacés ne doit pas s'entendre dire qu'il n'y a rien à analyser.
    /// Seuls le chemin, la date et la taille sont relevés : le contenu n'est jamais ouvert.
    /// </remarks>
    internal static class CrashDumpScanner
    {
        private const string CrashControlKey = @"SYSTEM\CurrentControlSet\Control\CrashControl";

        /// <summary>Au-delà, la liste cesse d'être lisible et le compte suffit.</summary>
        private const int MaxDumps = 50;

        public static (IReadOnlyList<CrashDump> Dumps, Measured<bool> Enabled, Measured<string> Mode) Scan(
            ProbeContext context)
        {
            var mode = context.Registry.ReadInt32(RegistryHive.LocalMachine, CrashControlKey, "CrashDumpEnabled");

            var enabled = mode.HasValue
                ? Measured.Ok(mode.Value != 0, DataSource.Registry)
                : Measured.Missing<bool>("Le réglage des rapports de plantage n'a pas pu être lu.");

            var modeLabel = mode.HasValue
                ? Measured.Ok(DescribeMode(mode.Value), DataSource.Registry)
                : Measured.Missing<string>("Le réglage des rapports de plantage n'a pas pu être lu.");

            var dumps = new List<CrashDump>();

            var full = Expand(context.Registry.ReadString(RegistryHive.LocalMachine, CrashControlKey, "DumpFile"))
                       ?? Path.Combine(WindowsDirectory(), "MEMORY.DMP");
            AddFile(dumps, full, isFull: true);

            var mini = Expand(context.Registry.ReadString(RegistryHive.LocalMachine, CrashControlKey, "MinidumpDir"))
                       ?? Path.Combine(WindowsDirectory(), "Minidump");
            AddFolder(dumps, mini);

            dumps.Sort((a, b) => b.Date.CompareTo(a.Date));
            if (dumps.Count > MaxDumps) dumps.RemoveRange(MaxDumps, dumps.Count - MaxDumps);

            return (dumps, enabled, modeLabel);
        }

        private static void AddFile(ICollection<CrashDump> dumps, string path, bool isFull)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                dumps.Add(new CrashDump
                {
                    Path = info.FullName,
                    Date = info.LastWriteTime,
                    SizeBytes = info.Length,
                    IsFullDump = isFull,
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Un rapport illisible n'est pas une panne de la machine : il ne figure pas à
                // l'inventaire, et le technicien le verra dans l'explorateur s'il le cherche.
            }
        }

        private static void AddFolder(ICollection<CrashDump> dumps, string folder)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) return;
                files = Directory.GetFiles(folder, "*.dmp");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return;
            }

            foreach (var file in files) AddFile(dumps, file, isFull: false);
        }

        private static string WindowsDirectory()
            => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        private static string? Expand(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value!);

        /// <summary>Valeurs de <c>CrashDumpEnabled</c>, telles que Windows les documente.</summary>
        private static string DescribeMode(int mode) => mode switch
        {
            0 => "Aucun rapport",
            1 => "Vidage complet de la mémoire",
            2 => "Vidage du noyau",
            3 => "Mini-vidage",
            7 => "Vidage automatique",
            _ => "Réglage " + mode.ToString(CultureInfo.CurrentCulture),
        };
    }
}
