using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Actions.Repairs;
using LDI12.Actions.Journal;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    /// <summary>Un dossier pesé, tel qu'il s'affiche.</summary>
    public sealed class DataFolderItem
    {
        public string Label { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        /// <summary>Ce qu'une copie emporterait réellement.</summary>
        public string OnDisk { get; init; } = string.Empty;

        public string Files { get; init; } = string.Empty;

        /// <summary>Renseigné quand une partie du dossier n'est pas sur ce disque.</summary>
        public string? CloudNote { get; init; }

        public bool HasCloudNote => CloudNote != null;
    }

    /// <summary>
    /// Écran des données à sauvegarder.
    /// </summary>
    /// <remarks>
    /// <b>Le pendant en lecture de l'écran de nettoyage.</b> L'un montre ce qui peut disparaître,
    /// l'autre ce qu'il ne faut pas perdre, et la question qu'il tranche est celle qu'un atelier
    /// pose avant chaque réinstallation : combien de temps, combien de place, et qu'est-ce qu'on
    /// oublie ?
    /// <para>
    /// Il compte des octets et des fichiers. Il n'affiche aucun nom de fichier, n'en retient
    /// aucun, et n'en écrit aucun dans le journal : ce sont les données du client, et les regarder
    /// ne fait pas partie du travail.
    /// </para>
    /// </remarks>
    public sealed class UserDataViewModel : ObservableObject
    {
        private const string Category = "Données";

        private readonly DiagnosticService _diagnostics;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;
        private readonly Func<Task<ActionRunner?>>? _runner;

        private IRepairAction? _backup;
        private ActionPreview? _backupPreview;

        private UserDataSurveyor? _surveyor;
        private CancellationTokenSource? _running;
        private SystemSnapshot? _snapshot;

        private bool _isRunning;
        private bool _hasResults;
        private string _status = "Aucun relevé n'a encore été fait.";
        private string _planSummary = string.Empty;
        private string _headline = string.Empty;
        private string _progress = string.Empty;
        private string _backupDestination = string.Empty;
        private string _backupSummary = string.Empty;
        private bool _isCopying;
        private bool _includePersonal = true;
        private bool _includeApplications = true;
        private bool _exportWifi;
        private string? _lastBackupFolder;

        public UserDataViewModel(
            DiagnosticService diagnostics, InterventionJournal journal, ILdiLogger logger,
            Func<Task<ActionRunner?>>? runner = null)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runner = runner;

            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            PrepareBackupCommand = new AsyncRelayCommand(PrepareBackupAsync, () => CanPrepareBackup);
            RunBackupCommand = new AsyncRelayCommand(CopyAsync, () => CanCopy);
            OpenBackupCommand = new RelayCommand(OpenBackup, () => _lastBackupFolder != null && !IsCopying);
            PrepareRestoreCommand = new AsyncRelayCommand(PrepareRestoreAsync, () => CanPrepareRestore);
            RunRestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore);
            AllDriversCommand = new RelayCommand(() => CheckAll(DriverChoices, true));
            NoDriversCommand = new RelayCommand(() => CheckAll(DriverChoices, false));
            ListApplicationsCommand = new AsyncRelayCommand(ListApplicationsAsync, () => !IsCopying && !IsListingApplications);
            AllApplicationsCommand = new RelayCommand(() => CheckAll(ApplicationChoices, true));
            NoApplicationsCommand = new RelayCommand(() => CheckAll(ApplicationChoices, false));

            StartDriveWatch();
        }

        // ---------- supports branchés

        private DispatcherTimer? _drivesTimer;
        private bool _refreshingDrives;
        private string _drivesSignature = string.Empty;

        /// <summary>Les volumes où déposer une sauvegarde, en tuiles.</summary>
        public ObservableCollection<DriveTile> BackupDrives { get; } = new ObservableCollection<DriveTile>();

        /// <summary>Les volumes qui portent au moins une sauvegarde LDI12.</summary>
        public ObservableCollection<DriveTile> RestoreDrives { get; } = new ObservableCollection<DriveTile>();

        public bool HasBackupDrives => BackupDrives.Count > 0;
        public bool HasNoBackupDrive => BackupDrives.Count == 0;
        public bool HasRestoreDrives => RestoreDrives.Count > 0;
        public bool HasNoRestoreDrive => RestoreDrives.Count == 0;

        /// <summary>
        /// Relit les volumes toutes les trois secondes.
        /// </summary>
        /// <remarks>
        /// <b>Brancher le disque doit suffire.</b> Le technicien branche son disque après avoir
        /// ouvert l'écran, presque toujours : il le voit apparaître sans rien toucher. La lecture
        /// se fait hors du fil d'interface, parce qu'un lecteur réseau déconnecté peut mettre
        /// plusieurs secondes à répondre, et elle n'est pas relancée tant que la précédente dure.
        /// </remarks>
        private void StartDriveWatch()
        {
            if (Dispatcher.FromThread(System.Threading.Thread.CurrentThread) == null) return;

            _drivesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _drivesTimer.Tick += async (_, _) => await RefreshDrivesAsync().ConfigureAwait(true);
            _drivesTimer.Start();
            _ = RefreshDrivesAsync();
        }

        private async Task RefreshDrivesAsync()
        {
            if (_refreshingDrives || IsBackupTransferring || IsRestoreTransferring) return;
            _refreshingDrives = true;

            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                var drives = await Task.Run(() => DestinationDrives.Detect(services.Files)).ConfigureAwait(true);

                // Rien ne bouge à l'écran tant que rien n'a changé : reconstruire les tuiles toutes
                // les trois secondes ferait clignoter la sélection et le survol.
                var signature = new System.Text.StringBuilder();
                foreach (var drive in drives)
                    signature.Append(drive.Root).Append('|').Append(drive.Label).Append('|')
                        .Append(drive.FreeBytes / (64L * 1024 * 1024)).Append('|').Append(drive.Backups).Append(';');

                if (signature.ToString() == _drivesSignature) return;
                _drivesSignature = signature.ToString();

                BackupDrives.Clear();
                RestoreDrives.Clear();
                foreach (var drive in drives)
                {
                    BackupDrives.Add(new DriveTile(drive, tile => BackupDestination = tile.Root));
                    if (drive.Backups > 0) RestoreDrives.Add(new DriveTile(drive, tile => RestoreSource = tile.Root));
                }

                MarkSelection();
                Raise(nameof(HasBackupDrives));
                Raise(nameof(HasNoBackupDrive));
                Raise(nameof(HasRestoreDrives));
                Raise(nameof(HasNoRestoreDrive));
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Les supports branchés n'ont pas pu être relus.", ex);
            }
            finally
            {
                _refreshingDrives = false;
            }
        }

        private void MarkSelection()
        {
            foreach (var tile in BackupDrives) tile.IsSelected = SameRoot(tile.Root, BackupDestination);
            foreach (var tile in RestoreDrives) tile.IsSelected = SameRoot(tile.Root, RestoreSource);
        }

        private static bool SameRoot(string root, string path)
            => string.Equals(root.TrimEnd('\\'), (path ?? string.Empty).Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------- pilotes et applications

        private bool _includeDrivers = true;
        private bool _includeApplicationList = true;
        private bool _isListingApplications;
        private string _driversNote = "Les pilotes se listent à partir d'une analyse : lancez-en une depuis l'accueil.";
        private string _applicationsNote = "Cliquez sur « Rechercher les applications » : winget dit lesquelles il sait réinstaller.";

        /// <summary>Les pilotes tiers de la machine, un par paquet. Tous cochés au départ.</summary>
        public ObservableCollection<ChoiceItem> DriverChoices { get; } = new ObservableCollection<ChoiceItem>();

        /// <summary>Les applications que winget sait réinstaller. Toutes cochées au départ.</summary>
        public ObservableCollection<ChoiceItem> ApplicationChoices { get; } = new ObservableCollection<ChoiceItem>();

        public bool HasDriverChoices => DriverChoices.Count > 0;
        public bool HasApplicationChoices => ApplicationChoices.Count > 0;

        public string DriversNote { get => _driversNote; private set => Set(ref _driversNote, value); }
        public string ApplicationsNote { get => _applicationsNote; private set => Set(ref _applicationsNote, value); }

        public bool IncludeDrivers
        {
            get => _includeDrivers;
            set { if (Set(ref _includeDrivers, value)) Invalidate(); }
        }

        public bool IncludeApplicationList
        {
            get => _includeApplicationList;
            set { if (Set(ref _includeApplicationList, value)) Invalidate(); }
        }

        public bool IsListingApplications
        {
            get => _isListingApplications;
            private set
            {
                if (Set(ref _isListingApplications, value))
                    (ListApplicationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand AllDriversCommand { get; }
        public ICommand NoDriversCommand { get; }
        public ICommand ListApplicationsCommand { get; }
        public ICommand AllApplicationsCommand { get; }
        public ICommand NoApplicationsCommand { get; }

        private void CheckAll(IEnumerable<ChoiceItem> items, bool value)
        {
            foreach (var item in items) item.SetSilently(value);
            Invalidate();
        }

        private List<DriverChoice> CheckedDrivers()
        {
            var result = new List<DriverChoice>();
            if (!IncludeDrivers) return result;
            foreach (var item in DriverChoices)
                if (item.IsChecked && item.Driver != null) result.Add(item.Driver);
            return result;
        }

        private List<string> CheckedApplications()
        {
            var result = new List<string>();
            if (!IncludeApplicationList) return result;
            foreach (var item in ApplicationChoices)
                if (item.IsChecked) result.Add(item.Key);
            return result;
        }

        private void LoadDrivers(SystemSnapshot? snapshot)
        {
            DriverChoices.Clear();

            foreach (var driver in DriverBackup.Choices(snapshot))
            {
                var detail = new List<string>();
                if (!string.IsNullOrEmpty(driver.DeviceClass)) detail.Add(driver.DeviceClass!);
                if (!string.IsNullOrEmpty(driver.Manufacturer)) detail.Add(driver.Manufacturer!);
                if (!string.IsNullOrEmpty(driver.Version)) detail.Add("version " + driver.Version);
                detail.Add(driver.InfName);

                DriverChoices.Add(new ChoiceItem(driver.InfName, driver.Label, string.Join(" · ", detail), true, Invalidate)
                {
                    Driver = driver,
                });
            }

            DriversNote = snapshot == null
                ? "Les pilotes se listent à partir d'une analyse : lancez-en une depuis l'accueil."
                : DriverChoices.Count == 0
                    ? "L'analyse n'a relevé aucun pilote tiers : tout ce que cette machine utilise revient avec Windows."
                    : DriverChoices.Count + " pilote(s) tiers relevés par l'analyse. Ceux fournis avec Windows reviennent avec lui " +
                      "et ne sont pas listés.";

            Raise(nameof(HasDriverChoices));
            Invalidate();
        }

        private async Task ListApplicationsAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            IsListingApplications = true;
            ApplicationsNote = "winget relève les applications installées. Compter une trentaine de secondes…";
            var export = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ldi12-winget-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                var listing = await Task.Run(() => WingetApplications.ListAsync(
                    runner.Context.Processes, runner.Context.Files, export, CancellationToken.None)).ConfigureAwait(true);

                ApplicationChoices.Clear();
                foreach (var id in listing.Packages)
                    ApplicationChoices.Add(new ChoiceItem(id, id, null, true, Invalidate));

                ApplicationsNote = listing.Failure ??
                    listing.Packages.Count + " application(s) que winget sait réinstaller, depuis leur éditeur et dans leur " +
                    "dernière version. Les autres restent dans la fiche de réinstallation, à remettre à la main.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La liste des applications n'a pas pu être établie.", ex);
                ApplicationsNote = "La liste des applications n'a pas pu être établie : " + ex.Message;
            }
            finally
            {
                try { if (System.IO.File.Exists(export)) System.IO.File.Delete(export); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }

                IsListingApplications = false;
                Raise(nameof(HasApplicationChoices));
                Invalidate();
            }
        }

        // ---------- avancement d'un transfert

        private bool _isBackupTransferring;
        private bool _isRestoreTransferring;
        private string _transferTitle = string.Empty;
        private string _transferDetail = string.Empty;
        private double _transferPercent;
        private string _transferRemaining = string.Empty;
        private string _transferElapsed = string.Empty;

        /// <summary>
        /// Une copie est en cours : l'écran ne montre plus que son avancement.
        /// </summary>
        /// <remarks>
        /// Pendant une heure de copie, le relevé, les réserves et les lignes de préparation ne
        /// servent plus à rien, et ils repoussent la seule information qui compte hors de l'écran.
        /// Ils reviennent à la fin, avec le compte rendu.
        /// </remarks>
        public bool IsBackupTransferring
        {
            get => _isBackupTransferring;
            private set { if (Set(ref _isBackupTransferring, value)) RaiseTransferStates(); }
        }

        public bool IsRestoreTransferring
        {
            get => _isRestoreTransferring;
            private set { if (Set(ref _isRestoreTransferring, value)) RaiseTransferStates(); }
        }

        public bool ShowScreen => !IsBackupTransferring && !IsRestoreTransferring;
        public bool ShowBackupSection => !IsRestoreTransferring;
        public bool ShowRestoreSection => !IsBackupTransferring;
        public bool ShowBackupForm => !IsBackupTransferring;
        public bool ShowRestoreForm => !IsRestoreTransferring;

        public string TransferTitle { get => _transferTitle; private set => Set(ref _transferTitle, value); }
        public string TransferDetail { get => _transferDetail; private set => Set(ref _transferDetail, value); }
        public double TransferPercent { get => _transferPercent; private set { if (Set(ref _transferPercent, value)) Raise(nameof(TransferPercentText)); } }
        public string TransferPercentText => ((int)Math.Floor(TransferPercent)).ToString(System.Globalization.CultureInfo.CurrentCulture) + " %";
        public string TransferRemaining { get => _transferRemaining; private set => Set(ref _transferRemaining, value); }
        public string TransferElapsed { get => _transferElapsed; private set => Set(ref _transferElapsed, value); }

        private void RaiseTransferStates()
        {
            Raise(nameof(ShowScreen));
            Raise(nameof(ShowBackupSection));
            Raise(nameof(ShowRestoreSection));
            Raise(nameof(ShowBackupForm));
            Raise(nameof(ShowRestoreForm));
        }

        private void BeginTransfer(string title)
        {
            TransferTitle = title;
            TransferDetail = string.Empty;
            TransferPercent = 0;
            TransferRemaining = "Estimation du temps restant en cours…";
            TransferElapsed = string.Empty;
        }

        private void OnTransfer(ActionProgress report)
        {
            TransferTitle = report.Text;
            if (report.Detail != null) TransferDetail = report.Detail;
            if (report.Fraction.HasValue) TransferPercent = Math.Max(0, Math.Min(100, report.Fraction.Value * 100));
            if (report.Elapsed.HasValue) TransferElapsed = "Écoulé : " + Duration(report.Elapsed.Value);

            // Seulement quand le rapport vient de la copie elle-même : les étapes de fin (export
            // Wi-Fi, fiche) ne portent pas d'estimation, et effacer la dernière laisserait un vide.
            if (report.Detail != null) TransferRemaining = Remaining(report.Remaining);
        }

        /// <summary>« environ 12 min restantes » : arrondi comme on le dirait à voix haute.</summary>
        internal static string Remaining(TimeSpan? remaining)
        {
            if (remaining == null) return "Estimation du temps restant en cours…";

            var value = remaining.Value;
            if (value < TimeSpan.FromMinutes(1)) return "Temps restant estimé : moins d'une minute";
            if (value < TimeSpan.FromHours(1))
                return "Temps restant estimé : environ " + (int)Math.Ceiling(value.TotalMinutes) + " min";

            var minutes = (int)Math.Ceiling(value.TotalMinutes);
            return "Temps restant estimé : environ " + minutes / 60 + " h " + (minutes % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Duration(TimeSpan value)
            => value < TimeSpan.FromMinutes(1)
                ? (int)value.TotalSeconds + " s"
                : value < TimeSpan.FromHours(1)
                    ? (int)value.TotalMinutes + " min " + value.Seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " s"
                    : (int)value.TotalHours + " h " + value.Minutes.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

        // ---------- restauration d'une sauvegarde

        private IRepairAction? _restore;
        private ActionPreview? _restorePreview;
        private string _restoreSource = string.Empty;
        private string _restoreSummary = string.Empty;
        private bool _restoreWifi = true;
        private bool _restoreDriversChecked = true;
        private bool _restoreApplicationsChecked = true;
        private IRepairAction? _restoreDrivers;
        private IRepairAction? _restoreApplications;
        private ActionPreview? _restoreDriversPreview;
        private ActionPreview? _restoreApplicationsPreview;

        /// <summary>Réinstaller les pilotes que la sauvegarde contient. Demande une invite Windows.</summary>
        public bool RestoreDriversChecked
        {
            get => _restoreDriversChecked;
            set { if (Set(ref _restoreDriversChecked, value)) InvalidateRestore(); }
        }

        /// <summary>Réinstaller par winget les applications que la sauvegarde liste. Demande Internet.</summary>
        public bool RestoreApplicationsChecked
        {
            get => _restoreApplicationsChecked;
            set { if (Set(ref _restoreApplicationsChecked, value)) InvalidateRestore(); }
        }

        /// <summary>Lignes du relevé de restauration, puis du compte rendu.</summary>
        public ObservableCollection<string> RestoreLines { get; } = new ObservableCollection<string>();

        /// <summary>Le disque ou le dossier de la sauvegarde. La plus récente qu'il contient est retenue.</summary>
        public string RestoreSource
        {
            get => _restoreSource;
            set
            {
                if (!Set(ref _restoreSource, value)) return;
                InvalidateRestore();
                MarkSelection();
            }
        }

        /// <summary>Réimporter les profils Wi-Fi que la sauvegarde contient. Coché par défaut : ils ont été exportés exprès.</summary>
        public bool RestoreWifi
        {
            get => _restoreWifi;
            set { if (Set(ref _restoreWifi, value)) InvalidateRestore(); }
        }

        public string RestoreSummary
        {
            get => _restoreSummary;
            private set { if (Set(ref _restoreSummary, value)) Raise(nameof(HasRestorePreview)); }
        }

        public bool HasRestorePreview => RestoreLines.Count > 0 || RestoreSummary.Length > 0;

        public bool CanPrepareRestore => !IsRunning && !IsCopying && !string.IsNullOrWhiteSpace(RestoreSource);

        public bool CanRestore
            => !IsCopying && (Ready(_restorePreview) || Ready(_restoreDriversPreview) || Ready(_restoreApplicationsPreview));

        private static bool Ready(ActionPreview? preview) => preview != null && preview.CanExecute;

        public ICommand PrepareRestoreCommand { get; }
        public ICommand RunRestoreCommand { get; }

        private void InvalidateRestore()
        {
            _restorePreview = null;
            _restoreDriversPreview = null;
            _restoreApplicationsPreview = null;
            RestoreLines.Clear();
            RestoreSummary = string.Empty;
            Raise(nameof(HasRestorePreview));
            RaiseBackupStates();
        }

        private IReadOnlyDictionary<string, string> RestoreParameters()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RestoreUserDataAction.SourceParameter] = RestoreSource.Trim(),
                [RestoreUserDataAction.WifiParameter] = RestoreWifi ? "1" : "0",
            };

        private async Task PrepareRestoreAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            _restore ??= ActionCatalog.Find(ActionIds.RestoreUserData, _logger);
            if (_restore == null) return;

            IsCopying = true;
            RestoreLines.Clear();
            _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = null;
            Status = "Relevé de la sauvegarde en cours. Rien n'est encore écrit sur cette machine.";

            try
            {
                _restorePreview = await runner
                    .PreviewAsync(_restore, RestoreParameters(), CancellationToken.None)
                    .ConfigureAwait(true);

                RestoreSummary = _restorePreview.Summary;
                Show(RestoreLines, _restorePreview);

                // Les pilotes et les applications ne sont préparés que si la sauvegarde en contient :
                // demander une invite Windows pour découvrir qu'il n'y a rien à installer serait
                // une invite de trop.
                var backup = RestoreCatalog.Find(runner.Context.Files, RestoreSource.Trim(), out _);
                var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RestoreDriversAction.SourceParameter] = RestoreSource.Trim(),
                };

                if (RestoreDriversChecked && backup != null &&
                    runner.Context.Files.DirectoryExists(System.IO.Path.Combine(backup, DriverBackup.Folder)))
                {
                    _restoreDrivers ??= ActionCatalog.Find(ActionIds.RestoreDrivers, _logger);
                    if (_restoreDrivers != null)
                    {
                        _restoreDriversPreview = await runner.PreviewAsync(_restoreDrivers, source, CancellationToken.None)
                            .ConfigureAwait(true);
                        RestoreLines.Add("Pilotes. " + _restoreDriversPreview.Summary);
                        Show(RestoreLines, _restoreDriversPreview);
                    }
                }

                if (RestoreApplicationsChecked && backup != null &&
                    runner.Context.Files.FileExists(System.IO.Path.Combine(backup, WingetApplications.FileName)))
                {
                    _restoreApplications ??= ActionCatalog.Find(ActionIds.RestoreApplications, _logger);
                    if (_restoreApplications != null)
                    {
                        _restoreApplicationsPreview = await runner.PreviewAsync(_restoreApplications, source, CancellationToken.None)
                            .ConfigureAwait(true);
                        RestoreLines.Add("Applications. " + _restoreApplicationsPreview.Summary);
                        Show(RestoreLines, _restoreApplicationsPreview);
                    }
                }

                Status = CanRestore
                    ? "Relevé de la sauvegarde terminé. Rien n'a encore été restauré."
                    : _restorePreview.Blocker ?? _restorePreview.Summary;
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la restauration a échoué.", ex);
                _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = null;
                Status = "Le relevé de la restauration a échoué : " + ex.Message;
            }
            finally
            {
                IsCopying = false;
                Raise(nameof(HasRestorePreview));
                RaiseBackupStates();
            }
        }

        /// <summary>Les lignes d'une prévisualisation, mises en garde d'abord.</summary>
        private static void Show(ICollection<string> lines, ActionPreview preview)
        {
            foreach (var line in preview.Measurements)
                if (line.Kind == PreviewLineKind.Caution) lines.Add("Attention. " + line.Label + " : " + line.Value);
            foreach (var line in preview.WillDo) lines.Add(line);
            foreach (var line in preview.WillNotDo) lines.Add(line);
            if (preview.Outcome == PreviewOutcome.Blocked && preview.Blocker != null) lines.Add("Impossible : " + preview.Blocker);
        }

        /// <summary>
        /// Pilotes, puis données, puis applications.
        /// </summary>
        /// <remarks>
        /// <b>L'ordre n'est pas indifférent.</b> Les pilotes d'abord : l'hôte élevé qui les a
        /// préparés se ferme après un quart d'heure d'inactivité, et une restauration de données
        /// dure bien plus. Ils ramènent aussi la carte Wi-Fi, sans laquelle les profils Wi-Fi des
        /// données ne se réimportent pas. Les applications en dernier : elles se téléchargent, et
        /// le réseau est alors revenu.
        /// </remarks>
        private async Task RestoreAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null || !CanRestore) return;

            IsCopying = true;
            Status = "Restauration en cours…";
            IsRestoreTransferring = true;
            var progress = new Progress<ActionProgress>(OnTransfer);
            var report = new List<string>();
            var summaries = new List<string>();

            try
            {
                if (Ready(_restoreDriversPreview) && _restoreDrivers != null)
                {
                    BeginTransfer("Réinstallation des pilotes…");
                    var drivers = await runner.ExecuteAsync(_restoreDrivers, _restoreDriversPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                    summaries.Add("Pilotes : " + drivers.Summary);
                    Report(report, "Pilotes", drivers);
                }

                if (Ready(_restorePreview) && _restore != null)
                {
                    BeginTransfer("Préparation de la restauration…");
                    var data = await runner.ExecuteAsync(_restore, _restorePreview!, RestoreParameters(), progress, CancellationToken.None)
                        .ConfigureAwait(true);
                    summaries.Add(data.Summary);
                    Report(report, "Données", data);
                }

                if (Ready(_restoreApplicationsPreview) && _restoreApplications != null)
                {
                    BeginTransfer("Réinstallation des applications…");
                    var applications = await runner.ExecuteAsync(
                            _restoreApplications, _restoreApplicationsPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                    summaries.Add("Applications : " + applications.Summary);
                    Report(report, "Applications", applications);
                }

                Status = string.Join(" ", summaries);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La restauration a échoué.", ex);
                Status = "La restauration a échoué : " + ex.Message;
            }
            finally
            {
                _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = null;
                RestoreLines.Clear();
                RestoreSummary = report.Count > 0 ? "Compte rendu de la restauration :" : string.Empty;
                foreach (var line in report) RestoreLines.Add(line);

                IsCopying = false;
                IsRestoreTransferring = false;
                Raise(nameof(HasRestorePreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private static void Report(ICollection<string> lines, string part, ActionOutcome outcome)
        {
            lines.Add(part + " : " + outcome.Summary);
            foreach (var line in outcome.Details) lines.Add(line);
        }

        public ObservableCollection<DataFolderItem> Folders { get; } = new ObservableCollection<DataFolderItem>();

        public ObservableCollection<string> Caveats { get; } = new ObservableCollection<string>();

        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }

        public bool IsRunning
        {
            get => _isRunning;
            private set { if (Set(ref _isRunning, value)) RaiseCommandStates(); }
        }

        public bool HasResults { get => _hasResults; private set => Set(ref _hasResults, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string Headline { get => _headline; private set => Set(ref _headline, value); }
        public string Progress { get => _progress; private set => Set(ref _progress, value); }

        /// <summary>Ce qui va être parcouru, avant de l'être.</summary>
        public string PlanSummary { get => _planSummary; private set => Set(ref _planSummary, value); }

        public bool HasCaveats => Caveats.Count > 0;

        // ---------- copie vers un support externe

        /// <summary>Lignes du relevé de copie : ce qui part, et ce qui ne bouge pas.</summary>
        public ObservableCollection<string> BackupLines { get; } = new ObservableCollection<string>();

        /// <summary>
        /// Dossier de destination, saisi par le technicien.
        /// </summary>
        /// <remarks>
        /// Un champ de texte comme sur l'écran des rapports, et non un sélecteur de dossier :
        /// WPF n'en fournit pas, et en apporter un demanderait soit une dépendance à Windows
        /// Forms, soit une centaine de lignes d'interopérabilité COM. La prévisualisation
        /// vérifie de toute façon que le dossier existe et qu'il a la place.
        /// </remarks>
        public string BackupDestination
        {
            get => _backupDestination;
            set
            {
                // Le relevé porte sur une destination précise : changer de support l'invalide.
                if (!Set(ref _backupDestination, value)) return;
                Invalidate();
                MarkSelection();
            }
        }

        /// <summary>Copier les dossiers personnels. Coché par défaut.</summary>
        public bool IncludePersonal
        {
            get => _includePersonal;
            set { if (Set(ref _includePersonal, value)) Invalidate(); }
        }

        /// <summary>Copier aussi les données des applications que le catalogue connaît. Coché par défaut.</summary>
        public bool IncludeApplications
        {
            get => _includeApplications;
            set { if (Set(ref _includeApplications, value)) Invalidate(); }
        }

        /// <summary>
        /// Exporter les profils Wi-Fi avec leurs clés.
        /// </summary>
        /// <remarks>
        /// Décoché par défaut, et jamais retenu d'une session à l'autre : les clés sortent en
        /// clair sur le support, et ce choix se refait en connaissance de cause à chaque client.
        /// </remarks>
        public bool ExportWifi
        {
            get => _exportWifi;
            set { if (Set(ref _exportWifi, value)) Invalidate(); }
        }

        public ICommand OpenBackupCommand { get; }

        private void Invalidate()
        {
            _backupPreview = null;
            _driversPreview = null;
            BackupLines.Clear();
            BackupSummary = string.Empty;
            Raise(nameof(HasBackupPreview));
            RaiseBackupStates();
        }

        public string BackupSummary
        {
            get => _backupSummary;
            private set { if (Set(ref _backupSummary, value)) Raise(nameof(HasBackupPreview)); }
        }

        public bool HasBackupPreview => BackupLines.Count > 0;

        public bool IsCopying
        {
            get => _isCopying;
            private set { if (Set(ref _isCopying, value)) RaiseBackupStates(); }
        }

        public bool CanPrepareBackup => !IsRunning && !IsCopying && !string.IsNullOrWhiteSpace(BackupDestination);

        /// <summary>
        /// La copie n'est possible qu'après un relevé.
        /// </summary>
        /// <remarks>
        /// Porté par le typage de l'action, pas seulement par l'écran : sans prévisualisation,
        /// l'exécution n'a rien à copier et le dit. Le bouton grisé ne fait que rendre visible
        /// une règle qui tient déjà toute seule.
        /// </remarks>
        public bool CanCopy => !IsCopying && _backupPreview != null && _backupPreview.CanExecute;

        public ICommand PrepareBackupCommand { get; }
        public ICommand RunBackupCommand { get; }

        private IRepairAction? _exportDrivers;
        private ActionPreview? _driversPreview;

        private async Task PrepareBackupAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            _backup ??= ActionCatalog.Find(ActionIds.BackupUserData, _logger);
            if (_backup == null) return;

            IsCopying = true;
            BackupLines.Clear();
            _backupPreview = _driversPreview = null;
            Status = "Relevé de la copie en cours. Aucun fichier n'est encore écrit.";

            try
            {
                _backupPreview = await runner
                    .PreviewAsync(_backup, BackupParameters(), CancellationToken.None)
                    .ConfigureAwait(true);

                BackupSummary = _backupPreview.Summary;

                // Les mises en garde d'abord : un navigateur ouvert se ferme avant de lancer la
                // copie, pas après avoir lu vingt lignes.
                Show(BackupLines, _backupPreview);

                // Les pilotes vont dans le dossier que la copie vient d'établir : sans lui, pas de pilotes.
                var drivers = CheckedDrivers();
                if (drivers.Count > 0 && _backupPreview.CanExecute && _backupPreview.Plan is BackupPlan plan)
                {
                    _exportDrivers ??= ActionCatalog.Find(ActionIds.ExportDrivers, _logger);
                    if (_exportDrivers != null)
                    {
                        _driversPreview = await runner.PreviewAsync(_exportDrivers,
                                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    [ExportDriversAction.DestinationParameter] = plan.Destination,
                                    [ExportDriversAction.DriversParameter] = DriverBackup.Encode(drivers),
                                },
                                CancellationToken.None)
                            .ConfigureAwait(true);

                        BackupLines.Add("Pilotes. " + _driversPreview.Summary);
                        Show(BackupLines, _driversPreview);
                    }
                }

                Status = _backupPreview.Outcome == PreviewOutcome.Blocked
                    ? _backupPreview.Blocker ?? "La copie ne peut pas être préparée."
                    : _driversPreview != null && !_driversPreview.CanExecute
                        ? "Relevé terminé, mais les pilotes ne seront pas exportés : " +
                          (_driversPreview.Blocker ?? _driversPreview.Summary)
                        : "Relevé terminé. Rien n'a encore été copié.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la copie a échoué.", ex);
                _backupPreview = _driversPreview = null;
                Status = "Le relevé de la copie a échoué : " + ex.Message;
            }
            finally
            {
                IsCopying = false;
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
            }
        }

        /// <summary>
        /// Les pilotes d'abord, puis la copie.
        /// </summary>
        /// <remarks>
        /// L'export des pilotes a été préparé par l'hôte élevé, qui se ferme après un quart d'heure
        /// sans requête : après une heure de copie, il n'aurait plus rien à exécuter. L'export ne
        /// prend que quelques minutes, il passe donc devant.
        /// </remarks>
        private async Task CopyAsync()
        {
            var preview = _backupPreview;
            var action = _backup;
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (preview == null || action == null || runner == null) return;

            IsCopying = true;
            Status = "Copie en cours…";
            IsBackupTransferring = true;

            var progress = new Progress<ActionProgress>(OnTransfer);
            ActionOutcome? outcome = null;
            ActionOutcome? drivers = null;

            try
            {
                if (Ready(_driversPreview) && _exportDrivers != null)
                {
                    BeginTransfer("Export des pilotes…");
                    drivers = await runner.ExecuteAsync(_exportDrivers, _driversPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                }

                BeginTransfer("Préparation de la copie…");
                outcome = await runner
                    .ExecuteAsync(action, preview, BackupParameters(), progress, CancellationToken.None)
                    .ConfigureAwait(true);

                Status = outcome.Summary + (drivers == null ? string.Empty : " Pilotes : " + drivers.Summary);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La copie a échoué.", ex);
                Status = "La copie a échoué : " + ex.Message;
            }
            finally
            {
                IsCopying = false;
                IsBackupTransferring = false;

                // Le relevé est consommé : le rejouer porterait sur un état qui a changé. Ce qui
                // reste à l'écran est le compte rendu, dossier par dossier.
                _backupPreview = _driversPreview = null;
                BackupLines.Clear();
                BackupSummary = string.Empty;

                if (outcome != null && preview.Plan is BackupPlan plan)
                {
                    _lastBackupFolder = plan.Destination;
                    BackupSummary = "Compte rendu de la sauvegarde, déposée dans " + plan.Destination + " :";
                    foreach (var line in outcome.Details) BackupLines.Add(line);
                    if (drivers != null) Report(BackupLines, "Pilotes", drivers);
                }

                (OpenBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private IReadOnlyDictionary<string, string> BackupParameters()
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BackupUserDataAction.DestinationParameter] = BackupDestination.Trim(),
                [BackupUserDataAction.PersonalParameter] = IncludePersonal ? "1" : "0",
                [BackupUserDataAction.ApplicationsParameter] = IncludeApplications ? "1" : "0",
                [BackupUserDataAction.WifiParameter] = ExportWifi ? "1" : "0",
                [BackupUserDataAction.DriversParameter] = CheckedDrivers().Count > 0 ? "1" : "0",
            };

            var applications = CheckedApplications();
            if (applications.Count > 0)
                parameters[BackupUserDataAction.WingetParameter] = WingetApplications.Encode(applications);

            return parameters;
        }

        /// <summary>Ouvre la sauvegarde dans l'explorateur : la fiche de réinstallation est à sa racine.</summary>
        private void OpenBackup()
        {
            if (_lastBackupFolder == null) return;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _lastBackupFolder + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le dossier de sauvegarde n'a pas pu être ouvert.", ex);
                Status = "Le dossier de sauvegarde n'a pas pu être ouvert : " + ex.Message;
            }
        }

        private void RaiseBackupStates()
        {
            Raise(nameof(CanPrepareBackup));
            Raise(nameof(CanCopy));
            Raise(nameof(CanPrepareRestore));
            Raise(nameof(CanRestore));
            (PrepareBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrepareRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ListApplicationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public event EventHandler? JournalChanged;

        /// <summary>Le diagnostic sert aux réserves : il connaît les autres volumes de la machine.</summary>
        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;
            LoadDrivers(snapshot);
            _ = DescribePlanAsync();
        }

        private async Task DescribePlanAsync()
        {
            try
            {
                var surveyor = await SurveyorAsync().ConfigureAwait(true);
                var labels = new List<string>();
                foreach (var entry in surveyor.Plan()) labels.Add(entry.Label);

                PlanSummary = labels.Count == 0
                    ? "Aucun dossier de données personnelles n'a été trouvé pour ce compte."
                    : "Seront parcourus, pour le compte « " + Environment.UserName + " » : " +
                      string.Join(", ", labels.ToArray()) + ".";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La liste des dossiers n'a pas pu être établie.", ex);
                PlanSummary = "La liste des dossiers n'a pas pu être établie : " + ex.Message;
            }
        }

        private async Task<UserDataSurveyor> SurveyorAsync()
        {
            if (_surveyor != null) return _surveyor;

            var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
            return _surveyor = new UserDataSurveyor(services.Files, _journal, _logger);
        }

        private async Task RunAsync()
        {
            if (IsRunning) return;

            _running?.Dispose();
            _running = new CancellationTokenSource();
            var token = _running.Token;

            IsRunning = true;
            HasResults = false;
            Folders.Clear();
            Caveats.Clear();
            Raise(nameof(HasCaveats));
            Status = "Relevé en cours. Aucun fichier n'est ouvert ni copié : ils sont seulement comptés.";

            try
            {
                var surveyor = await SurveyorAsync().ConfigureAwait(true);
                var snapshot = _snapshot;
                var progress = new Progress<string>(step => Progress = step);

                // Hors du fil d'interface : parcourir un profil peut demander une minute, et une
                // fenêtre figée pendant ce temps ferait croire à un plantage.
                var survey = await Task.Run(() => surveyor.Run(snapshot, progress, token), token)
                    .ConfigureAwait(true);

                Apply(survey);
                Status = "Relevé terminé en " + ValueFormat.Duration(survey.Duration) +
                         ", et consigné dans le journal d'intervention.";
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                Status = "Relevé annulé.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé a échoué.", ex);
                Status = "Le relevé n'a pas pu être mené : " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                Progress = string.Empty;
            }
        }

        private void Apply(UserDataSurvey survey)
        {
            foreach (var folder in survey.Folders)
            {
                Folders.Add(new DataFolderItem
                {
                    Label = folder.Label,
                    Path = folder.Path,
                    OnDisk = folder.OnDiskBytes.HasValue
                        ? ValueFormat.Bytes(folder.OnDiskBytes.Value)
                        : "-",
                    Files = folder.FileCount > 0 ? ValueFormat.Number(folder.FileCount) + " fichiers" : "-",
                    CloudNote = folder.HasCloudOnly
                        ? ValueFormat.Bytes(folder.CloudOnlyBytes) + " restent dans le nuage (" +
                          ValueFormat.Number(folder.CloudOnlyFileCount) + " fichiers)"
                        : folder.Truncated
                            ? "Relevé arrêté sur son budget de temps : ce dossier contient au moins cela."
                            : null,
                });
            }

            foreach (var caveat in survey.Caveats) Caveats.Add(caveat);

            Headline = survey.Headline;
            HasResults = Folders.Count > 0;
            Raise(nameof(HasCaveats));
        }

        private void Cancel()
        {
            Status = "Annulation demandée…";
            _running?.Cancel();
        }

        private void RaiseCommandStates()
        {
            (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Arrêt à la fermeture : un parcours de profil n'a pas à survivre à la fenêtre.</summary>
        public void Shutdown()
        {
            _drivesTimer?.Stop();
            _drivesTimer = null;

            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }
}
