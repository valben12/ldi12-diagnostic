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
    /// Stabilité dans le temps : ce qui plante sur cette machine, et depuis quand.
    /// </summary>
    /// <remarks>
    /// <b>« Depuis quand ? » est la première question du client.</b> Tout le reste de ce logiciel
    /// mesure un état, l'état du disque, l'état des services, l'état de la mémoire. Ce module
    /// mesure une histoire : quatre-vingt-dix jours de plantages de programmes, de blocages et
    /// de services tombés, chacun daté. C'est ce qui permet de distinguer une machine qui a
    /// toujours été comme ça d'une machine qui s'est mise à aller mal.
    /// <para>
    /// Les noms sont lus dans les données de l'événement, jamais dans sa description : Windows
    /// place le nom du programme fautif dans un champ numéroté que la traduction ne touche pas,
    /// et la phrase qui l'entoure change d'une langue à l'autre. C'est la même règle que pour les
    /// tâches planifiées et pour les journaux d'événements.
    /// </para>
    /// <para>
    /// Aucun code d'exception n'est retenu. Le module fautif, lui, l'est : il désigne un pilote
    /// ou une bibliothèque, il se lit, et c'est presque toujours la meilleure piste du relevé.
    /// </para>
    /// </remarks>
    public sealed class StabilityProbe : IDiagnosticProbe
    {
        private const int WindowDays = 90;

        /// <summary>Plafond de lecture : sur une machine très abîmée, tout lire prendrait des minutes.</summary>
        private const int MaxEvents = 4000;

        /// <summary>Au-delà, la chronologie ne se lit plus et le relevé cesse d'être transportable.</summary>
        private const int MaxIncidents = 400;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Stability,
            DisplayName = "Stabilité dans le temps",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(4),
            HardTimeout = TimeSpan.FromSeconds(60),
            Isolation = IsolationMode.SeparateProcess,
            FullScanOnly = true,
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var reading = new Reading();

            var application = Read(reading, "Application",
                "*[System[((Provider[@Name='Application Error'] and EventID=1000) or " +
                "(Provider[@Name='Application Hang'] and EventID=1002))" + Since() + "]]",
                cancellationToken);

            var system = Read(reading, "System",
                "*[System[Provider[@Name='Service Control Manager'] and " +
                "(EventID=7031 or EventID=7034)" + Since() + "]]",
                cancellationToken);

            if (application != null && system != null)
            {
                context.Draft.SetStability(new StabilityInfo
                {
                    WindowDays = Measured.Missing<int>(application),
                    OldestEntry = Measured.Missing<DateTimeOffset>(application),
                    ProgramCrashes = Measured.Missing<int>(application),
                    ProgramHangs = Measured.Missing<int>(application),
                    ServiceCrashes = Measured.Missing<int>(system),
                });
                return Task.FromResult(ProbeOutcome.Failed(
                    "L'historique de stabilité n'a pas pu être lu : " + application));
            }

            var programs = reading.Programs();
            var incidents = reading.Incidents;
            incidents.Sort((a, b) => b.Date.CompareTo(a.Date));
            if (incidents.Count > MaxIncidents) incidents.RemoveRange(MaxIncidents, incidents.Count - MaxIncidents);

            context.Draft.SetStability(new StabilityInfo
            {
                WindowDays = Measured.Ok(WindowDays, DataSource.EventLog),
                OldestEntry = reading.Oldest.HasValue
                    ? Measured.Ok(reading.Oldest.Value, DataSource.EventLog)
                    : Measured.Missing<DateTimeOffset>(
                        "Aucun incident enregistré : la période réellement couverte par les journaux " +
                        "ne peut pas être établie."),
                Programs = programs,
                Incidents = incidents,
                ProgramCrashes = Count(application, reading.Crashes),
                ProgramHangs = Count(application, reading.Hangs),
                ServiceCrashes = Count(system, reading.ServiceCrashes),
            });

            var total = reading.Crashes + reading.Hangs + reading.ServiceCrashes;
            var message = total == 0
                ? "Aucun plantage enregistré sur les " + WindowDays + " derniers jours."
                : total + " incident(s) sur " + WindowDays + " jours, " + programs.Count +
                  " programme(s) ou service(s) concerné(s).";

            return Task.FromResult(application != null || system != null
                ? ProbeOutcome.Partial(message + " Un journal n'a pas pu être lu.")
                : ProbeOutcome.Ok(message));
        }

        private static Measured<int> Count(string? failure, int value)
            => failure == null ? Measured.Ok(value, DataSource.EventLog) : Measured.Missing<int>(failure);

        /// <summary>Le module fautif, ou rien quand Windows ne l'a pas su.</summary>
        /// <remarks>
        /// Windows écrit littéralement <c>unknown</c> lorsqu'il n'a pas identifié le module, vu
        /// sur la machine d'essai pour l'outil Capture d'écran. Recopié tel quel, ce mot se
        /// serait affiché dans la colonne « composant mis en cause » et se serait lu comme le nom
        /// d'un composant. C'est une absence de mesure, et elle est traitée comme telle.
        /// </remarks>
        internal static string? NormalizeModule(string? raw)
        {
            var value = raw?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            return string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        private static string Since()
            => " and TimeCreated[timediff(@SystemTime) <= " +
               ((long)WindowDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "]";

        /// <summary>Lit un journal. Rend le motif de l'échec, ou null si la lecture a abouti.</summary>
        private static string? Read(
            Reading reading, string logName, string xpath, CancellationToken cancellationToken)
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < MaxEvents; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record) reading.Accept(record);
                }

                return null;
            }
            catch (Exception ex) when (
                ex is EventLogNotFoundException || ex is EventLogException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return "Le journal « " + logName + " » n'a pas pu être lu (" + ex.GetType().Name + ").";
            }
        }

        /// <summary>
        /// Ce qu'une lecture accumule : les incidents datés d'un côté, leur regroupement de l'autre.
        /// </summary>
        /// <remarks>
        /// Les deux sont construits dans la même passe parce qu'ils viennent des mêmes événements.
        /// Reconstituer la chronologie à partir des compteurs reviendrait à l'inventer, et
        /// recompter les incidents à partir de la chronologie tronquée les sous-estimerait.
        /// </remarks>
        private sealed class Reading
        {
            private readonly Dictionary<string, Group> _groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

            internal List<Incident> Incidents { get; } = new List<Incident>();

            internal DateTimeOffset? Oldest { get; private set; }

            internal int Crashes { get; private set; }

            internal int Hangs { get; private set; }

            internal int ServiceCrashes { get; private set; }

            internal void Accept(EventRecord record)
            {
                var kind = Classify(record);
                if (kind == IncidentKind.Unknown) return;

                var name = Subject(record);
                if (name == null) return;

                var when = new DateTimeOffset(record.TimeCreated ?? DateTime.Now);
                if (Oldest == null || when < Oldest.Value) Oldest = when;

                switch (kind)
                {
                    case IncidentKind.ProgramCrash: Crashes++; break;
                    case IncidentKind.ProgramHang: Hangs++; break;
                    case IncidentKind.ServiceCrash: ServiceCrashes++; break;
                }

                Incidents.Add(new Incident { Date = when, Kind = kind, Subject = name });

                var key = (int)kind + "|" + name;
                if (!_groups.TryGetValue(key, out var group))
                {
                    _groups[key] = new Group
                    {
                        Name = name,
                        Kind = kind,
                        Count = 1,
                        First = when,
                        Last = when,
                        Module = Module(record, kind),
                    };
                }
                else
                {
                    group.Count++;
                    if (when < group.First) group.First = when;
                    if (when > group.Last) group.Last = when;
                    group.Module ??= Module(record, kind);
                }
            }

            internal List<FailingProgram> Programs()
            {
                var list = new List<FailingProgram>(_groups.Count);
                foreach (var group in _groups.Values)
                    list.Add(new FailingProgram
                    {
                        Name = group.Name,
                        Kind = group.Kind,
                        Count = group.Count,
                        FirstSeen = group.First,
                        LastSeen = group.Last,
                        Module = group.Module,
                    });

                list.Sort((a, b) => b.Count.CompareTo(a.Count));
                return list;
            }

            private static IncidentKind Classify(EventRecord record) => record.Id switch
            {
                1000 => IncidentKind.ProgramCrash,
                1002 => IncidentKind.ProgramHang,
                7031 => IncidentKind.ServiceCrash,
                7034 => IncidentKind.ServiceCrash,
                _ => IncidentKind.Unknown,
            };

            /// <summary>
            /// Le nom du programme ou du service, lu dans les données de l'événement.
            /// </summary>
            /// <remarks>
            /// Position zéro dans les trois cas : c'est ce que déclarent les manifestes de
            /// « Application Error », « Application Hang » et du gestionnaire de services. La
            /// description, elle, est traduite et n'est jamais analysée.
            /// </remarks>
            private static string? Subject(EventRecord record)
            {
                var value = Property(record, 0);
                if (string.IsNullOrWhiteSpace(value)) return null;

                // Un service tombé porte son nom d'affichage, déjà lisible ; un programme porte
                // son nom de fichier, qu'on garde tel quel : c'est celui que le technicien
                // retrouvera dans le gestionnaire des tâches.
                return value!.Trim();
            }

            /// <summary>Le composant nommé par Windows, quand il l'est.</summary>
            /// <remarks>
            /// Position trois du manifeste de « Application Error ». Les blocages et les services
            /// n'en déclarent pas : la colonne reste vide plutôt que d'être remplie au hasard.
            /// <para>
            /// Windows écrit littéralement <c>unknown</c> lorsqu'il n'a pas su identifier le
            /// module, vu sur la machine d'essai pour l'outil Capture d'écran. Recopié tel quel,
            /// ce mot se serait affiché dans la colonne « composant mis en cause » et se serait lu
            /// comme le nom d'un composant. C'est une absence de mesure, et elle est traitée
            /// comme telle.
            /// </para>
            /// </remarks>
            private static string? Module(EventRecord record, IncidentKind kind)
            {
                if (kind != IncidentKind.ProgramCrash) return null;

                return NormalizeModule(Property(record, 3));
            }

            private static string? Property(EventRecord record, int index)
            {
                try
                {
                    var properties = record.Properties;
                    if (properties == null || index >= properties.Count) return null;
                    return properties[index]?.Value?.ToString();
                }
                catch (EventLogException)
                {
                    // Événement dont le manifeste n'est plus installé : les données restent
                    // illisibles, et l'incident est écarté plutôt que nommé de travers.
                    return null;
                }
            }

            private sealed class Group
            {
                internal string Name = string.Empty;
                internal IncidentKind Kind;
                internal int Count;
                internal DateTimeOffset First;
                internal DateTimeOffset Last;
                internal string? Module;
            }
        }
    }
}
