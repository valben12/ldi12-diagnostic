using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    public sealed class PreviewLineItem
    {
        public PreviewLineItem(PreviewLine line)
        {
            Label = line.Label;
            Value = line.Value;
            IsCaution = line.Kind == PreviewLineKind.Caution;
        }

        public string Label { get; }
        public string Value { get; }
        public bool IsCaution { get; }
    }

    /// <summary>
    /// Une action de réparation à l'écran : son état, sa prévisualisation, son compte rendu.
    /// </summary>
    /// <remarks>
    /// Le parcours imposé est toujours le même, prévisualiser, lire, confirmer, exécuter. Le
    /// bouton d'exécution n'existe qu'après la prévisualisation, et les actions qui engagent
    /// réellement la machine réclament une confirmation supplémentaire qui répète ce qui va se
    /// passer. Ce n'est pas de la prudence décorative : c'est ce qui permet au technicien de
    /// répondre à la question du client avant d'agir, pas après.
    /// </remarks>
    public sealed class RepairActionItem : ObservableObject
    {
        private readonly RepairsViewModel _owner;
        private readonly IRepairAction _action;
        private readonly Dictionary<string, string> _parameters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _running;
        private ActionPreview? _preview;
        private ActionReadiness _readiness = ActionReadiness.Ready;
        private bool _isBusy;
        private bool _isConfirming;
        private string _progressText = string.Empty;
        private ActionOutcome? _outcome;
        private string _title;

        public RepairActionItem(RepairsViewModel owner, IRepairAction action, string? titleOverride = null)
        {
            _owner = owner;
            _action = action;
            _title = titleOverride ?? action.Descriptor.DisplayName;

            PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !IsBusy && IsAvailable);
            RequestExecuteCommand = new RelayCommand(RequestExecute, () => CanExecute);
            ConfirmExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
            AbandonCommand = new RelayCommand(() => IsConfirming = false, () => IsConfirming);
            CancelCommand = new RelayCommand(() => _running?.Cancel(), () => IsBusy);
        }

        public ActionDescriptor Descriptor => _action.Descriptor;

        public string Title { get => _title; private set => Set(ref _title, value); }

        public string Purpose => Descriptor.Purpose;
        public string PlainPurpose => Descriptor.PlainPurpose;
        public string CategoryLabel => Labels.Describe(Descriptor.Category);

        public string RiskLabel => Descriptor.Risk switch
        {
            ActionRisk.ReadOnly => "Ne modifie rien",
            ActionRisk.Low => "Sans conséquence durable",
            ActionRisk.Moderate => "Modifie le système",
            _ => "Engage la machine",
        };

        /// <summary>Sert de déclencheur de style : la couleur du bandeau suit le risque réel.</summary>
        public string RiskKey => Descriptor.Risk.ToString();

        public string DurationLabel => "Durée typique : " + ValueFormat.Duration(Descriptor.TypicalDuration);

        public bool NeedsElevation => Descriptor.Requirements.RequiresElevation;

        public string ElevationLabel => NeedsElevation
            ? "Privilèges administrateur : une seule invite pour toute la session"
            : "Fonctionne en session utilisateur";

        public bool RequiresRestart => Descriptor.RequiresRestart;

        public bool IsAvailable => _readiness.Availability != ActionAvailability.Unavailable;

        public string? UnavailableReason => IsAvailable ? null : _readiness.Reason;

        public string? Workaround => IsAvailable ? null : _readiness.Workaround;

        public bool HasWorkaround => Workaround != null;

        public bool IsBusy
        {
            get => _isBusy;
            private set { if (Set(ref _isBusy, value)) RaiseStates(); }
        }

        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

        public bool IsConfirming
        {
            get => _isConfirming;
            private set { if (Set(ref _isConfirming, value)) RaiseStates(); }
        }

        /// <summary>Une action qui touche vraiment à la machine ne part pas sur un seul clic.</summary>
        public bool NeedsConfirmation => Descriptor.Risk >= ActionRisk.Moderate;

        public bool HasPreview => _preview != null;
        public string PreviewSummary => _preview?.Summary ?? string.Empty;
        public IReadOnlyList<string> WillDo => _preview?.WillDo ?? Array.Empty<string>();
        public IReadOnlyList<string> WillNotDo => _preview?.WillNotDo ?? Array.Empty<string>();
        public bool HasWillNotDo => WillNotDo.Count > 0;

        public IReadOnlyList<PreviewLineItem> Measurements
        {
            get
            {
                var lines = new List<PreviewLineItem>();
                foreach (var line in _preview?.Measurements ?? Array.Empty<PreviewLine>())
                    lines.Add(new PreviewLineItem(line));
                return lines;
            }
        }

        public bool CanExecute => _preview != null && _preview.CanExecute && !IsBusy && IsAvailable;

        public bool HasOutcome => _outcome != null;
        public string OutcomeSummary => _outcome?.Summary ?? string.Empty;
        public IReadOnlyList<string> OutcomeDetails => _outcome?.Details ?? Array.Empty<string>();
        public bool HasOutcomeDetails => OutcomeDetails.Count > 0;
        public string? RawOutput => _outcome?.RawOutput;
        public bool HasRawOutput => !string.IsNullOrWhiteSpace(RawOutput);

        public string OutcomeStatusLabel => _outcome == null ? string.Empty : Describe(_outcome.Status);

        /// <summary>Déclencheur de style du compte rendu : réussi, partiel ou en échec.</summary>
        public string OutcomeKey => _outcome == null
            ? "None"
            : _outcome.Succeeded ? (_outcome.Status == ActionStatus.PartiallySucceeded ? "Partial" : "Good") : "Bad";

        public ICommand PreviewCommand { get; }
        public ICommand RequestExecuteCommand { get; }
        public ICommand ConfirmExecuteCommand { get; }
        public ICommand AbandonCommand { get; }
        public ICommand CancelCommand { get; }

        public void SetParameter(string key, string? value)
        {
            if (value == null) _parameters.Remove(key);
            else _parameters[key] = value;
        }

        public async Task RefreshReadinessAsync()
        {
            var runner = await _owner.GetRunnerAsync().ConfigureAwait(true);
            _readiness = runner == null ? ActionReadiness.No("Plateforme non identifiée.") : runner.Readiness(_action, _parameters);

            Raise(nameof(IsAvailable));
            Raise(nameof(UnavailableReason));
            Raise(nameof(Workaround));
            Raise(nameof(HasWorkaround));
            RaiseStates();
        }

        private async Task PreviewAsync()
        {
            var runner = await _owner.GetRunnerAsync().ConfigureAwait(true);
            if (runner == null) return;

            IsBusy = true;
            IsConfirming = false;
            _outcome = null;
            ProgressText = "Relevé de ce qui sera fait…";
            RaiseOutcome();

            _running?.Dispose();
            _running = new CancellationTokenSource();

            try
            {
                _preview = await runner.PreviewAsync(_action, _parameters, _running.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _preview = null;
            }
            finally
            {
                IsBusy = false;
                ProgressText = string.Empty;
                RaisePreview();
            }
        }

        private void RequestExecute()
        {
            if (NeedsConfirmation) IsConfirming = true;
            else ((AsyncRelayCommand)ConfirmExecuteCommand).Execute(null!);
        }

        private async Task ExecuteAsync()
        {
            var preview = _preview;
            var runner = await _owner.GetRunnerAsync().ConfigureAwait(true);
            if (preview == null || runner == null) return;

            IsConfirming = false;
            IsBusy = true;
            ProgressText = "Exécution en cours…";

            _running?.Dispose();
            _running = new CancellationTokenSource();

            var progress = new Progress<ActionProgress>(report => ProgressText = report.Text);

            try
            {
                _outcome = await runner
                    .ExecuteAsync(_action, preview, _parameters, progress, _running.Token)
                    .ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
                ProgressText = string.Empty;

                // La prévisualisation est consommée : le relevé qui vient d'être traité ne doit
                // pas pouvoir être rejoué sur un état de machine qui a changé.
                _preview = null;
                RaisePreview();
                RaiseOutcome();
                _owner.NotifyJournalChanged();
            }
        }

        private static string Describe(ActionStatus status) => status switch
        {
            ActionStatus.Succeeded => "Terminé",
            ActionStatus.PartiallySucceeded => "Partiellement terminé",
            ActionStatus.NothingToDo => "Rien à faire",
            ActionStatus.Cancelled => "Interrompu",
            ActionStatus.TimedOut => "Délai dépassé",
            ActionStatus.ElevationRequired => "Privilèges requis",
            ActionStatus.Unavailable => "Indisponible",
            _ => "Échec",
        };

        private void RaisePreview()
        {
            Raise(nameof(HasPreview));
            Raise(nameof(PreviewSummary));
            Raise(nameof(WillDo));
            Raise(nameof(WillNotDo));
            Raise(nameof(HasWillNotDo));
            Raise(nameof(Measurements));
            RaiseStates();
        }

        private void RaiseOutcome()
        {
            Raise(nameof(HasOutcome));
            Raise(nameof(OutcomeSummary));
            Raise(nameof(OutcomeDetails));
            Raise(nameof(HasOutcomeDetails));
            Raise(nameof(RawOutput));
            Raise(nameof(HasRawOutput));
            Raise(nameof(OutcomeStatusLabel));
            Raise(nameof(OutcomeKey));
        }

        private void RaiseStates()
        {
            Raise(nameof(CanExecute));
            (PreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RequestExecuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConfirmExecuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AbandonCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Écran des réparations.
    /// </summary>
    /// <remarks>
    /// Le redémarrage de service n'apparaît pas comme une action générique à paramétrer, mais
    /// une fois par service que le diagnostic a trouvé dans un état anormal. Une liste déroulante
    /// de deux cents services demanderait au technicien de savoir d'avance lequel choisir ; ici,
    /// l'outil propose ce qu'il a lui-même constaté.
    /// </remarks>
    public sealed class RepairsViewModel : ObservableObject
    {
        /// <summary>Au-delà, la liste cesserait d'aider et deviendrait un inventaire.</summary>
        private const int MaxServiceItems = 5;

        private readonly Services.ActionService _actions;
        private readonly ILdiLogger _logger;

        private ActionRunner? _runner;
        private SystemSnapshot? _snapshot;
        private string _status = "Identification de la machine…";
        private string _restoreStatus = "Aucun point de restauration demandé pendant cette session.";
        private bool _restoreDone;

        public RepairsViewModel(Services.ActionService actions, ILdiLogger logger)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !_restoreDone);
        }

        public ObservableCollection<RepairActionItem> Items { get; } = new ObservableCollection<RepairActionItem>();

        public ICommand CreateRestorePointCommand { get; }

        public string Status { get => _status; private set => Set(ref _status, value); }

        public string RestoreStatus { get => _restoreStatus; private set => Set(ref _restoreStatus, value); }

        public event EventHandler? JournalChanged;

        internal void NotifyJournalChanged() => JournalChanged?.Invoke(this, EventArgs.Empty);

        internal async Task<ActionRunner?> GetRunnerAsync()
        {
            if (_runner != null) return _runner;

            try
            {
                return _runner = await _actions.GetRunnerAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error("Repairs", "L'orchestrateur d'actions n'a pas pu être construit.", ex);
                Status = "Les actions ne sont pas disponibles : " + ex.Message;
                return null;
            }
        }

        public async Task InitializeAsync()
        {
            var runner = await GetRunnerAsync().ConfigureAwait(true);
            if (runner == null) return;

            Items.Clear();
            foreach (var action in ActionCatalog.CreateAll(_logger))
            {
                // Le redémarrage de service ne se propose que rattaché à un service précis.
                if (action.Descriptor.Id == ActionIds.RestartService) continue;
                if (action.Descriptor.Id == ActionIds.Cleanup) continue;

                // Les pilotes et les applications d'une sauvegarde se traitent avec elle, sur
                // l'écran Données : seuls, ils n'ont ni sauvegarde ni support à qui s'adresser.
                if (action.Descriptor.Id == ActionIds.ExportDrivers ||
                    action.Descriptor.Id == ActionIds.RestoreDrivers ||
                    action.Descriptor.Id == ActionIds.RestoreApplications ||
                    action.Descriptor.Id == ActionIds.RestorePrinters ||
                    action.Descriptor.Id == ActionIds.CopyOpenFiles) continue;

                Items.Add(new RepairActionItem(this, action));
            }

            await RefreshAsync().ConfigureAwait(true);

            Status = runner.Context.Platform.IsElevated
                ? "Session administrateur : toutes les réparations sont exécutables directement."
                : "Session utilisateur. Les réparations qui exigent des privilèges ouvriront une " +
                  "invite Windows, une seule fois pour toute la session.";
        }

        public async void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;

            // Le service d'actions vient de reconstruire son exécuteur sur ce diagnostic. Garder
            // l'ancien, créé au démarrage avant la fin de la première analyse, faisait tourner
            // toutes les actions sans diagnostic : la fiche de réinstallation annonçait « aucune
            // analyse » juste après une analyse rapide.
            _runner = null;

            try
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error("Repairs", "La mise à jour de l'écran des réparations a échoué.", ex);
            }
        }

        private async Task RefreshAsync()
        {
            SyncServiceItems();
            ApplyParameters();

            foreach (var item in Items) await item.RefreshReadinessAsync().ConfigureAwait(true);
        }

        /// <summary>Un élément par service que le diagnostic a trouvé hors de son état attendu.</summary>
        private void SyncServiceItems()
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].Descriptor.Id == ActionIds.RestartService) Items.RemoveAt(i);

            if (_snapshot == null) return;

            var added = 0;
            foreach (var service in _snapshot.Windows.Services)
            {
                if (added >= MaxServiceItems) break;
                if (service.Deviation == null) continue;

                var action = ActionCatalog.Find(ActionIds.RestartService, _logger);
                if (action == null) return;

                var item = new RepairActionItem(
                    this, action, "Redémarrer le service « " + service.DisplayName + " »");
                item.SetParameter("service", service.Name);
                Items.Add(item);
                added++;
            }
        }

        /// <summary>
        /// Le volume à vérifier est résolu ici, à partir du diagnostic, et transmis à l'action.
        /// </summary>
        /// <remarks>
        /// L'hôte élevé n'a pas le diagnostic sous la main : il n'a pas analysé la machine. Tout
        /// ce dont une action a besoin doit donc lui être transmis explicitement, sans quoi elle
        /// retomberait sur le volume système du processus, qui se trouve être le bon la plupart
        /// du temps mais pas toujours.
        /// </remarks>
        private void ApplyParameters()
        {
            foreach (var item in Items)
            {
                if (item.Descriptor.Id != ActionIds.CheckDisk) continue;
                item.SetParameter("volume", ResolveSystemVolume());
            }
        }

        private string? ResolveSystemVolume()
        {
            foreach (var volume in _snapshot?.Storage.Volumes ?? Array.Empty<VolumeInfo>())
                if (volume.IsSystemVolume.Or(false) && volume.DriveLetter.HasValue)
                    return volume.DriveLetter.Value;
            return null;
        }

        private async Task CreateRestorePointAsync()
        {
            var runner = await GetRunnerAsync().ConfigureAwait(true);
            if (runner == null) return;

            RestoreStatus = "Création du point de restauration en cours…";

            var result = await runner
                .CreateRestorePointAsync("LDI12 Diagnostic : avant intervention", CancellationToken.None)
                .ConfigureAwait(true);

            RestoreStatus = result.Message;
            _restoreDone = result.ProtectionInPlace;
            (CreateRestorePointCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            NotifyJournalChanged();
        }
    }
}
