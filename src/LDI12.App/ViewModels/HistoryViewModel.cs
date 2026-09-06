using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Engine.Comparison;
using LDI12.Reports;
using LDI12.Reports.History;

namespace LDI12.App.ViewModels
{
    /// <summary>Un diagnostic archivé, tel qu'il s'affiche dans la liste.</summary>
    public sealed class HistoryItem
    {
        public HistoryItem(HistoryEntry entry)
        {
            Entry = entry;
            Date = entry.CreatedAt.ToString("dddd d MMMM yyyy à HH:mm", CultureInfo.CurrentCulture);
            Machine = string.IsNullOrWhiteSpace(entry.MachineName) ? "Machine non nommée" : entry.MachineName;
            Mode = entry.RunMode == RunMode.Quick ? "Analyse rapide" : "Analyse complète";
            Size = ValueFormat.Bytes(entry.SizeBytes);
            Score = entry.Score.HasValue
                ? entry.Score.Value.ToString(CultureInfo.CurrentCulture) + " / 100"
                : "Sans score";
            Findings = entry.FindingCount + (entry.FindingCount > 1 ? " constats" : " constat");
            Reference = string.IsNullOrWhiteSpace(entry.ClientReference) ? null : entry.ClientReference;
            Readable = entry.Readable;
            Problem = entry.Problem;
        }

        public HistoryEntry Entry { get; }
        public string Date { get; }
        public string Machine { get; }
        public string Mode { get; }
        public string Size { get; }
        public string Score { get; }
        public string Findings { get; }
        public string? Reference { get; }
        public bool Readable { get; }
        public string? Problem { get; }

        /// <summary>Ce qui s'affiche dans les listes déroulantes de comparaison.</summary>
        public string Label => Date + " : " + Machine + " (" + Score + ")";
    }

    /// <summary>Un changement de constat, tel qu'il s'affiche.</summary>
    public sealed class ChangeItem
    {
        public ChangeItem(FindingChange change)
        {
            Title = change.Finding.Title;
            Detail = change.Finding.TechnicalDetail;
            RuleId = change.Finding.RuleId;
            Subject = change.Finding.Subject;
            SeverityLabel = Labels.Describe(change.Finding.Severity);
            Severity = change.Finding.Severity;
            Note = change.Note;

            if (change.PreviousSeverity.HasValue)
            {
                Note = "Gravité passée de « " + Labels.Describe(change.PreviousSeverity.Value) +
                       " » à « " + SeverityLabel + " ».";
            }

            HasNote = !string.IsNullOrWhiteSpace(Note);
        }

        public string Title { get; }
        public string Detail { get; }
        public string RuleId { get; }
        public string? Subject { get; }
        public string SeverityLabel { get; }
        public Severity Severity { get; }
        public string? Note { get; }
        public bool HasNote { get; }
    }

    /// <summary>Une mesure suivie d'un diagnostic à l'autre.</summary>
    public sealed class MeasureItem
    {
        public MeasureItem(MeasureChange change)
        {
            Label = change.Label;
            Before = Format(change.Before, change.Unit);
            After = Format(change.After, change.Unit);
            Note = change.Note;
            HasNote = !string.IsNullOrWhiteSpace(Note);

            switch (change.Direction)
            {
                case ChangeDirection.Improved:
                    Verdict = "Amélioration";
                    Delta = Signed(change.Delta, change.Unit);
                    break;
                case ChangeDirection.Worsened:
                    Verdict = "Dégradation";
                    Delta = Signed(change.Delta, change.Unit);
                    break;
                case ChangeDirection.Unchanged:
                    Verdict = "Inchangé";
                    Delta = string.Empty;
                    break;
                default:
                    Verdict = "Non comparable";
                    Delta = string.Empty;
                    break;
            }

            Direction = change.Direction;
        }

        public string Label { get; }
        public string Before { get; }
        public string After { get; }
        public string Delta { get; }
        public string Verdict { get; }
        public ChangeDirection Direction { get; }
        public string? Note { get; }
        public bool HasNote { get; }

        private static string Format(Measured<double> measured, string unit)
        {
            if (!measured.HasValue) return "Non mesuré";
            var value = measured.Value.ToString(Math.Abs(measured.Value) < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
            return unit.Length == 0 ? value : value + " " + unit;
        }

        private static string Signed(double? delta, string unit)
        {
            if (!delta.HasValue) return string.Empty;

            // Le vrai signe moins, comme dans les rapports : un document se lit, il ne se
            // console-log pas.
            var value = Math.Abs(delta.Value).ToString(
                Math.Abs(delta.Value) < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
            var sign = delta.Value < 0 ? "−" : "+";
            return sign + value + (unit.Length == 0 ? string.Empty : " " + unit);
        }
    }

    /// <summary>
    /// Historique local et comparaison avant / après.
    /// </summary>
    /// <remarks>
    /// L'écran qui répond à « qu'est-ce que votre intervention a changé ? ». Deux principes le
    /// gouvernent, et ils tirent dans le même sens.
    ///
    /// <b>Rien n'est archivé sans un geste.</b> Un diagnostic contient des numéros de série, des
    /// noms de comptes, les réseaux Wi-Fi du logement : c'est de la donnée client, et elle
    /// resterait sur la clé USB de l'atelier. L'archivage est donc un bouton, jamais un effet de
    /// bord d'une analyse, et la suppression aussi, avec le nom du fichier sous les yeux.
    ///
    /// <b>Ce que la comparaison ne sait pas, elle le dit.</b> Les réserves s'affichent au-dessus
    /// du résultat et non en bas de page : un score qui monte parce que le second diagnostic a
    /// moins regardé n'est pas une réparation.
    /// </remarks>
    public sealed class HistoryViewModel : ObservableObject
    {
        private const string Category = "App.History";

        private readonly HistoryStore _store;
        private readonly ILdiLogger _logger;

        /// <summary>
        /// Dossier d'export, emprunté à l'écran Rapports.
        /// </summary>
        /// <remarks>
        /// Et non un second dossier propre à cet écran : le comparatif se remet au client avec le
        /// bilan, et deux destinations différentes dans une même application feraient chercher un
        /// fichier dans le mauvais dossier au mauvais moment.
        /// </remarks>
        private readonly Func<string> _exportDirectory;

        private SystemSnapshot? _current;
        private HistoryItem? _selected;
        private bool _isConfirmingDelete;
        // La ligne d'état dit à quoi sert l'écran tant qu'aucune action n'a eu lieu ; elle ne
        // décrit pas le contenu du dossier, dont le cadre d'archivage rend déjà compte.
        private string _status =
            "Un diagnostic archivé avant l'intervention, un autre après : l'écart entre les deux est " +
            "ce qu'il reste à montrer au client.";
        private string _folderSummary = string.Empty;
        private SnapshotDelta? _delta;
        private string? _lastExport;

        public HistoryViewModel(ILdiLogger logger, Func<string>? exportDirectory = null, HistoryStore? store = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _store = store ?? HistoryStore.Default();
            _exportDirectory = exportDirectory ?? (() => System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LDI12 Diagnostic"));

            ArchiveCommand = new RelayCommand(Archive, () => _current != null);
            RefreshCommand = new RelayCommand(Refresh);
            CompareCommand = new RelayCommand(Compare, CanCompare);
            RequestDeleteCommand = new RelayCommand(() => IsConfirmingDelete = true, () => _selected != null);
            ConfirmDeleteCommand = new RelayCommand(Delete, () => _selected != null);
            AbandonDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false, () => IsConfirmingDelete);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            ExportCommand = new RelayCommand(Export, () => _delta != null);

            Refresh();
        }

        public ObservableCollection<HistoryItem> Entries { get; } = new ObservableCollection<HistoryItem>();

        public ObservableCollection<ChangeItem> Resolved { get; } = new ObservableCollection<ChangeItem>();
        public ObservableCollection<ChangeItem> Appeared { get; } = new ObservableCollection<ChangeItem>();
        public ObservableCollection<ChangeItem> Persisting { get; } = new ObservableCollection<ChangeItem>();
        public ObservableCollection<ChangeItem> NotRechecked { get; } = new ObservableCollection<ChangeItem>();
        public ObservableCollection<MeasureItem> Measures { get; } = new ObservableCollection<MeasureItem>();
        public ObservableCollection<string> Caveats { get; } = new ObservableCollection<string>();

        public ICommand ArchiveCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CompareCommand { get; }
        public ICommand RequestDeleteCommand { get; }
        public ICommand ConfirmDeleteCommand { get; }
        public ICommand AbandonDeleteCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ExportCommand { get; }

        /// <summary>Libellé du diagnostic en cours, tel qu'il apparaît dans les listes.</summary>
        public string CurrentLabel { get; private set; } = "Diagnostic affiché (non archivé)";

        public string Status { get => _status; private set => Set(ref _status, value); }
        public string FolderSummary { get => _folderSummary; private set => Set(ref _folderSummary, value); }
        public string FolderPath => _store.Directory;

        public HistoryItem? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                IsConfirmingDelete = false;
                RaiseStates();
            }
        }

        public bool IsConfirmingDelete
        {
            get => _isConfirmingDelete;
            private set { if (Set(ref _isConfirmingDelete, value)) RaiseStates(); }
        }

        public bool HasEntries => Entries.Count > 0;
        public bool CanArchive => _current != null;

        // ---------- résultat de la comparaison

        public bool HasComparison => _delta != null;
        public string ScoreLine { get; private set; } = string.Empty;
        public string IdentityLine { get; private set; } = string.Empty;
        public string SummaryLine { get; private set; } = string.Empty;
        public bool HasCaveats => Caveats.Count > 0;

        /// <summary>Le diagnostic courant, réinjecté après chaque analyse.</summary>
        public void Apply(SystemSnapshot? snapshot)
        {
            _current = snapshot;

            CurrentLabel = snapshot == null
                ? "Diagnostic affiché (non archivé)"
                : "Diagnostic affiché : " +
                  snapshot.Metadata.CreatedAt.ToString("HH:mm", CultureInfo.CurrentCulture) +
                  (snapshot.Score != null ? " (" + snapshot.Score.Global + " / 100)" : string.Empty);

            Raise(nameof(CurrentLabel));
            RaiseStates();
        }

        private void Archive()
        {
            if (_current == null) return;

            try
            {
                var entry = _store.Archive(_current);
                Status = "Diagnostic archivé sous « " + entry.FileName + " ». Il pourra servir de point " +
                         "de comparaison après l'intervention.";
                Refresh();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Error(Category, "L'archivage a échoué.", ex);
                Status = "Le diagnostic n'a pas pu être archivé : " + ex.Message;
            }
        }

        private void Refresh()
        {
            Entries.Clear();
            long total = 0;
            var unreadable = 0;

            foreach (var entry in _store.List())
            {
                Entries.Add(new HistoryItem(entry));
                total += entry.SizeBytes;
                if (!entry.Readable) unreadable++;
            }

            FolderSummary = Entries.Count == 0
                ? "Aucun diagnostic archivé : le dossier est vide."
                : Entries.Count + (Entries.Count > 1 ? " diagnostics archivés, " : " diagnostic archivé, ") +
                  ValueFormat.Bytes(total) + " au total" +
                  (unreadable > 0 ? ", dont " + unreadable + " illisible(s)." : ".");

            Raise(nameof(HasEntries));
            RaiseStates();
        }

        /// <summary>
        /// Un seul geste de comparaison : l'archive choisie contre le diagnostic affiché.
        /// </summary>
        /// <remarks>
        /// C'est le déroulé réel d'une intervention : on archive en arrivant, on répare, on
        /// relance une analyse, on compare. Proposer de comparer deux archives entre elles aurait
        /// demandé deux listes de sélection à l'écran pour un besoin qui ne s'est pas présenté.
        /// </remarks>
        private bool CanCompare()
        {
            if (_current == null || _selected == null || !_selected.Readable) return false;

            // Comparer un diagnostic à lui-même n'apprend rien, et le proposer laisserait croire
            // que le résultat vide qui en sort veut dire quelque chose.
            return _selected.Entry.SnapshotId != _current.Metadata.SnapshotId;
        }

        /// <summary>Pourquoi la comparaison n'est pas proposée, affiché à la place du bouton.</summary>
        public string CompareReason
        {
            get
            {
                if (_current == null) return "Lancez une analyse : la comparaison se fait entre une archive et le diagnostic affiché.";
                if (_selected == null) return "Choisissez un diagnostic archivé dans la liste pour le comparer à celui affiché.";
                if (!_selected.Readable) return "Ce fichier n'est pas lisible : il ne peut pas servir de point de comparaison.";
                if (_selected.Entry.SnapshotId == _current.Metadata.SnapshotId)
                    return "C'est le diagnostic affiché lui-même : il n'y a rien à comparer.";
                return string.Empty;
            }
        }

        public bool CanCompareNow => CanCompare();

        private void Compare()
        {
            if (_current == null || _selected == null) return;

            try
            {
                var archived = _store.Load(_selected.Entry);

                // L'ordre chronologique décide lequel est « avant » : une comparaison présentée à
                // l'envers transformerait chaque réparation en dégradation.
                var archivedFirst = archived.Metadata.CreatedAt <= _current.Metadata.CreatedAt;
                var delta = archivedFirst
                    ? SnapshotComparer.Compare(archived, _current)
                    : SnapshotComparer.Compare(_current, archived);

                Show(delta);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                _logger.Error(Category, "La comparaison a échoué.", ex);
                Status = "La comparaison n'a pas pu être faite : " + ex.Message;
            }
        }

        private void Show(SnapshotDelta delta)
        {
            _delta = delta;

            Fill(Resolved, delta, FindingChangeKind.Resolved);
            Fill(Appeared, delta, FindingChangeKind.Appeared);
            Fill(Persisting, delta, FindingChangeKind.Persisting);
            Fill(NotRechecked, delta, FindingChangeKind.NotRechecked);

            Measures.Clear();
            foreach (var measure in delta.Measures) Measures.Add(new MeasureItem(measure));

            Caveats.Clear();
            foreach (var caveat in delta.Caveats) Caveats.Add(caveat);

            ScoreLine = DescribeScore(delta);
            IdentityLine = DescribeIdentity(delta);
            SummaryLine = DescribeSummary(delta);

            RaiseStates();
            Raise(nameof(HasComparison));
            Raise(nameof(HasCaveats));
            Raise(nameof(ScoreLine));
            Raise(nameof(IdentityLine));
            Raise(nameof(SummaryLine));
        }

        private static void Fill(
            ObservableCollection<ChangeItem> target, SnapshotDelta delta, FindingChangeKind kind)
        {
            target.Clear();
            foreach (var change in delta.Findings)
                if (change.Kind == kind) target.Add(new ChangeItem(change));
        }

        private static string DescribeScore(SnapshotDelta delta)
        {
            var before = delta.Before.Score?.ToString(CultureInfo.CurrentCulture) ?? "sans score";
            var after = delta.After.Score?.ToString(CultureInfo.CurrentCulture) ?? "sans score";

            if (!delta.ScoreComparable)
                return "Score " + before + " puis " + after + " : non comparables, voir les réserves.";

            var change = delta.ScoreDelta ?? 0;
            if (change == 0) return "Score inchangé : " + after + " / 100.";

            return "Score " + before + " → " + after + " / 100 (" +
                   (change > 0 ? "+" : "−") + Math.Abs(change) + " points).";
        }

        private static string DescribeIdentity(SnapshotDelta delta)
        {
            if (!delta.SameMachine.HasValue)
                return "Machine non confirmée : " + delta.SameMachine.Reason;

            if (!delta.SameMachine.Value)
                return "Attention, ces deux diagnostics ne viennent pas de la même machine.";

            return delta.SameMachine.IsReliable
                ? "Même machine, confirmée par son empreinte."
                : "Même machine, sous réserve : " + delta.SameMachine.Reason;
        }

        private static string DescribeSummary(SnapshotDelta delta)
        {
            if (delta.NothingChanged)
                return "Rien n'a changé entre ces deux diagnostics, et tous les contrôles ont été rejoués.";

            var parts = new List<string>();
            Append(parts, delta.Count(FindingChangeKind.Resolved), "constat réglé", "constats réglés");
            Append(parts, delta.Count(FindingChangeKind.Appeared), "constat nouveau", "constats nouveaux");
            Append(parts, delta.Count(FindingChangeKind.Persisting), "constat inchangé", "constats inchangés");
            Append(parts, delta.Count(FindingChangeKind.NotRechecked), "constat non revérifié", "constats non revérifiés");

            return parts.Count == 0 ? "Aucun constat de part et d'autre." : string.Join(", ", parts) + ".";
        }

        private static void Append(ICollection<string> parts, int count, string singular, string plural)
        {
            if (count > 0) parts.Add(count + " " + (count > 1 ? plural : singular));
        }

        private void Delete()
        {
            var item = _selected;
            if (item == null) return;

            try
            {
                _store.Delete(item.Entry);
                Status = "Diagnostic « " + item.Entry.FileName + " » supprimé du dossier d'historique.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Error(Category, "La suppression a échoué.", ex);
                Status = "Ce diagnostic n'a pas pu être supprimé : " + ex.Message;
            }
            finally
            {
                IsConfirmingDelete = false;
                Selected = null;
                Refresh();
            }
        }

        /// <summary>
        /// Écrit le comparatif en HTML autonome, dans le dossier des rapports.
        /// </summary>
        /// <remarks>
        /// Le document part chez le client : il est rédigé comme le bilan (sans identifiant de
        /// règle ni jargon) et il porte ses réserves en tête plutôt qu'en note de bas de page.
        /// </remarks>
        private void Export()
        {
            if (_delta == null) return;

            try
            {
                var exported = ReportExporter.ExportComparison(_delta, _exportDirectory());
                _lastExport = exported.Path;
                Status = "Comparatif écrit : " + System.IO.Path.GetFileName(exported.Path) +
                         " : il s'ouvre dans n'importe quel navigateur et s'imprime en PDF.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Error(Category, "Le comparatif n'a pas pu être écrit.", ex);
                Status = "Le comparatif n'a pas pu être écrit : " + ex.Message;
            }
        }

        private void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_store.Directory);
                Process.Start(new ProcessStartInfo(_store.Directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Warn(Category, "Le dossier d'historique n'a pas pu être ouvert.", ex);
                Status = "Le dossier n'a pas pu être ouvert : " + ex.Message;
            }
        }

        private void RaiseStates()
        {
            (ArchiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RequestDeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConfirmDeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AbandonDeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Raise(nameof(CanArchive));
            Raise(nameof(CanCompareNow));
            Raise(nameof(CompareReason));
        }
    }
}
