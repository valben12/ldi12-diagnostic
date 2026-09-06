using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions.Benchmarks;
using LDI12.Actions.Journal;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Benchmarks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.App.ViewModels
{
    /// <summary>Un volume mesurable, tel qu'il se choisit.</summary>
    public sealed class VolumeChoice
    {
        public string Root { get; init; } = string.Empty;

        public string Display { get; init; } = string.Empty;

        public bool IsSystem { get; init; }

        /// <summary>Ce que le disque déclare être. Confronté à ce qu'il fait réellement.</summary>
        public StorageMediaType Media { get; init; }
    }

    /// <summary>Une grandeur mesurée, telle qu'elle s'affiche.</summary>
    public sealed class BenchmarkItem
    {
        public string Label { get; init; } = string.Empty;
        public string Meaning { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
    }

    public sealed class VerdictItem
    {
        public string Statement { get; init; } = string.Empty;
        public string Explanation { get; init; } = string.Empty;
        public Severity Severity { get; init; }
    }

    /// <summary>
    /// Écran des mesures de performance.
    /// </summary>
    /// <remarks>
    /// <b>Le seul écran du logiciel qui sollicite volontairement la machine.</b> Tout le reste
    /// observe ; celui-ci charge le processeur à fond et écrit un fichier de deux cent cinquante
    /// mégaoctets. Il suit donc les règles des réparations : ce qui va être écrit, où et combien,
    /// est affiché avant le bouton qui le déclenche.
    /// <para>
    /// Ce qu'on vient y chercher n'est pas un score. C'est le temps d'un accès isolé, qui sépare
    /// un disque mécanique d'une mémoire flash par deux ordres de grandeur et explique, à lui
    /// seul, la plus grande part des machines « lentes sans raison ». Le reste des chiffres sert
    /// à comparer la machine à elle-même, avant et après.
    /// </para>
    /// </remarks>
    public sealed class MeasuresViewModel : ObservableObject
    {
        private const string Category = "Mesures";

        private readonly DiagnosticService _diagnostics;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;

        private BenchmarkRunner? _runner;
        private LDI12.Core.Execution.INativeSystemApi? _native;
        private CancellationTokenSource? _running;
        private StorageBenchmarkPlan? _plan;
        private SystemSnapshot? _snapshot;

        private VolumeChoice? _volume;
        private bool _measureStorage = true;
        private bool _measureProcessor = true;
        private bool _measureMemory = true;
        private bool _isRunning;
        private bool _hasResults;
        private string _status = "Aucune mesure n'a encore été lancée.";
        private string _planSummary = string.Empty;
        private string? _refusal;
        private string _progress = string.Empty;
        private string _headline = string.Empty;

        public MeasuresViewModel(
            DiagnosticService diagnostics, InterventionJournal journal, ILdiLogger logger)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning && CanRun);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        }

        public ObservableCollection<VolumeChoice> Volumes { get; } = new ObservableCollection<VolumeChoice>();

        public ObservableCollection<BenchmarkItem> Measures { get; } = new ObservableCollection<BenchmarkItem>();

        public ObservableCollection<VerdictItem> Verdicts { get; } = new ObservableCollection<VerdictItem>();

        public ObservableCollection<string> Caveats { get; } = new ObservableCollection<string>();

        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }

        public VolumeChoice? Volume
        {
            get => _volume;
            set { if (Set(ref _volume, value)) _ = RefreshPlanAsync(); }
        }

        /// <summary>
        /// Mesurer le disque, ou non.
        /// </summary>
        /// <remarks>
        /// Décochable, et c'est délibéré : sur une machine dont le disque donne déjà des signes de
        /// fin de vie, lui demander d'encaisser deux cent cinquante mégaoctets pour confirmer ce
        /// que le SMART annonce déjà est un mauvais calcul.
        /// </remarks>
        public bool MeasureStorage
        {
            get => _measureStorage;
            set { if (Set(ref _measureStorage, value)) { Raise(nameof(CanRun)); RaiseCommandStates(); } }
        }

        public bool MeasureProcessor
        {
            get => _measureProcessor;
            set { if (Set(ref _measureProcessor, value)) { Raise(nameof(CanRun)); RaiseCommandStates(); } }
        }

        public bool MeasureMemory
        {
            get => _measureMemory;
            set { if (Set(ref _measureMemory, value)) { Raise(nameof(CanRun)); RaiseCommandStates(); } }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set { if (Set(ref _isRunning, value)) RaiseCommandStates(); }
        }

        public bool HasResults { get => _hasResults; private set => Set(ref _hasResults, value); }

        public string Status { get => _status; private set => Set(ref _status, value); }

        /// <summary>Ce qui sera écrit, où, et ce qu'il reste de place. Affiché avant le bouton.</summary>
        public string PlanSummary { get => _planSummary; private set => Set(ref _planSummary, value); }

        /// <summary>Renseigné quand le disque ne peut pas être mesuré, jamais un bouton grisé muet.</summary>
        public string? Refusal
        {
            get => _refusal;
            private set { if (Set(ref _refusal, value)) { Raise(nameof(HasRefusal)); Raise(nameof(CanRun)); RaiseCommandStates(); } }
        }

        public bool HasRefusal => Refusal != null;

        public string Progress { get => _progress; private set => Set(ref _progress, value); }

        public string Headline { get => _headline; private set => Set(ref _headline, value); }

        public bool HasVerdicts => Verdicts.Count > 0;
        public bool HasCaveats => Caveats.Count > 0;

        /// <summary>Au moins une mesure réellement exécutable a été demandée.</summary>
        public bool CanRun => MeasureProcessor || MeasureMemory || (MeasureStorage && Refusal == null);

        public event EventHandler? JournalChanged;

        /// <summary>
        /// Reprend la liste des volumes sur le diagnostic affiché.
        /// </summary>
        /// <remarks>
        /// Les volumes viennent du diagnostic et non d'une énumération propre : c'est lui qui
        /// sait lequel porte Windows et ce que chaque disque déclare être, et c'est cette
        /// déclaration que la mesure va confronter à la réalité.
        /// </remarks>
        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;
            Volumes.Clear();

            foreach (var volume in snapshot.Storage.Volumes)
            {
                if (!volume.DriveLetter.HasValue) continue;

                var letter = volume.DriveLetter.Value;
                var system = volume.IsSystemVolume.Or(false);
                var label = volume.Label.HasValue && !string.IsNullOrWhiteSpace(volume.Label.Value)
                    ? " (" + volume.Label.Value + ")"
                    : string.Empty;

                Volumes.Add(new VolumeChoice
                {
                    Root = letter.EndsWith("\\", StringComparison.Ordinal) ? letter : letter + "\\",
                    Display = letter + label + (system ? " (Windows)" : string.Empty),
                    IsSystem = system,
                    Media = Media(snapshot, volume),
                });
            }

            if (Volume != null && Volumes.Count > 0) return;

            foreach (var choice in Volumes)
                if (choice.IsSystem) { Volume = choice; return; }

            if (Volumes.Count > 0) Volume = Volumes[0];
        }

        private static StorageMediaType Media(SystemSnapshot snapshot, VolumeInfo volume)
        {
            if (!volume.DiskIndex.HasValue) return StorageMediaType.Unknown;

            foreach (var disk in snapshot.Storage.PhysicalDisks)
                if (disk.Index == volume.DiskIndex.Value) return disk.MediaType.Or(StorageMediaType.Unknown);

            return StorageMediaType.Unknown;
        }

        private async Task RefreshPlanAsync()
        {
            var choice = Volume;
            if (choice == null) return;

            try
            {
                var runner = await RunnerAsync().ConfigureAwait(true);
                var plan = runner.Plan(choice.Root);
                _plan = plan;

                Refusal = plan.Refusal;
                PlanSummary = plan.CanRun
                    ? "La mesure écrira " + ValueFormat.Bytes(plan.Bytes) + " dans « " + plan.FilePath +
                      " », puis les relira sans passer par le cache de Windows. Le fichier est " +
                      "supprimé dès la fin, y compris si la mesure est annulée. Il reste " +
                      (plan.FreeBytes.HasValue ? ValueFormat.Bytes(plan.FreeBytes.Value) : "une place inconnue") +
                      " sur ce volume."
                    : string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Le plan de mesure n'a pas pu être établi.", ex);
                Refusal = "Le plan de mesure n'a pas pu être établi : " + ex.Message;
                PlanSummary = string.Empty;
            }
        }

        private async Task<BenchmarkRunner> RunnerAsync()
        {
            if (_runner != null) return _runner;

            var services = await _diagnostics.GetPlatformAsync(CancellationToken.None).ConfigureAwait(true);
            _native = services.Native;
            return _runner = new BenchmarkRunner(services.StorageBenchmarks, _journal, _logger);
        }

        private async Task RunAsync()
        {
            if (IsRunning) return;

            _running?.Dispose();
            _running = new CancellationTokenSource();

            IsRunning = true;
            HasResults = false;
            Measures.Clear();
            Verdicts.Clear();
            Caveats.Clear();
            Raise(nameof(HasVerdicts));
            Raise(nameof(HasCaveats));
            Status = "Mesures en cours. La machine est volontairement sollicitée : c'est normal qu'elle réponde moins bien pendant ce temps.";

            try
            {
                var runner = await RunnerAsync().ConfigureAwait(true);
                var request = Request();

                var progress = new System.Progress<string>(step => Progress = step);
                var run = await runner.RunAsync(request, progress, _running.Token).ConfigureAwait(true);

                Apply(run);
                Status = "Mesures terminées en " + ValueFormat.Duration(run.Duration) +
                         ", et consignées dans le journal d'intervention.";
                JournalChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                Status = "Mesures annulées. Le fichier de test a été supprimé.";
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Les mesures ont échoué.", ex);
                Status = "Les mesures n'ont pas pu être menées : " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                Progress = string.Empty;
            }
        }

        private BenchmarkRequest Request()
        {
            var storage = MeasureStorage ? _plan : null;

            return new BenchmarkRequest
            {
                Storage = storage != null && storage.CanRun ? storage : null,
                Processor = MeasureProcessor,
                Memory = MeasureMemory,
                DeclaredMedia = Volume?.Media ?? StorageMediaType.Unknown,
                SystemVolume = Volume?.IsSystem ?? false,
                BackgroundCpuPercent = _snapshot?.Performance.Responsiveness.CpuUsagePercent
                                       ?? Measured.NotCollected<double>(),
                OnBattery = _native?.ReadRunningOnBattery() ?? Measured.NotCollected<bool>(),
            };
        }

        private void Apply(BenchmarkRun run)
        {
            foreach (var measure in run.Measures)
                Measures.Add(new BenchmarkItem
                {
                    Label = measure.Label,
                    Meaning = measure.Meaning,
                    Display = measure.Display,
                });

            foreach (var verdict in run.Verdicts)
                Verdicts.Add(new VerdictItem
                {
                    Statement = verdict.Statement,
                    Explanation = verdict.Explanation,
                    Severity = verdict.Severity,
                });

            foreach (var caveat in run.Caveats) Caveats.Add(caveat);

            Headline = run.Headline;
            HasResults = Measures.Count > 0;
            Raise(nameof(HasVerdicts));
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

        /// <summary>Arrêt à la fermeture de la fenêtre : une mesure en cours écrit sur le disque.</summary>
        public void Shutdown()
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }
}
