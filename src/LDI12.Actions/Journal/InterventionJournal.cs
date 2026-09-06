using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;

namespace LDI12.Actions.Journal
{
    public enum InterventionKind
    {
        /// <summary>Ce qui allait être fait a été établi et montré. Rien n'a été modifié.</summary>
        Preview = 0,

        /// <summary>Une action a été exécutée sur la machine.</summary>
        Execution = 1,

        /// <summary>Une console Windows a été ouverte depuis le diagnostic.</summary>
        ToolLaunch = 2,

        /// <summary>Un point de restauration a été demandé.</summary>
        RestorePoint = 3,
    }

    public sealed class InterventionEntry
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
        public InterventionKind Kind { get; init; }
        public string ActionId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;

        /// <summary>Conclusion telle qu'elle a été affichée au technicien.</summary>
        public string Summary { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
        public bool Elevated { get; init; }
        public TimeSpan Duration { get; init; }
        public long FreedBytes { get; init; }
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

        public string TimeLabel => At.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        public string KindLabel => Kind switch
        {
            InterventionKind.Preview => "Prévisualisation",
            InterventionKind.Execution => "Exécution",
            InterventionKind.ToolLaunch => "Ouverture d'outil",
            _ => "Point de restauration",
        };
    }

    /// <summary>
    /// Journal d'intervention : ce qui a été fait sur la machine, dans l'ordre.
    /// </summary>
    /// <remarks>
    /// Distinct du journal technique de l'application, qui trace le fonctionnement du logiciel.
    /// Celui-ci trace ce que la <b>machine du client</b> a subi, dans des phrases qu'on peut lui
    /// lire. C'est ce qui permet de répondre trois semaines plus tard à « qu'est-ce que vous avez
    /// touché ? » autrement que de mémoire.
    /// <para>
    /// Chaque entrée est écrite sur disque au moment où elle est enregistrée : un journal qui ne
    /// serait sauvegardé qu'à la fermeture serait vide précisément dans le cas où il compte,
    /// celui où quelque chose a mal tourné.
    /// </para>
    /// </remarks>
    public sealed class InterventionJournal
    {
        private const string Category = "Journal";

        private readonly List<InterventionEntry> _entries = new List<InterventionEntry>();
        private readonly object _gate = new object();
        private readonly ILdiLogger? _logger;
        private readonly string? _path;

        public InterventionJournal(ILdiLogger? logger = null, string? path = null)
        {
            _logger = logger;
            _path = path ?? DefaultPath();
        }

        public event EventHandler? Changed;

        /// <summary>Fichier où le journal est écrit au fil de l'eau.</summary>
        public string? Path => _path;

        public string Machine { get; set; } = Environment.MachineName;

        /// <summary>Renseigné par l'écran des rapports : c'est la même identité sur les deux documents.</summary>
        public string? Technician { get; set; }

        public IReadOnlyList<InterventionEntry> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public bool IsEmpty
        {
            get { lock (_gate) return _entries.Count == 0; }
        }

        public void Record(InterventionEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            lock (_gate) _entries.Add(entry);
            Append(entry);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Record(
            InterventionKind kind, string actionId, string title, string status, string summary,
            bool elevated = false, TimeSpan duration = default, long freedBytes = 0,
            IReadOnlyList<string>? details = null)
            => Record(new InterventionEntry
            {
                Kind = kind,
                ActionId = actionId,
                Title = title,
                Status = status,
                Summary = summary,
                Elevated = elevated,
                Duration = duration,
                FreedBytes = freedBytes,
                Details = details ?? Array.Empty<string>(),
            });

        /// <summary>Rend le journal sous forme lisible, repris tel quel dans le rapport technicien.</summary>
        public string Render()
        {
            var builder = new StringBuilder();
            builder.Append("Journal d'intervention : ").Append(Machine);
            if (!string.IsNullOrWhiteSpace(Technician)) builder.Append(" : ").Append(Technician);
            builder.AppendLine();
            builder.AppendLine(new string('-', 72));

            foreach (var entry in Entries) builder.AppendLine(Format(entry));

            if (IsEmpty) builder.AppendLine("Aucune intervention : la machine n'a été que lue.");
            return builder.ToString();
        }

        internal static string Format(InterventionEntry entry)
        {
            var builder = new StringBuilder();
            builder.Append(entry.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                   .Append("  ").Append(entry.KindLabel.PadRight(18))
                   .Append(entry.ActionId.PadRight(24))
                   .Append(entry.Status);

            if (entry.Elevated) builder.Append(" [administrateur]");
            if (entry.Duration > TimeSpan.Zero) builder.Append(" [").Append(ValueFormat.Duration(entry.Duration)).Append(']');
            if (entry.FreedBytes > 0) builder.Append(" [").Append(ValueFormat.Bytes(entry.FreedBytes)).Append(" libérés]");

            builder.AppendLine();
            builder.Append("    ").Append(entry.Summary);

            foreach (var detail in entry.Details) builder.AppendLine().Append("      · ").Append(detail);
            return builder.ToString();
        }

        private void Append(InterventionEntry entry)
        {
            if (_path == null) return;

            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

                var header = !File.Exists(_path)
                    ? "Journal d'intervention LDI12 : " + Machine + Environment.NewLine +
                      new string('-', 72) + Environment.NewLine
                    : string.Empty;

                File.AppendAllText(_path, header + Format(entry) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Le journal ne doit jamais faire échouer une intervention : on note l'incident
                // dans le journal technique et l'entrée reste au moins en mémoire.
                _logger?.Warn(Category, "Le journal d'intervention n'a pas pu être écrit : " + ex.Message);
            }
        }

        private static string? DefaultPath()
        {
            try
            {
                var root = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LDI12", "Diagnostic", "interventions");

                return System.IO.Path.Combine(
                    root,
                    "intervention-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                return null;
            }
        }
    }
}
