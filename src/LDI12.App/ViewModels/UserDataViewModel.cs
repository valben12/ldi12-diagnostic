using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;

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
        private bool _browserPasswords;
        private bool _includeSettings = true;
        private string _excludeExtensions = string.Empty;
        private string _maxFileSize = string.Empty;
        private bool _restorePrintersChecked = true;
        private IRepairAction? _restorePrinters;
        private ActionPreview? _restorePrintersPreview;
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
            OpenReportCommand = new RelayCommand(OpenReport, () => !IsCopying && ReportPath != null);
            DismissBackupResultCommand = new RelayCommand(() => BackupResult = null);
            DismissRestoreResultCommand = new RelayCommand(() => RestoreResult = null);
            ShowBackupModeCommand = new RelayCommand(() => SelectMode(DataMode.Backup), () => CanChangeMode);
            ShowRestoreModeCommand = new RelayCommand(() => SelectMode(DataMode.Restore), () => CanChangeMode);
            ShowSurveyModeCommand = new RelayCommand(() => SelectMode(DataMode.Survey), () => CanChangeMode);
            CloseProgramsForBackupCommand = new AsyncRelayCommand(
                () => CloseProgramsAsync(PrepareBackupAsync), () => CanPrepareBackup);
            CloseProgramsForRestoreCommand = new AsyncRelayCommand(
                () => CloseProgramsAsync(PrepareRestoreAsync), () => CanPrepareRestore);
            RunRestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore);
            VerifyRestoreSourceCommand = new AsyncRelayCommand(
                () => VerifyAsync(RestoreSource.Trim(), DataMode.Restore), () => !IsCopying && HasRestoreSource);
            VerifyLastBackupCommand = new AsyncRelayCommand(
                () => VerifyAsync(_lastBackupFolder!, DataMode.Backup), () => !IsCopying && _lastBackupFolder != null);
            StopTransferCommand = new RelayCommand(StopTransfer, () => _transferCancel != null);
            AddExtraFoldersCommand = new RelayCommand(AddExtraFolders, () => !IsCopying);
            CancelPreparationCommand = new RelayCommand(() => _prepareCancel?.Cancel(), () => _prepareCancel != null);
            AllDriversCommand = new RelayCommand(() => CheckAll(DriverChoices, true));
            NoDriversCommand = new RelayCommand(() => CheckAll(DriverChoices, false));
            NewPcDriversCommand = new RelayCommand(() => { CheckPeripherals(DriverChoices); Invalidate(); });
            AllSourceDriversCommand = new RelayCommand(() => CheckAll(SourceDriverChoices, true));
            NoSourceDriversCommand = new RelayCommand(() => CheckAll(SourceDriverChoices, false));
            NewPcSourceDriversCommand = new RelayCommand(() => { CheckPeripherals(SourceDriverChoices); Invalidate(); });
            AllRestoreDriversCommand = new RelayCommand(() => CheckAllRestore(RestoreDriverChoices, true));
            NoRestoreDriversCommand = new RelayCommand(() => CheckAllRestore(RestoreDriverChoices, false));
            NewPcRestoreDriversCommand = new RelayCommand(() => { CheckPeripherals(RestoreDriverChoices); InvalidateRestore(); });
            ListApplicationsCommand = new AsyncRelayCommand(ListApplicationsAsync, () => !IsCopying && !IsListingApplications);
            AllApplicationsCommand = new RelayCommand(() => CheckAll(ApplicationChoices, true));
            NoApplicationsCommand = new RelayCommand(() => CheckAll(ApplicationChoices, false));

            StartDriveWatch();
            _ = LoadDriversAsync(null);
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
                var machine = BackupState.MachineOf(_snapshot);
                var drives = await Task.Run(() => DestinationDrives.Detect(services.Files, machine, Environment.UserName))
                    .ConfigureAwait(true);
                var volumes = await Task.Run(() => SourceVolumes.Detect(services.Files)).ConfigureAwait(true);
                ApplySources(volumes);

                // Rien ne bouge à l'écran tant que rien n'a changé : reconstruire les tuiles toutes
                // les trois secondes ferait clignoter la sélection et le survol.
                var signature = new System.Text.StringBuilder();
                foreach (var drive in drives)
                    signature.Append(drive.Root).Append('|').Append(drive.Label).Append('|')
                        .Append(drive.FreeBytes / (64L * 1024 * 1024)).Append('|').Append(drive.Backups).Append('|')
                        .Append(drive.Resumable?.Path).Append(';');

                if (signature.ToString() == _drivesSignature) return;
                _drivesSignature = signature.ToString();

                // Un support débranché oublie sa mesure : rebranché, peut-être sur un autre port, il
                // sera mesuré de nouveau.
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var drive in drives) present.Add(RootOf(drive.Root));
                Forget(present);
                _unmeasurable.RemoveWhere(key => key != MachineKey && !present.Contains(key));

                BackupDrives.Clear();
                RestoreDrives.Clear();
                foreach (var drive in drives)
                {
                    var backupTile = new DriveTile(drive, tile => BackupDestination = tile.Root);
                    ShowSpeed(backupTile, _speeds.TryGetValue(RootOf(drive.Root), out var known) ? known : null);
                    BackupDrives.Add(backupTile);
                    if (drive.Backups > 0)
                    {
                        var restoreTile = new DriveTile(drive, tile => RestoreSource = tile.Root);
                        ShowSpeed(restoreTile, _sourceSpeeds.TryGetValue(RootOf(drive.Root), out var read) ? read : null);
                        RestoreDrives.Add(restoreTile);
                    }
                }

                MarkSelection();
                ScheduleMeasures();
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

        private void Forget(ISet<string> present)
        {
            var gone = new List<string>();
            foreach (var key in _speeds.Keys) if (!present.Contains(key)) gone.Add(key);
            foreach (var key in _sourceSpeeds.Keys) if (!present.Contains(key)) gone.Add(key);
            foreach (var key in gone)
            {
                _speeds.Remove(key);
                _sourceSpeeds.Remove(key);
            }
        }

        private void MarkSelection()
        {
            foreach (var tile in BackupDrives) tile.IsSelected = SameRoot(tile.Root, BackupDestination);
            foreach (var tile in RestoreDrives) tile.IsSelected = SameRoot(tile.Root, RootOf(RestoreSource));
        }

        private static bool SameRoot(string root, string path)
            => string.Equals(root.TrimEnd('\\'), (path ?? string.Empty).Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------- vitesse des supports et durée, mesurées d'office

        private readonly Dictionary<string, CopySpeed> _speeds = new Dictionary<string, CopySpeed>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReadSpeed> _sourceSpeeds = new Dictionary<string, ReadSpeed>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unmeasurable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CopySpeed? _machineSpeed;
        private CancellationTokenSource? _measureCancel;
        private Task _measuring = Task.CompletedTask;
        private bool _measureAgain;
        private DispatcherTimer? _measureDelay;
        private string _speedNote = "Choisissez un support : sa vitesse est mesurée d'elle-même, en une dizaine de secondes.";
        private string _restoreSpeedNote = "Choisissez la sauvegarde : sa vitesse est mesurée d'elle-même, sans rien y écrire.";

        public string SpeedNote { get => _speedNote; private set => Set(ref _speedNote, value); }
        public string RestoreSpeedNote { get => _restoreSpeedNote; private set => Set(ref _restoreSpeedNote, value); }

        /// <summary>La durée de la copie préparée, recalculée dès qu'une mesure arrive.</summary>
        public string BackupDuration
        {
            get
            {
                if (_backupPreview?.Plan is not BackupPlan plan || plan.Files == 0) return string.Empty;

                var root = RootOf(plan.Destination);
                if (!_speeds.TryGetValue(root, out var speed))
                    return _unmeasurable.Contains(root)
                        ? "Durée : non estimée, ce support n'a pas pu être mesuré."
                        : "Durée : mesure du support en cours…";

                var remaining = Math.Max(0, plan.Bytes - plan.AlreadyBytes);
                var share = plan.Bytes > 0 ? (double)remaining / plan.Bytes : 1;
                return "Durée estimée : " +
                       CopyEstimate.Describe(CopyEstimate.Duration(remaining, (int)Math.Ceiling(plan.Files * share), speed)) + ".";
            }
        }

        /// <summary>La durée de la restauration préparée, recalculée dès qu'une mesure arrive.</summary>
        public string RestoreDuration
        {
            get
            {
                if (_restorePreview?.Plan is not RestorePlan plan || plan.Files == 0) return string.Empty;

                var root = RootOf(RestoreSource);
                if (_unmeasurable.Contains(root) || _unmeasurable.Contains(MachineKey))
                    return "Durée : non estimée, un des deux disques n'a pas pu être mesuré.";
                if (!_sourceSpeeds.TryGetValue(root, out var source) || _machineSpeed == null)
                    return "Durée : mesure des disques en cours…";

                return "Durée estimée : " +
                       CopyEstimate.Describe(CopyEstimate.RestoreDuration(plan.Bytes, plan.Files, source, _machineSpeed)) +
                       " pour les fichiers.";
            }
        }

        public bool HasBackupDuration => BackupDuration.Length > 0;
        public bool HasRestoreDuration => RestoreDuration.Length > 0;

        private const string MachineKey = "<machine>";

        private void RaiseDurations()
        {
            Raise(nameof(BackupDuration));
            Raise(nameof(HasBackupDuration));
            Raise(nameof(RestoreDuration));
            Raise(nameof(HasRestoreDuration));
            Raise(nameof(BackupBarDetail));
            Raise(nameof(RestoreBarDetail));
        }

        /// <summary>
        /// Lance les mesures qui manquent, un instant après le dernier changement de support.
        /// </summary>
        /// <remarks>
        /// Le délai évite de mesurer chaque lettre tapée dans le champ. Aucune mesure ne démarre
        /// pendant un transfert : elle se disputerait le support avec la copie et fausserait
        /// l'une comme l'autre.
        /// </remarks>
        private void ScheduleMeasures()
        {
            if (_measureDelay == null)
            {
                if (Dispatcher.FromThread(System.Threading.Thread.CurrentThread) == null) return;
                _measureDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _measureDelay.Tick += (_, _) =>
                {
                    _measureDelay!.Stop();
                    _ = MeasureMissingAsync();
                };
            }

            _measureDelay.Stop();
            _measureDelay.Start();
        }

        private async Task MeasureMissingAsync()
        {
            if (IsBackupTransferring || IsRestoreTransferring) return;
            if (!_measuring.IsCompleted)
            {
                _measureAgain = true;
                return;
            }

            _measureCancel?.Dispose();
            _measureCancel = new CancellationTokenSource();
            var token = _measureCancel.Token;

            var run = MeasureAsync(token);
            _measuring = run;
            await run.ConfigureAwait(true);

            if (_measureAgain && !token.IsCancellationRequested)
            {
                _measureAgain = false;
                await MeasureMissingAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Les mesures, l'une après l'autre : le support de sauvegarde choisi, puis la sauvegarde à
        /// restaurer et le disque de cette machine.
        /// </summary>
        /// <remarks>
        /// La sauvegarde à restaurer est mesurée en lecture seule, sur ses propres fichiers. Le
        /// support de destination et le disque de cette machine reçoivent des fichiers d'essai,
        /// effacés à la fin. Chacun n'est mesuré qu'une fois tant qu'il reste branché.
        /// </remarks>
        private async Task MeasureAsync(CancellationToken token)
        {
            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                var probe = new CopySpeedProbe(_logger);

                var destination = BackupDestination.Trim();
                var root = RootOf(destination);
                if (root.Length > 0 && !_speeds.ContainsKey(root) && !_unmeasurable.Contains(root) &&
                    services.Files.DirectoryExists(destination))
                {
                    SpeedNote = "Mesure de " + root + " en cours…";
                    var speed = await Task.Run(() => probe.Measure(destination, null, token), token).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();

                    if (speed.IsValid)
                    {
                        _speeds[root] = speed;
                        foreach (var tile in BackupDrives)
                            if (string.Equals(RootOf(tile.Root), root, StringComparison.OrdinalIgnoreCase)) ShowSpeed(tile, speed);
                        SpeedNote = root + " : " + CopyEstimate.Speeds(speed) + "." +
                                    (CopyEstimate.Advice(speed) is string advice ? " " + advice : string.Empty);
                    }
                    else
                    {
                        _unmeasurable.Add(root);
                        SpeedNote = root + " n'a pas pu être mesuré : " + (speed.Failure ?? "cause inconnue") +
                                    ". La copie reste possible, sans durée annoncée.";
                    }

                    RaiseDurations();
                }

                var source = RestoreSource.Trim();
                var sourceRoot = RootOf(source);
                if (sourceRoot.Length > 0 && !_sourceSpeeds.ContainsKey(sourceRoot) && !_unmeasurable.Contains(sourceRoot))
                {
                    var backup = RestoreCatalog.Find(services.Files, source, out _);
                    if (backup != null)
                    {
                        RestoreSpeedNote = "Lecture de la sauvegarde en cours, sans rien y écrire…";
                        var read = await Task.Run(() => probe.MeasureSource(backup, token), token).ConfigureAwait(true);
                        token.ThrowIfCancellationRequested();

                        if (read.IsValid)
                        {
                            _sourceSpeeds[sourceRoot] = read;
                            foreach (var tile in RestoreDrives)
                                if (string.Equals(RootOf(tile.Root), sourceRoot, StringComparison.OrdinalIgnoreCase)) ShowSpeed(tile, read);
                        }
                        else
                        {
                            _unmeasurable.Add(sourceRoot);
                        }

                        if (_machineSpeed == null && !_unmeasurable.Contains(MachineKey) && read.IsValid)
                        {
                            RestoreSpeedNote = "Mesure du disque de cette machine en cours…";
                            var machine = await Task.Run(() => probe.Measure(Path.GetTempPath(), null, token), token)
                                .ConfigureAwait(true);
                            token.ThrowIfCancellationRequested();

                            if (machine.IsValid) _machineSpeed = machine;
                            else _unmeasurable.Add(MachineKey);
                        }

                        RestoreSpeedNote = !read.IsValid
                            ? "La sauvegarde n'a pas pu être mesurée : " + (read.Failure ?? "cause inconnue") +
                              ". La restauration reste possible, sans durée annoncée."
                            : "Sauvegarde : " + CopyEstimate.Speeds(read) + "." +
                              (_machineSpeed == null ? string.Empty : " Cette machine : " + CopyEstimate.Speeds(_machineSpeed) + ".") +
                              (CopyEstimate.Advice(read) is string advice ? " " + advice : string.Empty);

                        RaiseDurations();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Arrêtée pour laisser la place à une copie : elle reprendra au prochain choix de support.
                SpeedNote = "Mesure interrompue.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La vitesse d'un support n'a pas pu être mesurée.", ex);
            }
        }

        /// <summary>
        /// Arrête une mesure en cours, et attend qu'elle ait rendu la main.
        /// </summary>
        /// <remarks>
        /// Appelé avant chaque transfert : une mesure qui écrirait sur le support pendant la
        /// copie la ralentirait, et serait elle-même faussée.
        /// </remarks>
        private async Task StopMeasuresAsync()
        {
            _measureDelay?.Stop();
            _measureAgain = false;
            _measureCancel?.Cancel();

            try { await _measuring.ConfigureAwait(true); }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException) { }
        }


        private static void ShowSpeed(DriveTile tile, ReadSpeed? speed)
        {
            tile.SpeedText = speed == null ? null : "Mesuré : " + CopyEstimate.Speeds(speed) + ".";
            tile.SpeedAdvice = speed == null ? null : CopyEstimate.Advice(speed);
        }

        private static void ShowSpeed(DriveTile tile, CopySpeed? speed)
        {
            tile.SpeedText = speed == null ? null : "Mesuré : " + CopyEstimate.Speeds(speed) + ".";
            tile.SpeedAdvice = speed == null ? null : CopyEstimate.Advice(speed);
        }

        private static string RootOf(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetPathRoot(Path.GetFullPath(path.Trim())) ?? string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException ||
                                       ex is System.Security.SecurityException)
            {
                return string.Empty;
            }
        }

        // ---------- ce qu'on sauvegarde : le compte ouvert, ou un disque branché

        private string _sourcesSignature = string.Empty;
        private SourceTile? _selectedSource;
        private bool _includeOfflineDrivers = true;
        private readonly List<(IReadOnlyDictionary<string, string> Parameters, ActionPreview Preview, string Title)> _volumeJobs =
            new List<(IReadOnlyDictionary<string, string>, ActionPreview, string)>();

        /// <summary>Le compte ouvert, puis chaque disque branché qui porte un Windows ou des données.</summary>
        public ObservableCollection<SourceTile> Sources { get; } = new ObservableCollection<SourceTile>();

        /// <summary>Les comptes du Windows choisi, tous cochés au départ.</summary>
        public ObservableCollection<ChoiceItem> SourceAccounts { get; } = new ObservableCollection<ChoiceItem>();

        /// <summary>Les dossiers du disque choisi : tous pour un disque de données, ceux hors de Windows sinon.</summary>
        public ObservableCollection<ChoiceItem> SourceFolders { get; } = new ObservableCollection<ChoiceItem>();

        public bool IsSessionSource => _selectedSource?.Volume == null;
        public bool IsVolumeSource => !IsSessionSource;
        public bool IsWindowsSource => _selectedSource?.Volume?.HasWindows == true;
        public bool HasSourceAccounts => SourceAccounts.Count > 0;
        public bool HasSourceFolders => SourceFolders.Count > 0;

        /// <summary>Dossiers personnels et données d'applications : pour le compte ouvert et les comptes d'un Windows.</summary>
        public bool ShowAccountOptions => IsSessionSource || IsWindowsSource;

        /// <summary>Les pilotes d'un autre Windows : ceux de ce PC se choisissent un par un, plus bas.</summary>
        public bool ShowOfflineDrivers => IsWindowsSource && _selectedSource?.Volume?.IsSystem == false;

        /// <summary>Les pilotes tiers du Windows choisi, lus sur son disque, tous cochés au départ.</summary>
        public ObservableCollection<ChoiceItem> SourceDriverChoices { get; } = new ObservableCollection<ChoiceItem>();

        public bool HasSourceDriverChoices => SourceDriverChoices.Count > 0;

        public ICommand AllSourceDriversCommand { get; }
        public ICommand NoSourceDriversCommand { get; }
        public ICommand NewPcSourceDriversCommand { get; }

        private int _sourceDriversListing;

        private async Task ListSourceDriversAsync(SourceVolume volume)
        {
            var ticket = ++_sourceDriversListing;
            IReadOnlyList<DriverChoice> drivers;
            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                drivers = await Task.Run(() => OfflineDrivers.List(services.Files, volume.Root)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Les pilotes du disque " + volume.Root + " n'ont pas pu être listés.", ex);
                drivers = Array.Empty<DriverChoice>();
            }

            if (ticket != _sourceDriversListing || _selectedSource?.Volume != volume) return;

            SourceDriverChoices.Clear();
            foreach (var driver in drivers) SourceDriverChoices.Add(DriverItem(driver));
            Raise(nameof(HasSourceDriverChoices));
            Invalidate();
        }

        /// <summary>Exporter les pilotes du Windows choisi. Coché par défaut.</summary>
        public bool IncludeOfflineDrivers
        {
            get => _includeOfflineDrivers;
            set { if (Set(ref _includeOfflineDrivers, value)) Invalidate(); }
        }

        private void ApplySources(IReadOnlyList<SourceVolume> volumes)
        {
            var signature = new System.Text.StringBuilder();
            foreach (var volume in volumes)
                signature.Append(volume.Root).Append('|').Append(volume.Label).Append('|').Append(volume.HasWindows)
                    .Append('|').Append(volume.IsLocked)
                    .Append('|').Append(volume.Profiles.Count).Append('|').Append(volume.Folders.Count).Append(';');
            if (signature.ToString() == _sourcesSignature && Sources.Count > 0) return;
            _sourcesSignature = signature.ToString();

            var selected = _selectedSource?.Key ?? "session";
            Sources.Clear();
            Sources.Add(new SourceTile(null, SelectSource));
            foreach (var volume in volumes)
                if (volume.IsLocked || (volume.HasWindows ? volume.Profiles.Count > 0 || volume.Folders.Count > 0 : volume.Folders.Count > 0))
                    Sources.Add(new SourceTile(volume, SelectSource));

            SourceTile? match = null;
            foreach (var tile in Sources) if (tile.Key == selected) match = tile;

            // Le disque choisi a été débranché : on revient au compte ouvert.
            if (match == null || !ReferenceEquals(match.Volume?.Root, _selectedSource?.Volume?.Root) && match.Key != selected)
                match = Sources[0];

            SelectSource(match, keepChoices: match.Key == selected && _selectedSource != null);
        }

        private void SelectSource(SourceTile tile) => SelectSource(tile, keepChoices: false);

        private void SelectSource(SourceTile tile, bool keepChoices)
        {
            if (IsCopying || tile.Volume?.IsLocked == true) return;

            var changed = _selectedSource?.Key != tile.Key;
            _selectedSource = tile;
            foreach (var candidate in Sources) candidate.IsSelected = ReferenceEquals(candidate, tile);
            RaiseBar();

            if (changed || !keepChoices)
            {
                SourceAccounts.Clear();
                SourceFolders.Clear();
                SourceDriverChoices.Clear();
                Raise(nameof(HasSourceDriverChoices));
                if (tile.Volume is { HasWindows: true, IsSystem: false }) _ = ListSourceDriversAsync(tile.Volume);

                var volume = tile.Volume;
                if (volume != null)
                {
                    foreach (var (name, path) in volume.Profiles)
                        SourceAccounts.Add(new ChoiceItem(path, name, path, true, Invalidate));

                    // Un disque de données : tout le disque d'un coup, ou dossier par dossier.
                    if (!volume.HasWindows)
                        SourceFolders.Add(new ChoiceItem(volume.Root, "Tout le disque " + volume.Root.TrimEnd('\\'),
                            "Tous les dossiers et les fichiers de la racine, sans les dossiers système", false, Invalidate));

                    foreach (var folder in volume.Folders)
                        SourceFolders.Add(new ChoiceItem(folder, System.IO.Path.GetFileName(folder), folder, true, Invalidate));
                }
            }

            Raise(nameof(IsSessionSource));
            Raise(nameof(IsVolumeSource));
            Raise(nameof(IsWindowsSource));
            Raise(nameof(HasSourceAccounts));
            Raise(nameof(HasSourceFolders));
            Raise(nameof(ShowAccountOptions));
            Raise(nameof(ShowOfflineDrivers));
            if (changed) Invalidate();
        }

        /// <summary>
        /// Les sauvegardes à mener pour le disque choisi : une par compte coché, et une pour ses
        /// dossiers et les dossiers ajoutés à la main.
        /// </summary>
        /// <remarks>
        /// Un dossier de sauvegarde par compte : c'est ce qui permet de les restaurer chacun dans
        /// son compte sur le nouveau PC. Toutes se lisent avec les droits administrateur, en mode
        /// sauvegarde : une seule invite pour l'ensemble.
        /// </remarks>
        private List<(IReadOnlyDictionary<string, string> Parameters, string Title)> VolumeJobs()
        {
            var jobs = new List<(IReadOnlyDictionary<string, string>, string)>();
            var volume = _selectedSource?.Volume;
            if (volume == null) return jobs;

            var driversAsked = IncludeOfflineDrivers && volume.HasWindows && !volume.IsSystem;
            foreach (var account in SourceAccounts)
            {
                if (!account.IsChecked) continue;

                var parameters = CommonParameters();
                parameters[BackupUserDataAction.ProfileParameter] = account.Key;
                parameters[BackupUserDataAction.PersonalParameter] = IncludePersonal ? "1" : "0";
                parameters[BackupUserDataAction.ApplicationsParameter] = IncludeApplications ? "1" : "0";
                if (driversAsked)
                {
                    // Les pilotes sont ceux du Windows, pas d'un compte : emportés une fois. Sans liste
                    // lisible sur le disque, ils partent tous, par DISM.
                    var chosen = new List<DriverChoice>();
                    foreach (var item in SourceDriverChoices)
                        if (item.IsChecked && item.Driver != null) chosen.Add(item.Driver);

                    if (!HasSourceDriverChoices) parameters[BackupUserDataAction.OfflineDriversParameter] = OfflineDrivers.All;
                    else if (chosen.Count > 0) parameters[BackupUserDataAction.OfflineDriversParameter] = DriverBackup.Encode(chosen);
                    driversAsked = false;
                }

                jobs.Add((parameters, "Compte « " + account.Title + " »"));
            }

            var folders = new List<string>();
            var wholeDisk = false;
            foreach (var item in SourceFolders)
                if (item.IsChecked)
                {
                    if (string.Equals(item.Key, volume.Root, StringComparison.OrdinalIgnoreCase)) wholeDisk = true;
                    else folders.Add(item.Key);
                }

            if (wholeDisk) folders = new List<string> { volume.Root };
            foreach (var item in ExtraFolders) folders.Add(item.Path);

            if (folders.Count > 0)
            {
                var parameters = CommonParameters();
                parameters[BackupUserDataAction.PersonalParameter] = "0";
                parameters[BackupUserDataAction.ApplicationsParameter] = "0";
                parameters[BackupUserDataAction.LabelParameter] = "Disque-" + volume.Root.TrimEnd('\\', ':') +
                    (volume.Label.Length > 0 ? "-" + volume.Label : string.Empty);
                parameters[BackupUserDataAction.ExtraFoldersParameter] = LDI12.Actions.Backup.ExtraFolders.Encode(folders);
                jobs.Add((parameters, "Dossiers de " + volume.Title));
            }

            return jobs;
        }

        private Dictionary<string, string> CommonParameters()
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BackupUserDataAction.DestinationParameter] = BackupDestination.Trim(),
                [BackupUserDataAction.ResumeParameter] = ResumeBackup ? "1" : "0",
                [BackupUserDataAction.ElevatedParameter] = "1",
                [BackupUserDataAction.ExcludeExtensionsParameter] = ExcludeExtensions.Trim(),
                [BackupUserDataAction.MaxFileSizeParameter] = MaxFileSize.Trim(),
            };

            if (_speeds.TryGetValue(RootOf(BackupDestination), out var speed))
                parameters[BackupUserDataAction.SpeedParameter] = speed.Encode();

            return parameters;
        }

        private bool VolumeJobsReady
        {
            get
            {
                foreach (var job in _volumeJobs) if (job.Preview.CanExecute) return true;
                return false;
            }
        }

        private async Task PrepareVolumeAsync(ActionRunner runner)
        {
            var jobs = VolumeJobs();
            if (jobs.Count == 0)
            {
                Status = "Rien n'est coché sur ce disque : cochez un compte ou un dossier.";
                return;
            }

            IsCopying = true;
            BackupLines.Clear();
            _volumeJobs.Clear();
            Status = "Relevé du disque en cours, avec les droits administrateur. Aucun fichier n'est encore écrit.";
            var preparing = StartPreparation();

            try
            {
                foreach (var (parameters, title) in jobs)
                {
                    var preview = await runner.PreviewAsync(_backup!, parameters, preparing).ConfigureAwait(true);
                    _volumeJobs.Add((parameters, preview, title));

                    BackupLines.Add(title + ". " + preview.Summary);
                    Show(BackupLines, preview);
                }

                var ready = 0;
                foreach (var job in _volumeJobs) if (job.Preview.CanExecute) ready++;
                BackupSummary = ready + " sauvegarde(s) prête(s) sur " + _volumeJobs.Count + ".";
                Status = ready > 0 ? "Relevé terminé. Rien n'a encore été copié." : "Rien ne peut être copié : voir le détail.";
            }
            catch (OperationCanceledException)
            {
                _volumeJobs.Clear();
                BackupLines.Clear();
                Status = "Préparation annulée. Rien n'a été copié.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé du disque a échoué.", ex);
                _volumeJobs.Clear();
                Status = "Le relevé du disque a échoué : " + ex.Message;
            }
            finally
            {
                EndPreparation();
                IsCopying = false;
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
            }
        }

        /// <summary>Les sauvegardes du disque, l'une après l'autre, dans l'hôte élevé.</summary>
        private async Task CopyVolumeAsync(ActionRunner runner)
        {
            await StopMeasuresAsync().ConfigureAwait(true);
            using var awake = KeepAwake.Start();

            IsCopying = true;
            Mode = DataMode.Backup;
            IsBackupTransferring = true;
            BackupResult = null;
            StartTransferStats();
            Status = "Copie en cours…";
            var progress = new Progress<ActionProgress>(OnTransfer);
            var report = new List<string>();
            var summaries = new List<string>();
            var outcomes = new List<ActionOutcome>();
            string? failure = null;
            var token = StartCancellable();

            try
            {
                foreach (var (parameters, preview, title) in _volumeJobs)
                {
                    if (!preview.CanExecute) continue;
                    if (token.IsCancellationRequested) break;

                    BeginTransfer(title + " : préparation de la copie…");
                    var outcome = await runner.ExecuteAsync(_backup!, preview, parameters, progress, token).ConfigureAwait(true);
                    outcomes.Add(outcome);
                    summaries.Add(title + " : " + outcome.Summary);
                    Report(report, title, outcome);
                }

                Status = string.Join(" ", summaries) +
                         (token.IsCancellationRequested
                             ? " Relancez la sauvegarde vers le même support : elle reprendra où elle s'est arrêtée."
                             : string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La copie du disque a échoué.", ex);
                Status = "La copie a échoué : " + ex.Message;
                failure = ex.Message;
            }
            finally
            {
                EndCancellable();
                BackupResult = Conclude("Sauvegarde", outcomes, SecuredLine(), failure);
                Taskbar.Finish(BackupResult.Kind == TransferResultKind.Failed);
                BackupFinished?.Invoke(this, new TransferFinishedEventArgs(BackupResult, null));
                IsCopying = false;
                IsBackupTransferring = false;
                _volumeJobs.Clear();
                BackupLines.Clear();
                BackupSummary = report.Count > 0 ? "Compte rendu de la sauvegarde :" : string.Empty;
                foreach (var line in report) BackupLines.Add(line);

                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // ---------- dossiers ajoutés à la main

        /// <summary>Les dossiers ajoutés à la sauvegarde, où qu'ils soient.</summary>
        public ObservableCollection<ExtraFolderItem> ExtraFolders { get; } = new ObservableCollection<ExtraFolderItem>();

        public bool HasExtraFolders => ExtraFolders.Count > 0;

        public ICommand AddExtraFoldersCommand { get; }

        /// <summary>
        /// Ouvre la fenêtre de choix de dossiers de l'Explorateur.
        /// </summary>
        /// <remarks>
        /// Plusieurs dossiers d'un coup, et la barre d'adresse pour coller un chemin. La racine d'un
        /// disque se choisit aussi : tout ce disque est alors sauvegardé, sans ses dossiers système.
        /// </remarks>
        private void AddExtraFolders()
        {
            var owner = IntPtr.Zero;
            try
            {
                var window = System.Windows.Application.Current?.MainWindow;
                if (window != null) owner = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            }
            catch (InvalidOperationException) { }

            var added = false;
            foreach (var path in FolderPicker.Pick(owner, "Dossiers à ajouter à la sauvegarde"))
            {
                var normalized = LDI12.Actions.Backup.ExtraFolders.Normalize(path);
                var known = false;
                foreach (var item in ExtraFolders) known |= string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase);
                if (known) continue;

                ExtraFolders.Add(new ExtraFolderItem(normalized, RemoveExtraFolder));
                added = true;
            }

            if (!added) return;
            Raise(nameof(HasExtraFolders));
            Invalidate();
        }

        private void RemoveExtraFolder(ExtraFolderItem item)
        {
            if (IsCopying || !ExtraFolders.Remove(item)) return;
            Raise(nameof(HasExtraFolders));
            Invalidate();
        }

        // ---------- pilotes et applications

        private bool _resumeBackup = true;

        /// <summary>
        /// Reprendre la dernière sauvegarde inachevée de cette machine sur le support choisi. Coché par défaut.
        /// </summary>
        /// <remarks>
        /// Décocher sert au cas rare où l'on veut repartir de zéro, par exemple après avoir
        /// réorganisé les dossiers du client entre deux passages.
        /// </remarks>
        public bool ResumeBackup
        {
            get => _resumeBackup;
            set { if (Set(ref _resumeBackup, value)) Invalidate(); }
        }

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

        /// <summary>La liste des pilotes n'est montrée que si la tuile est cochée : grisée, elle n'apprenait rien.</summary>
        public bool ShowDriverChoices => IncludeDrivers && HasDriverChoices;

        public bool ShowRestoreDriverChoices => RestoreDriversChecked && HasRestoreDriverChoices;

        public string DriversNote { get => _driversNote; private set => Set(ref _driversNote, value); }
        public string ApplicationsNote { get => _applicationsNote; private set => Set(ref _applicationsNote, value); }

        public bool IncludeDrivers
        {
            get => _includeDrivers;
            set { if (Set(ref _includeDrivers, value)) { Raise(nameof(ShowDriverChoices)); Invalidate(); } }
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
        public ICommand NewPcDriversCommand { get; }
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

        /// <summary>Une case par pilote : ce qu'il fait fonctionner, puis de quoi le reconnaître.</summary>
        private ChoiceItem DriverItem(DriverChoice driver)
        {
            var detail = new List<string> { DriverKinds.Describe(driver.Kind) };
            if (!string.IsNullOrEmpty(driver.DeviceClass)) detail.Add(driver.DeviceClass!);
            if (!string.IsNullOrEmpty(driver.Manufacturer)) detail.Add(driver.Manufacturer!);
            if (!string.IsNullOrEmpty(driver.Version)) detail.Add("version " + driver.Version);
            detail.Add(driver.InfName);

            return new ChoiceItem(driver.InfName, driver.Label, string.Join(" · ", detail), true, Invalidate) { Driver = driver };
        }

        /// <summary>
        /// Pour un PC neuf : les périphériques seulement. Ce qui équipe l'ancienne carte mère, son
        /// chipset ou sa carte graphique n'a rien à faire sur une autre machine.
        /// </summary>
        private static void CheckPeripherals(IEnumerable<ChoiceItem> items)
        {
            foreach (var item in items) item.SetSilently(item.Driver?.Kind == DriverKind.Peripheral);
        }

        private void CheckAllRestore(IEnumerable<ChoiceItem> items, bool value)
        {
            foreach (var item in items) item.SetSilently(value);
            InvalidateRestore();
        }

        private int _driverListing;

        /// <summary>
        /// Les pilotes tiers de ce PC, lus dans ses fichiers <c>Windows\INF\oemNN.inf</c>.
        /// </summary>
        /// <remarks>
        /// La liste venait du diagnostic complet, qui ne tourne pas à l'ouverture : elle restait
        /// vide, et disait à tort qu'il n'y avait aucun pilote. Lue sur le disque, elle est là tout
        /// de suite, sans droits particuliers. Quand un diagnostic complet a été fait, les noms des
        /// périphériques qu'il a vus remplacent ceux que les fichiers donnent. Ce qui a été coché ou
        /// décoché est gardé d'une relecture à l'autre.
        /// </remarks>
        private async Task LoadDriversAsync(SystemSnapshot? snapshot)
        {
            var ticket = ++_driverListing;
            IReadOnlyList<DriverChoice> listed;
            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                var root = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
                listed = await Task.Run(() => OfflineDrivers.List(services.Files, root)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Les pilotes de cette machine n'ont pas pu être listés.", ex);
                listed = Array.Empty<DriverChoice>();
            }

            if (ticket != _driverListing) return;

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in DriverBackup.Choices(snapshot)) seen[device.InfName] = device.Label;

            var previous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in DriverChoices) previous[item.Key] = item.IsChecked;

            DriverChoices.Clear();
            var peripherals = 0;
            foreach (var driver in listed)
            {
                var shown = seen.TryGetValue(driver.InfName, out var label) && label.Length > 0
                    ? new DriverChoice
                    {
                        InfName = driver.InfName,
                        Label = label,
                        DeviceClass = driver.DeviceClass,
                        Manufacturer = driver.Manufacturer,
                        Version = driver.Version,
                        Kind = driver.Kind,
                    }
                    : driver;

                var item = DriverItem(shown);
                if (previous.TryGetValue(driver.InfName, out var wasChecked)) item.SetSilently(wasChecked);
                DriverChoices.Add(item);
                if (driver.Kind == DriverKind.Peripheral) peripherals++;
            }

            DriversNote = DriverChoices.Count == 0
                ? "Aucun pilote tiers sur cette machine : tout ce qu'elle utilise revient avec Windows."
                : DriverChoices.Count + " pilote(s) tiers, dont " + peripherals + " de périphériques. Pour réinstaller ce PC, " +
                  "gardez-les tous ; pour passer sur un PC neuf, « PC neuf » ne garde que les périphériques.";

            Raise(nameof(HasDriverChoices));
            Raise(nameof(ShowDriverChoices));
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

        /// <summary>
        /// Ce que l'écran montre : la sauvegarde, la restauration, ou le relevé.
        /// </summary>
        /// <remarks>
        /// Les trois tenaient autrefois sur une seule page, l'une sous l'autre, sur six écrans de
        /// haut : le technicien descendait à travers la restauration pour atteindre le bouton de
        /// copie. Ce sont deux gestes opposés, faits à des moments opposés de l'intervention ; ils
        /// se choisissent maintenant d'un clic, et l'écran ne montre que celui qu'on a choisi.
        /// </remarks>
        public enum DataMode
        {
            Backup = 0,
            Restore = 1,
            Survey = 2,
        }

        private DataMode _mode = DataMode.Backup;

        public DataMode Mode
        {
            get => _mode;
            private set { if (Set(ref _mode, value)) RaiseTransferStates(); }
        }

        public bool IsBackupMode => Mode == DataMode.Backup;
        public bool IsRestoreMode => Mode == DataMode.Restore;
        public bool IsSurveyMode => Mode == DataMode.Survey;

        /// <summary>Un transfert en cours fige le choix : on ne quitte pas une copie par mégarde.</summary>
        public bool CanChangeMode => !IsBackupTransferring && !IsRestoreTransferring;

        public ICommand ShowBackupModeCommand { get; }
        public ICommand ShowRestoreModeCommand { get; }
        public ICommand ShowSurveyModeCommand { get; }

        private void SelectMode(DataMode mode)
        {
            if (!CanChangeMode) return;
            Mode = mode;
        }

        /// <summary>
        /// Impose le mode au démarrage, pour le banc de captures.
        /// </summary>
        /// <remarks>
        /// Depuis que l'écran montre un seul geste à la fois, la restauration ne s'atteint plus
        /// en faisant défiler : sans cette entrée, le banc n'en prendrait jamais de capture, et
        /// une faute d'affichage y passerait inaperçue d'une version à l'autre.
        /// </remarks>
        public void ShowMode(string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "restore":
                case "restauration":
                    Mode = DataMode.Restore;
                    break;

                case "survey":
                case "releve":
                case "relevé":
                    Mode = DataMode.Survey;
                    break;

                case "transfert":
                    ShowDemoTransfer();
                    break;

                case "bilan":
                    Mode = DataMode.Backup;
                    BackupResult = new TransferResult(TransferResultKind.Partial,
                        "Sauvegarde terminée, avec des points à revoir ci-dessous",
                        "48 213 fichier(s), 61,4 Go, en 14 min 08 s · 74,2 Mo/s en moyenne");
                    BackupSummary = "Compte rendu de la sauvegarde, déposée dans E:\\LDI12-Sauvegardes\\PC-CLIENT :";
                    BackupLines.Clear();
                    BackupLines.Add("Documents : 12 408 fichier(s) copiés et relus.");
                    BackupLines.Add("Images : 30 117 fichier(s) copiés et relus.");
                    BackupLines.Add("2 fichier(s) présents seulement en ligne (OneDrive) : non copiés.");
                    Raise(nameof(HasBackupPreview));
                    break;

                default:
                    Mode = DataMode.Backup;
                    break;
            }
        }

        /// <summary>
        /// Un transfert figé en plein milieu, pour le banc de captures.
        /// </summary>
        /// <remarks>
        /// Le tableau de bord n'existe que pendant une copie : sans cette entrée, aucune capture ne
        /// le montrerait jamais, et un chiffre coupé ou une courbe vide passeraient inaperçus.
        /// Rien n'est copié : seuls les chiffres affichés sont posés.
        /// </remarks>
        private void ShowDemoTransfer()
        {
            Mode = DataMode.Backup;
            Status = "Sauvegarde en cours vers E:\\LDI12-Sauvegardes\\PC-CLIENT.";
            IsBackupTransferring = true;
            BeginTransfer("Copie de « Images »");
            TransferPercent = 42.6;
            TransferDetail = "25,9 Go sur 61,4 Go · 20 518 fichier(s) sur 48 213";
            TransferElapsed = "Écoulé : 6 min 02 s";
            TransferRemainingShort = "environ 8 min";
            TransferSpeed = "71,5 Mo/s";
            TransferBytes = "25,9 Go";
            TransferBytesOf = "sur 61,4 Go";
            TransferFiles = "20 518";
            TransferFilesOf = "sur 48 213";

            _rates.Clear();
            for (var second = 0; second < CurveLength; second++)
                _rates.Add(70 + 18 * Math.Sin(second / 9.0) - (second > 80 && second < 95 ? 45 : 0) + (second % 7) * 2.5);
            TransferCurve = Curve(_rates);
            Raise(nameof(HasTransferCurve));
        }

        public bool ShowModePicker => CanChangeMode;
        public bool ShowScreen => IsSurveyMode && CanChangeMode;
        public bool ShowBackupSection => IsBackupMode && !IsRestoreTransferring;
        public bool ShowRestoreSection => IsRestoreMode && !IsBackupTransferring;
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
            Raise(nameof(ShowModePicker));
            Raise(nameof(CanChangeMode));
            Raise(nameof(IsBackupMode));
            Raise(nameof(IsRestoreMode));
            Raise(nameof(IsSurveyMode));
            RaiseBar();
        }

        private CancellationTokenSource? _transferCancel;
        private CancellationTokenSource? _prepareCancel;

        /// <summary>
        /// Arrête une préparation en cours.
        /// </summary>
        /// <remarks>
        /// Sur un gros profil ou un disque lent, le relevé prend plusieurs minutes : il doit pouvoir
        /// s'arrêter, quand on s'aperçoit qu'on s'est trompé de support ou d'option.
        /// </remarks>
        public ICommand CancelPreparationCommand { get; }

        private CancellationToken StartPreparation()
        {
            _prepareCancel?.Dispose();
            _prepareCancel = new CancellationTokenSource();
            (CancelPreparationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseBar();
            return _prepareCancel.Token;
        }

        private void EndPreparation()
        {
            _prepareCancel?.Dispose();
            _prepareCancel = null;
            (CancelPreparationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseBar();
        }

        /// <summary>
        /// Arrête la copie ou la restauration en cours, entre deux fichiers.
        /// </summary>
        /// <remarks>
        /// Le fichier en cours n'est jamais laissé sous son vrai nom : sa copie provisoire est
        /// retirée. La sauvegarde reste marquée « en cours », et la relancer la reprend.
        /// </remarks>
        public ICommand StopTransferCommand { get; }

        private CancellationToken StartCancellable()
        {
            _transferCancel?.Dispose();
            _transferCancel = new CancellationTokenSource();
            (StopTransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
            return _transferCancel.Token;
        }

        private void EndCancellable()
        {
            _transferCancel?.Dispose();
            _transferCancel = null;
            (StopTransferCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void StopTransfer()
        {
            if (_transferCancel == null) return;
            TransferTitle = "Arrêt demandé : la copie s'arrête après le fichier en cours…";
            _transferCancel.Cancel();
        }

        private void BeginTransfer(string title)
        {
            TransferTitle = title;
            TransferDetail = string.Empty;
            TransferPercent = 0;
            TransferRemaining = "Estimation du temps restant en cours…";
            TransferElapsed = string.Empty;
            TransferRemainingShort = "en calcul…";
            TransferSpeed = "—";
            TransferBytes = "—";
            TransferFiles = "—";
            TransferBytesOf = string.Empty;
            TransferFilesOf = string.Empty;
        }

        private void OnTransfer(ActionProgress report)
        {
            TransferTitle = report.Text;
            if (report.Detail != null) TransferDetail = report.Detail;
            if (report.Fraction.HasValue) TransferPercent = Math.Max(0, Math.Min(100, report.Fraction.Value * 100));
            if (report.Elapsed.HasValue) TransferElapsed = "Écoulé : " + Duration(report.Elapsed.Value);

            // Seulement quand le rapport vient de la copie elle-même : les étapes de fin (export
            // Wi-Fi, fiche) ne portent pas d'estimation, et effacer la dernière laisserait un vide.
            if (report.Detail != null)
            {
                TransferRemaining = Remaining(report.Remaining);
                TransferRemainingShort = RemainingShort(report.Remaining);
            }

            if (report.Figures != null) ShowFigures(report.Figures);
            Taskbar.Show(TransferPercent / 100);
        }

        // ---------- tableau de bord du transfert

        private string _transferSpeed = "—";
        private string _transferBytes = "—";
        private string _transferFiles = "—";
        private string _transferBytesOf = string.Empty;
        private string _transferFilesOf = string.Empty;
        private System.Windows.Media.PointCollection _transferCurve = new System.Windows.Media.PointCollection();
        private readonly List<double> _rates = new List<double>();
        private readonly Stopwatch _rateClock = new Stopwatch();
        private readonly Stopwatch _transferClock = new Stopwatch();
        private TransferFigures? _lastFigures;
        private long _bytesSecured;
        private int _filesSecured;

        private string _transferRemainingShort = "en calcul…";

        /// <summary>« environ 12 min », pour la case du tableau de bord.</summary>
        public string TransferRemainingShort { get => _transferRemainingShort; private set => Set(ref _transferRemainingShort, value); }

        /// <summary>Le débit des dernières secondes : « 48 Mo/s ».</summary>
        public string TransferSpeed { get => _transferSpeed; private set => Set(ref _transferSpeed, value); }

        /// <summary>« 12,4 Go », en grand ; le total dessous, « sur 30 Go ».</summary>
        public string TransferBytes { get => _transferBytes; private set => Set(ref _transferBytes, value); }

        public string TransferBytesOf { get => _transferBytesOf; private set => Set(ref _transferBytesOf, value); }

        /// <summary>« 1 234 », en grand ; le total dessous, « sur 5 678 ».</summary>
        public string TransferFiles { get => _transferFiles; private set => Set(ref _transferFiles, value); }

        public string TransferFilesOf { get => _transferFilesOf; private set => Set(ref _transferFilesOf, value); }

        /// <summary>Sous le débit : ce qu'il veut dire, relecture comprise.</summary>
        public string TransferSpeedNote => "écrit puis relu";

        /// <summary>
        /// La courbe du débit, une valeur par seconde sur les deux dernières minutes.
        /// </summary>
        /// <remarks>
        /// Un chiffre dit la vitesse, la courbe dit ce qui se passe : un support qui chauffe et
        /// ralentit, un antivirus qui s'en mêle, des milliers de petits fichiers qui passent. Les
        /// points vont de 0 à 100 en hauteur, le plus fort débit en haut ; la forme est fermée
        /// par le bas pour être remplie, et l'affichage l'étire à sa taille.
        /// </remarks>
        public System.Windows.Media.PointCollection TransferCurve
        {
            get => _transferCurve;
            private set => Set(ref _transferCurve, value);
        }

        public bool HasTransferCurve => _rates.Count >= 2;

        private const int CurveLength = 120;

        private void ShowFigures(TransferFigures figures)
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            TransferBytes = ValueFormat.Bytes(figures.BytesDone);
            TransferBytesOf = "sur " + ValueFormat.Bytes(figures.BytesTotal);
            TransferFiles = figures.FilesDone.ToString("N0", culture);
            TransferFilesOf = "sur " + figures.FilesTotal.ToString("N0", culture);
            if (figures.BytesPerSecond.HasValue) TransferSpeed = ValueFormat.Bytes((long)figures.BytesPerSecond.Value) + "/s";

            // Une copie suivie d'une autre (fichiers ouverts, plusieurs comptes) : les chiffres de
            // la précédente sont acquis au bilan quand la suivante repart de zéro.
            if (_lastFigures != null && (figures.BytesTotal != _lastFigures.BytesTotal || figures.FilesTotal != _lastFigures.FilesTotal))
            {
                _bytesSecured += _lastFigures.BytesDone;
                _filesSecured += _lastFigures.FilesDone;
            }

            _lastFigures = figures;

            if (!figures.BytesPerSecond.HasValue) return;
            if (_rateClock.IsRunning && _rateClock.Elapsed < TimeSpan.FromSeconds(1)) return;
            _rateClock.Restart();

            _rates.Add(figures.BytesPerSecond.Value);
            if (_rates.Count > CurveLength) _rates.RemoveAt(0);
            TransferCurve = Curve(_rates);
            Raise(nameof(HasTransferCurve));
        }

        /// <summary>Les points de la courbe, fermée par le bas : (0,100) … (n-1,100).</summary>
        internal static System.Windows.Media.PointCollection Curve(IReadOnlyList<double> rates)
        {
            var points = new System.Windows.Media.PointCollection();
            if (rates.Count == 0) return points;

            var max = 0.0;
            foreach (var rate in rates) max = Math.Max(max, rate);
            if (max <= 0) max = 1;

            points.Add(new System.Windows.Point(0, 100));
            for (var index = 0; index < rates.Count; index++)
                points.Add(new System.Windows.Point(index, 100 - Math.Max(0, rates[index]) / max * 100));
            points.Add(new System.Windows.Point(rates.Count - 1, 100));
            points.Freeze();
            return points;
        }

        private void StartTransferStats()
        {
            _rates.Clear();
            _rateClock.Reset();
            _transferClock.Restart();
            _lastFigures = null;
            _bytesSecured = 0;
            _filesSecured = 0;
            TransferCurve = new System.Windows.Media.PointCollection();
            Raise(nameof(HasTransferCurve));
            Taskbar.Show(0);
        }

        /// <summary>Ce que la copie a mis en sûreté, d'un bout à l'autre : « 12,4 Go, 5 432 fichiers en 8 min 12 s ».</summary>
        private string SecuredLine()
        {
            _transferClock.Stop();
            var bytes = _bytesSecured + (_lastFigures?.BytesDone ?? 0);
            var files = _filesSecured + (_lastFigures?.FilesDone ?? 0);
            var elapsed = _transferClock.Elapsed;

            if (files == 0 && bytes == 0) return "Durée : " + Duration(elapsed) + ".";

            var line = files.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " fichier(s), " +
                       ValueFormat.Bytes(bytes) + ", en " + Duration(elapsed);
            if (bytes > 0 && elapsed.TotalSeconds >= 5)
                line += " · " + ValueFormat.Bytes((long)(bytes / elapsed.TotalSeconds)) + "/s en moyenne";
            return line;
        }

        // ---------- bilan d'un transfert

        private TransferResult? _backupResult;
        private TransferResult? _restoreResult;

        /// <summary>Une sauvegarde est finie : le dossier d'intervention en garde le bilan et s'y recopie.</summary>
        public event EventHandler<TransferFinishedEventArgs>? BackupFinished;

        /// <summary>Une sauvegarde est choisie pour être restaurée : le dossier d'intervention qu'elle porte peut être repris.</summary>
        public event EventHandler<string>? RestoreSourceChosen;

        /// <summary>Une restauration est finie.</summary>
        public event EventHandler<TransferFinishedEventArgs>? RestoreFinished;

        /// <summary>Une sauvegarde a été relue contre son manifeste.</summary>
        public event EventHandler<TransferFinishedEventArgs>? VerificationFinished;

        /// <summary>Le bilan de la dernière sauvegarde, en tête de l'écran ; nul avant la première.</summary>
        public TransferResult? BackupResult
        {
            get => _backupResult;
            private set { if (Set(ref _backupResult, value)) Raise(nameof(HasBackupResult)); }
        }

        public TransferResult? RestoreResult
        {
            get => _restoreResult;
            private set { if (Set(ref _restoreResult, value)) Raise(nameof(HasRestoreResult)); }
        }

        public bool HasBackupResult => BackupResult != null;
        public bool HasRestoreResult => RestoreResult != null;

        public ICommand DismissBackupResultCommand { get; }
        public ICommand DismissRestoreResultCommand { get; }

        /// <summary>
        /// Le bilan d'un transfert fait de plusieurs opérations : la pire l'emporte.
        /// </summary>
        /// <remarks>
        /// Un compte rendu de trente lignes ne dit pas d'un coup d'œil si tout est passé. Le bilan
        /// le dit en une phrase et une couleur ; le détail reste dessous pour qui le cherche.
        /// </remarks>
        internal static TransferResult Conclude(string gesture, IReadOnlyCollection<ActionOutcome> outcomes, string secured, string? failure)
        {
            if (failure != null)
                return new TransferResult(TransferResultKind.Failed, gesture + " interrompue par une erreur", failure);

            var worst = TransferResultKind.Succeeded;
            foreach (var outcome in outcomes)
            {
                var kind = outcome.Status switch
                {
                    ActionStatus.Succeeded or ActionStatus.NothingToDo => TransferResultKind.Succeeded,
                    ActionStatus.Cancelled => TransferResultKind.Stopped,
                    ActionStatus.PartiallySucceeded => TransferResultKind.Partial,
                    _ => TransferResultKind.Failed,
                };
                if (kind > worst) worst = kind;
            }

            if (outcomes.Count == 0) worst = TransferResultKind.Failed;

            var title = worst switch
            {
                TransferResultKind.Succeeded => gesture + " terminée, tout est vérifié",
                TransferResultKind.Partial => gesture + " terminée, avec des points à revoir ci-dessous",
                TransferResultKind.Stopped => gesture + " arrêtée : la relancer reprend où elle en était",
                _ => gesture + " en échec",
            };

            return new TransferResult(worst, title, secured);
        }

        /// <summary>« Temps restant estimé : environ 12 min ».</summary>
        internal static string Remaining(TimeSpan? remaining)
            => remaining == null ? "Estimation du temps restant en cours…" : "Temps restant estimé : " + RemainingShort(remaining);

        /// <summary>« environ 12 min » : arrondi comme on le dirait à voix haute.</summary>
        internal static string RemainingShort(TimeSpan? remaining)
        {
            if (remaining == null) return "en calcul…";

            var value = remaining.Value;
            if (value < TimeSpan.FromMinutes(1)) return "moins d'une minute";
            if (value < TimeSpan.FromHours(1)) return "environ " + (int)Math.Ceiling(value.TotalMinutes) + " min";

            var minutes = (int)Math.Ceiling(value.TotalMinutes);
            return "environ " + minutes / 60 + " h " + (minutes % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
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
            set { if (Set(ref _restoreDriversChecked, value)) { Raise(nameof(ShowRestoreDriverChoices)); InvalidateRestore(); } }
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
                ScheduleMeasures();
                _ = ListRestoreBackupsAsync();
                if (!string.IsNullOrWhiteSpace(value)) RestoreSourceChosen?.Invoke(this, value.Trim());
            }
        }

        /// <summary>
        /// Les sauvegardes du support choisi, quand il en porte plusieurs : un dossier par compte
        /// d'un même PC, ou les PC de plusieurs clients. La plus récente est retenue d'office.
        /// </summary>
        public ObservableCollection<BackupEntryTile> RestoreBackups { get; } = new ObservableCollection<BackupEntryTile>();

        /// <summary>Les pilotes de la sauvegarde retenue, tous cochés : on décoche ceux qui n'ont rien à faire sur ce PC.</summary>
        public ObservableCollection<ChoiceItem> RestoreDriverChoices { get; } = new ObservableCollection<ChoiceItem>();

        public bool HasRestoreDriverChoices => RestoreDriverChoices.Count > 0;

        public ICommand AllRestoreDriversCommand { get; }
        public ICommand NoRestoreDriversCommand { get; }
        public ICommand NewPcRestoreDriversCommand { get; }

        public bool HasRestoreChoice => RestoreBackups.Count > 1;

        private int _restoreListing;

        private async Task ListRestoreBackupsAsync()
        {
            var ticket = ++_restoreListing;
            var source = RestoreSource.Trim();

            try
            {
                var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
                var (entries, chosen, packages) = await Task.Run(() =>
                {
                    var found = RestoreCatalog.Find(services.Files, source, out _);
                    if (found == null)
                        return ((IReadOnlyList<BackupEntry>)Array.Empty<BackupEntry>(), (string?)null,
                            (IReadOnlyList<(string Folder, string Label, DriverKind Kind)>)Array.Empty<(string, string, DriverKind)>());

                    // Une sauvegarde désignée elle-même : ses voisines sont dans le dossier au-dessus.
                    var folder = SameRoot(found, source) ? System.IO.Path.GetDirectoryName(found.TrimEnd('\\')) : source;
                    return (RestoreCatalog.List(services.Files, folder ?? source), found,
                        RestoreDriversAction.Describe(services.Files, found));
                }).ConfigureAwait(true);

                // Une frappe plus récente dans le champ a déjà pris le relais.
                if (ticket != _restoreListing) return;

                RestoreBackups.Clear();
                foreach (var entry in entries)
                    RestoreBackups.Add(new BackupEntryTile(entry, tile => RestoreSource = tile.Entry.Path)
                    {
                        IsSelected = chosen != null && SameRoot(entry.Path, chosen),
                    });

                // Périphériques d'abord : ce sont eux qu'on garde en passant sur un PC neuf.
                RestoreDriverChoices.Clear();
                foreach (var kind in new[] { DriverKind.Peripheral, DriverKind.Machine, DriverKind.Unknown })
                    foreach (var (folder, label, packageKind) in packages)
                    {
                        if (packageKind != kind) continue;
                        var name = System.IO.Path.GetFileName(folder);
                        RestoreDriverChoices.Add(new ChoiceItem(name, label, DriverKinds.Describe(kind), true, InvalidateRestore)
                        {
                            Driver = new DriverChoice { InfName = name, Label = label, Kind = kind },
                        });
                    }
            }
            catch (Exception ex)
            {
                if (ticket != _restoreListing) return;
                RestoreBackups.Clear();
                RestoreDriverChoices.Clear();
                _logger.Error(Category, "Les sauvegardes du support n'ont pas pu être listées.", ex);
            }

            Raise(nameof(HasRestoreChoice));
            Raise(nameof(HasRestoreDriverChoices));
            Raise(nameof(ShowRestoreDriverChoices));
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
            => !IsCopying && (Ready(_restorePreview) || Ready(_restoreDriversPreview) || Ready(_restoreApplicationsPreview) ||
                              Ready(_restorePrintersPreview));

        private static bool Ready(ActionPreview? preview) => preview != null && preview.CanExecute;

        public ICommand PrepareRestoreCommand { get; }

        public ICommand CloseProgramsForBackupCommand { get; }
        public ICommand CloseProgramsForRestoreCommand { get; }

        /// <summary>
        /// Les programmes qui tiennent les données qu'on sauvegarde ou qu'on restaure : navigateurs,
        /// messagerie, Office, Pense-bêtes.
        /// </summary>
        private static readonly string[] ClientPrograms =
        {
            "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe", "thunderbird.exe",
            "outlook.exe", "winword.exe", "excel.exe", "powerpnt.exe", "onenote.exe", "Microsoft.Notes.exe", "ms-teams.exe",
        };

        /// <summary>
        /// Ferme les navigateurs et la messagerie, puis prépare de nouveau.
        /// </summary>
        /// <remarks>
        /// Edge reste ouvert en arrière-plan sur la plupart des Windows 10 et 11, fenêtre fermée :
        /// sa restauration était alors sautée, et le technicien ne voyait aucune fenêtre à fermer.
        /// Fermés de force, processus d'arrière-plan compris : ce que le client avait en cours dans
        /// ces programmes est perdu, d'où le bouton, jamais une fermeture d'office.
        /// </remarks>
        private async Task CloseProgramsAsync(Func<Task> prepare)
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            Status = "Fermeture des navigateurs et de la messagerie…";
            var arguments = "/f";
            foreach (var program in ClientPrograms) arguments += " /im " + program;

            try
            {
                await runner.Context.Processes.RunAsync(
                    new ProcessRequest("taskkill.exe", arguments) { Timeout = TimeSpan.FromSeconds(30) },
                    CancellationToken.None).ConfigureAwait(true);

                // Le temps que Windows relâche les fichiers que ces programmes tenaient.
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Les programmes n'ont pas pu être fermés.", ex);
            }

            await prepare().ConfigureAwait(true);
        }
        public ICommand RunRestoreCommand { get; }

        private void InvalidateRestore()
        {
            _restorePreview = null;
            RaiseDurations();
            _restoreDriversPreview = null;
            _restoreApplicationsPreview = null;
            _restorePrintersPreview = null;
            RestoreLines.Clear();
            RestoreSummary = string.Empty;
            Raise(nameof(HasRestorePreview));
            RaiseBackupStates();
        }

        private IReadOnlyDictionary<string, string> RestoreParameters()
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RestoreUserDataAction.SourceParameter] = RestoreSource.Trim(),
                [RestoreUserDataAction.WifiParameter] = RestoreWifi ? "1" : "0",
            };

            if (_sourceSpeeds.TryGetValue(RootOf(RestoreSource), out var source) && _machineSpeed != null)
            {
                parameters[RestoreUserDataAction.SourceSpeedParameter] = source.Encode();
                parameters[RestoreUserDataAction.TargetSpeedParameter] = _machineSpeed.Encode();
            }

            return parameters;
        }

        private async Task PrepareRestoreAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            _restore ??= ActionCatalog.Find(ActionIds.RestoreUserData, _logger);
            if (_restore == null) return;

            IsCopying = true;
            RestoreLines.Clear();
            _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = _restorePrintersPreview = null;
            Status = "Relevé de la sauvegarde en cours. Rien n'est encore écrit sur cette machine.";
            var preparing = StartPreparation();

            try
            {
                _restorePreview = await runner
                    .PreviewAsync(_restore, RestoreParameters(), preparing)
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

                // Tout décoché vaut « pas de pilotes » : un paramètre vide se lirait « tous ».
                var drivers = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
                var anyDriver = true;
                if (HasRestoreDriverChoices)
                {
                    var names = new List<string>();
                    foreach (var item in RestoreDriverChoices) if (item.IsChecked) names.Add(item.Key);
                    drivers[RestoreDriversAction.PackagesParameter] = string.Join("\n", names);
                    anyDriver = names.Count > 0;
                }

                if (RestoreDriversChecked && anyDriver && backup != null &&
                    runner.Context.Files.DirectoryExists(System.IO.Path.Combine(backup, DriverBackup.Folder)))
                {
                    _restoreDrivers ??= ActionCatalog.Find(ActionIds.RestoreDrivers, _logger);
                    if (_restoreDrivers != null)
                    {
                        _restoreDriversPreview = await runner.PreviewAsync(_restoreDrivers, drivers, CancellationToken.None)
                            .ConfigureAwait(true);
                        RestoreLines.Add("Pilotes. " + _restoreDriversPreview.Summary);
                        Show(RestoreLines, _restoreDriversPreview);
                    }
                }

                if (RestorePrintersChecked && backup != null &&
                    runner.Context.Files.FileExists(System.IO.Path.Combine(backup, MachineSettings.FileName)))
                {
                    _restorePrinters ??= ActionCatalog.Find(ActionIds.RestorePrinters, _logger);
                    if (_restorePrinters != null)
                    {
                        var printers = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
                        {
                            [RestorePrintersAction.SessionUserParameter] = Environment.UserName,
                        };
                        _restorePrintersPreview = await runner.PreviewAsync(_restorePrinters, printers, CancellationToken.None)
                            .ConfigureAwait(true);
                        if (_restorePrintersPreview.Outcome != PreviewOutcome.NothingToDo)
                        {
                            RestoreLines.Add("Imprimantes. " + _restorePrintersPreview.Summary);
                            Show(RestoreLines, _restorePrintersPreview);
                        }
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
            catch (OperationCanceledException)
            {
                _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = _restorePrintersPreview = null;
                RestoreLines.Clear();
                Status = "Préparation annulée. Rien n'a été écrit.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la restauration a échoué.", ex);
                _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = _restorePrintersPreview = null;
                Status = "Le relevé de la restauration a échoué : " + ex.Message;
            }
            finally
            {
                EndPreparation();
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
            // La durée est affichée à part, et recalculée à l'arrivée de chaque mesure.
            // La durée d'une sauvegarde préparée ici est affichée à part, et recalculée à l'arrivée de
            // chaque mesure. Celle d'une sauvegarde préparée par l'hôte élevé n'a que cette ligne.
            foreach (var line in preview.Measurements)
                if (line.Kind == PreviewLineKind.Fact && (line.Label != "Durée estimée" || preview.Plan == null))
                    lines.Add(line.Label + " : " + line.Value + ".");
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

            await StopMeasuresAsync().ConfigureAwait(true);
            using var awake = KeepAwake.Start();

            IsCopying = true;
            Status = "Restauration en cours…";
            Mode = DataMode.Restore;
            IsRestoreTransferring = true;
            RestoreResult = null;
            StartTransferStats();
            var progress = new Progress<ActionProgress>(OnTransfer);
            var report = new List<string>();
            var summaries = new List<string>();
            var outcomes = new List<ActionOutcome>();
            string? failure = null;

            try
            {
                if (Ready(_restoreDriversPreview) && _restoreDrivers != null)
                {
                    BeginTransfer("Réinstallation des pilotes…");
                    var drivers = await runner.ExecuteAsync(_restoreDrivers, _restoreDriversPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                    outcomes.Add(drivers);
                    summaries.Add("Pilotes : " + drivers.Summary);
                    Report(report, "Pilotes", drivers);
                }

                // Après les pilotes : une imprimante réseau se recrée avec le sien.
                if (Ready(_restorePrintersPreview) && _restorePrinters != null)
                {
                    BeginTransfer("Réinstallation des imprimantes…");
                    var printers = await runner.ExecuteAsync(_restorePrinters, _restorePrintersPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                    outcomes.Add(printers);
                    summaries.Add("Imprimantes : " + printers.Summary);
                    Report(report, "Imprimantes", printers);
                }

                if (Ready(_restorePreview) && _restore != null)
                {
                    BeginTransfer("Préparation de la restauration…");
                    var data = await runner.ExecuteAsync(_restore, _restorePreview!, RestoreParameters(), progress, StartCancellable())
                        .ConfigureAwait(true);
                    outcomes.Add(data);
                    summaries.Add(data.Summary);
                    Report(report, "Données", data);
                }

                if (Ready(_restoreApplicationsPreview) && _restoreApplications != null)
                {
                    BeginTransfer("Réinstallation des applications…");
                    var applications = await runner.ExecuteAsync(
                            _restoreApplications, _restoreApplicationsPreview!, null, progress, StartCancellable())
                        .ConfigureAwait(true);
                    outcomes.Add(applications);
                    summaries.Add("Applications : " + applications.Summary);
                    Report(report, "Applications", applications);
                }

                Status = string.Join(" ", summaries);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La restauration a échoué.", ex);
                Status = "La restauration a échoué : " + ex.Message;
                failure = ex.Message;
            }
            finally
            {
                EndCancellable();
                RestoreResult = Conclude("Restauration", outcomes, SecuredLine(), failure);
                Taskbar.Finish(RestoreResult.Kind == TransferResultKind.Failed);
                RestoreFinished?.Invoke(this, new TransferFinishedEventArgs(RestoreResult, RestoreSource.Trim()));
                _restorePreview = _restoreDriversPreview = _restoreApplicationsPreview = _restorePrintersPreview = null;
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
                ScheduleMeasures();
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

        /// <summary>
        /// Emporter la clé des mots de passe de Chrome, Edge et des autres navigateurs Chromium.
        /// Décoché à chaque ouverture, comme le Wi-Fi : elle ouvre les mots de passe de la sauvegarde.
        /// </summary>
        public bool BrowserPasswords
        {
            get => _browserPasswords;
            set { if (Set(ref _browserPasswords, value)) Invalidate(); }
        }

        /// <summary>Relever les imprimantes, les lecteurs réseau et la clé de Windows. Coché par défaut.</summary>
        public bool IncludeSettings
        {
            get => _includeSettings;
            set { if (Set(ref _includeSettings, value)) Invalidate(); }
        }

        /// <summary>Types de fichiers à écarter des dossiers personnels : « iso, vhdx ».</summary>
        public string ExcludeExtensions
        {
            get => _excludeExtensions;
            set { if (Set(ref _excludeExtensions, value ?? string.Empty)) Invalidate(); }
        }

        /// <summary>Taille, en Go, au-delà de laquelle un fichier personnel est écarté. Vide : aucune.</summary>
        public string MaxFileSize
        {
            get => _maxFileSize;
            set { if (Set(ref _maxFileSize, value ?? string.Empty)) Invalidate(); }
        }

        /// <summary>Remettre les imprimantes relevées avec la sauvegarde. Demande une invite Windows.</summary>
        public bool RestorePrintersChecked
        {
            get => _restorePrintersChecked;
            set { if (Set(ref _restorePrintersChecked, value)) InvalidateRestore(); }
        }

        public ICommand OpenBackupCommand { get; }

        private void Invalidate()
        {
            _backupPreview = null;
            _driversPreview = null;
            _volumeJobs.Clear();
            RaiseDurations();
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
        public bool CanCopy => !IsCopying && (IsVolumeSource ? VolumeJobsReady : _backupPreview != null && _backupPreview.CanExecute);

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

            if (IsVolumeSource)
            {
                await PrepareVolumeAsync(runner).ConfigureAwait(true);
                return;
            }

            IsCopying = true;
            BackupLines.Clear();
            _backupPreview = _driversPreview = null;
            Status = "Relevé de la copie en cours. Aucun fichier n'est encore écrit.";
            var preparing = StartPreparation();

            try
            {
                _backupPreview = await runner
                    .PreviewAsync(_backup, BackupParameters(), preparing)
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
            catch (OperationCanceledException)
            {
                _backupPreview = _driversPreview = null;
                BackupLines.Clear();
                Status = "Préparation annulée. Rien n'a été copié.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la copie a échoué.", ex);
                _backupPreview = _driversPreview = null;
                Status = "Le relevé de la copie a échoué : " + ex.Message;
            }
            finally
            {
                EndPreparation();
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
            if (IsVolumeSource)
            {
                var volumeRunner = _runner == null ? null : await _runner().ConfigureAwait(true);
                if (volumeRunner != null && _backup != null && VolumeJobsReady) await CopyVolumeAsync(volumeRunner).ConfigureAwait(true);
                return;
            }

            var preview = _backupPreview;
            var action = _backup;
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (preview == null || action == null || runner == null) return;

            await StopMeasuresAsync().ConfigureAwait(true);

            // La machine reste éveillée jusqu'à la fin : une mise en veille couperait le disque USB.
            using var awake = KeepAwake.Start();

            IsCopying = true;
            Status = "Copie en cours…";
            Mode = DataMode.Backup;
            IsBackupTransferring = true;
            BackupResult = null;
            StartTransferStats();

            var progress = new Progress<ActionProgress>(OnTransfer);
            ActionOutcome? outcome = null;
            ActionOutcome? drivers = null;
            ActionOutcome? openFiles = null;
            string? failure = null;

            try
            {
                if (Ready(_driversPreview) && _exportDrivers != null)
                {
                    BeginTransfer("Export des pilotes…");
                    drivers = await runner.ExecuteAsync(_exportDrivers, _driversPreview!, null, progress, CancellationToken.None)
                        .ConfigureAwait(true);
                }

                BeginTransfer("Préparation de la copie…");
                var token = StartCancellable();
                outcome = await runner
                    .ExecuteAsync(action, preview, BackupParameters(), progress, token)
                    .ConfigureAwait(true);

                // Des fichiers tenus ouverts par un programme : récupérés par un cliché instantané,
                // sans rien fermer. Une invite administrateur, seulement dans ce cas.
                if (outcome.Status != ActionStatus.Cancelled && preview.Plan is BackupPlan copied && copied.OpenFiles.Count > 0)
                    openFiles = await CopyOpenFilesAsync(runner, copied, progress).ConfigureAwait(true);

                Status = outcome.Summary + (openFiles == null ? string.Empty : " Fichiers ouverts : " + openFiles.Summary) +
                         (outcome.Status == ActionStatus.Cancelled
                             ? " Relancez la sauvegarde vers le même support : elle reprendra où elle s'est arrêtée."
                             : string.Empty) +
                         (drivers == null ? string.Empty : " Pilotes : " + drivers.Summary);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La copie a échoué.", ex);
                Status = "La copie a échoué : " + ex.Message;
                failure = ex.Message;
            }
            finally
            {
                EndCancellable();

                var outcomes = new List<ActionOutcome>();
                foreach (var part in new[] { drivers, outcome, openFiles }) if (part != null) outcomes.Add(part);
                BackupResult = Conclude("Sauvegarde", outcomes, SecuredLine(), failure);
                Taskbar.Finish(BackupResult.Kind == TransferResultKind.Failed);

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
                    if (openFiles != null) Report(BackupLines, "Fichiers ouverts", openFiles);
                    if (drivers != null) Report(BackupLines, "Pilotes", drivers);
                }

                (OpenBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (OpenReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (VerifyLastBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                BackupFinished?.Invoke(this, new TransferFinishedEventArgs(BackupResult!, _lastBackupFolder));
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private IRepairAction? _openFiles;

        private async Task<ActionOutcome?> CopyOpenFilesAsync(ActionRunner runner, BackupPlan plan, IProgress<ActionProgress> progress)
        {
            _openFiles ??= ActionCatalog.Find(ActionIds.CopyOpenFiles, _logger);
            if (_openFiles == null) return null;

            BeginTransfer("Fichiers ouverts : préparation d'un cliché instantané…");
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OpenFilesAction.DestinationParameter] = plan.Destination,
                [OpenFilesAction.FilesParameter] = OpenFilesAction.Encode(plan.OpenFiles),
            };

            var preview = await runner.PreviewAsync(_openFiles, parameters, CancellationToken.None).ConfigureAwait(true);
            if (!preview.CanExecute)
                return preview.Outcome == PreviewOutcome.NothingToDo
                    ? null
                    : ActionOutcome.Simple(ActionStatus.Failed, preview.Blocker ?? preview.Summary);

            return await runner.ExecuteAsync(_openFiles, preview, parameters, progress, CancellationToken.None).ConfigureAwait(true);
        }

        private IReadOnlyDictionary<string, string> BackupParameters()
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BackupUserDataAction.DestinationParameter] = BackupDestination.Trim(),
                [BackupUserDataAction.PersonalParameter] = IncludePersonal ? "1" : "0",
                [BackupUserDataAction.ApplicationsParameter] = IncludeApplications ? "1" : "0",
                [BackupUserDataAction.WifiParameter] = ExportWifi ? "1" : "0",
                [BackupUserDataAction.BrowserPasswordsParameter] = BrowserPasswords ? "1" : "0",
                [BackupUserDataAction.SettingsParameter] = IncludeSettings ? "1" : "0",
                [BackupUserDataAction.ExcludeExtensionsParameter] = ExcludeExtensions.Trim(),
                [BackupUserDataAction.MaxFileSizeParameter] = MaxFileSize.Trim(),
                [BackupUserDataAction.DriversParameter] = CheckedDrivers().Count > 0 ? "1" : "0",
                [BackupUserDataAction.ResumeParameter] = ResumeBackup ? "1" : "0",
            };

            if (_speeds.TryGetValue(RootOf(BackupDestination), out var speed))
                parameters[BackupUserDataAction.SpeedParameter] = speed.Encode();

            if (ExtraFolders.Count > 0)
            {
                var paths = new List<string>();
                foreach (var item in ExtraFolders) paths.Add(item.Path);
                parameters[BackupUserDataAction.ExtraFoldersParameter] = LDI12.Actions.Backup.ExtraFolders.Encode(paths);
            }

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

        public ICommand OpenReportCommand { get; }

        // ---------- vérification d'une sauvegarde

        private IRepairAction? _verify;

        /// <summary>Relit la sauvegarde choisie pour la restauration, contre son manifeste.</summary>
        public ICommand VerifyRestoreSourceCommand { get; }

        /// <summary>Relit la sauvegarde qui vient d'être faite : avant d'effacer le disque du client.</summary>
        public ICommand VerifyLastBackupCommand { get; }

        /// <summary>
        /// Relit une sauvegarde sur son support et compare chaque fichier à son empreinte.
        /// </summary>
        /// <remarks>
        /// Le même tableau de bord qu'une copie : une vérification de cent gigaoctets dure aussi
        /// longtemps qu'une relecture de cent gigaoctets, et mérite le même avancement. Le bilan dit
        /// en une phrase si le support peut servir, et si le disque d'origine peut être effacé.
        /// </remarks>
        private async Task VerifyAsync(string source, DataMode mode)
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null || IsCopying || string.IsNullOrWhiteSpace(source)) return;

            _verify ??= ActionCatalog.Find(ActionIds.VerifyBackup, _logger);
            if (_verify == null) return;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VerifyBackupAction.SourceParameter] = source,
            };

            await StopMeasuresAsync().ConfigureAwait(true);
            IsCopying = true;
            Mode = mode;
            if (mode == DataMode.Backup) { IsBackupTransferring = true; BackupResult = null; }
            else { IsRestoreTransferring = true; RestoreResult = null; }
            StartTransferStats();
            BeginTransfer("Lecture du manifeste de la sauvegarde…");
            Status = "Vérification de la sauvegarde en cours : rien n'est écrit.";

            TransferResult result;
            var token = StartCancellable();
            try
            {
                var preview = await runner.PreviewAsync(_verify, parameters, token).ConfigureAwait(true);
                if (!preview.CanExecute)
                {
                    result = new TransferResult(TransferResultKind.Failed, "Vérification impossible",
                        preview.Blocker ?? preview.Summary);
                }
                else
                {
                    var outcome = await runner.ExecuteAsync(_verify, preview, parameters, new Progress<ActionProgress>(OnTransfer), token)
                        .ConfigureAwait(true);
                    result = Verified(outcome);
                    Status = outcome.Summary;

                    var lines = mode == DataMode.Backup ? BackupLines : RestoreLines;
                    lines.Clear();
                    foreach (var line in outcome.Details) lines.Add(line);
                    if (mode == DataMode.Backup) BackupSummary = "Vérification de la sauvegarde :";
                    else RestoreSummary = "Vérification de la sauvegarde :";
                }
            }
            catch (OperationCanceledException)
            {
                result = new TransferResult(TransferResultKind.Stopped, "Vérification arrêtée", "Rien n'a été modifié.");
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La vérification de la sauvegarde a échoué.", ex);
                result = new TransferResult(TransferResultKind.Failed, "La vérification a échoué", ex.Message);
            }
            finally
            {
                EndCancellable();
                IsCopying = false;
                IsBackupTransferring = false;
                IsRestoreTransferring = false;
                Raise(nameof(HasBackupPreview));
                Raise(nameof(HasRestorePreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }

            if (mode == DataMode.Backup) BackupResult = result;
            else RestoreResult = result;
            Taskbar.Finish(result.Kind == TransferResultKind.Failed);
            VerificationFinished?.Invoke(this, new TransferFinishedEventArgs(result, source));
        }

        /// <summary>Le bilan d'une vérification : ce que le technicien peut faire du support.</summary>
        internal static TransferResult Verified(ActionOutcome outcome) => outcome.Status switch
        {
            ActionStatus.Succeeded => new TransferResult(TransferResultKind.Succeeded,
                "Sauvegarde intacte : chaque fichier relu est identique", outcome.Summary),
            ActionStatus.PartiallySucceeded => new TransferResult(TransferResultKind.Partial,
                "Sauvegarde abîmée : des fichiers sont en défaut", outcome.Summary),
            ActionStatus.Cancelled => new TransferResult(TransferResultKind.Stopped, "Vérification arrêtée", outcome.Summary),
            _ => new TransferResult(TransferResultKind.Failed, "Sauvegarde inutilisable en l'état", outcome.Summary),
        };

        /// <summary>Le rapport à remettre au client, s'il a été déposé avec la dernière sauvegarde.</summary>
        private string? ReportPath
        {
            get
            {
                if (_lastBackupFolder == null) return null;
                var path = Path.Combine(_lastBackupFolder, BackupUserDataAction.ClientReportFileName);
                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// Ouvre le rapport client dans le navigateur, pour l'imprimer ou le montrer.
        /// </summary>
        /// <remarks>
        /// La sauvegarde finie, c'est le document qu'on remet : l'atteindre demandait d'ouvrir le
        /// dossier de sauvegarde et de le chercher parmi les autres.
        /// </remarks>
        private void OpenReport()
        {
            var path = ReportPath;
            if (path == null) return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le rapport client n'a pas pu être ouvert.", ex);
                Status = "Le rapport client n'a pas pu être ouvert : " + ex.Message;
            }
        }

        // ---------- barre d'action, en bas de l'écran

        /// <summary>
        /// Ce qui va partir, et où : « Ce compte, sur ce PC → E:\ ».
        /// </summary>
        /// <remarks>
        /// Le formulaire fait plusieurs écrans de haut, et ses boutons étaient au bout. La barre
        /// reste en bas de la fenêtre, où qu'on soit dans le formulaire : elle rappelle le choix
        /// fait, dit ce qui manque encore, et porte les deux boutons qui comptent.
        /// </remarks>
        public string BackupBarTitle
        {
            get
            {
                var source = _selectedSource?.Title ?? "Ce compte, sur ce PC";
                var destination = BackupDestination.Trim();
                return source + "  →  " + (destination.Length == 0 ? "support à choisir" : destination);
            }
        }

        public string BackupBarDetail
        {
            get
            {
                if (_prepareCancel != null) return "Préparation en cours : relevé des fichiers, de la place et des programmes ouverts…";
                if (string.IsNullOrWhiteSpace(BackupDestination))
                    return "Choisissez où copier, à l'étape 2 : un disque apparaît dès qu'il est branché.";
                if (CanCopy) return (HasBackupDuration ? BackupDuration + " " : string.Empty) + "Tout est prêt : lancez la copie.";
                if (_backupPreview != null && !_backupPreview.CanExecute)
                    return "La préparation a trouvé un empêchement : le détail est à l'étape 4.";
                return "Préparez la copie : durée, place et programmes ouverts sont relevés sans rien écrire.";
            }
        }

        public string RestoreBarTitle
        {
            get
            {
                var source = RestoreSource.Trim();
                return source.Length == 0 ? "Sauvegarde à choisir" : "Sauvegarde : " + source;
            }
        }

        public string RestoreBarDetail
        {
            get
            {
                if (_prepareCancel != null) return "Préparation en cours : relecture de la sauvegarde, sans rien écrire sur ce PC…";
                if (string.IsNullOrWhiteSpace(RestoreSource))
                    return "Branchez le disque de la sauvegarde : il apparaît à l'étape 1 de lui-même.";
                if (CanRestore) return (HasRestoreDuration ? RestoreDuration + " " : string.Empty) + "Tout est prêt : lancez la restauration.";
                if (_restorePreview != null && !_restorePreview.CanExecute)
                    return "La préparation a trouvé un empêchement : le détail est à l'étape 3.";
                return "Préparez la restauration : la sauvegarde est relue, rien n'est encore écrit sur ce PC.";
            }
        }

        /// <summary>Une préparation est en cours : la barre montre de quoi l'arrêter.</summary>
        public bool IsPreparing => _prepareCancel != null;

        public bool ShowBackupBar => IsBackupMode && !IsBackupTransferring;
        public bool ShowRestoreBar => IsRestoreMode && !IsRestoreTransferring;

        /// <summary>Les étapes franchies : leur pastille se coche.</summary>
        public bool HasBackupDestination => !string.IsNullOrWhiteSpace(BackupDestination);
        public bool HasRestoreSource => !string.IsNullOrWhiteSpace(RestoreSource);

        private void RaiseBar()
        {
            Raise(nameof(BackupBarTitle));
            Raise(nameof(BackupBarDetail));
            Raise(nameof(RestoreBarTitle));
            Raise(nameof(RestoreBarDetail));
            Raise(nameof(ShowBackupBar));
            Raise(nameof(ShowRestoreBar));
            Raise(nameof(HasBackupDestination));
            Raise(nameof(HasRestoreSource));
            Raise(nameof(IsPreparing));
        }

        private void RaiseBackupStates()
        {
            RaiseBar();
            Raise(nameof(CanPrepareBackup));
            Raise(nameof(CanCopy));
            Raise(nameof(CanPrepareRestore));
            Raise(nameof(CanRestore));
            (PrepareBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrepareRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CloseProgramsForBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CloseProgramsForRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (VerifyRestoreSourceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (VerifyLastBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ListApplicationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AddExtraFoldersCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseDurations();
        }

        public event EventHandler? JournalChanged;

        /// <summary>Le diagnostic sert aux réserves : il connaît les autres volumes de la machine.</summary>
        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;
            _ = LoadDriversAsync(snapshot);
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
