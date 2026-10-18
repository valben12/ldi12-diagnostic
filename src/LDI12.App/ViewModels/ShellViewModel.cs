using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    /// <summary>
    /// Une catégorie de la barre latérale : Diagnostic, Agir, Mesurer… repliable d'un clic.
    /// </summary>
    /// <remarks>
    /// Vingt entrées à plat se parcourent ; cinq catégories se lisent. Repliée, une catégorie
    /// garde la pastille de la plus grave de ses entrées : rien de ce qui demande attention ne
    /// disparaît en la fermant. Les entrées d'accueil (vue d'ensemble, intervention, assistant)
    /// n'ont pas de titre et restent toujours visibles.
    /// </remarks>
    public sealed class NavigationGroup : ObservableObject
    {
        private bool _isExpanded, _holdsCurrent;
        private Severity? _worst;

        public NavigationGroup(string title, bool expanded)
        {
            Title = title;
            _isExpanded = expanded;
        }

        public string Title { get; }

        public bool HasHeader => Title.Length > 0;

        /// <summary>La catégorie vient de s'ouvrir : la barre referme les autres.</summary>
        public event EventHandler? Expanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!Set(ref _isExpanded, value)) return;
                Raise(nameof(ShowsItems));
                Raise(nameof(ShowsAlert));
                Raise(nameof(ShowsCurrent));
                if (value) Expanded?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Les entrées se montrent : catégorie ouverte, ou entrées d'accueil sans titre.</summary>
        public bool ShowsItems => !HasHeader || _isExpanded;

        /// <summary>La plus grave des pastilles de la catégorie.</summary>
        public Severity? Worst
        {
            get => _worst;
            set
            {
                if (!Set(ref _worst, value)) return;
                Raise(nameof(ShowsAlert));
            }
        }

        /// <summary>Elle contient l'écran affiché.</summary>
        public bool HoldsCurrent
        {
            get => _holdsCurrent;
            set
            {
                if (!Set(ref _holdsCurrent, value)) return;
                Raise(nameof(ShowsCurrent));
            }
        }

        /// <summary>Repliée sur l'écran affiché : son titre le signale, on sait toujours où l'on est.</summary>
        public bool ShowsCurrent => HasHeader && !_isExpanded && _holdsCurrent;

        /// <summary>Repliée avec un problème dedans : la pastille passe sur le titre.</summary>
        public bool ShowsAlert => HasHeader && !_isExpanded && _worst.HasValue && _worst.Value >= Severity.Warning;

        public override string ToString() => Title;

        /// <summary>Les entrées d'accueil, sans titre de catégorie.</summary>
        public static readonly NavigationGroup None = new NavigationGroup(string.Empty, true);
    }

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
        /// Première entrée d'un groupe : un filet la sépare de la précédente.
        /// </summary>
        /// <remarks>
        /// Dix-sept entrées à plat se lisent comme une liste à parcourir. Groupées (le diagnostic,
        /// les mesures, ce qu'on fait sur la machine, ce qu'on rend au client, le logiciel), elles
        /// se lisent comme le déroulé d'une intervention. Un filet plutôt qu'un titre : la barre
        /// doit tenir entière dans la plus petite fenêtre.
        /// </remarks>
        public bool StartsGroup { get; init; }

        /// <summary>La catégorie qui la porte dans la barre latérale.</summary>
        public NavigationGroup Group { get; set; } = NavigationGroup.None;

        /// <summary>Le raccourci clavier qui ouvre l'écran, « Ctrl+3 » ; nul au-delà du neuvième.</summary>
        public string? Shortcut { get; set; }

        public string Tip => Shortcut == null ? Title : Title + "  (" + Shortcut + ")";

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
        private readonly InterventionViewModel _intervention;
        private readonly AssistantViewModel _assistant;
        private readonly RepairsViewModel _repairs;
        private readonly CleanupViewModel _cleanup;
        private readonly DiskUsageViewModel _diskUsage;
        private readonly DisksViewModel _disks;
        private readonly CrashesViewModel _crashes;
        private readonly ProgramsViewModel _programs;
        private readonly DriverStoreViewModel _driverStore;
        private readonly LanViewModel _lan;
        private readonly ToolsViewModel _tools;
        private readonly HistoryViewModel _history;
        private readonly MonitoringViewModel _monitoring;
        private readonly MeasuresViewModel _measures;
        private readonly UserDataViewModel _userData;

        /// <summary>L'écran Données, pour que le banc de captures puisse choisir ce qu'il photographie.</summary>
        public UserDataViewModel? UserData { get; private set; }
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
            OpenOverviewCommand = new RelayCommand(() => SelectedNavigation = Navigation[0]);

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
            _diskUsage = new DiskUsageViewModel(logger);
            _disks = new DisksViewModel(_repairs, logger);
            _crashes = new CrashesViewModel(_repairs, logger);
            _programs = new ProgramsViewModel(_repairs, logger);
            _driverStore = new DriverStoreViewModel(_repairs, logger);
            _lan = new LanViewModel(_repairs, logger);
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

            // Le dossier d'intervention suit ce qui se passe ailleurs : il relève de lui-même la
            // fin d'une sauvegarde, d'une restauration, d'une vérification.
            _intervention = new InterventionViewModel(
                LDI12.Reports.Interventions.InterventionStore.Default(), _actions.Journal, logger,
                () => RunAsync(RunMode.Full), () => Snapshot);
            _assistant = new AssistantViewModel(
                logger, LDI12.Assistant.ApiKeyVault.Default(), () => Snapshot, () => RunAsync(RunMode.Full), _repairs,
                async () => (await _diagnostics.GetPlatformAsync(System.Threading.CancellationToken.None).ConfigureAwait(true)).Control);
            _userData.BackupFinished += (_, e) => _intervention.OnBackupFinished(e);
            _userData.RestoreFinished += (_, e) => _intervention.OnRestoreFinished(e);
            _userData.VerificationFinished += (_, e) => _intervention.OnVerificationFinished(e);
            _userData.RestoreSourceChosen += (_, path) => _intervention.OnBackupSourceSelected(path);
            UserData = _userData;

            // Un barème modifié périme le diagnostic affiché : le score et les constats à l'écran
            // ont été calculés avec les seuils précédents.
            _settingsScreen.ProfileChanged += (_, __) =>
                StatusLine = "Barème modifié, relancez une analyse pour appliquer les nouveaux seuils.";

            Navigation = new ObservableCollection<NavigationItem>
            {
                new NavigationItem("Vue d'ensemble", "Icon.Home", _overview),
                new NavigationItem("Intervention", "Icon.Clipboard", _intervention),
                new NavigationItem("Assistant", "Icon.Sparkles", _assistant),
                NavSection("Matériel", "Icon.Cpu", DiagnosticCategory.Hardware, startsGroup: true),
                NavSection("Stockage", "Icon.HardDrive", DiagnosticCategory.Storage),
                NavSection("Windows", "Icon.Window", DiagnosticCategory.Windows),
                NavSection("Réseau", "Icon.Globe", DiagnosticCategory.Network),
                NavSection("Sécurité", "Icon.Shield", DiagnosticCategory.Security),
                NavSection("Performances", "Icon.Activity", DiagnosticCategory.Performance),
                new NavigationItem("Réparations", "Icon.Wrench", _repairs) { StartsGroup = true },
                new NavigationItem("Nettoyage", "Icon.Trash", _cleanup),
                new NavigationItem("Espace disque", "Icon.Treemap", _diskUsage),
                new NavigationItem("Données", "Icon.Archive", _userData),
                new NavigationItem("Outils", "Icon.Toolbox", _tools),
                new NavigationItem("Disques", "Icon.Disc", _disks) { StartsGroup = true },
                new NavigationItem("Plantages", "Icon.MonitorX", _crashes),
                new NavigationItem("Programmes", "Icon.Layers", _programs),
                new NavigationItem("Magasin de pilotes", "Icon.Package", _driverStore),
                new NavigationItem("Réseau local", "Icon.Network", _lan),
                new NavigationItem("Surveillance", "Icon.Pulse", _monitoring) { StartsGroup = true },
                new NavigationItem("Mesures", "Icon.Gauge", _measures),
                new NavigationItem("Rapports", "Icon.FileText", _reports) { StartsGroup = true },
                new NavigationItem("Historique", "Icon.History", _history),
                new NavigationItem("Réglages", "Icon.Sliders", _settingsScreen) { StartsGroup = true },

                // En dernier, et après les réglages : c'est l'écran qu'on ouvre par curiosité ou
                // pour retrouver une adresse, jamais au milieu d'un diagnostic.
                new NavigationItem("À propos", "Icon.Info", _about),
            };

            for (var index = 0; index < Navigation.Count && index < 9; index++)
                Navigation[index].Shortcut = "Ctrl+" + (index + 1).ToString(CultureInfo.InvariantCulture);

            // Les catégories de la barre latérale, dans l'ordre d'une intervention : constater,
            // agir, mesurer dans la durée, rendre compte. Une seule ouverte à la fois : la barre
            // tient toujours en hauteur, sans défilement, même sur un petit écran.
            var diagnostic = new NavigationGroup("Diagnostic", true);
            var act = new NavigationGroup("Agir", false);
            var deep = new NavigationGroup("Approfondir", false);
            var measure = new NavigationGroup("Mesurer", false);
            var report = new NavigationGroup("Comptes rendus", false);
            var software = new NavigationGroup("Logiciel", false);
            Assign(diagnostic, "Matériel", "Stockage", "Windows", "Réseau", "Sécurité", "Performances");
            Assign(act, "Réparations", "Nettoyage", "Espace disque", "Données", "Outils");
            Assign(deep, "Disques", "Plantages", "Programmes", "Magasin de pilotes", "Réseau local");
            Assign(measure, "Surveillance", "Mesures");
            Assign(report, "Rapports", "Historique");
            Assign(software, "Réglages", "À propos");
            var groups = new[] { diagnostic, act, deep, measure, report, software };
            foreach (var group in groups)
                group.Expanded += (sender, __) =>
                {
                    foreach (var other in groups)
                        if (!ReferenceEquals(other, sender)) other.IsExpanded = false;
                };
            NavigationView = new System.Windows.Data.ListCollectionView(Navigation);
            NavigationView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(NavigationItem.Group)));

            SelectedNavigation = Navigation[0];
            _isDarkTheme = ThemeManager.Current == AppTheme.Dark;
            ActionNavigator.Open = OpenAction;
            ActionNavigator.Solve = (title, detail) =>
            {
                Show(_assistant);
                _ = _assistant.SolveAsync(title, detail);
            };
            Search = new SearchViewModel(SearchEntries);
        }

        /// <summary>
        /// Ce qu'une fermeture de la fenêtre interromprait, en une phrase ; nul si rien ne tourne.
        /// </summary>
        /// <remarks>
        /// Fermer la fenêtre en pleine copie l'arrêtait sans un mot : une heure de sauvegarde
        /// perdue sur un clic de travers. La fenêtre demande maintenant confirmation, et dit ce
        /// qui se passera.
        /// </remarks>
        public string? BusyReason
        {
            get
            {
                if (_userData.IsBackupTransferring)
                    return "Une sauvegarde est en cours. Quitter l'arrête ; relancer la sauvegarde vers le même support " +
                           "reprendra où elle en était, sans recopier ce qui est déjà vérifié.";
                if (_userData.IsRestoreTransferring)
                    return "Une restauration est en cours. Quitter l'arrête ; la relancer ne recopiera pas ce qui est déjà restauré.";
                foreach (var item in _repairs.Items)
                    if (item.IsBusy)
                        return "La réparation « " + item.Title + " » est en cours. Quitter peut la laisser à mi-chemin.";
                if (_cleanup.IsBusy) return "Un nettoyage est en cours. Quitter l'arrête entre deux fichiers.";
                if (_assistant.IsBusy) return "L'assistant travaille : analyse ou plan en cours. Quitter l'interrompt.";
                return null;
            }
        }

        /// <summary>L'assistant, que les captures d'écran montrent sur un plan d'exemple.</summary>
        public AssistantViewModel Assistant => _assistant;

        /// <summary>L'analyse du disque, que les captures d'écran lancent d'elles-mêmes.</summary>
        public DiskUsageViewModel DiskUsage => _diskUsage;

        /// <summary>Les plantages, que les captures d'écran montrent sur un historique d'exemple.</summary>
        public CrashesViewModel Crashes => _crashes;

        /// <summary>Les programmes, que les captures d'écran montrent sur une liste d'exemple.</summary>
        public ProgramsViewModel Programs => _programs;

        /// <summary>Le magasin de pilotes, que les captures d'écran montrent sur un exemple.</summary>
        public DriverStoreViewModel DriverStore => _driverStore;

        /// <summary>Les disques, que les captures d'écran montrent sur un exemple.</summary>
        public DisksViewModel Disks => _disks;

        /// <summary>Le réseau local, que les captures d'écran montrent sur un exemple.</summary>
        public LanViewModel Lan => _lan;

        /// <summary>La recherche universelle, Ctrl+K.</summary>
        public SearchViewModel Search { get; }

        /// <summary>
        /// Ce que la recherche trouve : les écrans, les gestes de l'écran Données, chaque
        /// réparation et chaque console de Windows, avec de quoi les ouvrir.
        /// </summary>
        private IReadOnlyList<SearchEntry> SearchEntries()
        {
            var entries = new List<SearchEntry>();
            foreach (var item in Navigation)
            {
                var target = item;
                entries.Add(new SearchEntry(item.Title, "Écran", item.Shortcut ?? string.Empty, () => SelectedNavigation = target));
            }

            void Data(string title, string detail, string mode, string keywords)
                => entries.Add(new SearchEntry(title, "Données", detail, () =>
                {
                    if (_userData.CanChangeMode) _userData.ShowMode(mode);
                    Show(_userData);
                }, keywords));

            Data("Sauvegarder les données", "Vers un support externe, chaque fichier relu", "backup",
                 "copie disque externe cle usb transfert nouveau pc");
            Data("Restaurer une sauvegarde", "Sur la machine réinstallée ou le nouveau PC", "restore",
                 "restauration remettre transfert nouveau pc");
            Data("Vérifier une sauvegarde", "Relire chaque fichier contre son empreinte", "restore",
                 "verification integrite empreinte sha256 controle");
            Data("Relever ce qu'il y a à sauvegarder", "Place et durée, sans rien copier", "survey",
                 "releve volume taille");

            entries.Add(new SearchEntry("Analyser les pilotes", "Assistant", "Chaque pilote comparé au matériel",
                () => Show(_assistant), "ia pilotes drivers mise a jour version constructeur"));
            entries.Add(new SearchEntry("Dépannage guidé", "Assistant", "Le domaine, ce qu'on observe : la cause est cherchée et réglée",
                () => Show(_assistant), "ia panne probleme cause depannage"));
            entries.Add(new SearchEntry("Écrans bleus et plantages", "Plantages", "Le pilote en cause, les programmes qui plantent",
                () => Show(_crashes), "bsod ecran bleu bluescreen bluescreenview whocrashed minidump dump crash plantage pilote driver redemarrage arret brutal"));
            entries.Add(new SearchEntry("Processus en cours", "Programmes", "Ce qui tourne, qui l'a signé, terminer un programme bloqué",
                () => { _programs.IsProcessesTab = true; Show(_programs); }, "process explorer gestionnaire des taches processus tuer terminer bloque cpu memoire signature"));
            entries.Add(new SearchEntry("Programmes au démarrage", "Programmes", "Tout ce qui se lance seul : session, tâches, services, pilotes",
                () => { _programs.IsAutorunsTab = true; Show(_programs); }, "autoruns demarrage startup msconfig taches planifiees services pilotes extensions lancement automatique"));
            entries.Add(new SearchEntry("Anciens pilotes du magasin", "Magasin de pilotes", "Les versions remplacées, ce qu'elles pèsent, les retirer",
                () => Show(_driverStore), "driver store explorer rapr magasin pilotes driverstore filerepository oem inf anciennes versions nettoyer pnputil"));
            entries.Add(new SearchEntry("Santé SMART des disques", "Disques", "Chaque attribut SMART expliqué, l'usure, la température",
                () => Show(_disks), "smart gsmartcontrol crystaldiskinfo sante disque attributs secteurs realloues temperature usure ssd"));
            entries.Add(new SearchEntry("Autotest du disque", "Disques", "Le test court ou long que le disque mène lui-même",
                () => Show(_disks), "autotest self test smart court long gsmartcontrol"));
            entries.Add(new SearchEntry("Test de surface", "Disques", "Chaque bloc lu et chronométré, la carte des zones lentes",
                () => Show(_disks), "hddscan surface secteurs defectueux badblocks lent mhdd victoria lecture"));
            entries.Add(new SearchEntry("Optimiser un volume", "Disques", "TRIM d'un SSD, défragmentation d'un disque dur",
                () => Show(_disks), "defrag defragmenter jkdefrag trim optimiser ssd consolidation"));
            entries.Add(new SearchEntry("Appareils du réseau", "Réseau local", "Chaque appareil branché : nom, fabricant, services, partages",
                () => { _lan.IsDevicesTab = true; Show(_lan); }, "advanced ip scanner angry ip scan reseau appareils imprimante nas box mac fabricant partages wake on lan bureau a distance"));
            entries.Add(new SearchEntry("Connexions de ce PC", "Réseau local", "Qui parle à qui, programme par programme",
                () => { _lan.IsConnectionsTab = true; Show(_lan); }, "tcpview netstat connexions ports programme adresse distante"));
            entries.Add(new SearchEntry("Écouter le trafic", "Réseau local", "Ce qui passe sur le fil, fichier pour Wireshark",
                () => { _lan.IsTrafficTab = true; Show(_lan); }, "wireshark capture trafic paquets pcap dns bande passante lent"));
            entries.Add(new SearchEntry("Ce qui prend de la place", "Espace disque", "La carte de chaque fichier, les plus gros, les pistes",
                () => Show(_diskUsage), "windirstat treemap carte gros fichiers place disque plein volume dossier"));

            foreach (var item in _repairs.Items)
            {
                var id = item.Descriptor.Id;
                entries.Add(new SearchEntry(item.Title, "Réparation", item.Purpose, () => OpenAction(id), id));
            }

            foreach (var item in _tools.Items)
            {
                var id = item.Id;
                entries.Add(new SearchEntry(item.Title, "Outil", item.Purpose, () => OpenAction(id), item.FileName));
            }

            return entries;
        }

        /// <summary>
        /// Ouvre l'écran où se fait une opération, et l'y désigne.
        /// </summary>
        /// <remarks>
        /// Une console de Windows mène aux Outils, le nettoyage et la sauvegarde à leur écran ;
        /// tout le reste aux Réparations, où la carte est encadrée et amenée à l'écran.
        /// </remarks>
        public void OpenAction(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return;

            if (actionId.StartsWith(LDI12.Actions.ActionIds.ToolPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Show(_tools);
                _tools.Highlight(actionId);
            }
            else if (actionId == InterventionViewModel.RestoreTarget)
            {
                if (_userData.CanChangeMode) _userData.ShowMode("restore");
                Show(_userData);
            }
            else if (actionId == "REPAIRS")
            {
                Show(_repairs);
            }
            else if (string.Equals(actionId, LDI12.Actions.ActionIds.Cleanup, StringComparison.OrdinalIgnoreCase))
            {
                Show(_cleanup);
            }
            else if (string.Equals(actionId, LDI12.Actions.ActionIds.BackupUserData, StringComparison.OrdinalIgnoreCase))
            {
                // Pendant un transfert, l'écran reste sur lui : on ne quitte pas une copie par mégarde.
                if (_userData.CanChangeMode) _userData.ShowMode("backup");
                Show(_userData);
            }
            else
            {
                Show(_repairs);
                _repairs.Highlight(actionId);
            }
        }

        /// <summary>Ctrl+1 à Ctrl+9 : l'écran de ce rang dans la barre latérale.</summary>
        public void Navigate(int index)
        {
            if (index >= 0 && index < Navigation.Count) SelectedNavigation = Navigation[index];
        }

        private void Show(object content)
        {
            foreach (var item in Navigation)
                if (ReferenceEquals(item.Content, content))
                {
                    SelectedNavigation = item;
                    return;
                }
        }

        public ObservableCollection<NavigationItem> Navigation { get; }

        /// <summary>La barre latérale : les mêmes entrées, rangées par catégorie.</summary>
        public System.Windows.Data.ListCollectionView NavigationView { get; private set; } = null!;

        private void Assign(NavigationGroup group, params string[] titles)
        {
            foreach (var item in Navigation)
                if (Array.IndexOf(titles, item.Title) >= 0) item.Group = group;
        }

        /// <summary>Le bandeau de mise à jour, et ce qu'il déclenche.</summary>
        public UpdateViewModel Updates { get; }

        /// <summary>Écran d'analyse plein cadre, actif pendant une collecte.</summary>
        public ScanViewModel Scan { get; }

        public ICommand RunFullScanCommand { get; }
        public ICommand RunQuickScanCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ToggleThemeCommand { get; }

        /// <summary>La pastille d'état de la barre supérieure ramène à la vue d'ensemble.</summary>
        public ICommand OpenOverviewCommand { get; }

        public SystemSnapshot? Snapshot { get; private set; }

        /// <summary>Diagnostic terminé, sert notamment au mode capture d'écran.</summary>
        public event EventHandler? ScanCompleted;

        public NavigationItem? SelectedNavigation
        {
            get => _selectedNavigation;
            set
            {
                var previous = _selectedNavigation;
                if (!Set(ref _selectedNavigation, value)) return;
                CurrentContent = value?.Content;
                // Les écrans qui lisent la machine à la demande ne travaillent que regardés.
                (previous?.Content as IScreenActivation)?.Deactivated();
                (value?.Content as IScreenActivation)?.Activated();
                if (previous != null) previous.Group.HoldsCurrent = false;
                if (value != null) value.Group.HoldsCurrent = true;
                // Un écran ouvert par raccourci, recherche ou lien : sa catégorie s'ouvre, l'entrée active reste visible.
                if (value != null) value.Group.IsExpanded = true;
            }
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
            _disks.Update(snapshot);
            _userData.Update(snapshot);
            _intervention.OnScanCompleted(snapshot);

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

            foreach (var group in Navigation.Select(n => n.Group).Distinct())
                group.Worst = Navigation.Where(n => n.Group == group && n.Worst.HasValue).Select(n => n.Worst).Max();

            HealthScore = snapshot.Score?.Global;
            HealthBand = snapshot.Score?.Band;
            HealthText = snapshot.Score == null ? string.Empty : snapshot.Score.Global + " · " + DiagnosticScore.BandLabel(snapshot.Score.Band);
        }

        /// <summary>L'état général, rappelé dans la barre supérieure quel que soit l'écran.</summary>
        public int? HealthScore { get => _healthScore; private set { if (Set(ref _healthScore, value)) Raise(nameof(HasHealth)); } }

        public ScoreBand? HealthBand { get => _healthBand; private set => Set(ref _healthBand, value); }

        public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }

        public bool HasHealth => _healthScore.HasValue;

        private int? _healthScore;
        private ScoreBand? _healthBand;
        private string _healthText = string.Empty;

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

        private NavigationItem NavSection(string title, string iconKey, DiagnosticCategory category, bool startsGroup = false)
        {
            var section = new SectionViewModel(title, iconKey, category);
            _sections.Add(section);
            return new NavigationItem(title, iconKey, section) { StartsGroup = startsGroup };
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
