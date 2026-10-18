using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.Collectors.Programs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Programs;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;

namespace LDI12.App.ViewModels
{
    /// <summary>Un écran qui travaille seulement quand on le regarde : relevés en direct, lectures à la demande.</summary>
    public interface IScreenActivation
    {
        void Activated();

        void Deactivated();
    }

    /// <summary>Une ligne de processus : mise à jour en place à chaque relevé, pour ne pas faire sauter la liste.</summary>
    public sealed class ProcessRow : ObservableObject
    {
        private double _cpu;
        private long _memory;
        private int _order;
        private int _depth;
        private FileIdentity? _identity;

        public ProcessRow(ProcessEntry entry)
        {
            Id = entry.Id;
            Started = entry.StartedUtc;
            Name = entry.Name;
            Path = entry.Path;
            SessionId = entry.SessionId;
            Update(entry, 0);
        }

        public int Id { get; }
        public DateTime? Started { get; }
        public string Name { get; }
        public string? Path { get; }
        public int SessionId { get; }
        public int ParentId { get; private set; }
        public int Threads { get; private set; }
        public int Handles { get; private set; }
        public long CpuTime { get; private set; }

        public double Cpu { get => _cpu; private set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
        public long Memory { get => _memory; private set { if (Set(ref _memory, value)) Raise(nameof(MemoryText)); } }

        /// <summary>Rang dans l'arborescence : la vue s'y trie quand elle montre les parents et leurs enfants.</summary>
        public int Order { get => _order; set => Set(ref _order, value); }

        public int Depth { get => _depth; set { if (Set(ref _depth, value)) Raise(nameof(Indent)); } }

        private bool _flat;

        /// <summary>Liste triée ou filtrée : plus d'indentation, la hiérarchie n'y a plus de sens.</summary>
        public bool Flat { get => _flat; set { if (Set(ref _flat, value)) Raise(nameof(Indent)); } }

        public Thickness Indent => new Thickness(_flat ? 0 : _depth * 16, 0, 0, 0);

        public string CpuText => _cpu < 0.05 ? string.Empty : _cpu.ToString(_cpu < 10 ? "0.0" : "0", Formats.French) + " %";

        public string MemoryText => _memory <= 0 ? string.Empty : ValueFormat.Bytes(_memory);

        public FileIdentity? Identity
        {
            get => _identity;
            set
            {
                if (!Set(ref _identity, value)) return;
                foreach (var name in new[] { nameof(Description), nameof(Publisher), nameof(IsUnsigned), nameof(IsSuspicious), nameof(Signature) }) Raise(name);
            }
        }

        /// <summary>Ce que le programme dit être (« Google Chrome »), à défaut son nom.</summary>
        public string Description => _identity?.Description ?? (Path == null ? "Processus du système" : string.Empty);

        public string Publisher => _identity == null ? string.Empty : _identity.Signer ?? _identity.Company ?? string.Empty;

        public string Signature => _identity?.Describe() ?? (Path == null ? "protégé par Windows" : "vérification…");

        public bool IsUnsigned => _identity != null && _identity.Exists && _identity.Signature != SignatureState.Signed;

        /// <summary>
        /// Non signé et lancé depuis un dossier temporaire ou de profil : là où les logiciels
        /// indésirables s'installent sans demander les droits d'administrateur.
        /// </summary>
        public bool IsSuspicious => IsUnsigned && Path != null &&
            (Path.IndexOf(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 ||
             Path.IndexOf(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 ||
             Path.IndexOf(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase) >= 0 ||
             Path.IndexOf(@"\ProgramData\", StringComparison.OrdinalIgnoreCase) >= 0 && Path.Count(c => c == '\\') <= 3);

        public void Update(ProcessEntry entry, double cpu)
        {
            ParentId = entry.ParentId;
            Threads = entry.Threads;
            Handles = entry.Handles;
            CpuTime = entry.CpuTime;
            Memory = entry.PrivateBytes;
            Cpu = cpu;
        }
    }

    /// <summary>Une entrée de démarrage, avec son interrupteur.</summary>
    public sealed class AutorunRow : ObservableObject
    {
        private readonly ProgramsViewModel _owner;
        private bool _enabled;
        private bool _isBusy;
        private string _status = string.Empty;

        public AutorunRow(AutorunEntry entry, ProgramsViewModel owner)
        {
            Entry = entry;
            _owner = owner;
            _enabled = entry.Enabled;
            OpenCommand = new RelayCommand(() => ProgramsViewModel.Reveal(entry.ImagePath), () => entry.ImagePath != null && Identity?.Exists == true);
            SearchCommand = new RelayCommand(() => ProgramsViewModel.SearchOnline(Path.GetFileName(entry.ImagePath ?? entry.Name)));
        }

        public AutorunEntry Entry { get; }

        public string Name => Entry.Name;
        public string Location => Entry.Location;
        public string Command => Entry.Command;
        public string Detail => Entry.Detail ?? string.Empty;
        public bool HasDetail => !string.IsNullOrEmpty(Entry.Detail);
        public FileIdentity? Identity => Entry.Identity;

        public string Publisher => Identity?.Describe() ?? "vérification…";
        public bool IsMicrosoft => Identity?.IsMicrosoft == true;
        public bool IsMissing => Identity != null && !Identity.Exists && Entry.ImagePath != null;
        public bool IsUnsigned => Identity != null && Identity.Exists && Identity.Signature != SignatureState.Signed;

        /// <summary>
        /// Basculable : un service ou un pilote de Microsoft ne l'est pas d'ici. Les désactiver est
        /// le geste qui casse un Windows, et ce n'est jamais ce qu'on cherche en dépannage.
        /// </summary>
        public bool CanToggle => Entry.CanToggle && !_isBusy &&
            !((Entry.Kind == AutorunKind.Service || Entry.Kind == AutorunKind.Driver) && (IsMicrosoft || Identity == null));

        public bool IsEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value || !CanToggle) { Raise(); return; }
                _ = _owner.ToggleAsync(this, value);
            }
        }

        internal void Applied(bool enabled)
        {
            _enabled = enabled;
            Raise(nameof(IsEnabled));
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (Set(ref _isBusy, value)) Raise(nameof(CanToggle)); }
        }

        public string Status { get => _status; set => Set(ref _status, value); }

        public ICommand OpenCommand { get; }
        public ICommand SearchCommand { get; }

        internal void IdentityChanged()
        {
            foreach (var name in new[] { nameof(Publisher), nameof(IsMicrosoft), nameof(IsMissing), nameof(IsUnsigned), nameof(CanToggle) }) Raise(name);
        }
    }

    /// <summary>
    /// L'écran Programmes : ce qui tourne (Process Explorer) et ce qui démarre tout seul
    /// (Autoruns), avec la signature de chaque fichier et un geste réversible pour chacun.
    /// </summary>
    public sealed class ProgramsViewModel : ObservableObject, IScreenActivation
    {
        private const string Category = "Programmes";

        private readonly RepairsViewModel _repairs;
        private readonly ILdiLogger _logger;
        private readonly FileIdentitySource _identities = new FileIdentitySource();
        private readonly Dictionary<(int, DateTime?), ProcessRow> _rows = new Dictionary<(int, DateTime?), ProcessRow>();
        private readonly DispatcherTimer _timer;
        private readonly ListCollectionView _processView;
        private readonly ListCollectionView _autorunView;
        private long _lastSystemTime;
        private DateTime _lastSample;
        private bool _sampling, _autorunsLoaded, _isLoadingAutoruns, _hideMicrosoft = true, _demo;
        private string _tab = "Processes", _sort = "Tree", _filter = string.Empty, _autorunCategory = "All";
        private ProcessRow? _selected;
        private string _summary = string.Empty, _status = string.Empty, _commandLine = string.Empty, _autorunSummary = string.Empty;

        public ProgramsViewModel(RepairsViewModel repairs, ILdiLogger logger)
        {
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _processView = new ListCollectionView(Processes) { Filter = FilterProcess };
            _processView.IsLiveSorting = true;
            ApplySort();
            _autorunView = new ListCollectionView(Autoruns) { Filter = FilterAutorun };

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, __) => Sample();

            EndCommand = new AsyncRelayCommand(() => EndAsync(tree: false), () => _selected?.Path != null);
            EndTreeCommand = new AsyncRelayCommand(() => EndAsync(tree: true), () => _selected?.Path != null);
            RevealCommand = new RelayCommand(() => Reveal(_selected?.Path), () => _selected?.Path != null);
            SearchCommand = new RelayCommand(() => SearchOnline(_selected?.Name), () => _selected != null);
            RefreshAutorunsCommand = new AsyncRelayCommand(LoadAutorunsAsync, () => !IsLoadingAutoruns);
        }

        public ObservableCollection<ProcessRow> Processes { get; } = new ObservableCollection<ProcessRow>();
        public ObservableCollection<AutorunRow> Autoruns { get; } = new ObservableCollection<AutorunRow>();

        public ICollectionView ProcessView => _processView;
        public ICollectionView AutorunView => _autorunView;

        public AsyncRelayCommand EndCommand { get; }
        public AsyncRelayCommand EndTreeCommand { get; }
        public RelayCommand RevealCommand { get; }
        public RelayCommand SearchCommand { get; }
        public AsyncRelayCommand RefreshAutorunsCommand { get; }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string AutorunSummary { get => _autorunSummary; private set => Set(ref _autorunSummary, value); }

        // ------------------------------------------------------------------ onglets

        public bool IsProcessesTab { get => _tab == "Processes"; set { if (value) Tab("Processes"); } }
        public bool IsAutorunsTab { get => _tab == "Autoruns"; set { if (value) Tab("Autoruns"); } }

        private void Tab(string tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            Raise(nameof(IsProcessesTab));
            Raise(nameof(IsAutorunsTab));
            Raise(nameof(ShowsDetail));
            if (tab == "Autoruns" && !_autorunsLoaded) _ = LoadAutorunsAsync();
        }

        // ------------------------------------------------------------------ processus

        public bool IsTreeSort { get => _sort == "Tree"; set { if (value) Sort("Tree"); } }
        public bool IsCpuSort { get => _sort == "Cpu"; set { if (value) Sort("Cpu"); } }
        public bool IsMemorySort { get => _sort == "Memory"; set { if (value) Sort("Memory"); } }

        /// <summary>Les rangs ne s'indentent que dans l'arborescence.</summary>
        public bool ShowsTree => _sort == "Tree" && _filter.Length == 0;

        private void Sort(string sort)
        {
            if (_sort == sort) return;
            _sort = sort;
            foreach (var name in new[] { nameof(IsTreeSort), nameof(IsCpuSort), nameof(IsMemorySort), nameof(ShowsTree) }) Raise(name);
            ApplySort();
            Flatten();
        }

        private void ApplySort()
        {
            using (_processView.DeferRefresh())
            {
                _processView.SortDescriptions.Clear();
                _processView.LiveSortingProperties.Clear();
                var property = _sort == "Cpu" ? nameof(ProcessRow.Cpu) : _sort == "Memory" ? nameof(ProcessRow.Memory) : nameof(ProcessRow.Order);
                _processView.SortDescriptions.Add(new SortDescription(property, _sort == "Tree" ? ListSortDirection.Ascending : ListSortDirection.Descending));
                if (_sort != "Tree") _processView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Order), ListSortDirection.Ascending));
                _processView.LiveSortingProperties.Add(property);
            }
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (!Set(ref _filter, value ?? string.Empty)) return;
                Raise(nameof(ShowsTree));
                Flatten();
                _processView.Refresh();
                _autorunView.Refresh();
            }
        }

        private void Flatten()
        {
            foreach (var row in Processes) row.Flat = !ShowsTree;
        }

        private bool FilterProcess(object item)
        {
            if (_filter.Length == 0) return true;
            var row = (ProcessRow)item;
            return Contains(row.Name) || Contains(row.Path) || Contains(row.Description) || Contains(row.Publisher)
                   || row.Id.ToString(CultureInfo.InvariantCulture) == _filter.Trim();
        }

        private bool Contains(string? text) => text != null && text.IndexOf(_filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        public ProcessRow? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(HasSelection));
                Raise(nameof(ShowsDetail));
                CommandLine = value == null ? string.Empty : ProcessNative.CommandLine(value.Id) ?? (value.Path == null ? "Inaccessible : processus protégé par Windows." : "Non disponible sur ce Windows.");
                EndCommand.RaiseCanExecuteChanged();
                EndTreeCommand.RaiseCanExecuteChanged();
                RevealCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
                Raise(nameof(SelectedDetail));
            }
        }

        public bool HasSelection => _selected != null;

        public bool ShowsDetail => _selected != null && IsProcessesTab;

        public string CommandLine { get => _commandLine; private set => Set(ref _commandLine, value); }

        public string SelectedDetail
        {
            get
            {
                var row = _selected;
                if (row == null) return string.Empty;
                var parent = Processes.FirstOrDefault(p => p.Id == row.ParentId && (p.Started ?? DateTime.MinValue) <= (row.Started ?? DateTime.MaxValue));
                return "PID " + row.Id + " · " + row.Threads + " fils · " + row.Handles + " poignées"
                       + (row.SessionId == 0 ? " · session des services" : " · session " + row.SessionId)
                       + (row.Started.HasValue ? " · lancé le " + row.Started.Value.ToLocalTime().ToString("d MMM 'à' HH:mm", Formats.French) : string.Empty)
                       + (parent != null ? " · par " + parent.Name : string.Empty);
            }
        }

        public void Activated()
        {
            if (_demo) return;
            Sample();
            _timer.Start();
            if (_tab == "Autoruns" && !_autorunsLoaded) _ = LoadAutorunsAsync();
        }

        public void Deactivated() => _timer.Stop();

        /// <summary>Un relevé : les lignes existantes sont mises à jour, les nouvelles ajoutées, les disparues retirées.</summary>
        private async void Sample()
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                var (entries, systemTime) = await Task.Run(() => (ProcessNative.Snapshot(), ProcessNative.SystemTime())).ConfigureAwait(true);
                if (_demo) return; // les captures montrent l'exemple, pas ce PC
                var now = DateTime.UtcNow;
                var elapsed = _lastSystemTime > 0 ? systemTime - _lastSystemTime : 0;
                _lastSystemTime = systemTime;
                _lastSample = now;

                var seen = new HashSet<(int, DateTime?)>();
                var fresh = new List<ProcessRow>();
                foreach (var entry in entries)
                {
                    var key = (entry.Id, entry.StartedUtc);
                    seen.Add(key);
                    if (_rows.TryGetValue(key, out var row))
                    {
                        var cpu = elapsed > 0 ? Math.Max(0, Math.Min(100, (entry.CpuTime - row.CpuTime) * 100.0 / elapsed)) : 0;
                        row.Update(entry, entry.Id == 0 ? 0 : cpu);
                    }
                    else
                    {
                        row = new ProcessRow(entry) { Flat = !ShowsTree };
                        _rows[key] = row;
                        Processes.Add(row);
                        fresh.Add(row);
                    }
                }

                foreach (var gone in _rows.Where(r => !seen.Contains(r.Key)).ToList())
                {
                    _rows.Remove(gone.Key);
                    Processes.Remove(gone.Value);
                    if (ReferenceEquals(gone.Value, _selected)) Selected = null;
                }

                Arrange();
                Identify(fresh);

                var cpuTotal = Processes.Where(p => p.Id != 0).Sum(p => p.Cpu);
                Summary = Processes.Count + " processus · processeur " + Math.Min(100, cpuTotal).ToString("0", Formats.French) + " % · mémoire engagée "
                          + ValueFormat.Bytes(Processes.Sum(p => p.Memory));
                Raise(nameof(SelectedDetail));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Status = "Relevé des processus impossible : " + ex.Message;
            }
            finally
            {
                _sampling = false;
            }
        }

        /// <summary>Range les processus sous leur parent, dans l'ordre de leur lancement.</summary>
        private void Arrange()
        {
            var byId = Processes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            var children = new Dictionary<int, List<ProcessRow>>();
            var roots = new List<ProcessRow>();
            foreach (var row in Processes)
            {
                // Un numéro de parent peut avoir été réattribué : le « parent » doit être né avant l'enfant.
                if (row.Id != row.ParentId && byId.TryGetValue(row.ParentId, out var parent) && row.Id != 0 &&
                    (parent.Started ?? DateTime.MinValue) <= (row.Started ?? DateTime.MaxValue))
                {
                    if (!children.TryGetValue(parent.Id, out var list)) children[parent.Id] = list = new List<ProcessRow>();
                    list.Add(row);
                }
                else roots.Add(row);
            }

            var order = 0;
            void Walk(ProcessRow row, int depth, HashSet<int> path)
            {
                row.Order = order++;
                row.Depth = depth;
                if (!children.TryGetValue(row.Id, out var list) || !path.Add(row.Id)) return;
                foreach (var child in list.OrderBy(c => c.Started ?? DateTime.MinValue)) Walk(child, Math.Min(depth + 1, 12), path);
                path.Remove(row.Id);
            }
            foreach (var root in roots.OrderBy(r => r.Started ?? DateTime.MinValue)) Walk(root, 0, new HashSet<int>());
        }

        /// <summary>Les signatures se vérifient en arrière-plan : quelques dizaines de millisecondes chacune.</summary>
        private void Identify(IReadOnlyList<ProcessRow> rows)
        {
            var pending = rows.Where(r => r.Path != null && r.Identity == null).ToList();
            if (pending.Count == 0) return;
            _ = Task.Run(() =>
            {
                foreach (var row in pending)
                {
                    var identity = _identities.Identify(row.Path!);
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => row.Identity = identity));
                }
            });
        }

        private async Task EndAsync(bool tree)
        {
            var row = _selected;
            if (row == null) return;

            var targets = new List<ProcessRow> { row };
            if (tree)
            {
                for (var index = 0; index < targets.Count; index++)
                    targets.AddRange(Processes.Where(p => p.ParentId == targets[index].Id && p.Id != targets[index].Id &&
                                                          (p.Started ?? DateTime.MaxValue) >= (targets[index].Started ?? DateTime.MinValue) && !targets.Contains(p)));
            }

            var question = tree && targets.Count > 1
                ? "Terminer « " + row.Name + " » et les " + (targets.Count - 1) + " processus qu'il a lancés ?"
                : "Terminer « " + row.Name + " » ?";
            var owner = Application.Current?.MainWindow;
            var answer = owner == null
                ? MessageBox.Show(question + "\n\nCe qui n'était pas enregistré sera perdu.", "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(owner, question + "\n\nCe qui n'était pas enregistré sera perdu.", "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var parameters = new Dictionary<string, string>
            {
                [ProcessEndAction.IdsParameter] = string.Join(",", targets.Select(t => t.Id.ToString(CultureInfo.InvariantCulture))),
                [ProcessEndAction.NamesParameter] = string.Join("|", targets.Select(t => t.Name)),
            };
            var outcome = await _repairs.RunQuietAsync(ActionIds.ProcessEnd, parameters, null, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Succeeded && outcome.Summary.IndexOf("administrateur", StringComparison.OrdinalIgnoreCase) >= 0)
                outcome = await _repairs.RunQuietAsync(ActionIds.ProcessEndAdmin, parameters, null, CancellationToken.None).ConfigureAwait(true);

            Status = outcome.Summary;
            _logger.Info(Category, "Fin de " + row.Name + " : " + outcome.Summary);
            Sample();
        }

        // ------------------------------------------------------------------ démarrage

        public bool IsLoadingAutoruns
        {
            get => _isLoadingAutoruns;
            private set { if (Set(ref _isLoadingAutoruns, value)) RefreshAutorunsCommand.RaiseCanExecuteChanged(); }
        }

        public bool HideMicrosoft
        {
            get => _hideMicrosoft;
            set
            {
                if (!Set(ref _hideMicrosoft, value)) return;
                _autorunView.Refresh();
                CountAutoruns();
            }
        }

        public bool IsAllCategory { get => _autorunCategory == "All"; set { if (value) Pick("All"); } }
        public bool IsLogonCategory { get => _autorunCategory == nameof(AutorunCategory.Logon); set { if (value) Pick(nameof(AutorunCategory.Logon)); } }
        public bool IsTaskCategory { get => _autorunCategory == nameof(AutorunCategory.Task); set { if (value) Pick(nameof(AutorunCategory.Task)); } }
        public bool IsServiceCategory { get => _autorunCategory == nameof(AutorunCategory.Service); set { if (value) Pick(nameof(AutorunCategory.Service)); } }
        public bool IsDriverCategory { get => _autorunCategory == nameof(AutorunCategory.Driver); set { if (value) Pick(nameof(AutorunCategory.Driver)); } }
        public bool IsExplorerCategory { get => _autorunCategory == nameof(AutorunCategory.Explorer); set { if (value) Pick(nameof(AutorunCategory.Explorer)); } }
        public bool IsSensitiveCategory { get => _autorunCategory == nameof(AutorunCategory.Sensitive); set { if (value) Pick(nameof(AutorunCategory.Sensitive)); } }

        public string AllLabel { get; private set; } = "Tout";
        public string LogonLabel { get; private set; } = "Session";
        public string TaskLabel { get; private set; } = "Tâches";
        public string ServiceLabel { get; private set; } = "Services";
        public string DriverLabel { get; private set; } = "Pilotes";
        public string ExplorerLabel { get; private set; } = "Explorateur";
        public string SensitiveLabel { get; private set; } = "Points sensibles";

        private void Pick(string category)
        {
            if (_autorunCategory == category) return;
            _autorunCategory = category;
            foreach (var name in new[] { nameof(IsAllCategory), nameof(IsLogonCategory), nameof(IsTaskCategory), nameof(IsServiceCategory), nameof(IsDriverCategory), nameof(IsExplorerCategory), nameof(IsSensitiveCategory) })
                Raise(name);
            _autorunView.Refresh();
        }

        private bool FilterAutorun(object item)
        {
            var row = (AutorunRow)item;
            if (_hideMicrosoft && row.IsMicrosoft) return false;
            if (_autorunCategory != "All" && row.Entry.Category.ToString() != _autorunCategory) return false;
            return _filter.Length == 0 || Contains(row.Name) || Contains(row.Command) || Contains(row.Publisher) || Contains(row.Location);
        }

        public async Task LoadAutorunsAsync()
        {
            if (IsLoadingAutoruns) return;
            IsLoadingAutoruns = true;
            AutorunSummary = "Relevé de ce qui démarre tout seul…";
            try
            {
                var runner = await _repairs.GetRunnerAsync().ConfigureAwait(true);
                if (runner == null) return;
                var collector = new AutorunsCollector(runner.Context.Registry, runner.Context.Platform.Profile.Build);
                var entries = await Task.Run(() =>
                {
                    var list = collector.Collect(CancellationToken.None);
                    foreach (var entry in list)
                        entry.Identity = entry.ImagePath == null ? null : _identities.Identify(entry.ImagePath);
                    return list;
                }).ConfigureAwait(true);
                if (_demo) return;

                Autoruns.Clear();
                foreach (var entry in entries.OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                    Autoruns.Add(new AutorunRow(entry, this));
                _autorunsLoaded = true;
                CountAutoruns();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.Error(Category, "Relevé du démarrage impossible.", ex);
                AutorunSummary = "Relevé impossible : " + ex.Message;
            }
            finally
            {
                IsLoadingAutoruns = false;
            }
        }

        private void CountAutoruns()
        {
            var visible = Autoruns.Where(r => !_hideMicrosoft || !r.IsMicrosoft).ToList();
            string Label(string title, AutorunCategory? category)
            {
                var count = category == null ? visible.Count : visible.Count(r => r.Entry.Category == category);
                return count == 0 ? title : title + " " + count;
            }

            AllLabel = Label("Tout", null);
            LogonLabel = Label("Session", AutorunCategory.Logon);
            TaskLabel = Label("Tâches", AutorunCategory.Task);
            ServiceLabel = Label("Services", AutorunCategory.Service);
            DriverLabel = Label("Pilotes", AutorunCategory.Driver);
            ExplorerLabel = Label("Explorateur", AutorunCategory.Explorer);
            SensitiveLabel = Label("Points sensibles", AutorunCategory.Sensitive);
            foreach (var name in new[] { nameof(AllLabel), nameof(LogonLabel), nameof(TaskLabel), nameof(ServiceLabel), nameof(DriverLabel), nameof(ExplorerLabel), nameof(SensitiveLabel) })
                Raise(name);

            var unsigned = visible.Count(r => r.IsUnsigned);
            var missing = visible.Count(r => r.IsMissing);
            var off = visible.Count(r => !r.IsEnabled);
            AutorunSummary = visible.Count + " entrée(s)" + (_hideMicrosoft ? " hors Microsoft" : string.Empty)
                             + (off > 0 ? " · " + off + " désactivée(s)" : string.Empty)
                             + (unsigned > 0 ? " · " + unsigned + " non signée(s)" : string.Empty)
                             + (missing > 0 ? " · " + missing + " vers un fichier introuvable" : string.Empty) + ".";
        }

        internal async Task ToggleAsync(AutorunRow row, bool enable)
        {
            row.IsBusy = true;
            row.Status = enable ? "Réactivation…" : "Désactivation…";
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    [AutorunToggleAction.KindParameter] = row.Entry.Kind.ToString(),
                    [AutorunToggleAction.KeyParameter] = row.Entry.Key,
                    [AutorunToggleAction.HiveParameter] = row.Entry.Hive ?? "HKLM",
                    [AutorunToggleAction.NameParameter] = row.Entry.Name,
                    [AutorunToggleAction.EnableParameter] = enable ? "1" : "0",
                };

                // Ce qui appartient à l'utilisateur se règle sans invite ; le reste par l'hôte élevé.
                var userOwned = row.Entry.Hive == "HKCU" || row.Entry.Kind == AutorunKind.ScheduledTask;
                var outcome = await _repairs.RunQuietAsync(userOwned ? ActionIds.AutorunToggle : ActionIds.AutorunToggleAdmin,
                    parameters, null, CancellationToken.None).ConfigureAwait(true);
                if (userOwned && !outcome.Succeeded && row.Entry.Kind == AutorunKind.ScheduledTask)
                    outcome = await _repairs.RunQuietAsync(ActionIds.AutorunToggleAdmin, parameters, null, CancellationToken.None).ConfigureAwait(true);

                if (outcome.Succeeded) row.Applied(enable);
                row.Status = outcome.Succeeded ? string.Empty : outcome.Summary;
                Status = outcome.Summary;
                CountAutoruns();
            }
            finally
            {
                row.IsBusy = false;
                row.Applied(row.IsEnabled);
            }
        }

        // ------------------------------------------------------------------ gestes communs

        internal static void Reveal(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
        }

        internal static void SearchOnline(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(name + " processus")) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
        }

        /// <summary>Une liste d'exemple, pour les captures d'écran seulement.</summary>
        public void ShowDemo(string tab)
        {
            _demo = true;
            _timer.Stop();
            FileIdentity Id(string path, string company, string description, bool signed = true) => new FileIdentity
            {
                Path = path, Exists = true, Company = company, Description = description,
                Signature = signed ? SignatureState.Signed : SignatureState.Unsigned, Signer = signed ? company : null,
            };

            ProcessRow Row(int id, int parent, string name, string? path, long memory, double cpu, FileIdentity? identity, int minutes)
            {
                var row = new ProcessRow(new ProcessEntry
                {
                    Id = id, ParentId = parent, Name = name, Path = path, PrivateBytes = memory, Threads = 12, Handles = 340,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-minutes), SessionId = id < 1000 ? 0 : 1,
                }) { Identity = identity };
                row.Update(new ProcessEntry { Id = id, ParentId = parent, PrivateBytes = memory, Threads = 12, Handles = 340 }, cpu);
                return row;
            }

            Processes.Clear();
            _rows.Clear();
            var rows = new[]
            {
                Row(4, 0, "System", null, 200_000, 0.4, null, 600),
                Row(684, 4, "services.exe", @"C:\Windows\System32\services.exe", 6_200_000, 0, Id(@"C:\Windows\System32\services.exe", "Microsoft Windows", "Applications services et contrôleur"), 599),
                Row(1240, 684, "svchost.exe", @"C:\Windows\System32\svchost.exe", 24_500_000, 0.2, Id(@"C:\Windows\System32\svchost.exe", "Microsoft Windows", "Processus hôte pour les services Windows"), 598),
                Row(5120, 4900, "explorer.exe", @"C:\Windows\explorer.exe", 98_000_000, 1.1, Id(@"C:\Windows\explorer.exe", "Microsoft Windows", "Explorateur Windows"), 590),
                Row(7200, 5120, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 412_000_000, 6.4, Id(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", "Google Chrome"), 120),
                Row(7340, 7200, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 188_000_000, 2.1, Id(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", "Google Chrome"), 119),
                Row(8120, 5120, "OUTLOOK.EXE", @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", 236_000_000, 0.8, Id(@"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", "Microsoft Corporation", "Microsoft Outlook"), 95),
                Row(9050, 5120, "updater.exe", @"C:\Users\Marie\AppData\Roaming\WebHelper\updater.exe", 38_000_000, 23.5, Id(@"C:\Users\Marie\AppData\Roaming\WebHelper\updater.exe", "WebHelper", "Web Helper", signed: false), 88),
            };
            foreach (var row in rows)
            {
                _rows[(row.Id, row.Started)] = row;
                Processes.Add(row);
            }
            Arrange();
            Summary = "186 processus · processeur 34 % · mémoire engagée 6,2 Go";

            Autoruns.Clear();
            AutorunEntry Entry(AutorunCategory category, AutorunKind kind, string name, string location, string command, FileIdentity? identity, bool enabled = true, string? detail = null)
                => new AutorunEntry { Category = category, Kind = kind, Name = name, Location = location, Command = command, ImagePath = identity?.Path, Enabled = enabled, Detail = detail, Identity = identity, CanToggle = true, Key = name, Hive = "HKCU" };
            foreach (var entry in new[]
            {
                Entry(AutorunCategory.Logon, AutorunKind.RunKey, "WebHelper", "HKCU\\…\\Run", "\"C:\\Users\\Marie\\AppData\\Roaming\\WebHelper\\updater.exe\" /silent", Id(@"C:\Users\Marie\AppData\Roaming\WebHelper\updater.exe", "WebHelper", "Web Helper", signed: false)),
                Entry(AutorunCategory.Logon, AutorunKind.RunKey, "Spotify", "HKCU\\…\\Run", "\"C:\\Users\\Marie\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart", Id(@"C:\Users\Marie\AppData\Roaming\Spotify\Spotify.exe", "Spotify AB", "Spotify"), enabled: false),
                Entry(AutorunCategory.Logon, AutorunKind.RunKey, "OneDrive", "HKCU\\…\\Run", "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background", Id(@"C:\Program Files\Microsoft OneDrive\OneDrive.exe", "Microsoft Corporation", "Microsoft OneDrive")),
                Entry(AutorunCategory.Logon, AutorunKind.StartupFolder, "Imprimante HP", "Démarrage de l'utilisateur", @"C:\Program Files\HP\HP Smart\hpqtra08.exe", new FileIdentity { Path = @"C:\Program Files\HP\HP Smart\hpqtra08.exe", Exists = false, Signature = SignatureState.Missing }),
                Entry(AutorunCategory.Task, AutorunKind.ScheduledTask, "GoogleUpdateTaskMachineUA", "Planificateur : \\GoogleUpdateTaskMachineUA", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", Id(@"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google LLC", "Google Installer"), detail: "à l'ouverture de session"),
                Entry(AutorunCategory.Service, AutorunKind.Service, "Adobe Acrobat Update Service", "Service AdobeARMservice", @"C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\armsvc.exe", Id(@"C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\armsvc.exe", "Adobe Inc.", "Adobe Acrobat Update Service"), detail: "automatique"),
                Entry(AutorunCategory.Explorer, AutorunKind.ShellExtension, "7-Zip", "Menu contextuel des fichiers", @"C:\Program Files\7-Zip\7-zip.dll", Id(@"C:\Program Files\7-Zip\7-zip.dll", "Igor Pavlov", "7-Zip Shell Extension", signed: false)),
            })
                Autoruns.Add(new AutorunRow(entry, this));
            _autorunsLoaded = true;
            CountAutoruns();
            if (tab == "autoruns") IsAutorunsTab = true;
        }
    }
}
