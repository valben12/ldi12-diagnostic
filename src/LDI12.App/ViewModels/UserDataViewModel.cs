using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                if (!Set(ref _backupDestination, value)) return;

                // Le relevé porte sur une destination précise : changer de support l'invalide.
                _backupPreview = null;
                BackupLines.Clear();
                BackupSummary = string.Empty;
                RaiseBackupStates();
            }
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

            var progress = new Progress<ActionProgress>(report => Progress = report.Text);

            try
            {
                var outcome = await runner
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
                Progress = string.Empty;

                // Le relevé est consommé : le rejouer porterait sur un état qui a changé.
                _backupPreview = null;
                BackupLines.Clear();
                BackupSummary = string.Empty;

                Raise(nameof(HasBackupPreview));
                RaiseBackupStates();
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private IReadOnlyDictionary<string, string> BackupParameters()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BackupUserDataAction.DestinationParameter] = BackupDestination.Trim(),
            };

        private void RaiseBackupStates()
        {
            Raise(nameof(CanPrepareBackup));
            Raise(nameof(CanCopy));
            (PrepareBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
