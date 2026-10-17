using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Journal;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Engine.Comparison;
using LDI12.Reports.Html;
using LDI12.Reports.Interventions;

namespace LDI12.App.ViewModels
{
    /// <summary>Une pastille à cocher : un motif, une phrase toute faite.</summary>
    public sealed class ChipItem : ObservableObject
    {
        private bool _isSelected;

        public ChipItem(string text, bool selected = false)
        {
            Text = text;
            _isSelected = selected;
        }

        public string Text { get; }

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    }

    /// <summary>Une étape du fil de l'intervention : faite, en cours, ou à venir.</summary>
    public sealed class StageItem
    {
        public StageItem(InterventionStage stage, int number, InterventionStage current)
        {
            Stage = stage;
            Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Label = InterventionCase.Describe(stage);
            IsDone = stage < current;
            IsCurrent = stage == current;
        }

        public InterventionStage Stage { get; }
        public string Number { get; }
        public string Label { get; }
        public bool IsDone { get; }
        public bool IsCurrent { get; }
    }

    /// <summary>
    /// Le dossier d'intervention : un fil qui guide de l'accueil du client au rapport final.
    /// </summary>
    /// <remarks>
    /// <b>Le rapport s'écrit en avançant.</b> Chaque étape ne demande que ce qu'elle seule sait :
    /// les motifs à l'accueil, d'un clic sur des pastilles ; le diagnostic de départ, pris de
    /// lui-même dès qu'il finit ; la décision, d'un clic ; la sauvegarde, la restauration et le
    /// contrôle, relevés dès qu'ils se terminent sur leurs écrans. Le rapport final est composé
    /// et sa conclusion proposée dès le diagnostic de contrôle : le technicien relit, retouche,
    /// et imprime.
    /// <para>
    /// Le dossier vit à côté du logiciel (la clé de l'atelier) et se recopie dans la sauvegarde :
    /// après la réinstallation, le logiciel relancé le reprend à la restauration.
    /// </para>
    /// </remarks>
    public sealed class InterventionViewModel : ObservableObject
    {
        private readonly InterventionStore _store;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;
        private readonly Func<Task> _runFullScan;
        private readonly Func<SystemSnapshot?> _snapshot;

        private InterventionCase? _case;
        private string _client = string.Empty;
        private string _reference = string.Empty;
        private string _technician = string.Empty;
        private string _request = string.Empty;
        private string _note = string.Empty;
        private string _conclusion = string.Empty;
        private string _status = string.Empty;
        private string? _reportPath;

        public InterventionViewModel(
            InterventionStore store, InterventionJournal journal, ILdiLogger logger,
            Func<Task> runFullScan, Func<SystemSnapshot?> snapshot)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runFullScan = runFullScan ?? throw new ArgumentNullException(nameof(runFullScan));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

            foreach (var reason in new[]
                     {
                         "Lenteurs", "Ne démarre plus", "Virus ou publicités", "Écran bleu", "Internet ou Wi-Fi",
                         "Imprimante", "Réinstallation demandée", "Changement de PC", "Mises à jour", "Bruit ou chauffe",
                     })
                Reasons.Add(new ChipItem(reason));

            foreach (var phrase in new[]
                     {
                         "Système trop endommagé pour une réparation fiable.", "Infection persistante.",
                         "Disque remplacé.", "À la demande du client.", "Réparation possible sans réinstaller.",
                     })
                DecisionPhrases.Add(new ChipItem(phrase));

            foreach (var phrase in new[]
                     {
                         "Données vérifiées avec le client.", "Navigateurs et mots de passe retrouvés.",
                         "Messagerie fonctionnelle.", "Imprimante testée.", "Mises à jour de Windows faites.",
                         "Antivirus actif et à jour.",
                     })
                ClosingPhrases.Add(new ChipItem(phrase));

            StartCommand = new RelayCommand(Start, () => _case == null);
            NewCommand = new RelayCommand(New);
            ResumeCommand = new RelayCommand<InterventionCase>(Resume);
            UseCurrentDiagnosisCommand = new RelayCommand(UseCurrentDiagnosis, () => IsStage(InterventionStage.Diagnosis) && _snapshot() != null);
            RunDiagnosisCommand = new AsyncRelayCommand(_runFullScan, () => _case != null);
            RepairCommand = new RelayCommand(() => Decide(InterventionPath.Repair), () => IsStage(InterventionStage.Decision));
            ReinstallCommand = new RelayCommand(() => Decide(InterventionPath.Reinstall), () => IsStage(InterventionStage.Decision));
            GoBackupCommand = new RelayCommand(() => ActionNavigator.Open?.Invoke(ActionIds.BackupUserData));
            ReinstalledCommand = new RelayCommand(Reinstalled, () => IsStage(InterventionStage.Reinstall));
            GoRestoreCommand = new RelayCommand(() => ActionNavigator.Open?.Invoke(RestoreTarget));
            GoRepairsCommand = new RelayCommand(() => ActionNavigator.Open?.Invoke("REPAIRS"));
            AddNoteCommand = new RelayCommand(AddNote, () => _case != null && !string.IsNullOrWhiteSpace(Note));
            FinishCommand = new RelayCommand(Finish, () => _case != null && _case.Stage >= InterventionStage.Verification);
            OpenReportCommand = new RelayCommand(OpenReport, () => _reportPath != null);

            Refresh();
        }

        /// <summary>L'écran de restauration : ouvert par le dossier sur le mode qui convient.</summary>
        internal const string RestoreTarget = "RESTORE";

        public ObservableCollection<ChipItem> Reasons { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<ChipItem> DecisionPhrases { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<ChipItem> ClosingPhrases { get; } = new ObservableCollection<ChipItem>();
        public ObservableCollection<StageItem> Stages { get; } = new ObservableCollection<StageItem>();
        public ObservableCollection<InterventionCase> OpenCases { get; } = new ObservableCollection<InterventionCase>();
        public ObservableCollection<string> Journal { get; } = new ObservableCollection<string>();

        public ICommand StartCommand { get; }
        public ICommand NewCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand UseCurrentDiagnosisCommand { get; }
        public ICommand RunDiagnosisCommand { get; }
        public ICommand RepairCommand { get; }
        public ICommand ReinstallCommand { get; }
        public ICommand GoBackupCommand { get; }
        public ICommand ReinstalledCommand { get; }
        public ICommand GoRestoreCommand { get; }
        public ICommand GoRepairsCommand { get; }
        public ICommand AddNoteCommand { get; }
        public ICommand FinishCommand { get; }
        public ICommand OpenReportCommand { get; }

        public string Client { get => _client; set { if (Set(ref _client, value)) Sync(c => c.Client = value); } }
        public string Reference { get => _reference; set { if (Set(ref _reference, value)) Sync(c => c.Reference = value); } }
        public string Technician { get => _technician; set { if (Set(ref _technician, value)) Sync(c => c.Technician = value); } }
        public string Request { get => _request; set { if (Set(ref _request, value)) Sync(c => c.Request = value); } }
        public string Note { get => _note; set { if (Set(ref _note, value)) RaiseCommands(); } }

        public string Conclusion
        {
            get => _conclusion;
            set { if (Set(ref _conclusion, value)) Sync(c => c.Conclusion = value); }
        }

        public string Status { get => _status; private set => Set(ref _status, value); }

        public bool HasCase => _case != null;
        public bool HasNoCase => _case == null;
        public bool HasOpenCases => OpenCases.Count > 0 && _case == null;

        public string CaseTitle => _case?.Title ?? "Nouvelle intervention";

        public bool IsWelcome => _case == null || IsStage(InterventionStage.Welcome);
        public bool IsDiagnosis => IsStage(InterventionStage.Diagnosis);
        public bool IsDecision => IsStage(InterventionStage.Decision);
        public bool IsBackup => IsStage(InterventionStage.Backup);
        public bool IsReinstall => IsStage(InterventionStage.Reinstall);
        public bool IsRestore => IsStage(InterventionStage.Restore);
        public bool IsVerification => IsStage(InterventionStage.Verification);
        public bool IsDone => IsStage(InterventionStage.Done);
        public bool IsRepair => _case?.Path == InterventionPath.Repair;

        /// <summary>Ce que le diagnostic de départ a trouvé, en une ligne.</summary>
        public string DiagnosisSummary
        {
            get
            {
                var before = _case == null ? null : _store.LoadSnapshot(_case, _case.BeforeSnapshot);
                if (before == null) return string.Empty;

                int problems = 0, critical = 0;
                foreach (var finding in before.Findings)
                {
                    if (finding.Severity == Severity.Critical) critical++;
                    else if (finding.Severity == Severity.Problem) problems++;
                }

                return (before.Score == null ? string.Empty : "État général " + before.Score.Global + "/100 · ") +
                       critical + " critique(s), " + problems + " problème(s).";
            }
        }

        /// <summary>
        /// Ce que le logiciel conseille : réinstaller quand Windows lui-même est atteint.
        /// </summary>
        public string Suggestion
        {
            get
            {
                var before = _case == null ? null : _store.LoadSnapshot(_case, _case.BeforeSnapshot);
                if (before == null) return string.Empty;

                var windows = 0;
                foreach (var finding in before.Findings)
                    if (finding.Severity >= Severity.Problem &&
                        (finding.Category == DiagnosticCategory.Windows || finding.Category == DiagnosticCategory.Security))
                        windows++;

                return windows >= 3
                    ? "Conseil : plusieurs atteintes à Windows lui-même. Une réinstallation sera plus sûre qu'une suite de réparations."
                    : "Conseil : les problèmes relevés se réparent sans réinstaller.";
            }
        }

        public string ReportPath => _reportPath ?? string.Empty;

        // ------------------------------------------------------------------ étapes

        private void Start()
        {
            var intervention = new InterventionCase
            {
                Client = Client.Trim(),
                Reference = Reference.Trim(),
                Technician = Technician.Trim(),
                Request = Request.Trim(),
                Machine = _snapshot()?.Machine.MachineName ?? Environment.MachineName,
                Stage = InterventionStage.Diagnosis,
            };
            foreach (var reason in Reasons) if (reason.IsSelected) intervention.Reasons.Add(reason.Text);

            _case = intervention;
            Save();
            Status = "Dossier ouvert. Lancez le diagnostic de départ, ou gardez celui qui vient d'être fait.";
            Refresh();
        }

        private void New()
        {
            _case = null;
            Client = Reference = Request = Note = Conclusion = string.Empty;
            foreach (var chip in Reasons) chip.IsSelected = false;
            _reportPath = null;
            Status = string.Empty;
            Refresh();
        }

        private void Resume(InterventionCase? intervention)
        {
            if (intervention == null) return;
            _case = intervention;
            _client = intervention.Client;
            _reference = intervention.Reference;
            _technician = intervention.Technician;
            _request = intervention.Request;
            _conclusion = intervention.Conclusion;
            _reportPath = null;
            Status = "Dossier repris à l'étape « " + InterventionCase.Describe(intervention.Stage) + " ».";
            Refresh();
        }

        /// <summary>Le diagnostic affiché devient l'état « avant » du dossier.</summary>
        private void UseCurrentDiagnosis()
        {
            var snapshot = _snapshot();
            if (_case == null || snapshot == null) return;

            _case.BeforeSnapshot = _store.SaveSnapshot(_case, snapshot, after: false);
            _case.Machine = snapshot.Machine.MachineName;
            Advance(InterventionStage.Decision, "Diagnostic de départ enregistré. Réparer, ou réinstaller ?");
        }

        private void Decide(InterventionPath path)
        {
            if (_case == null) return;
            _case.Path = path;
            foreach (var chip in DecisionPhrases)
                if (chip.IsSelected) _case.Note(chip.Text);

            Advance(path == InterventionPath.Reinstall ? InterventionStage.Backup : InterventionStage.Verification,
                path == InterventionPath.Reinstall
                    ? "Sauvegardez les données : le dossier part avec la sauvegarde."
                    : "Faites les réparations, puis lancez le diagnostic de contrôle.");
        }

        private void Reinstalled()
            => Advance(InterventionStage.Restore, "Windows est réinstallé. Restaurez les données depuis la sauvegarde.");

        private void AddNote()
        {
            if (_case == null || string.IsNullOrWhiteSpace(Note)) return;
            _case.Note(Note);
            Note = string.Empty;
            Save();
            Refresh();
        }

        /// <summary>Compose le rapport, avec les phrases de clôture cochées, et l'ouvre.</summary>
        private void Finish()
        {
            if (_case == null) return;
            foreach (var chip in ClosingPhrases)
                if (chip.IsSelected && !_case.Notes.Exists(note => note.Text == chip.Text))
                    _case.Notes.Add(new InterventionNote { Stage = InterventionStage.Done, Text = chip.Text });

            Compose();
            OpenReport();
        }

        // ------------------------------------------------------------------ ce qui arrive des autres écrans

        /// <summary>Un diagnostic vient de finir : il sert d'état « avant » ou « après » selon l'étape.</summary>
        public void OnScanCompleted(SystemSnapshot snapshot)
        {
            if (_case == null || snapshot == null) return;

            if (_case.Stage == InterventionStage.Diagnosis)
            {
                UseCurrentDiagnosis();
            }
            else if (_case.Stage == InterventionStage.Verification)
            {
                _case.AfterSnapshot = _store.SaveSnapshot(_case, snapshot, after: true);
                CollectActions();
                _case.Stage = InterventionStage.Done;
                Compose();
                Status = "Contrôle fait : le rapport est prêt. Relisez la conclusion, puis imprimez.";
                Refresh();
            }
        }

        public void OnBackupFinished(TransferFinishedEventArgs e)
        {
            if (_case == null || _case.Stage != InterventionStage.Backup || e.Result.Kind == TransferResultKind.Failed) return;

            _case.BackupSummary = e.Result.Line;
            _case.BackupFolder = e.Folder;
            Save();
            var copied = e.Folder != null && _store.CopyInto(_case, e.Folder);
            Advance(InterventionStage.Reinstall,
                "Sauvegarde faite" + (copied ? ", dossier recopié dessus" : string.Empty) +
                ". Réinstallez Windows, puis relancez LDI12 depuis cette clé.");
        }

        public void OnVerificationFinished(TransferFinishedEventArgs e)
        {
            if (_case == null) return;
            _case.VerificationSummary = e.Result.Title + " (" + e.Result.Line + ")";
            Save();
            Refresh();
        }

        public void OnRestoreFinished(TransferFinishedEventArgs e)
        {
            if (_case == null || e.Result.Kind == TransferResultKind.Failed) return;
            if (_case.Stage != InterventionStage.Restore && _case.Stage != InterventionStage.Reinstall) return;

            _case.RestoreSummary = e.Result.Line;
            Advance(InterventionStage.Verification, "Données restaurées. Lancez le diagnostic de contrôle : le rapport se compose de lui-même.");
        }

        /// <summary>
        /// Un dossier trouvé dans une sauvegarde qu'on vient de choisir pour la restaurer.
        /// </summary>
        public void OnBackupSourceSelected(string backupFolder)
        {
            var adopted = _store.Adopt(backupFolder);
            if (adopted == null) return;
            LoadOpenCases();
            if (_case == null) Resume(adopted);
        }

        // ------------------------------------------------------------------ outils

        private void Advance(InterventionStage stage, string status)
        {
            if (_case == null) return;
            _case.Stage = stage;
            Save();
            Status = status;
            Refresh();
        }

        /// <summary>Les opérations exécutées depuis l'ouverture du dossier, relevées au journal.</summary>
        private void CollectActions()
        {
            if (_case == null) return;
            _case.Actions.Clear();
            foreach (var entry in _journal.Entries)
            {
                if (entry.Kind != InterventionKind.Execution || entry.At < _case.CreatedAt) continue;
                _case.Actions.Add(new InterventionAction
                {
                    At = entry.At,
                    Title = entry.Title,
                    Status = entry.Status,
                    Summary = entry.Summary,
                });
            }
        }

        /// <summary>Compose le rapport ; la conclusion est proposée si le technicien n'en a pas écrit.</summary>
        private void Compose()
        {
            if (_case == null) return;

            try
            {
                CollectActions();
                var before = _store.LoadSnapshot(_case, _case.BeforeSnapshot);
                var after = _store.LoadSnapshot(_case, _case.AfterSnapshot);
                var delta = before != null && after != null ? SnapshotComparer.Compare(before, after) : null;

                if (string.IsNullOrWhiteSpace(_case.Conclusion))
                {
                    _case.Conclusion = InterventionReport.DraftConclusion(_case, before, after, delta);
                    _conclusion = _case.Conclusion;
                    Raise(nameof(Conclusion));
                }

                Save();
                _reportPath = _store.SaveReport(_case, InterventionReport.Render(_case, before, after, delta));
                Raise(nameof(ReportPath));
            }
            catch (Exception ex)
            {
                _logger.Error("Intervention", "Le rapport d'intervention n'a pas pu être composé.", ex);
                Status = "Le rapport n'a pas pu être composé : " + ex.Message;
            }

            RaiseCommands();
        }

        private void OpenReport()
        {
            if (_reportPath == null) return;
            try { Process.Start(new ProcessStartInfo(_reportPath) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                _logger.Error("Intervention", "Le rapport n'a pas pu être ouvert.", ex);
                Status = "Le rapport n'a pas pu être ouvert : " + ex.Message;
            }
        }

        private void Save()
        {
            if (_case == null) return;
            try { _store.Save(_case); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _logger.Error("Intervention", "Le dossier n'a pas pu être enregistré.", ex);
                Status = "Le dossier n'a pas pu être enregistré : " + ex.Message;
            }
        }

        private void Sync(Action<InterventionCase> apply)
        {
            if (_case == null) return;
            apply(_case);
            Save();
            Raise(nameof(CaseTitle));
        }

        private bool IsStage(InterventionStage stage) => _case != null && _case.Stage == stage;

        private void LoadOpenCases()
        {
            OpenCases.Clear();
            foreach (var found in _store.List())
                if (found.Stage != InterventionStage.Done) OpenCases.Add(found);
        }

        private void Refresh()
        {
            LoadOpenCases();

            Stages.Clear();
            var shown = (_case ?? new InterventionCase { Path = InterventionPath.Reinstall }).Stages();
            var current = _case?.Stage ?? InterventionStage.Welcome;
            for (var index = 0; index < shown.Count; index++)
                Stages.Add(new StageItem(shown[index], index + 1, current));

            Journal.Clear();
            if (_case != null)
                foreach (var note in _case.Notes) Journal.Add(InterventionCase.Describe(note.Stage) + " · " + note.Text);

            foreach (var name in new[]
                     {
                         nameof(HasCase), nameof(HasNoCase), nameof(HasOpenCases), nameof(CaseTitle), nameof(IsWelcome),
                         nameof(IsDiagnosis), nameof(IsDecision), nameof(IsBackup), nameof(IsReinstall), nameof(IsRestore),
                         nameof(IsVerification), nameof(IsDone), nameof(IsRepair), nameof(DiagnosisSummary),
                         nameof(Suggestion), nameof(Client), nameof(Reference), nameof(Technician), nameof(Request),
                         nameof(Conclusion),
                     })
                Raise(name);

            RaiseCommands();
        }

        private void RaiseCommands()
        {
            foreach (var command in new[]
                     {
                         StartCommand, UseCurrentDiagnosisCommand, RepairCommand, ReinstallCommand, ReinstalledCommand,
                         AddNoteCommand, FinishCommand, OpenReportCommand,
                     })
                (command as RelayCommand)?.RaiseCanExecuteChanged();
            (RunDiagnosisCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
