using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.App.Mvvm;
using LDI12.Assistant;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    /// <summary>Une ligne du fil de l'analyse : ce que Claude est en train de lire.</summary>
    public sealed class AssistantTrace : ObservableObject
    {
        private bool _isDone;

        public AssistantTrace(string text) => Text = text;

        public string Text { get; }

        /// <summary>Faux pour la dernière ligne : c'est ce que Claude fait en ce moment.</summary>
        public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
    }

    /// <summary>Un constat de Claude, avec sa couleur.</summary>
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

    /// <summary>Une étape du plan, cochée par défaut, avec son compte rendu une fois lancée.</summary>
    public sealed class AssistantStepItem : ObservableObject
    {
        private bool _isChecked;
        private string _status = string.Empty;
        private string _statusKey = "None";

        public AssistantStepItem(PlanStep step, int number, string? actionTitle)
        {
            Step = step;
            Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            KindText = step.IsAutomatic ? "LDI12 : " + (actionTitle ?? step.ActionId) : "À faire à la main";
            _isChecked = step.IsAutomatic;
        }

        public PlanStep Step { get; }

        public string Number { get; }

        public string Title => Step.Title;

        public string Why => Step.Why;

        public string? Manual => Step.Manual;

        public bool HasManual => !string.IsNullOrWhiteSpace(Step.Manual);

        public bool IsAutomatic => Step.IsAutomatic;

        public string KindText { get; }

        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && IsAutomatic); }

        public string Status { get => _status; set => Set(ref _status, value); }

        /// <summary>None, Running, Good, Bad.</summary>
        public string StatusKey { get => _statusKey; set => Set(ref _statusKey, value); }
    }

    /// <summary>
    /// L'assistant : Claude analyse les pilotes, ou un problème décrit en quelques choix, et rend
    /// un plan que le technicien lance d'un clic.
    /// </summary>
    /// <remarks>
    /// Le technicien ne converse pas avec Claude : il choisit, Claude travaille en arrière-plan, un
    /// plan revient. Rien ne touche la machine avant le clic sur « Appliquer » ; chaque opération
    /// passe alors par le même relevé préalable et le même journal que depuis l'écran Réparations.
    /// </remarks>
    public sealed class AssistantViewModel : ObservableObject
    {
        private const string Category = "Assistant";

        private static readonly string[] ProblemChoices =
        {
            "Lenteurs", "Démarrage long ou impossible", "Écran bleu, plantages", "Internet ou Wi-Fi",
            "Son", "Affichage, écran", "Imprimante", "Périphérique USB", "Chauffe, bruit, batterie", "Autre",
        };

        private static readonly string[] DetailChoices =
        {
            "Depuis une mise à jour", "Depuis l'installation d'un logiciel", "Après un choc ou une coupure",
            "Par intermittence", "En permanence", "Au démarrage", "Sous charge (jeu, vidéo)", "Depuis toujours",
        };

        private readonly ILdiLogger _logger;
        private readonly ApiKeyVault _vault;
        private readonly Func<SystemSnapshot?> _snapshot;
        private readonly Func<Task> _runDiagnosis;
        private readonly RepairsViewModel _repairs;
        private CancellationTokenSource? _running;
        private AssistantRequest? _lastRequest;
        private string _stage = "Key";
        private string _keyInput = string.Empty;
        private string _keyError = string.Empty;
        private string _keyText = string.Empty;
        private bool _isCheckingKey;
        private string _notes = string.Empty;
        private string _runTitle = string.Empty;
        private string _summary = string.Empty;
        private string _confidence = string.Empty;
        private string _costText = string.Empty;
        private string _error = string.Empty;
        private string _applyText = string.Empty;
        private bool _isApplying;
        private bool _isApplied;
        private readonly Stopwatch _clock = new Stopwatch();

        /// <summary>
        /// Les plans déjà obtenus pour ce diagnostic : la même demande sur le même relevé ne se
        /// repaie pas. Vidé de fait à chaque nouveau diagnostic, qui change l'identifiant.
        /// </summary>
        private readonly Dictionary<string, (AssistantPlan Plan, IReadOnlyList<AvailableAction> Actions)> _plans
            = new Dictionary<string, (AssistantPlan, IReadOnlyList<AvailableAction>)>();

        /// <summary>Les opérations du catalogue qui modifient vraiment le système : un point de restauration les précède.</summary>
        private readonly HashSet<string> _risky = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les constats avant application du plan, pour dire ensuite ce qui est réglé.</summary>
        private HashSet<string>? _before;

        public AssistantViewModel(
            ILdiLogger logger, ApiKeyVault vault, Func<SystemSnapshot?> snapshot, Func<Task> runDiagnosis, RepairsViewModel repairs)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _runDiagnosis = runDiagnosis ?? throw new ArgumentNullException(nameof(runDiagnosis));
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));

            foreach (var choice in ProblemChoices) Problems.Add(new ChipItem(choice));
            foreach (var choice in DetailChoices) Details.Add(new ChipItem(choice));
            foreach (var chip in Problems) chip.PropertyChanged += OnProblemChanged;

            SaveKeyCommand = new AsyncRelayCommand(SaveKeyAsync, () => !_isCheckingKey && _keyInput.Trim().Length > 10);
            ForgetKeyCommand = new RelayCommand(ForgetKey);
            OpenConsoleCommand = new RelayCommand(() => Open("https://console.anthropic.com/settings/keys"));
            DriversCommand = new AsyncRelayCommand(() => RunAsync(new AssistantRequest { Mode = AssistantMode.Drivers }, "Analyse des pilotes"));
            GuideCommand = new RelayCommand(() => Stage = "Guide");
            StartGuidedCommand = new AsyncRelayCommand(StartGuidedAsync, () => SelectedProblem != null);
            BackCommand = new RelayCommand(() => Stage = "Home");
            CancelCommand = new RelayCommand(() => _running?.Cancel());
            RetryCommand = new AsyncRelayCommand(() => _lastRequest == null ? Task.CompletedTask : RunAsync(_lastRequest, _runTitle));
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !_isApplying && !_isApplied && CheckedCount > 0);
            VerifyCommand = new AsyncRelayCommand(VerifyAsync);

            Refresh();
        }

        public ObservableCollection<ChipItem> Problems { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<ChipItem> Details { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<AssistantTrace> Trace { get; } = new ObservableCollection<AssistantTrace>();
        public ObservableCollection<AssistantFindingItem> Findings { get; } = new ObservableCollection<AssistantFindingItem>();
        public ObservableCollection<AssistantStepItem> Steps { get; } = new ObservableCollection<AssistantStepItem>();

        public ICommand SaveKeyCommand { get; }
        public ICommand ForgetKeyCommand { get; }
        public ICommand OpenConsoleCommand { get; }
        public ICommand DriversCommand { get; }
        public ICommand GuideCommand { get; }
        public AsyncRelayCommand StartGuidedCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RetryCommand { get; }
        public AsyncRelayCommand ApplyCommand { get; }
        public ICommand VerifyCommand { get; }

        /// <summary>Key, Home, Guide, Running, Result, Failed.</summary>
        public string Stage
        {
            get => _stage;
            private set
            {
                if (!Set(ref _stage, value)) return;
                foreach (var name in new[] { nameof(IsKey), nameof(IsHome), nameof(IsGuide), nameof(IsRunning), nameof(IsResult), nameof(IsFailed), nameof(HasKey) })
                    Raise(name);
            }
        }

        public bool IsKey => _stage == "Key";
        public bool IsHome => _stage == "Home";
        public bool IsGuide => _stage == "Guide";
        public bool IsRunning => _stage == "Running";
        public bool IsResult => _stage == "Result";
        public bool IsFailed => _stage == "Failed";

        /// <summary>Une analyse ou un plan en cours : la fenêtre demande confirmation avant de fermer.</summary>
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

        /// <summary>« sk-ant-…4f2a · sur cette clé USB ».</summary>
        public string KeyText { get => _keyText; private set => Set(ref _keyText, value); }

        public string Notes { get => _notes; set => Set(ref _notes, value ?? string.Empty); }

        public ChipItem? SelectedProblem
        {
            get
            {
                foreach (var chip in Problems) if (chip.IsSelected) return chip;
                return null;
            }
        }

        public string RunTitle { get => _runTitle; private set => Set(ref _runTitle, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Confidence { get => _confidence; private set => Set(ref _confidence, value); }
        public string CostText { get => _costText; private set => Set(ref _costText, value); }
        public string Error { get => _error; private set => Set(ref _error, value); }
        public string ApplyText { get => _applyText; private set => Set(ref _applyText, value); }

        public bool HasFindings => Findings.Count > 0;

        public int CheckedCount
        {
            get
            {
                var count = 0;
                foreach (var step in Steps) if (step.IsChecked) count++;
                return count;
            }
        }

        public string ApplyLabel => CheckedCount switch
        {
            0 => "Aucune opération cochée",
            1 => "Appliquer l'opération",
            _ => "Appliquer les " + CheckedCount + " opérations",
        };

        public bool IsApplied { get => _isApplied; private set => Set(ref _isApplied, value); }

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
            // Un seul genre de problème : choisir l'un décoche l'autre.
            foreach (var other in Problems)
                if (!ReferenceEquals(other, chip)) other.IsSelected = false;
            Raise(nameof(SelectedProblem));
            StartGuidedCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Un constat confié à l'assistant depuis son écran : l'analyse part aussitôt.</summary>
        public Task SolveAsync(string title, string detail)
        {
            if (IsBusy) return Task.CompletedTask;
            return RunAsync(new AssistantRequest { Mode = AssistantMode.Guided, Problem = title, Notes = detail }, title);
        }

        private Task StartGuidedAsync()
        {
            var details = new List<string>();
            foreach (var chip in Details) if (chip.IsSelected) details.Add(chip.Text);
            return RunAsync(new AssistantRequest
            {
                Mode = AssistantMode.Guided,
                Problem = SelectedProblem?.Text,
                Details = details,
                Notes = _notes,
            }, SelectedProblem?.Text ?? "Diagnostic guidé");
        }

        private async Task RunAsync(AssistantRequest request, string title)
        {
            var key = _vault.Read();
            if (key == null)
            {
                Stage = "Key";
                return;
            }

            _lastRequest = request;
            RunTitle = title;
            Trace.Clear();
            Findings.Clear();
            Steps.Clear();
            Error = string.Empty;
            ApplyText = string.Empty;
            IsApplied = false;
            Stage = "Running";
            Raise(nameof(IsBusy));

            var cancellation = _running = new CancellationTokenSource();
            var progress = new Progress<string>(AddTrace);
            _clock.Restart();

            try
            {
                // Sans diagnostic complet, Claude n'aurait rien à lire : les pilotes, les journaux
                // et les services n'existent que dans celui-là.
                var snapshot = _snapshot();
                if (snapshot == null || snapshot.Metadata.RunMode != RunMode.Full)
                {
                    AddTrace("Diagnostic complet de la machine…");
                    await _runDiagnosis().ConfigureAwait(true);
                    snapshot = _snapshot();
                }
                if (snapshot == null) throw new AssistantException("Le diagnostic n'a pas abouti : il n'y a rien à analyser.");

                var cacheKey = snapshot.Metadata.SnapshotId + "|" + request.Mode + "|" + request.Problem + "|"
                               + string.Join(",", request.Details) + "|" + request.Notes;
                if (_plans.TryGetValue(cacheKey, out var known))
                {
                    Show(new AssistantPlan
                    {
                        Summary = known.Plan.Summary, Confidence = known.Plan.Confidence,
                        Findings = known.Plan.Findings, Steps = known.Plan.Steps, FromCache = true,
                    }, known.Actions);
                    return;
                }

                var actions = _repairs.PlanCatalog();
                var plan = await new ClaudeAnalyst(key)
                    .AnalyseAsync(snapshot, request, actions, progress, cancellation.Token)
                    .ConfigureAwait(true);

                _plans[cacheKey] = (plan, actions);
                Show(plan, actions);
                _logger.Info(Category, title + " : " + plan.Steps.Count + " étape(s), " + (plan.InputTokens + plan.OutputTokens) + " jetons.");
            }
            catch (OperationCanceledException)
            {
                Stage = "Home";
            }
            catch (AssistantException ex)
            {
                _logger.Warn(Category, title + " : " + ex.Message, ex.InnerException);
                Error = ex.Message;
                Stage = "Failed";
            }
            finally
            {
                _clock.Stop();
                Raise(nameof(IsBusy));
            }
        }

        /// <summary>Une ligne de plus au fil : la précédente est faite.</summary>
        private void AddTrace(string text)
        {
            if (Trace.Count > 0) Trace[Trace.Count - 1].IsDone = true;
            Trace.Add(new AssistantTrace(text));
        }

        private void Show(AssistantPlan plan, IReadOnlyList<AvailableAction> actions)
        {
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions) titles[action.Id] = action.Title;

            Summary = plan.Summary;
            Confidence = "Confiance " + plan.Confidence;
            foreach (var finding in plan.Findings) Findings.Add(new AssistantFindingItem(finding));

            var number = 1;
            foreach (var step in plan.Steps)
            {
                var item = new AssistantStepItem(step, number++, step.ActionId != null && titles.TryGetValue(step.ActionId, out var t) ? t : null);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(AssistantStepItem.IsChecked)) return;
                    Raise(nameof(CheckedCount));
                    Raise(nameof(ApplyLabel));
                    ApplyCommand.RaiseCanExecuteChanged();
                };
                Steps.Add(item);
            }

            _risky.Clear();
            foreach (var action in actions)
                if (action.Risk == "modéré" || action.Risk == "élevé") _risky.Add(action.Id);

            var seconds = Math.Max(1, (int)Math.Round(_clock.Elapsed.TotalSeconds));
            CostText = plan.FromCache
                ? "Même analyse qu'à l'instant, sur le même diagnostic : reprise telle quelle, sans rien consommer."
                : "Analyse en " + seconds + " s  ·  " + ValueFormat.Number(plan.InputTokens + plan.OutputTokens) + " jetons  ·  "
                  + plan.CostDollars.ToString("0.00", Formats.French) + " $"
                  + (plan.WebSearches > 0 ? "  ·  " + plan.WebSearches + " recherche(s) web" : string.Empty);

            Raise(nameof(HasFindings));
            Raise(nameof(CheckedCount));
            Raise(nameof(ApplyLabel));
            ApplyCommand.RaiseCanExecuteChanged();
            Stage = "Result";
        }

        private async Task ApplyAsync()
        {
            _isApplying = true;
            Raise(nameof(IsBusy));
            ApplyCommand.RaiseCanExecuteChanged();

            int done = 0, failed = 0;
            _before = Keys(_snapshot());
            try
            {
                // Une seule opération qui engage le système, et le filet de sécurité passe avant tout.
                foreach (var step in Steps)
                {
                    if (!step.IsChecked || step.Step.ActionId == null || !_risky.Contains(step.Step.ActionId)) continue;
                    ApplyText = "Point de restauration avant d'agir…";
                    ApplyText = await _repairs.EnsureRestorePointAsync().ConfigureAwait(true);
                    break;
                }

                foreach (var step in Steps)
                {
                    if (!step.IsChecked || step.Step.ActionId == null) continue;

                    step.StatusKey = "Running";
                    step.Status = "En cours…";
                    ApplyText = "Opération " + (done + failed + 1) + " sur " + CheckedCount + " : " + step.Title;

                    var outcome = await _repairs.RunFromPlanAsync(step.Step.ActionId, step.Step.Parameters).ConfigureAwait(true);
                    if (outcome == null)
                    {
                        step.StatusKey = "Bad";
                        step.Status = "Opération non proposée sur cette machine.";
                        failed++;
                        continue;
                    }

                    step.StatusKey = outcome.Value.Succeeded ? "Good" : "Bad";
                    step.Status = outcome.Value.Summary;
                    if (outcome.Value.Succeeded) done++; else failed++;
                }
            }
            finally
            {
                _isApplying = false;
                IsApplied = true;
                Raise(nameof(IsBusy));
                ApplyCommand.RaiseCanExecuteChanged();
            }

            ApplyText = failed == 0
                ? done + " opération(s) réussie(s). Relancez le diagnostic pour vérifier."
                : done + " réussie(s), " + failed + " en échec : le détail est sous chaque étape.";
            _logger.Info(Category, "Plan appliqué : " + done + " réussie(s), " + failed + " en échec.");
        }

        /// <summary>Relance un diagnostic complet et dit ce que le plan a réglé.</summary>
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

            var solved = 0;
            foreach (var key in _before) if (!after.Contains(key)) solved++;
            var remaining = after.Count;
            ApplyText = solved == 0 && remaining == 0
                ? "Vérifié : aucun problème relevé."
                : "Vérifié : " + solved + " constat(s) réglé(s), " + remaining + " encore présent(s).";
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
        /// Un plan d'exemple, pour les captures d'écran seulement : l'écran de résultat se
        /// contrôle sans compte ni réseau. Jamais appelé en usage normal.
        /// </summary>
        public void ShowDemo(string mode)
        {
            RunTitle = "Analyse des pilotes";
            if (mode == "running")
            {
                Trace.Clear();
                foreach (var line in new[] { "Lecture du diagnostic…", "Lecture : la machine", "Lecture : les pilotes",
                                             "Lecture : les périphériques (« pci »)", "Recherche sur le web : Realtek RTL8821CE driver" })
                    AddTrace(line);
                Stage = "Running";
                return;
            }

            _clock.Reset();
            Show(new AssistantPlan
            {
                Summary = "Le Wi-Fi décroche à cause d'un pilote Realtek de 2019, remplacé par Windows Update par une version générique. "
                          + "La carte graphique tourne aussi sur un pilote ancien. Le plan réinitialise le réseau et indique les pilotes à installer.",
                Confidence = "forte",
                Findings = new[]
                {
                    new PlanFinding { Title = "Wi-Fi : pilote générique", Detail = "Realtek RTL8821CE, pilote Microsoft 2019.6.12 au lieu du pilote constructeur 2024.", Severity = "problème" },
                    new PlanFinding { Title = "Carte graphique ancienne version", Detail = "Intel UHD 620, pilote 26.20 de 2020 ; la 31.0 corrige des écrans noirs.", Severity = "attention" },
                    new PlanFinding { Title = "Aucun périphérique en erreur", Detail = "Gestionnaire de périphériques sans code d'erreur.", Severity = "info" },
                },
                Steps = new[]
                {
                    new PlanStep
                    {
                        Title = "Réinstaller la carte Wi-Fi", Why = "Elle est en erreur (code 43) : un pilote remis à neuf la relance.",
                        ActionId = "DEVICE-REINSTALL",
                        Parameters = new Dictionary<string, string> { ["device"] = @"PCI\VEN_10EC&DEV_C821\4&2F0E3F0&0&00E1" },
                    },
                    new PlanStep
                    {
                        Title = "Installer le pilote Realtek de Windows Update", Why = "Remplace le pilote générique de 2019.",
                        ActionId = "DRIVERS-WINDOWS-UPDATE", Parameters = new Dictionary<string, string> { ["filter"] = "Realtek" },
                    },
                    new PlanStep { Title = "Désactiver le démarrage rapide", Why = "Le pilote repartira à neuf à chaque allumage.", ActionId = "FAST-STARTUP-OFF" },
                    new PlanStep { Title = "Pilote graphique Intel", Why = "La version 31.0 corrige les écrans noirs.", Manual = "Site d'assistance Intel, outil Driver & Support Assistant." },
                },
                InputTokens = 9_800,
                OutputTokens = 1_450,
                WebSearches = 1,
                CostDollars = 0.07,
            }, _repairs.PlanCatalog());
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
        }
    }
}
