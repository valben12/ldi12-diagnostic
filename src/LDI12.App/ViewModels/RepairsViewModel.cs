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

        private bool _isHighlighted;
        private string? _recommendation;

        /// <summary>Désignée depuis la vue d'ensemble ou un constat : encadrée, et amenée à l'écran.</summary>
        public bool IsHighlighted { get => _isHighlighted; internal set => Set(ref _isHighlighted, value); }

        /// <summary>
        /// La recommandation du diagnostic qui la propose : « Corriger la configuration DNS ».
        /// </summary>
        /// <remarks>
        /// Quinze opérations au même niveau ne disent pas laquelle sert ici. Celles que le diagnostic
        /// de cette machine recommande passent en tête, et disent pourquoi.
        /// </remarks>
        public string? Recommendation
        {
            get => _recommendation;
            internal set
            {
                if (!Set(ref _recommendation, value)) return;
                Raise(nameof(IsRecommended));
                Raise(nameof(RecommendationNote));
            }
        }

        public bool IsRecommended => _recommendation != null;

        /// <summary>La recommandation, seulement quand elle dit autre chose que le titre de la carte.</summary>
        public string? RecommendationNote
            => _recommendation != null && !string.Equals(_recommendation, Title, StringComparison.OrdinalIgnoreCase)
                ? _recommendation
                : null;
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

        /// <summary>
        /// Lancée depuis un plan validé d'un clic : le même relevé préalable, la même exécution et
        /// le même journal qu'à la main, sans la confirmation qui a déjà eu lieu sur le plan.
        /// </summary>
        /// <returns>Le compte rendu, en une phrase.</returns>
        public async Task<(bool Succeeded, string Summary)> RunFromPlanAsync()
        {
            await RefreshReadinessAsync().ConfigureAwait(true);
            if (!IsAvailable) return (false, UnavailableReason ?? "Indisponible sur cette machine.");

            await PreviewAsync().ConfigureAwait(true);
            if (_preview == null) return (false, "Relevé préalable interrompu.");
            if (!CanExecute) return (false, string.IsNullOrWhiteSpace(_preview.Summary) ? "Rien à faire." : _preview.Summary);

            await ExecuteAsync().ConfigureAwait(true);
            return _outcome == null ? (false, "Interrompue.") : (_outcome.Succeeded, _outcome.Summary);
        }

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
                // La sauvegarde et la restauration ont leur écran, Données, avec leurs supports et
                // leurs choix : présentées ici aussi, elles demandaient un dossier qu'on ne pouvait
                // pas choisir, et faisaient croire à un second chemin.
                if (action.Descriptor.Id == ActionIds.BackupUserData ||
                    action.Descriptor.Id == ActionIds.RestoreUserData ||
                    action.Descriptor.Id == ActionIds.ExportDrivers ||
                    action.Descriptor.Id == ActionIds.RestoreDrivers ||
                    action.Descriptor.Id == ActionIds.RestoreApplications ||
                    action.Descriptor.Id == ActionIds.RestorePrinters ||
                    action.Descriptor.Id == ActionIds.VerifyBackup ||
                    action.Descriptor.Id == ActionIds.CopyOpenFiles) continue;

                Items.Add(new RepairActionItem(this, action));
            }

            await RefreshAsync().ConfigureAwait(true);

            Status = runner.Context.Platform.IsElevated
                ? "Session administrateur : toutes les réparations sont exécutables directement."
                : "Session utilisateur. Les réparations qui exigent des privilèges ouvriront une " +
                  "invite Windows, une seule fois pour toute la session.";
        }

        /// <summary>Les opérations qu'un plan de Claude peut proposer, telles que Claude les lit.</summary>
        public IReadOnlyList<LDI12.Assistant.AvailableAction> PlanCatalog()
        {
            var catalog = new List<LDI12.Assistant.AvailableAction>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
            {
                if (!item.IsAvailable || !seen.Add(item.Descriptor.Id) || item.Descriptor.Id == ActionIds.RestartService) continue;
                catalog.Add(Describe(item.Descriptor, null));
            }

            var restart = ActionCatalog.Find(ActionIds.RestartService, _logger);
            if (restart != null) catalog.Add(Describe(restart.Descriptor, "service : nom court du service Windows (par exemple Spooler)"));
            return catalog;
        }

        /// <summary>Lance une opération d'un plan validé ; null si elle n'est pas proposée sur cette machine.</summary>
        public async Task<(bool Succeeded, string Summary)?> RunFromPlanAsync(string actionId, IReadOnlyDictionary<string, string> parameters)
        {
            RepairActionItem? item = null;
            if (string.Equals(actionId, ActionIds.RestartService, StringComparison.OrdinalIgnoreCase))
            {
                var action = ActionCatalog.Find(ActionIds.RestartService, _logger);
                if (action == null || !parameters.TryGetValue("service", out var service) || string.IsNullOrWhiteSpace(service)) return null;
                item = new RepairActionItem(this, action, "Redémarrer le service « " + service + " »");
            }
            else
            {
                foreach (var candidate in Items)
                    if (string.Equals(candidate.Descriptor.Id, actionId, StringComparison.OrdinalIgnoreCase)) { item = candidate; break; }
            }
            if (item == null) return null;

            foreach (var parameter in parameters) item.SetParameter(parameter.Key, parameter.Value);
            return await item.RunFromPlanAsync().ConfigureAwait(true);
        }

        private static LDI12.Assistant.AvailableAction Describe(ActionDescriptor descriptor, string? parameter) => new()
        {
            Id = descriptor.Id,
            Title = descriptor.DisplayName,
            Purpose = descriptor.Purpose,
            Risk = descriptor.Risk switch
            {
                ActionRisk.ReadOnly => "lecture seule",
                ActionRisk.Low => "faible",
                ActionRisk.Moderate => "modéré",
                _ => "élevé",
            },
            Parameter = parameter,
        };

        /// <summary>
        /// Désigne une opération : encadrée quelques secondes, et amenée à l'écran par la vue.
        /// </summary>
        public void Highlight(string actionId)
        {
            RepairActionItem? target = null;
            foreach (var item in Items)
            {
                item.IsHighlighted = false;
                if (target == null && string.Equals(item.Descriptor.Id, actionId, StringComparison.OrdinalIgnoreCase)) target = item;
            }

            if (target == null) return;
            target.IsHighlighted = true;

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                target.IsHighlighted = false;
            };
            timer.Start();
        }

        /// <summary>Les opérations que ce diagnostic recommande passent en tête, avec leur raison.</summary>
        private void ApplyRecommendations()
        {
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var recommendation in _snapshot?.Recommendations ?? Array.Empty<Recommendation>())
                if (recommendation.LinkedAction != null && !reasons.ContainsKey(recommendation.LinkedAction))
                    reasons[recommendation.LinkedAction] = recommendation.Title;

            foreach (var item in Items)
                item.Recommendation = reasons.TryGetValue(item.Descriptor.Id, out var title) ? title : null;

            // Tri stable : les recommandées d'abord, chacune gardant sa place relative.
            var ordered = new List<RepairActionItem>(Items);
            var rank = new Dictionary<RepairActionItem, int>();
            for (var index = 0; index < ordered.Count; index++) rank[ordered[index]] = index;
            ordered.Sort((a, b) => a.IsRecommended != b.IsRecommended
                ? (a.IsRecommended ? -1 : 1)
                : rank[a].CompareTo(rank[b]));

            for (var index = 0; index < ordered.Count; index++)
            {
                var current = Items.IndexOf(ordered[index]);
                if (current != index) Items.Move(current, index);
            }

            RecommendedCount = reasons.Count == 0 ? 0 : CountRecommended();
        }

        private int CountRecommended()
        {
            var count = 0;
            foreach (var item in Items) if (item.IsRecommended) count++;
            return count;
        }

        private int _recommendedCount;

        /// <summary>Combien d'opérations ce diagnostic recommande : le dire en tête de l'écran.</summary>
        public int RecommendedCount
        {
            get => _recommendedCount;
            private set { if (Set(ref _recommendedCount, value)) Raise(nameof(RecommendedText)); }
        }

        public string RecommendedText => RecommendedCount == 0
            ? "OPÉRATIONS DISPONIBLES"
            : "OPÉRATIONS DISPONIBLES · " + RecommendedCount + " RECOMMANDÉE" + (RecommendedCount > 1 ? "S" : "") + " PAR LE DIAGNOSTIC, EN TÊTE";

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
            ApplyRecommendations();

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
