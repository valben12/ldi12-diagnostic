using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
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

        /// <summary>Lignes du relevé de restauration, puis du compte rendu.</summary>
        public ObservableCollection<string> RestoreLines { get; } = new ObservableCollection<string>();

        /// <summary>Le disque ou le dossier de la sauvegarde. La plus récente qu'il contient est retenue.</summary>
        public string RestoreSource
        {
            get => _restoreSource;
            set { if (Set(ref _restoreSource, value)) InvalidateRestore(); }
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

        public bool CanRestore => !IsCopying && _restorePreview != null && _restorePreview.CanExecute;

        public ICommand PrepareRestoreCommand { get; }
        public ICommand RunRestoreCommand { get; }

        private void InvalidateRestore()
        {
            _restorePreview = null;
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
            Status = "Relevé de la sauvegarde en cours. Rien n'est encore écrit sur cette machine.";

            try
            {
                _restorePreview = await runner
                    .PreviewAsync(_restore, RestoreParameters(), CancellationToken.None)
                    .ConfigureAwait(true);

                RestoreSummary = _restorePreview.Summary;

                foreach (var line in _restorePreview.Measurements)
                    if (line.Kind == PreviewLineKind.Caution) RestoreLines.Add("Attention. " + line.Label + " : " + line.Value);
                foreach (var line in _restorePreview.WillDo) RestoreLines.Add(line);
                foreach (var line in _restorePreview.WillNotDo) RestoreLines.Add(line);

                Status = _restorePreview.Outcome == PreviewOutcome.Blocked
                    ? _restorePreview.Blocker ?? "La restauration ne peut pas être préparée."
                    : "Relevé de la sauvegarde terminé. Rien n'a encore été restauré.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la restauration a échoué.", ex);
                _restorePreview = null;
                Status = "Le relevé de la restauration a échoué : " + ex.Message;
            }
            finally
            {
                IsCopying = false;
                Raise(nameof(HasRestorePreview));
                RaiseBackupStates();
            }
        }

        private async Task RestoreAsync()
        {
            var preview = _restorePreview;
            var action = _restore;
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (preview == null || action == null || runner == null) return;

            IsCopying = true;
            Status = "Restauration en cours…";
            BeginTransfer("Préparation de la restauration…");
            IsRestoreTransferring = true;
            var progress = new Progress<ActionProgress>(OnTransfer);

            try
            {
                var outcome = await runner
                    .ExecuteAsync(action, preview, RestoreParameters(), progress, CancellationToken.None)
                    .ConfigureAwait(true);

                Status = outcome.Summary;
                _restorePreview = null;
                RestoreLines.Clear();
                RestoreSummary = "Compte rendu de la restauration :";
                foreach (var line in outcome.Details) RestoreLines.Add(line);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La restauration a échoué.", ex);
                Status = "La restauration a échoué : " + ex.Message;
                _restorePreview = null;
            }
            finally
            {
                IsCopying = false;
                IsRestoreTransferring = false;
                Raise(nameof(HasRestorePreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
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
                if (Set(ref _backupDestination, value)) Invalidate();
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

        private async Task PrepareBackupAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null) return;

            _backup ??= ActionCatalog.Find(ActionIds.BackupUserData, _logger);
            if (_backup == null) return;

            IsCopying = true;
            BackupLines.Clear();
            Status = "Relevé de la copie en cours. Aucun fichier n'est encore écrit.";

            try
            {
                _backupPreview = await runner
                    .PreviewAsync(_backup, BackupParameters(), CancellationToken.None)
                    .ConfigureAwait(true);

                BackupSummary = _backupPreview.Summary;

                // Les mises en garde d'abord : un navigateur ouvert se ferme avant de lancer la
                // copie, pas après avoir lu vingt lignes.
                foreach (var line in _backupPreview.Measurements)
                    if (line.Kind == PreviewLineKind.Caution) BackupLines.Add("Attention. " + line.Label + " : " + line.Value);

                foreach (var line in _backupPreview.WillDo) BackupLines.Add(line);
                foreach (var line in _backupPreview.WillNotDo) BackupLines.Add(line);

                Status = _backupPreview.Outcome == PreviewOutcome.Blocked
                    ? _backupPreview.Blocker ?? "La copie ne peut pas être préparée."
                    : "Relevé terminé. Rien n'a encore été copié.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le relevé de la copie a échoué.", ex);
                _backupPreview = null;
                Status = "Le relevé de la copie a échoué : " + ex.Message;
            }
            finally
            {
                IsCopying = false;
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
            }
        }

        private async Task CopyAsync()
        {
            var preview = _backupPreview;
            var action = _backup;
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (preview == null || action == null || runner == null) return;

            IsCopying = true;
            Status = "Copie en cours…";
            BeginTransfer("Préparation de la copie…");
            IsBackupTransferring = true;

            var progress = new Progress<ActionProgress>(OnTransfer);
            ActionOutcome? outcome = null;

            try
            {
                outcome = await runner
                    .ExecuteAsync(action, preview, BackupParameters(), progress, CancellationToken.None)
                    .ConfigureAwait(true);

                Status = outcome.Summary;
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
                _backupPreview = null;
                BackupLines.Clear();
                BackupSummary = string.Empty;

                if (outcome != null && preview.Plan is BackupPlan plan)
                {
                    _lastBackupFolder = plan.Destination;
                    BackupSummary = "Compte rendu de la sauvegarde, déposée dans " + plan.Destination + " :";
                    foreach (var line in outcome.Details) BackupLines.Add(line);
                }

                (OpenBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private IReadOnlyDictionary<string, string> BackupParameters()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BackupUserDataAction.DestinationParameter] = BackupDestination.Trim(),
                [BackupUserDataAction.PersonalParameter] = IncludePersonal ? "1" : "0",
                [BackupUserDataAction.ApplicationsParameter] = IncludeApplications ? "1" : "0",
                [BackupUserDataAction.WifiParameter] = ExportWifi ? "1" : "0",
            };

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
        }

        public event EventHandler? JournalChanged;

        /// <summary>Le diagnostic sert aux réserves : il connaît les autres volumes de la machine.</summary>
        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;
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
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }
}
