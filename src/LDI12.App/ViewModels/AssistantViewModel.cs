using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.App.Mvvm;
using LDI12.Assistant;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    /// <summary>Une ligne du fil de l'analyse : ce qui est en train d'être lu.</summary>
    public sealed class AssistantTrace : ObservableObject
    {
        private bool _isDone;

        public AssistantTrace(string text) => Text = text;

        public string Text { get; }

        /// <summary>Faux pour la dernière ligne : c'est ce qui se fait en ce moment.</summary>
        public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
    }

    /// <summary>Un constat de l'analyse, avec sa couleur.</summary>
    public sealed class AssistantFindingItem
    {
        public AssistantFindingItem(PlanFinding finding)
        {
            Title = finding.Title;
            Detail = finding.Detail;
            SeverityKey = finding.Severity switch
            {
                "critique" => "Critical",
                "problème" => "Problem",
                "attention" => "Warning",
                _ => "Info",
            };
        }

        public string Title { get; }

        public string Detail { get; }

        /// <summary>Déclencheur de style : Critical, Problem, Warning, Info.</summary>
        public string SeverityKey { get; }
    }

    /// <summary>Une cause envisagée : sa barre de vraisemblance et son état.</summary>
    public sealed class HypothesisItem
    {
        public HypothesisItem(Hypothesis hypothesis)
        {
            Cause = hypothesis.Cause;
            Share = hypothesis.Likelihood / 100.0;
            LikelihoodText = hypothesis.Likelihood + " %";
            StateKey = hypothesis.State switch { "écartée" => "Ruled", "confirmée" => "Confirmed", _ => "Pending" };
            StateText = hypothesis.State;
        }

        public string Cause { get; }

        public double Share { get; }

        public string LikelihoodText { get; }

        /// <summary>Pending, Ruled, Confirmed.</summary>
        public string StateKey { get; }

        public string StateText { get; }
    }

    /// <summary>Une vérification : la question, et ses réponses en boutons, une seule retenue.</summary>
    public sealed class CheckItem : ObservableObject
    {
        public CheckItem(CheckQuestion check)
        {
            Question = check.Question;
            foreach (var answer in check.Answers)
            {
                var chip = new ChipItem(answer);
                chip.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(ChipItem.IsSelected) || !chip.IsSelected) return;
                    foreach (var other in Answers) if (!ReferenceEquals(other, chip)) other.IsSelected = false;
                    Raise(nameof(Answer));
                    Raise(nameof(IsAnswered));
                };
                Answers.Add(chip);
            }
        }

        public string Question { get; }

        public ObservableCollection<ChipItem> Answers { get; } = new ObservableCollection<ChipItem>();

        public string? Answer => Answers.FirstOrDefault(a => a.IsSelected)?.Text;

        public bool IsAnswered => Answer != null;
    }

    /// <summary>
    /// Une action de l'étape : une opération de LDI12 qu'on lance d'un clic, ou un geste manuel
    /// qu'on coche une fois fait.
    /// </summary>
    public sealed class AssistantStepItem : ObservableObject
    {
        private bool _isChecked;
        private string _status = string.Empty;
        private string _statusKey = "None";

        public AssistantStepItem(PlanStep step, int number, string? actionTitle, Func<AssistantStepItem, Task> run)
        {
            Step = step;
            Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            KindText = step.IsAutomatic ? "Automatique · " + (actionTitle ?? step.ActionId) : "À faire à la main";
            RunCommand = new AsyncRelayCommand(() => run(this), () => IsAutomatic && _statusKey != "Running" && _statusKey != "Good");
        }

        public PlanStep Step { get; }

        public string Number { get; }

        public string Title => Step.Title;

        public string Why => Step.Why;

        public string? Manual => Step.Manual;

        public bool HasManual => !string.IsNullOrWhiteSpace(Step.Manual);

        public bool IsAutomatic => Step.IsAutomatic;

        public string KindText { get; }

        /// <summary>Pour un geste manuel : fait.</summary>
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && !IsAutomatic); }

        public string Status { get => _status; set => Set(ref _status, value); }

        /// <summary>None, Running, Good, Bad.</summary>
        public string StatusKey
        {
            get => _statusKey;
            set
            {
                if (!Set(ref _statusKey, value)) return;
                ((AsyncRelayCommand)RunCommand).RaiseCanExecuteChanged();
                Raise(nameof(HasRun));
            }
        }

        public bool HasRun => _statusKey == "Good" || _statusKey == "Bad";

        public ICommand RunCommand { get; }
    }

    /// <summary>Une étape passée, en une ligne, au-dessus de l'étape en cours.</summary>
    public sealed class RoundSummary
    {
        public RoundSummary(int round, string summary, string done)
        {
            Title = "Étape " + round;
            Summary = summary;
            Done = done;
        }

        public string Title { get; }

        public string Summary { get; }

        public string Done { get; }
    }

    /// <summary>
    /// L'assistant : les pilotes, ou un dépannage guidé, par étapes que le technicien mène aux boutons.
    /// </summary>
    /// <remarks>
    /// <b>Au plus simple, sans s'interdire d'aller loin.</b> L'analyse vise une seule étape : si la
    /// cause est claire, toutes les opérations qui la règlent arrivent d'emblée, et le dépannage
    /// finit sur « C'est réglé ». Quand elle ne l'est pas, l'étape propose peu d'actions et des
    /// vérifications à réponses toutes prêtes, et « Continuer » renvoie ce qui a été fait et
    /// constaté pour l'étape suivante. Jamais de conversation : des cartes et des boutons.
    /// <para>
    /// Rien ne touche la machine sans un clic, et chaque opération passe par le même relevé
    /// préalable et le même journal que depuis l'écran Réparations.
    /// </para>
    /// </remarks>
    public sealed class AssistantViewModel : ObservableObject
    {
        private const string Category = "Assistant";

        /// <summary>Le domaine du problème, et les observations qui comptent pour lui.</summary>
        private static readonly (string Component, string[] Observations)[] Components =
        {
            ("Son", new[] { "Pilote installé et reconnu", "Aucun son", "Son qui grésille ou coupe", "Son seulement au casque",
                            "Pas de son en HDMI / sur l'écran", "Croix rouge sur l'icône du son", "Micro muet" }),
            ("Internet ou Wi-Fi", new[] { "Aucun réseau visible", "Connecté sans Internet", "Se déconnecte souvent", "Lent",
                                          "Les autres appareils fonctionnent", "Wi-Fi absent des paramètres" }),
            ("Réseau filaire", new[] { "Câble branché, pas de connexion", "Connecté sans Internet", "Lent", "Voyant de la prise éteint" }),
            ("Affichage", new[] { "Écran noir", "Définition bloquée ou floue", "Second écran non détecté", "Scintillement",
                                  "Pilote graphique générique" }),
            ("Imprimante", new[] { "N'imprime plus", "Travaux bloqués dans la file", "Imprimante hors ligne", "Introuvable sur le réseau",
                                   "Imprime des caractères incohérents" }),
            ("Périphérique USB", new[] { "Non reconnu", "Se déconnecte tout seul", "Point d'exclamation dans le Gestionnaire",
                                         "Clé ou disque invisible" }),
            ("Bluetooth", new[] { "Bluetooth absent", "L'appareil ne s'appaire pas", "Son Bluetooth haché" }),
            ("Webcam, micro", new[] { "Non détectée", "Image noire", "Refusée par l'application", "Micro muet" }),
            ("Lenteurs", new[] { "Au démarrage", "Tout le temps", "Disque à 100 %", "Ventilateur bruyant", "Navigateur lent" }),
            ("Démarrage, plantages", new[] { "Écran bleu", "Redémarre tout seul", "Démarrage très long", "Gel complet",
                                             "Réparation automatique en boucle" }),
            ("Autre", Array.Empty<string>()),
        };

        /// <summary>Les observations qui valent pour tous les domaines.</summary>
        private static readonly string[] CommonObservations =
        {
            "Depuis une mise à jour", "Depuis l'installation d'un logiciel", "Après un choc ou une coupure",
            "Par intermittence", "Depuis toujours",
        };

        private readonly ILdiLogger _logger;
        private readonly ApiKeyVault _vault;
        private readonly Func<SystemSnapshot?> _snapshot;
        private readonly Func<Task> _runDiagnosis;
        private readonly RepairsViewModel _repairs;
        private readonly Func<Task<ISystemControl?>> _control;
        private readonly Stopwatch _clock = new Stopwatch();
        private CancellationTokenSource? _running;
        private ClaudeAnalyst? _analyst;
        private AssistantRequest? _lastRequest;
        private StepFeedback? _failedFeedback;
        private IReadOnlyList<AvailableAction> _actions = Array.Empty<AvailableAction>();
        private string _stage = "Key";
        private string _keyInput = string.Empty;
        private string _keyError = string.Empty;
        private string _keyText = string.Empty;
        private bool _isCheckingKey;
        private string _notes = string.Empty;
        private string _stepNote = string.Empty;
        private string _runTitle = string.Empty;
        private string _summary = string.Empty;
        private string _confidence = string.Empty;
        private string _next = string.Empty;
        private string _statusKey = "Progress";
        private string _costText = string.Empty;
        private string _error = string.Empty;
        private string _applyText = string.Empty;
        private bool _isApplying;
        private int _round;

        /// <summary>Les opérations du catalogue qui modifient vraiment le système : un point de restauration les précède.</summary>
        private readonly HashSet<string> _risky = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les constats avant le dépannage, pour dire ensuite ce qui est réglé.</summary>
        private HashSet<string>? _before;

        public AssistantViewModel(
            ILdiLogger logger, ApiKeyVault vault, Func<SystemSnapshot?> snapshot, Func<Task> runDiagnosis, RepairsViewModel repairs,
            Func<Task<ISystemControl?>> control)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _runDiagnosis = runDiagnosis ?? throw new ArgumentNullException(nameof(runDiagnosis));
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _control = control ?? throw new ArgumentNullException(nameof(control));

            foreach (var (component, _) in Components)
            {
                var chip = new ChipItem(component);
                chip.PropertyChanged += OnProblemChanged;
                Problems.Add(chip);
            }
            ResetObservations(null);

            SaveKeyCommand = new AsyncRelayCommand(SaveKeyAsync, () => !_isCheckingKey && _keyInput.Trim().Length > 10);
            ForgetKeyCommand = new RelayCommand(ForgetKey);
            OpenConsoleCommand = new RelayCommand(() => Open("https://console.anthropic.com/settings/keys"));
            DriversCommand = new AsyncRelayCommand(() => StartAsync(new AssistantRequest { Mode = AssistantMode.Drivers }, "Analyse des pilotes"));
            GuideCommand = new RelayCommand(() => Stage = "Guide");
            StartGuidedCommand = new AsyncRelayCommand(StartGuidedAsync, () => SelectedProblem != null);
            BackCommand = new RelayCommand(() => Stage = "Home");
            ReturnToStepCommand = new RelayCommand(() => Stage = "Result");
            CancelCommand = new RelayCommand(() => _running?.Cancel());
            RetryCommand = new AsyncRelayCommand(RetryAsync);
            ApplyCommand = new AsyncRelayCommand(ApplyAllAsync, () => !_isApplying && PendingCount > 0);
            ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => !_isApplying && _analyst?.CanContinue == true);
            SolvedCommand = new RelayCommand(Solved);
            VerifyCommand = new AsyncRelayCommand(VerifyAsync);

            Refresh();
        }

        public ObservableCollection<ChipItem> Problems { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<ChipItem> Details { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<AssistantTrace> Trace { get; } = new ObservableCollection<AssistantTrace>();
        public ObservableCollection<AssistantFindingItem> Findings { get; } = new ObservableCollection<AssistantFindingItem>();
        public ObservableCollection<HypothesisItem> Hypotheses { get; } = new ObservableCollection<HypothesisItem>();
        public ObservableCollection<AssistantStepItem> Steps { get; } = new ObservableCollection<AssistantStepItem>();
        public ObservableCollection<CheckItem> Checks { get; } = new ObservableCollection<CheckItem>();
        public ObservableCollection<RoundSummary> History { get; } = new ObservableCollection<RoundSummary>();

        public ICommand SaveKeyCommand { get; }
        public ICommand ForgetKeyCommand { get; }
        public ICommand OpenConsoleCommand { get; }
        public ICommand DriversCommand { get; }
        public ICommand GuideCommand { get; }
        public AsyncRelayCommand StartGuidedCommand { get; }
        public ICommand BackCommand { get; }

        /// <summary>Après l'échec de la suite : l'étape précédente reste là, à reprendre.</summary>
        public ICommand ReturnToStepCommand { get; }

        public bool CanReturnToStep => _failedFeedback != null;
        public ICommand CancelCommand { get; }
        public ICommand RetryCommand { get; }
        public AsyncRelayCommand ApplyCommand { get; }
        public AsyncRelayCommand ContinueCommand { get; }
        public ICommand SolvedCommand { get; }
        public ICommand VerifyCommand { get; }

        /// <summary>Key, Home, Guide, Running, Result, Failed, Done.</summary>
        public string Stage
        {
            get => _stage;
            private set
            {
                if (!Set(ref _stage, value)) return;
                foreach (var name in new[] { nameof(IsKey), nameof(IsHome), nameof(IsGuide), nameof(IsRunning), nameof(IsResult),
                                             nameof(IsFailed), nameof(IsDone), nameof(HasKey) })
                    Raise(name);
            }
        }

        public bool IsKey => _stage == "Key";
        public bool IsHome => _stage == "Home";
        public bool IsGuide => _stage == "Guide";
        public bool IsRunning => _stage == "Running";
        public bool IsResult => _stage == "Result";
        public bool IsFailed => _stage == "Failed";
        public bool IsDone => _stage == "Done";

        /// <summary>Une analyse ou une opération en cours : la fenêtre demande confirmation avant de fermer.</summary>
        public bool IsBusy => IsRunning || _isApplying;

        public bool HasKey => _vault.Location != ApiKeyLocation.None && !IsKey;

        /// <summary>La saisie, relayée par la vue depuis le champ masqué.</summary>
        public string KeyInput
        {
            get => _keyInput;
            set
            {
                if (!Set(ref _keyInput, value ?? string.Empty)) return;
                KeyError = string.Empty;
                ((AsyncRelayCommand)SaveKeyCommand).RaiseCanExecuteChanged();
            }
        }

        public string KeyError { get => _keyError; private set => Set(ref _keyError, value); }

        public bool IsCheckingKey
        {
            get => _isCheckingKey;
            private set
            {
                if (!Set(ref _isCheckingKey, value)) return;
                ((AsyncRelayCommand)SaveKeyCommand).RaiseCanExecuteChanged();
            }
        }

        /// <summary>« sk-ant-…4f2a · rangée sur ce support ».</summary>
        public string KeyText { get => _keyText; private set => Set(ref _keyText, value); }

        /// <summary>Le constat du technicien, avant de lancer.</summary>
        public string Notes { get => _notes; set => Set(ref _notes, value ?? string.Empty); }

        /// <summary>Ce que le technicien a constaté pendant l'étape, joint au compte rendu.</summary>
        public string StepNote { get => _stepNote; set => Set(ref _stepNote, value ?? string.Empty); }

        public ChipItem? SelectedProblem => Problems.FirstOrDefault(p => p.IsSelected);

        public string RunTitle { get => _runTitle; private set => Set(ref _runTitle, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Confidence { get => _confidence; private set => Set(ref _confidence, value); }
        public string Next { get => _next; private set => Set(ref _next, value); }
        public string CostText { get => _costText; private set => Set(ref _costText, value); }
        public string Error { get => _error; private set => Set(ref _error, value); }
        public string ApplyText { get => _applyText; private set => Set(ref _applyText, value); }

        /// <summary>Progress, Resolved, OutOfReach.</summary>
        public string StatusKey
        {
            get => _statusKey;
            private set
            {
                if (!Set(ref _statusKey, value)) return;
                Raise(nameof(StatusText));
                Raise(nameof(IsResolved));
                Raise(nameof(IsInProgress));
                Raise(nameof(ContinueLabel));
                Raise(nameof(ContinueIsPrimary));
                Raise(nameof(SolvedIsPrimary));
            }
        }

        public string StatusText => _statusKey switch
        {
            "Resolved" => "Ces opérations devraient régler le problème",
            "OutOfReach" => "Au-delà de ce que le logiciel peut faire",
            _ => "Étape " + _round + " · " + _confidence.ToLowerInvariant(),
        };

        public bool IsResolved => _statusKey == "Resolved";
        public bool IsInProgress => _statusKey == "Progress";

        /// <summary>Le libellé du bouton de suite : une invitation quand l'étape est en cours, un recours quand elle se dit finale.</summary>
        public string ContinueLabel => _statusKey == "Progress" ? "Continuer l'analyse" : "Toujours pas : continuer";

        public bool HasFindings => Findings.Count > 0;
        public bool HasHypotheses => Hypotheses.Count > 0;
        public bool HasChecks => Checks.Count > 0;
        public bool HasHistory => History.Count > 0;
        public bool HasNext => !string.IsNullOrWhiteSpace(_next);

        /// <summary>Les opérations de l'étape qui n'ont pas encore été lancées.</summary>
        public int PendingCount => Steps.Count(s => s.IsAutomatic && !s.HasRun && s.StatusKey != "Running");

        public string ApplyLabel => PendingCount switch
        {
            0 => "Opérations faites",
            1 => "Lancer l'opération",
            _ => "Lancer les " + PendingCount + " opérations",
        };

        public bool HasPending => PendingCount > 0;

        /// <summary>« Continuer » en bouton plein : plus rien à lancer, et l'étape ne se dit pas finale.</summary>
        public bool ContinueIsPrimary => !HasPending && !IsResolved;

        /// <summary>« C'est réglé » en bouton plein : plus rien à lancer, et l'étape se dit finale.</summary>
        public bool SolvedIsPrimary => !HasPending && IsResolved;

        private void Refresh()
        {
            var location = _vault.Location;
            if (location == ApiKeyLocation.None)
            {
                Stage = "Key";
                return;
            }

            var key = _vault.Read();
            if (key == null)
            {
                // Rangée, mais illisible ici : copiée depuis un autre support.
                KeyError = "La clé enregistrée ne se lit pas sur ce support. Saisissez-la à nouveau.";
                Stage = "Key";
                return;
            }

            KeyText = ApiKeyVault.Mask(key) + (location == ApiKeyLocation.Portable ? "  ·  rangée sur ce support" : "  ·  rangée dans ce profil Windows");
            if (IsKey) Stage = "Home";
        }

        private async Task SaveKeyAsync()
        {
            var key = _keyInput.Trim();
            IsCheckingKey = true;
            KeyError = string.Empty;
            try
            {
                await new ClaudeAnalyst(key).CheckAsync(CancellationToken.None).ConfigureAwait(true);
                var location = _vault.Save(key);
                _logger.Info(Category, "Clé d'API enregistrée (" + (location == ApiKeyLocation.Portable ? "à côté du logiciel" : "profil Windows") + ").");
                KeyInput = string.Empty;
                Stage = "Home";
                Refresh();
            }
            catch (AssistantException ex)
            {
                KeyError = ex.Message;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.Security.Cryptography.CryptographicException)
            {
                KeyError = "Enregistrement impossible : " + ex.Message;
            }
            finally
            {
                IsCheckingKey = false;
            }
        }

        private void ForgetKey()
        {
            _vault.Forget();
            _logger.Info(Category, "Clé d'API oubliée.");
            KeyText = string.Empty;
            Stage = "Key";
        }

        private void OnProblemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChipItem.IsSelected) || !(sender is ChipItem chip) || !chip.IsSelected) return;
            // Un seul domaine : choisir l'un décoche l'autre, et ses observations remplacent les précédentes.
            foreach (var other in Problems)
                if (!ReferenceEquals(other, chip)) other.IsSelected = false;
            ResetObservations(chip.Text);
            Raise(nameof(SelectedProblem));
            StartGuidedCommand.RaiseCanExecuteChanged();
        }

        private void ResetObservations(string? component)
        {
            Details.Clear();
            var specific = Components.FirstOrDefault(c => c.Component == component).Observations ?? Array.Empty<string>();
            foreach (var observation in specific.Concat(CommonObservations)) Details.Add(new ChipItem(observation));
        }

        /// <summary>Un constat confié à l'assistant depuis son écran : l'analyse part aussitôt.</summary>
        public Task SolveAsync(string title, string detail)
        {
            if (IsBusy) return Task.CompletedTask;
            return StartAsync(new AssistantRequest { Mode = AssistantMode.Guided, Problem = title, Notes = detail }, title);
        }

        private Task StartGuidedAsync()
        {
            var component = SelectedProblem?.Text ?? "Autre";
            return StartAsync(new AssistantRequest
            {
                Mode = AssistantMode.Guided,
                Problem = component,
                Details = Details.Where(d => d.IsSelected).Select(d => d.Text).ToList(),
                Notes = _notes,
            }, component);
        }

        private async Task StartAsync(AssistantRequest request, string title)
        {
            var key = _vault.Read();
            if (key == null)
            {
                Stage = "Key";
                return;
            }

            _lastRequest = request;
            _failedFeedback = null;
            Raise(nameof(CanReturnToStep));
            _analyst = new ClaudeAnalyst(key);
            _round = 0;
            History.Clear();
            RunTitle = title;
            ClearRound();
            Raise(nameof(HasHistory));
            Error = string.Empty;
            ApplyText = string.Empty;

            await RunAsync(async (live, progress, token) =>
            {
                // Sans diagnostic complet, l'analyse n'aurait rien à lire : les pilotes, les journaux
                // et les services n'existent que dans celui-là.
                var snapshot = _snapshot();
                if (snapshot == null || snapshot.Metadata.RunMode != RunMode.Full)
                {
                    AddTrace("Diagnostic complet de la machine…");
                    await _runDiagnosis().ConfigureAwait(true);
                    snapshot = _snapshot();
                }
                if (snapshot == null) throw new AssistantException("Le diagnostic n'a pas abouti : il n'y a rien à analyser.");
                _before = Keys(snapshot);

                _actions = _repairs.PlanCatalog();
                _risky.Clear();
                foreach (var action in _actions)
                    if (action.Risk == "modéré" || action.Risk == "élevé") _risky.Add(action.Id);

                // Ce qui se relit à l'instant pour ce domaine part avec la demande : une lecture de moins.
                // Hors du fil de l'interface : l'essai réseau peut attendre plusieurs secondes.
                var prefetched = live == null ? null : await Task.Run(() => Prefetch(live, request.Problem), token).ConfigureAwait(true);
                var withLive = prefetched == null ? request : new AssistantRequest
                {
                    Mode = request.Mode, Problem = request.Problem, Details = request.Details, Notes = request.Notes, LiveContext = prefetched,
                };
                return await _analyst.StartAsync(snapshot, withLive, _actions, live, progress, token).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        private async Task ContinueAsync()
        {
            if (_analyst == null) return;
            var feedback = new StepFeedback
            {
                Operations = Steps.Where(s => s.IsAutomatic && s.HasRun)
                    .Select(s => (s.Title, (s.StatusKey == "Good" ? "réussie" : "en échec") + " — " + s.Status)).ToList(),
                Answers = Checks.Select(c => (c.Question, c.Answer ?? "sans réponse")).ToList(),
                Manual = Steps.Where(s => !s.IsAutomatic).Select(s => (s.Title, s.IsChecked)).ToList(),
                Note = _stepNote,
            };

            await ContinueWithAsync(feedback).ConfigureAwait(true);
        }

        /// <summary>
        /// Envoie le compte rendu d'une étape. En cas d'échec, il est gardé : « Réessayer » le renvoie
        /// tel quel au lieu de reprendre la séance depuis le début.
        /// </summary>
        private async Task ContinueWithAsync(StepFeedback feedback)
        {
            var analyst = _analyst;
            if (analyst == null) return;
            var round = _round;
            var summary = _summary;
            _failedFeedback = null;

            if (await RunAsync((_, progress, token) => analyst.ContinueAsync(feedback, progress, token)).ConfigureAwait(true))
            {
                var done = string.Join(" · ", feedback.Operations.Select(o => o.Title)
                    .Concat(feedback.Answers.Where(a => a.Answer != "sans réponse").Select(a => a.Answer)));
                History.Add(new RoundSummary(round, summary, done.Length == 0 ? "rien de fait" : done));
                Raise(nameof(HasHistory));
            }
            else if (Stage == "Failed" && analyst.CanContinue)
            {
                _failedFeedback = feedback;
            }
            Raise(nameof(CanReturnToStep));
        }

        private Task RetryAsync()
        {
            if (_failedFeedback != null && _analyst?.CanContinue == true) return ContinueWithAsync(_failedFeedback);
            return _lastRequest == null ? Task.CompletedTask : StartAsync(_lastRequest, _runTitle);
        }

        /// <summary>Vrai si l'étape est arrivée et s'affiche.</summary>
        private async Task<bool> RunAsync(Func<ILiveMachine?, IProgress<string>, CancellationToken, Task<AssistantPlan>> work)
        {
            Trace.Clear();
            Stage = "Running";
            Raise(nameof(IsBusy));
            var cancellation = _running = new CancellationTokenSource();
            _clock.Restart();

            try
            {
                var control = await _control().ConfigureAwait(true);
                var live = control == null ? null : new Services.LiveMachine(control);
                var plan = await work(live, new Progress<string>(AddTrace), cancellation.Token).ConfigureAwait(true);
                Show(plan);
                _logger.Info(Category, RunTitle + ", étape " + plan.Round + " : " + plan.Status + ", " + plan.Steps.Count + " action(s), "
                                       + plan.Checks.Count + " vérification(s), " + plan.CostDollars.ToString("0.00", Formats.French) + " $ cumulés.");
                return true;
            }
            catch (OperationCanceledException)
            {
                Stage = _round > 0 ? "Result" : "Home";
            }
            catch (AssistantException ex)
            {
                _logger.Warn(Category, RunTitle + " : " + ex.Message, ex.InnerException);
                Error = ex.Message;
                Stage = "Failed";
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Imprévu (réponse illisible, diagnostic interrompu) : l'écran reste cohérent, le
                // détail part au journal.
                _logger.Error(Category, RunTitle + " : échec inattendu.", ex);
                Error = "L'analyse s'est interrompue sur une erreur imprévue. Réessayez ; si elle revient, le journal en garde le détail.";
                Stage = "Failed";
            }
            finally
            {
                _clock.Stop();
                Raise(nameof(IsBusy));
                ContinueCommand.RaiseCanExecuteChanged();
            }
            return false;
        }

        private static string? Prefetch(ILiveMachine live, string? component)
        {
            try
            {
                return component switch
                {
                    "Son" or "Webcam, micro" or "Bluetooth" => live.Read("audio", null),
                    "Internet ou Wi-Fi" or "Réseau filaire" => live.Read("network", null),
                    _ => null,
                };
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return null;
            }
        }

        /// <summary>Une ligne de plus au fil : la précédente est faite.</summary>
        private void AddTrace(string text)
        {
            if (Trace.Count > 0) Trace[Trace.Count - 1].IsDone = true;
            Trace.Add(new AssistantTrace(text));
        }

        private void ClearRound()
        {
            Findings.Clear();
            Hypotheses.Clear();
            Steps.Clear();
            Checks.Clear();
            StepNote = string.Empty;
        }

        private void Show(AssistantPlan plan)
        {
            ClearRound();
            _round = plan.Round;
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in _actions) titles[action.Id] = action.Title;

            Summary = plan.Summary;
            Confidence = "Confiance " + plan.Confidence;
            Next = plan.Next;
            foreach (var finding in plan.Findings) Findings.Add(new AssistantFindingItem(finding));
            foreach (var hypothesis in plan.Hypotheses) Hypotheses.Add(new HypothesisItem(hypothesis));
            foreach (var check in plan.Checks) Checks.Add(new CheckItem(check));

            var number = 1;
            foreach (var step in plan.Steps)
            {
                var item = new AssistantStepItem(step, number++, step.ActionId != null && titles.TryGetValue(step.ActionId, out var t) ? t : null, RunStepAsync);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(AssistantStepItem.StatusKey)) return;
                    RaisePending();
                };
                Steps.Add(item);
            }

            StatusKey = plan.Status switch { "resolu" => "Resolved", "hors_portee" => "OutOfReach", _ => "Progress" };
            Raise(nameof(StatusText));

            var seconds = Math.Max(1, (int)Math.Round(_clock.Elapsed.TotalSeconds));
            CostText = "Étape en " + seconds + " s  ·  depuis le début : " + ValueFormat.Number(plan.InputTokens + plan.OutputTokens) + " jetons, "
                       + plan.CostDollars.ToString("0.00", Formats.French) + " $"
                       + (plan.WebSearches > 0 ? ", " + plan.WebSearches + " recherche(s) web" : string.Empty);

            foreach (var name in new[] { nameof(HasFindings), nameof(HasHypotheses), nameof(HasChecks), nameof(HasNext) }) Raise(name);
            RaisePending();
            ContinueCommand.RaiseCanExecuteChanged();
            Stage = "Result";
        }

        private void RaisePending()
        {
            Raise(nameof(PendingCount));
            Raise(nameof(HasPending));
            Raise(nameof(ContinueIsPrimary));
            Raise(nameof(SolvedIsPrimary));
            Raise(nameof(ApplyLabel));
            ApplyCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Une opération lancée seule, depuis sa carte.</summary>
        private async Task RunStepAsync(AssistantStepItem step)
        {
            if (_isApplying) return;
            _isApplying = true;
            Raise(nameof(IsBusy));
            ApplyCommand.RaiseCanExecuteChanged();
            ContinueCommand.RaiseCanExecuteChanged();
            try
            {
                await ProtectAsync(new[] { step }).ConfigureAwait(true);
                await ExecuteAsync(step).ConfigureAwait(true);
            }
            finally
            {
                _isApplying = false;
                Raise(nameof(IsBusy));
                RaisePending();
                ContinueCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Toutes les opérations de l'étape qui n'ont pas encore été lancées, dans l'ordre.</summary>
        private async Task ApplyAllAsync()
        {
            var pending = Steps.Where(s => s.IsAutomatic && !s.HasRun).ToList();
            _isApplying = true;
            Raise(nameof(IsBusy));
            ApplyCommand.RaiseCanExecuteChanged();
            ContinueCommand.RaiseCanExecuteChanged();

            int done = 0, failed = 0;
            try
            {
                await ProtectAsync(pending).ConfigureAwait(true);
                foreach (var step in pending)
                {
                    ApplyText = "Opération " + (done + failed + 1) + " sur " + pending.Count + " : " + step.Title;
                    if (await ExecuteAsync(step).ConfigureAwait(true)) done++; else failed++;
                }
            }
            finally
            {
                _isApplying = false;
                Raise(nameof(IsBusy));
                RaisePending();
                ContinueCommand.RaiseCanExecuteChanged();
            }

            ApplyText = failed == 0 ? done + " opération(s) réussie(s)." : done + " réussie(s), " + failed + " en échec : le détail est sous chaque carte.";
            _logger.Info(Category, "Étape " + _round + " : " + done + " opération(s) réussie(s), " + failed + " en échec.");
        }

        /// <summary>Une opération qui engage le système, et le point de restauration passe avant elle, une fois par intervention.</summary>
        private async Task ProtectAsync(IEnumerable<AssistantStepItem> steps)
        {
            if (!steps.Any(s => s.Step.ActionId != null && _risky.Contains(s.Step.ActionId))) return;
            ApplyText = "Point de restauration avant d'agir…";
            ApplyText = await _repairs.EnsureRestorePointAsync().ConfigureAwait(true);
        }

        private async Task<bool> ExecuteAsync(AssistantStepItem step)
        {
            step.StatusKey = "Running";
            step.Status = "En cours…";
            (bool Succeeded, string Summary)? outcome;
            try
            {
                outcome = await _repairs.RunFromPlanAsync(step.Step.ActionId!, step.Step.Parameters).ConfigureAwait(true);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // La carte ne doit pas rester « en cours » : l'échec s'y lit, la suite reste possible.
                _logger.Error(Category, step.Title + " : échec inattendu.", ex);
                step.Status = "Échec inattendu : " + ex.Message;
                step.StatusKey = "Bad";
                return false;
            }
            if (outcome == null)
            {
                step.Status = "Opération non proposée sur cette machine.";
                step.StatusKey = "Bad";
                return false;
            }

            step.Status = outcome.Value.Summary;
            step.StatusKey = outcome.Value.Succeeded ? "Good" : "Bad";
            return outcome.Value.Succeeded;
        }

        /// <summary>Le technicien déclare le problème réglé : la séance se ferme, sans rien consommer de plus.</summary>
        private void Solved()
        {
            _logger.Info(Category, RunTitle + " : réglé en " + _round + " étape(s).");
            ApplyText = "Réglé en " + _round + " étape" + (_round > 1 ? "s" : string.Empty) + ".";
            _analyst = null;
            ContinueCommand.RaiseCanExecuteChanged();
            Stage = "Done";
        }

        /// <summary>Relance un diagnostic complet et dit ce que le dépannage a réglé.</summary>
        private async Task VerifyAsync()
        {
            ApplyText = "Diagnostic de vérification…";
            await _runDiagnosis().ConfigureAwait(true);
            var after = Keys(_snapshot());
            if (_before == null || after == null)
            {
                ApplyText = "Diagnostic refait.";
                return;
            }

            var solved = _before.Count(key => !after.Contains(key));
            ApplyText = solved == 0 && after.Count == 0
                ? "Vérifié : aucun problème relevé."
                : "Vérifié : " + solved + " constat(s) réglé(s), " + after.Count + " encore présent(s).";
        }

        /// <summary>Les constats qui comptent, par règle et par sujet : de quoi comparer avant et après.</summary>
        private static HashSet<string>? Keys(SystemSnapshot? snapshot)
        {
            if (snapshot == null) return null;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var finding in snapshot.Findings)
                if (finding.Severity >= Severity.Warning) keys.Add(finding.RuleId + "|" + finding.Subject);
            return keys;
        }

        /// <summary>
        /// Une étape d'exemple, pour les captures d'écran seulement : l'écran se contrôle sans
        /// compte ni réseau. Jamais appelé en usage normal.
        /// </summary>
        public void ShowDemo(string mode)
        {
            RunTitle = "Son";
            if (mode == "running")
            {
                Trace.Clear();
                foreach (var line in new[] { "Lecture du diagnostic…", "Relevé à l'instant : le son", "Lecture : les pilotes (« audio »)" })
                    AddTrace(line);
                Stage = "Running";
                return;
            }

            _actions = _repairs.PlanCatalog();
            History.Add(new RoundSummary(1, "Pilote Realtek en place et sans erreur ; la sortie par défaut est l'écran HDMI.", "Choisir la sortie audio · Non"));
            Raise(nameof(HasHistory));
            _clock.Reset();
            Show(new AssistantPlan
            {
                Round = 2,
                Status = "en_cours",
                Summary = "Le son sort bien sur les haut-parleurs, mais rien ne s'entend : le pilote Realtek installé est la version générique, "
                          + "sans l'amplificateur du constructeur. On redémarre le périphérique, puis on vérifie à l'oreille.",
                Confidence = "moyenne",
                Next = "Si la note ne s'entend toujours pas, on installera le pilote du constructeur.",
                Hypotheses = new[]
                {
                    new Hypothesis { Cause = "Sortie par défaut sur l'écran HDMI", Likelihood = 5, State = "écartée" },
                    new Hypothesis { Cause = "Pilote audio générique, amplificateur muet", Likelihood = 70, State = "à vérifier" },
                    new Hypothesis { Cause = "Haut-parleurs internes défectueux", Likelihood = 20, State = "à vérifier" },
                },
                Steps = new[]
                {
                    new PlanStep
                    {
                        Title = "Redémarrer la carte son", Why = "Relance le pilote et l'amplificateur.", ActionId = "DEVICE-RESTART",
                        Parameters = new Dictionary<string, string> { ["device"] = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0257\4&1A2B3C&0&0001" },
                    },
                    new PlanStep { Title = "Jouer un son de test", Why = "Seule l'oreille dira si le son sort.", ActionId = "AUDIO-TEST-SOUND" },
                    new PlanStep { Title = "Brancher un casque", Why = "Sépare le pilote des haut-parleurs.", Manual = "Branchez un casque sur la prise jack, puis rejouez le son." },
                },
                Checks = new[]
                {
                    new CheckQuestion { Question = "Avez-vous entendu la note sur les haut-parleurs ?", Answers = new[] { "Oui", "Non", "Très faible" } },
                    new CheckQuestion { Question = "Et au casque ?", Answers = new[] { "Oui", "Non", "Pas testé" } },
                },
                InputTokens = 14_200,
                OutputTokens = 2_100,
                CostDollars = 0.06,
            });
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
        }
    }
}
