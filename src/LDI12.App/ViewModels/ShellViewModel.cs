using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.App.Theming;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine.Orchestration;
using LDI12.Publishing.Klarvi;
using LDI12.Reports.Facts;

namespace LDI12.App.ViewModels
{
    public sealed class NavigationItem : ObservableObject
    {
        private Severity? _worst;

        public NavigationItem(string title, string iconKey, object content)
        {
            Title = title;
            IconKey = iconKey;
            Content = content;
        }

        public string Title { get; }
        public string IconKey { get; }
        public object Content { get; }

        /// <summary>
        /// Pastille de sévérité : le technicien voit où sont les problèmes sans ouvrir les onglets.
        /// </summary>
        public Severity? Worst
        {
            get => _worst;
            set => Set(ref _worst, value);
        }
    }

    /// <summary>
    /// Coquille de l'application : navigation, exécution du diagnostic, état global.
    /// </summary>
    /// <remarks>
    /// Rien de bloquant ne se produit à la construction : la fenêtre doit s'afficher en moins de
    /// 800 ms sur la machine la plus lente du parc. La couche plateforme se construit et la
    /// première analyse se lance seulement après le premier rendu.
    /// </remarks>
    public sealed class ShellViewModel : ObservableObject, IDisposable
    {
        private readonly DiagnosticService _diagnostics;
        private readonly ILdiLogger _logger;
        private readonly OverviewViewModel _overview = new OverviewViewModel();
        private readonly List<SectionViewModel> _sections = new List<SectionViewModel>();
        private readonly ReportsViewModel _reports;
        private readonly RepairsViewModel _repairs;
        private readonly CleanupViewModel _cleanup;
        private readonly ToolsViewModel _tools;
        private readonly HistoryViewModel _history;
        private readonly MonitoringViewModel _monitoring;
        private readonly MeasuresViewModel _measures;
        private readonly UserDataViewModel _userData;
        private readonly ActionService _actions;
        private readonly SettingsService _settings;
        private readonly SettingsViewModel _settingsScreen;
        private readonly AboutViewModel _about;
        private readonly UpdateService _updates;
        private CancellationTokenSource? _running;

        private NavigationItem? _selectedNavigation;
        private object? _currentContent;
        private bool _isScanning;
        private double _progressValue;
        private string _progressText = string.Empty;
        private bool _hasProgressText;
        private string _machineName = Environment.MachineName;
        private string _windowsLabel = "Identification en cours…";
        private string _windowsDetail = string.Empty;
        private string _elevationLabel = "…";
        private string _statusLine = "Préparation de l'analyse…";
        private bool _isDarkTheme = true;

        public ShellViewModel(DiagnosticService diagnostics, ILdiLogger logger)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            RunFullScanCommand = new AsyncRelayCommand(() => RunAsync(RunMode.Full), () => !IsScanning);
            RunQuickScanCommand = new AsyncRelayCommand(() => RunAsync(RunMode.Quick), () => !IsScanning);
            CancelCommand = new RelayCommand(Cancel, () => IsScanning);
            ToggleThemeCommand = new RelayCommand(ToggleTheme);

            // Le barème est relu avant la première analyse : un seuil ajusté doit s'appliquer
            // dès l'analyse rapide du démarrage, sans quoi le premier score afficherait autre
            // chose que les suivants.
            _settings = new SettingsService(logger);
            _settings.Load();
            _diagnostics.ActiveProfile = () => _settings.Active;

            _settingsScreen = new SettingsViewModel(
                _settings, logger, ApplyAdvancedSensors,
                _diagnostics.DescribeCatalogue(),
                selection => _ = RunAsync(RunMode.Custom, selection),
                () => _repairs.GetRunnerAsync());
            _actions = new ActionService(_diagnostics, logger);
            _reports = new ReportsViewModel(logger, _actions.Journal, new KlarviPublisher(logger));
            _repairs = new RepairsViewModel(_actions, logger);
            _cleanup = new CleanupViewModel(_actions, logger, () => _repairs.GetRunnerAsync());
            _tools = new ToolsViewModel(logger, () => _repairs.GetRunnerAsync());
            _about = new AboutViewModel(logger);

            // La vérification de mise à jour ne part jamais pendant une analyse : le
            // remplacement de l'exécutable ferme l'application, et la fermer au milieu d'un
            // diagnostic chez un client serait exactement le mauvais moment.
            _updates = new UpdateService(_settings, logger);
            Updates = new UpdateViewModel(_updates, logger, () => IsScanning);
            Updates.QuitRequested += (_, __) => System.Windows.Application.Current?.Shutdown(0);

            // Une mise à jour demandée pendant une analyse attend la fin de celle-ci, puis
            // repart d'elle-même.
            ScanCompleted += (_, __) => Updates.Resume();
            _history = new HistoryViewModel(logger, () => _reports.Directory);
            _monitoring = new MonitoringViewModel(_diagnostics, _actions.Journal, logger);
            _measures = new MeasuresViewModel(_diagnostics, _actions.Journal, logger);
            _userData = new UserDataViewModel(_diagnostics, _actions.Journal, logger, () => _repairs.GetRunnerAsync());
            Scan = new ScanViewModel();

            // Le journal se relit au même endroit que les rapports : c'est là qu'on vérifie que
            // ce qu'on s'apprête à remettre correspond à ce qu'on a fait.
            _repairs.JournalChanged += (_, __) => _reports.RefreshJournal();
            _cleanup.JournalChanged += (_, __) => _reports.RefreshJournal();
            _tools.JournalChanged += (_, __) => _reports.RefreshJournal();
            _monitoring.JournalChanged += (_, __) => _reports.RefreshJournal();
            _measures.JournalChanged += (_, __) => _reports.RefreshJournal();
            _userData.JournalChanged += (_, __) => _reports.RefreshJournal();

            // Un barème modifié périme le diagnostic affiché : le score et les constats à l'écran
            // ont été calculés avec les seuils précédents.
            _settingsScreen.ProfileChanged += (_, __) =>
                StatusLine = "Barème modifié, relancez une analyse pour appliquer les nouveaux seuils.";

            Navigation = new ObservableCollection<NavigationItem>
            {
                new NavigationItem("Vue d'ensemble", "Icon.Home", _overview),
                NavSection("Matériel", "Icon.Cpu", DiagnosticCategory.Hardware),
                NavSection("Stockage", "Icon.HardDrive", DiagnosticCategory.Storage),
                NavSection("Windows", "Icon.Window", DiagnosticCategory.Windows),
                NavSection("Réseau", "Icon.Globe", DiagnosticCategory.Network),
                NavSection("Sécurité", "Icon.Shield", DiagnosticCategory.Security),
                NavSection("Performances", "Icon.Activity", DiagnosticCategory.Performance),
                new NavigationItem("Surveillance", "Icon.Pulse", _monitoring),
                new NavigationItem("Mesures", "Icon.Gauge", _measures),
                new NavigationItem("Réparations", "Icon.Wrench", _repairs),
                new NavigationItem("Nettoyage", "Icon.Trash", _cleanup),
                new NavigationItem("Données", "Icon.Archive", _userData),
                new NavigationItem("Outils", "Icon.Toolbox", _tools),
                new NavigationItem("Rapports", "Icon.FileText", _reports),
                new NavigationItem("Historique", "Icon.History", _history),
                new NavigationItem("Réglages", "Icon.Sliders", _settingsScreen),

                // En dernier, et après les réglages : c'est l'écran qu'on ouvre par curiosité ou
                // pour retrouver une adresse, jamais au milieu d'un diagnostic.
                new NavigationItem("À propos", "Icon.Info", _about),
            };

            SelectedNavigation = Navigation[0];
            _isDarkTheme = ThemeManager.Current == AppTheme.Dark;
        }

        public ObservableCollection<NavigationItem> Navigation { get; }

        /// <summary>Le bandeau de mise à jour, et ce qu'il déclenche.</summary>
        public UpdateViewModel Updates { get; }

        /// <summary>Écran d'analyse plein cadre, actif pendant une collecte.</summary>
        public ScanViewModel Scan { get; }

        public ICommand RunFullScanCommand { get; }
        public ICommand RunQuickScanCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ToggleThemeCommand { get; }

        public SystemSnapshot? Snapshot { get; private set; }

        /// <summary>Diagnostic terminé, sert notamment au mode capture d'écran.</summary>
        public event EventHandler? ScanCompleted;

        public NavigationItem? SelectedNavigation
        {
            get => _selectedNavigation;
            set { if (Set(ref _selectedNavigation, value)) CurrentContent = value?.Content; }
        }

        public object? CurrentContent { get => _currentContent; private set => Set(ref _currentContent, value); }

        public bool IsScanning
        {
            get => _isScanning;
            private set { if (Set(ref _isScanning, value)) RaiseCommandStates(); }
        }

        public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
        public bool HasProgressText { get => _hasProgressText; private set => Set(ref _hasProgressText, value); }
        public string MachineName { get => _machineName; private set => Set(ref _machineName, value); }
        public string WindowsLabel { get => _windowsLabel; private set => Set(ref _windowsLabel, value); }

        /// <summary>Version complète, compilation et architecture comprises. Infobulle de la barre.</summary>
        public string WindowsDetail { get => _windowsDetail; private set => Set(ref _windowsDetail, value); }
        public string ElevationLabel { get => _elevationLabel; private set => Set(ref _elevationLabel, value); }
        public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
        public bool IsDarkTheme { get => _isDarkTheme; private set => Set(ref _isDarkTheme, value); }

        /// <summary>
        /// Lancée après le premier rendu : identification de la plateforme puis analyse rapide,
        /// pour que l'écran d'accueil soit renseigné dès l'arrivée chez le client.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                var profile = services.Platform.Profile;

                WindowsLabel = profile.ShortName;
                WindowsDetail = profile.DisplayName;
                ElevationLabel = services.Platform.IsElevated ? "Administrateur" : "Utilisateur standard";

                // Le réglage relu au démarrage doit s'appliquer avant la première analyse.
                services.Sensors.Enable(_settings.AdvancedSensors);

                if (profile.Level == CompatibilityLevel.Unsupported)
                {
                    StatusLine = "Plateforme non prise en charge : " + profile.LevelReason;
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Shell", "La plateforme n'a pas pu être identifiée.", ex);
                WindowsLabel = "Plateforme non identifiée";
                StatusLine = "La plateforme n'a pas pu être identifiée : " + ex.Message;
                return;
            }

            // Les écrans d'intervention n'attendent pas l'analyse : la disponibilité d'une
            // réparation dépend de la plateforme, pas des constats.
            await _repairs.InitializeAsync().ConfigureAwait(true);
            await _tools.InitializeAsync().ConfigureAwait(true);

            await RunAsync(RunMode.Quick).ConfigureAwait(true);
        }

        /// <summary>
        /// Applique le réglage des capteurs à la couche plateforme déjà construite.
        /// </summary>
        /// <remarks>
        /// Rien n'est chargé ici : la bibliothèque n'ouvre le pilote qu'à la première lecture,
        /// donc à la prochaine analyse. Cocher la case ne doit pas, à soi seul, toucher à la
        /// machine du client.
        /// </remarks>
        private async void ApplyAdvancedSensors(bool enabled)
        {
            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                services.Sensors.Enable(enabled);
            }
            catch (Exception ex)
            {
                _logger.Error("Shell", "Le réglage des capteurs n'a pas pu être appliqué.", ex);
            }
        }

        private void Cancel()
        {
            StatusLine = "Annulation demandée…";
            _running?.Cancel();
        }

        private void ToggleTheme()
        {
            ThemeManager.Toggle();
            IsDarkTheme = ThemeManager.Current == AppTheme.Dark;
        }

        /// <param name="selection">
        /// Modules retenus pour une analyse personnalisée. Nul pour les deux modes ordinaires.
        /// </param>
        private async Task RunAsync(RunMode mode, IReadOnlyCollection<string>? selection = null)
        {
            if (IsScanning) return;

            _running?.Dispose();
            _running = new CancellationTokenSource();

            IsScanning = true;
            ProgressValue = 0;
            HasProgressText = true;
            ProgressText = "Préparation…";
            StatusLine = mode switch
            {
                RunMode.Quick => "Analyse rapide en cours…",
                RunMode.Custom => "Analyse personnalisée en cours…",
                _ => "Analyse complète en cours…",
            };
            _overview.BeginScan();
            Scan.Machine = MachineName;
            Scan.Begin(mode, Plan(mode, selection));

            // Progress<T> capture le contexte de synchronisation : les rapports reviennent
            // automatiquement sur le fil d'interface, sans Dispatcher explicite.
            var progress = new Progress<DiagnosticProgress>(report =>
            {
                Scan.Report(report);
                ProgressValue = report.Fraction * 100d;
                ProgressText = report.Completed + " / " + report.Total + " : " +
                               (report.StartedProbe ?? report.CurrentProbe);
            });

            try
            {
                var snapshot = await _diagnostics.RunAsync(mode, progress, _running.Token, selection)
                    .ConfigureAwait(true);
                Apply(snapshot);

                var duration = (snapshot.Metadata.DurationMs / 1000d).ToString("0.0", CultureInfo.CurrentCulture);
                var label = mode switch
                {
                    RunMode.Quick => "Analyse rapide",
                    RunMode.Custom => "Analyse personnalisée",
                    _ => "Analyse complète",
                };

                StatusLine = label + " terminée en " + duration + " s : " +
                             snapshot.Findings.Count + " constat(s).";
            }
            catch (OperationCanceledException)
            {
                StatusLine = "Analyse annulée. Les résultats précédents sont conservés.";
            }
            catch (Exception ex)
            {
                _logger.Error("Shell", "L'analyse a échoué.", ex);
                StatusLine = "L'analyse n'a pas pu être menée à son terme : " + ex.Message;
            }
            finally
            {
                IsScanning = false;
                HasProgressText = false;
                ProgressValue = 0;

                // Après Apply : l'écran d'analyse s'efface et l'anneau de score enchaîne sur le
                // balayage vers sa valeur.
                Scan.End();
                _overview.EndScan();
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Apply(SystemSnapshot snapshot)
        {
            Snapshot = snapshot;
            _overview.Update(snapshot);

            // Une seule construction pour les six écrans : les fiches sont les mêmes que celles
            // qu'utiliseront les rapports, et les rebâtir par onglet coûterait quatre passes de
            // plus pour un résultat identique.
            var sheets = FactSheetBuilder.Build(snapshot);
            foreach (var section in _sections) section.Update(snapshot, sheets);
            _reports.Update(snapshot);
            _actions.Update(snapshot);
            _repairs.Update(snapshot);
            _history.Apply(snapshot);
            _measures.Update(snapshot);
            _userData.Update(snapshot);

            foreach (var item in Navigation)
            {
                if (!(item.Content is SectionViewModel section)) continue;

                Severity? worst = null;
                foreach (var finding in snapshot.Findings)
                {
                    if (finding.Category != section.Category) continue;
                    if (finding.Severity == Severity.Info) continue;
                    if (worst == null || finding.Severity > worst.Value) worst = finding.Severity;
                }
                item.Worst = worst;
            }
        }

        /// <summary>
        /// Le plan affiché par l'écran d'analyse.
        /// </summary>
        /// <remarks>
        /// Une analyse personnalisée doit montrer ce que le technicien a choisi, et rien d'autre :
        /// afficher le plan d'un diagnostic complet ferait attendre douze modules qui ne
        /// s'exécuteront pas.
        /// </remarks>
        private IReadOnlyList<(string Id, string Name, DiagnosticCategory Category)> Plan(
            RunMode mode, IReadOnlyCollection<string>? selection)
        {
            var modules = _diagnostics.DescribeModules(mode == RunMode.Custom ? RunMode.Full : mode);
            if (selection == null) return modules;

            var chosen = new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase);
            var plan = new List<(string, string, DiagnosticCategory)>();
            foreach (var module in modules)
                if (chosen.Contains(module.Id)) plan.Add(module);

            return plan;
        }

        private NavigationItem NavSection(string title, string iconKey, DiagnosticCategory category)
        {
            var section = new SectionViewModel(title, iconKey, category);
            _sections.Add(section);
            return new NavigationItem(title, iconKey, section);
        }

        private void RaiseCommandStates()
        {
            (RunFullScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunQuickScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            // La surveillance tourne sur le fil d'interface : la laisser vivre après la fermeture
            // de la fenêtre garderait un relevé par seconde sur la machine d'un client.
            _monitoring.Shutdown();
            _measures.Shutdown();
            _userData.Shutdown();

            _running?.Dispose();
            _actions.Dispose();
            _diagnostics.Dispose();
        }
    }
}
