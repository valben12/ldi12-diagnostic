using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Maintenance;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;

namespace LDI12.App.ViewModels
{
    /// <summary>Une source de nettoyage à l'écran, avec ce que le dernier relevé y a trouvé.</summary>
    public sealed class CleanupSourceItem : ObservableObject
    {
        private readonly Action _changed;
        private bool _isSelected;
        private CleanupGroup? _group;

        public CleanupSourceItem(CleanupProvider provider, Action changed)
        {
            Provider = provider;
            _changed = changed;

            // Jamais coché d'avance quand la source contient des éléments produits par
            // l'utilisateur : une case pré-cochée dans un écran qu'on valide vite est une
            // suppression automatique déguisée.
            _isSelected = provider.SelectedByDefault;
        }

        public CleanupProvider Provider { get; }

        public string Title => Provider.Title;
        public string Explanation => Provider.Explanation;
        public string? Consequence => Provider.Consequence;
        public bool HasConsequence => Consequence != null;
        public bool ContainsUserData => Provider.ContainsUserData;

        public bool NeedsElevation => Provider.RequiresElevation;

        public string ScopeLabel => Provider.MinimumAge.HasValue
            ? "Seuls les fichiers de plus de " + ValueFormat.Duration(Provider.MinimumAge.Value) + " sont retenus."
            : string.Empty;

        public bool HasScopeLabel => ScopeLabel.Length > 0;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (Set(ref _isSelected, value)) _changed(); }
        }

        public bool HasResult => _group != null;

        public string ResultLabel
        {
            get
            {
                if (_group == null) return "Non analysé.";
                if (_group.Unavailable != null) return _group.Unavailable;
                if (_group.ItemCount == 0) return "Rien à supprimer.";

                return ValueFormat.Number(_group.ItemCount) + " élément(s) : " + ValueFormat.Bytes(_group.Bytes);
            }
        }

        public bool HasFiles => _group != null && _group.Files.Count > 0;

        /// <summary>
        /// Les chemins effectivement relevés.
        /// </summary>
        /// <remarks>
        /// Bornés à l'affichage : quelques centaines de lignes suffisent à voir <i>de quoi</i>
        /// il s'agit, et le décompte exact figure au-dessus. Ce qui compte est que rien ne soit
        /// supprimé qui ne figure pas dans le relevé, pas que le relevé tienne à l'écran.
        /// </remarks>
        public IReadOnlyList<string> Paths
        {
            get
            {
                var paths = new List<string>();
                foreach (var file in _group?.Files ?? Array.Empty<Core.Execution.FileEntry>())
                {
                    if (paths.Count >= 200) break;
                    paths.Add(file.Path);
                }
                return paths;
            }
        }

        public string PathsNote => _group == null
            ? string.Empty
            : _group.ItemCount > Paths.Count
                ? "200 premiers chemins affichés sur " + ValueFormat.Number(_group.ItemCount) + "."
                : string.Empty;

        public bool HasPathsNote => PathsNote.Length > 0;

        public void Apply(CleanupGroup? group)
        {
            _group = group;
            Raise(nameof(HasResult));
            Raise(nameof(ResultLabel));
            Raise(nameof(HasFiles));
            Raise(nameof(Paths));
            Raise(nameof(PathsNote));
            Raise(nameof(HasPathsNote));
        }
    }

    /// <summary>
    /// Écran de nettoyage.
    /// </summary>
    /// <remarks>
    /// Trois clics séparent le technicien d'une suppression, et chacun ajoute une information :
    /// « Analyser » relève et affiche, « Supprimer » réclame une confirmation qui répète les
    /// totaux, « Confirmer » exécute. Aucun de ces écrans ne peut être franchi sans avoir vu ce
    /// qui va partir ; c'est la consigne, et elle est portée par le type : l'exécution réclame
    /// l'objet produit par le relevé.
    /// </remarks>
    public sealed class CleanupViewModel : ObservableObject
    {
        private readonly Services.ActionService _actions;
        private readonly ILdiLogger _logger;
        private readonly Func<Task<ActionRunner?>> _runner;

        private IRepairAction? _action;
        private ActionPreview? _preview;
        private CancellationTokenSource? _running;
        private bool _isBusy;
        private bool _isConfirming;
        private string _status = "Choisissez ce qu'il y a lieu de nettoyer, puis lancez l'analyse.";
        private string _summary = string.Empty;
        private ActionOutcome? _outcome;

        public CleanupViewModel(Services.ActionService actions, ILdiLogger logger, Func<Task<ActionRunner?>> runner)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));

            foreach (var provider in CleanupCatalog.Create())
                Sources.Add(new CleanupSourceItem(provider, OnSelectionChanged));

            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && HasSelection);
            RequestDeleteCommand = new RelayCommand(() => IsConfirming = true, () => CanDelete);
            ConfirmDeleteCommand = new AsyncRelayCommand(DeleteAsync, () => CanDelete);
            AbandonCommand = new RelayCommand(() => IsConfirming = false, () => IsConfirming);
            CancelCommand = new RelayCommand(() => _running?.Cancel(), () => IsBusy);
        }

        public ObservableCollection<CleanupSourceItem> Sources { get; } =
            new ObservableCollection<CleanupSourceItem>();

        public ICommand AnalyzeCommand { get; }
        public ICommand RequestDeleteCommand { get; }
        public ICommand ConfirmDeleteCommand { get; }
        public ICommand AbandonCommand { get; }
        public ICommand CancelCommand { get; }

        public event EventHandler? JournalChanged;

        public string Status { get => _status; private set => Set(ref _status, value); }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }

        public bool HasSummary => Summary.Length > 0;

        public bool IsBusy
        {
            get => _isBusy;
            private set { if (Set(ref _isBusy, value)) RaiseStates(); }
        }

        public bool IsConfirming
        {
            get => _isConfirming;
            private set { if (Set(ref _isConfirming, value)) RaiseStates(); }
        }

        public bool CanDelete => _preview != null && _preview.CanExecute && !IsBusy;

        public bool HasSelection
        {
            get
            {
                foreach (var source in Sources)
                    if (source.IsSelected) return true;
                return false;
            }
        }

        /// <summary>Répété dans la confirmation : c'est ce que le client demandera.</summary>
        public IReadOnlyList<string> WillNotDo => _preview?.WillNotDo ?? Array.Empty<string>();

        public bool HasWillNotDo => WillNotDo.Count > 0;

        public bool ContainsUserData
        {
            get
            {
                foreach (var source in Sources)
                    if (source.IsSelected && source.ContainsUserData) return true;
                return false;
            }
        }

        public bool HasOutcome => _outcome != null;
        public string OutcomeSummary => _outcome?.Summary ?? string.Empty;
        public IReadOnlyList<string> OutcomeDetails => _outcome?.Details ?? Array.Empty<string>();
        public string OutcomeKey => _outcome == null
            ? "None"
            : _outcome.Succeeded ? (_outcome.Status == ActionStatus.PartiallySucceeded ? "Partial" : "Good") : "Bad";

        private void OnSelectionChanged()
        {
            // Un changement de sélection périme le relevé : ce qui a été montré ne correspond
            // plus à ce qui serait supprimé.
            _preview = null;
            IsConfirming = false;
            Summary = string.Empty;
            foreach (var source in Sources) source.Apply(null);

            Raise(nameof(HasSelection));
            Raise(nameof(ContainsUserData));
            Raise(nameof(HasSummary));
            RaiseStates();
        }

        private async Task AnalyzeAsync()
        {
            var runner = await _runner().ConfigureAwait(true);
            if (runner == null) return;

            _action ??= ActionCatalog.Find(ActionIds.Cleanup, _logger);
            if (_action == null) return;

            IsBusy = true;
            IsConfirming = false;
            _outcome = null;
            Status = "Relevé en cours : rien n'est supprimé à cette étape.";
            RaiseOutcome();

            _running?.Dispose();
            _running = new CancellationTokenSource();

            try
            {
                _preview = await runner
                    .PreviewAsync(_action, Parameters(), _running.Token)
                    .ConfigureAwait(true);

                Apply(_preview);
                Status = _preview.Outcome == PreviewOutcome.Blocked
                    ? _preview.Blocker ?? "Le relevé n'a pas pu être fait."
                    : "Relevé terminé. Rien n'a été supprimé.";
            }
            catch (OperationCanceledException)
            {
                _preview = null;
                Status = "Relevé interrompu.";
            }
            finally
            {
                IsBusy = false;
                Raise(nameof(WillNotDo));
                Raise(nameof(HasWillNotDo));
                RaiseStates();
            }
        }

        private void Apply(ActionPreview preview)
        {
            Summary = preview.Summary;
            Raise(nameof(HasSummary));

            var plan = preview.Plan as CleanupPlan;
            foreach (var source in Sources)
            {
                CleanupGroup? group = null;
                foreach (var candidate in plan?.Groups ?? Array.Empty<CleanupGroup>())
                    if (candidate.ProviderId == source.Provider.Id) group = candidate;

                source.Apply(source.IsSelected ? group : null);
            }
        }

        private async Task DeleteAsync()
        {
            var preview = _preview;
            var action = _action;
            var runner = await _runner().ConfigureAwait(true);
            if (preview == null || action == null || runner == null) return;

            IsConfirming = false;
            IsBusy = true;
            Status = "Suppression en cours…";

            _running?.Dispose();
            _running = new CancellationTokenSource();

            var progress = new Progress<ActionProgress>(report => Status = report.Text);

            try
            {
                _outcome = await runner
                    .ExecuteAsync(action, preview, Parameters(), progress, _running.Token)
                    .ConfigureAwait(true);

                Status = "Suppression terminée.";
            }
            finally
            {
                IsBusy = false;

                // Le relevé est consommé : impossible de le rejouer sur un état qui a changé.
                _preview = null;
                Summary = string.Empty;
                foreach (var source in Sources) source.Apply(null);

                Raise(nameof(HasSummary));
                RaiseOutcome();
                RaiseStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private IReadOnlyDictionary<string, string> Parameters()
        {
            var selected = new List<string>();
            foreach (var source in Sources)
                if (source.IsSelected) selected.Add(source.Provider.Id);

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["providers"] = string.Join(",", selected),
            };
        }

        private void RaiseOutcome()
        {
            Raise(nameof(HasOutcome));
            Raise(nameof(OutcomeSummary));
            Raise(nameof(OutcomeDetails));
            Raise(nameof(OutcomeKey));
        }

        private void RaiseStates()
        {
            Raise(nameof(CanDelete));
            (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RequestDeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConfirmDeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AbandonCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
