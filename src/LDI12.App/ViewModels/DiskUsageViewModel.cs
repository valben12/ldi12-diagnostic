using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LDI12.App.Controls;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Diagnostics;
using LDI12.Core.DiskUsage;
using LDI12.Core.Logging;
using LDI12.Platform.Native;

namespace LDI12.App.ViewModels
{
    /// <summary>Un volume proposé à l'analyse.</summary>
    public sealed class DiskDriveItem
    {
        public DiskDriveItem(string root, string title, long total, long free, ICommand select)
        {
            Root = root;
            Title = title;
            Total = total;
            Free = free;
            SelectCommand = select;
        }

        public string Root { get; }

        public string Title { get; }

        public long Total { get; }

        public long Free { get; }

        public double UsedShare => Total > 0 ? (double)(Total - Free) / Total : 0;

        public string SpaceText => ValueFormat.Bytes(Total - Free) + " utilisés sur " + ValueFormat.Bytes(Total);

        public ICommand SelectCommand { get; }
    }

    /// <summary>Une ligne de l'arborescence, à plat : l'indentation dit la profondeur.</summary>
    public sealed class DiskRow : ObservableObject
    {
        private bool _isExpanded;

        public DiskRow(DiskNode node, int depth, DiskUsageTree tree)
        {
            Node = node;
            Depth = depth;
            var stat = tree.ExtensionOf(node);
            Swatch = node.IsDirectory ? null : Brush(TreemapView.PaletteColor(stat?.Color ?? -1));
        }

        /// <summary>La ligne « … et 1 234 autres » au bout d'un dossier trop fourni.</summary>
        public DiskRow(DiskNode parent, int depth, int hidden, long hiddenBytes)
        {
            Node = parent;
            Depth = depth;
            MoreText = "… et " + ValueFormat.Number(hidden) + " autres éléments, " + ValueFormat.Bytes(hiddenBytes);
        }

        public DiskNode Node { get; }

        public int Depth { get; }

        public bool IsMore => MoreText != null;

        public string? MoreText { get; }

        public Thickness Indent => new Thickness(Depth * 18, 0, 0, 0);

        /// <summary>La racine par son dernier élément : « Git » plutôt que « C:\Program Files\Git ».</summary>
        public string Name
            => Node.Parent != null ? Node.Name
                : System.IO.Path.GetFileName(Node.Name.TrimEnd('\\')) is { Length: > 0 } last ? last : Node.Name;

        public bool IsDirectory => Node.IsDirectory && !IsMore;

        public bool CanExpand => IsDirectory && Node.HasChildren;

        public Brush? Swatch { get; }

        public string SizeText => ValueFormat.Bytes(Node.Size);

        /// <summary>Part du dossier parent, pour la barre.</summary>
        public double Share => Node.Parent != null && Node.Parent.Size > 0 ? (double)Node.Size / Node.Parent.Size : 1;

        public string ShareText => Math.Round(Share * 100, Share < 0.1 ? 1 : 0) + " %";

        public string FilesText => Node.IsDirectory ? ValueFormat.Number(Node.Files) + " fichiers" : string.Empty;

        public bool IsUnreadable => Node.Unreadable;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public void Refresh()
        {
            Raise(nameof(SizeText));
            Raise(nameof(Share));
            Raise(nameof(ShareText));
            Raise(nameof(FilesText));
            Raise(nameof(CanExpand));
        }

        internal static Brush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Un type de fichier dans la légende.</summary>
    public sealed class ExtensionRow
    {
        public ExtensionRow(ExtensionStat stat, long total)
        {
            Extension = string.IsNullOrEmpty(stat.Extension) ? "(sans extension)" : stat.Extension;
            Kind = stat.Kind;
            Swatch = DiskRow.Brush(TreemapView.PaletteColor(stat.Color));
            SizeText = ValueFormat.Bytes(stat.Bytes);
            FilesText = ValueFormat.Number(stat.Files) + " fichier" + (stat.Files > 1 ? "s" : "");
            Share = total > 0 ? (double)stat.Bytes / total : 0;
            ShareText = Math.Round(Share * 100, Share < 0.1 ? 1 : 0) + " %";
        }

        public string Extension { get; }

        public string Kind { get; }

        public Brush Swatch { get; }

        public string SizeText { get; }

        public string FilesText { get; }

        public double Share { get; }

        public string ShareText { get; }
    }

    /// <summary>Un fichier de la liste des plus gros, ou une piste de place à regagner.</summary>
    public sealed class DiskPick
    {
        public DiskPick(DiskNode node, string title, string detail, Brush? swatch, ICommand show)
        {
            Node = node;
            Title = title;
            Detail = detail;
            Swatch = swatch;
            ShowCommand = show;
        }

        public DiskNode Node { get; }

        public string Title { get; }

        public string Detail { get; }

        public string SizeText => ValueFormat.Bytes(Node.Size);

        public Brush? Swatch { get; }

        public bool HasSwatch => Swatch != null;

        public ICommand ShowCommand { get; }
    }

    /// <summary>Un cran du fil d'Ariane de la carte.</summary>
    public sealed class DiskCrumb
    {
        public DiskCrumb(DiskNode node, bool isLast, ICommand go)
        {
            Node = node;
            IsLast = isLast;
            GoCommand = go;
        }

        public DiskNode Node { get; }

        public string Name => Node.Parent == null ? Node.Name.TrimEnd('\\') : Node.Name;

        public bool IsLast { get; }

        public ICommand GoCommand { get; }
    }

    /// <summary>
    /// L'analyse du disque : où est passée la place, en une carte et trois listes.
    /// </summary>
    /// <remarks>
    /// Le pendant de WinDirStat, en plus rapide (lecture native sur plusieurs fils) et plus
    /// sûr : rien ne part autrement qu'à la corbeille, et jamais un fichier du système. Les
    /// pistes connues (ancienne installation, corbeille, temporaires) sont signalées d'office.
    /// </remarks>
    public sealed class DiskUsageViewModel : ObservableObject
    {
        private const string Category = "DiskUsage";

        /// <summary>Au-delà, un dossier ne montre que ses plus gros éléments : les suivants tiennent sur une ligne.</summary>
        private const int RowsPerFolder = 200;

        private readonly ILdiLogger _logger;
        private readonly DispatcherTimer _ticker;
        private readonly HashSet<DiskNode> _expanded = new HashSet<DiskNode>();
        private CancellationTokenSource? _scan;
        private DiskUsageScanner? _scanner;
        private long? _expectedBytes;
        private string _stage = "Choose";
        private string _scanTarget = string.Empty;
        private string _scanFiles = string.Empty;
        private string _scanBytes = string.Empty;
        private string _scanCurrent = string.Empty;
        private double _scanShare;
        private bool _scanShareKnown;
        private DiskUsageTree? _tree;
        private DiskNode? _mapRoot;
        private DiskNode? _selected;
        private DiskNode? _hovered;
        private DiskRow? _selectedRow;
        private int _revision;
        private int _view;
        private string _status = string.Empty;
        private bool _syncing;

        public DiskUsageViewModel(ILdiLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _ticker.Tick += (_, __) => Tick();

            ChooseFolderCommand = new RelayCommand(ChooseFolder);
            CancelCommand = new RelayCommand(() => _scan?.Cancel());
            NewAnalysisCommand = new RelayCommand(() =>
            {
                RefreshDrives();
                Stage = "Choose";
            });
            RescanCommand = new RelayCommand(() =>
            {
                if (_tree != null) _ = ScanAsync(_tree.Root.Name);
            });
            ToggleCommand = new RelayCommand<DiskRow>(Toggle);
            ZoomCommand = new RelayCommand<DiskNode>(node =>
            {
                if (node != null && node.IsDirectory) MapRoot = node;
            });
            ZoomOutCommand = new RelayCommand(() =>
            {
                if (_mapRoot?.Parent != null) MapRoot = _mapRoot.Parent;
            });
            ZoomSelectedCommand = new RelayCommand(() =>
            {
                var folder = _selected == null ? null : _selected.IsDirectory ? _selected : _selected.Parent;
                if (folder != null) MapRoot = folder;
            });
            ShowCommand = new RelayCommand<DiskNode>(node =>
            {
                if (node != null) Selected = node;
            });
            OpenLocationCommand = new RelayCommand(OpenLocation);
            CopyPathCommand = new RelayCommand(() =>
            {
                if (_selected == null) return;
                try { Clipboard.SetText(_selected.FullPath); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            });
            RecycleCommand = new AsyncRelayCommand(RecycleAsync, () => _selected != null && Refusal == null);

            RefreshDrives();
        }

        public ObservableCollection<DiskDriveItem> Drives { get; } = new ObservableCollection<DiskDriveItem>();

        public ObservableCollection<DiskRow> Rows { get; } = new ObservableCollection<DiskRow>();

        public ObservableCollection<ExtensionRow> Extensions { get; } = new ObservableCollection<ExtensionRow>();

        public ObservableCollection<DiskPick> Largest { get; } = new ObservableCollection<DiskPick>();

        public ObservableCollection<DiskPick> Hints { get; } = new ObservableCollection<DiskPick>();

        public ObservableCollection<DiskCrumb> Crumbs { get; } = new ObservableCollection<DiskCrumb>();

        public ICommand ChooseFolderCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand NewAnalysisCommand { get; }
        public ICommand RescanCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand ZoomCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomSelectedCommand { get; }
        public ICommand ShowCommand { get; }
        public ICommand OpenLocationCommand { get; }
        public ICommand CopyPathCommand { get; }
        public AsyncRelayCommand RecycleCommand { get; }

        /// <summary>« Choose », « Scanning » ou « Ready ».</summary>
        public string Stage
        {
            get => _stage;
            private set
            {
                if (!Set(ref _stage, value)) return;
                Raise(nameof(IsChoosing));
                Raise(nameof(IsScanning));
                Raise(nameof(IsReady));
            }
        }

        public bool IsChoosing => _stage == "Choose";
        public bool IsScanning => _stage == "Scanning";
        public bool IsReady => _stage == "Ready";

        /// <summary>Vrai pendant l'exploration : la fenêtre demande confirmation avant de fermer.</summary>
        public bool IsBusy => IsScanning;

        public string ScanTarget { get => _scanTarget; private set => Set(ref _scanTarget, value); }
        public string ScanFiles { get => _scanFiles; private set => Set(ref _scanFiles, value); }
        public string ScanBytes { get => _scanBytes; private set => Set(ref _scanBytes, value); }
        public string ScanCurrent { get => _scanCurrent; private set => Set(ref _scanCurrent, value); }
        public double ScanShare { get => _scanShare; private set => Set(ref _scanShare, value); }
        public bool ScanShareKnown { get => _scanShareKnown; private set => Set(ref _scanShareKnown, value); }

        public string Status { get => _status; private set => Set(ref _status, value); }

        public DiskUsageTree? Tree
        {
            get => _tree;
            private set => Set(ref _tree, value);
        }

        /// <summary>Le dossier qu'affiche la carte : la racine, ou celui où l'on est entré.</summary>
        public DiskNode? MapRoot
        {
            get => _mapRoot;
            set
            {
                if (!Set(ref _mapRoot, value)) return;
                Crumbs.Clear();
                var chain = new List<DiskNode>();
                for (var node = value; node != null; node = node.Parent) chain.Insert(0, node);
                for (var index = 0; index < chain.Count; index++)
                    Crumbs.Add(new DiskCrumb(chain[index], index == chain.Count - 1, ZoomCommand));
                Raise(nameof(CanZoomOut));
            }
        }

        public bool CanZoomOut => _mapRoot?.Parent != null;

        public int Revision { get => _revision; private set => Set(ref _revision, value); }

        /// <summary>0 dossiers, 1 types, 2 plus gros fichiers, 3 pistes.</summary>
        public int View
        {
            get => _view;
            set
            {
                if (!Set(ref _view, value)) return;
                Raise(nameof(IsFoldersView));
                Raise(nameof(IsTypesView));
                Raise(nameof(IsLargestView));
                Raise(nameof(IsHintsView));
            }
        }

        public bool IsFoldersView { get => _view == 0; set { if (value) View = 0; } }
        public bool IsTypesView { get => _view == 1; set { if (value) View = 1; } }
        public bool IsLargestView { get => _view == 2; set { if (value) View = 2; } }
        public bool IsHintsView { get => _view == 3; set { if (value) View = 3; } }

        public bool HasHints => Hints.Count > 0;

        public string HintsLabel => Hints.Count == 0 ? "Pistes" : "Pistes (" + Hints.Count + ")";

        public DiskNode? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                RaiseSelection();
                if (!_syncing && value != null) Reveal(value);
            }
        }

        public DiskRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!Set(ref _selectedRow, value) || value == null || value.IsMore) return;
                _syncing = true;
                try { Selected = value.Node; }
                finally { _syncing = false; }
            }
        }

        public DiskNode? Hovered
        {
            get => _hovered;
            set
            {
                if (!Set(ref _hovered, value)) return;
                Raise(nameof(HoverText));
            }
        }

        /// <summary>Ce qui est sous la souris, sur la carte : le chemin et la taille, sans ouvrir de bulle.</summary>
        public string HoverText
        {
            get
            {
                var node = _hovered ?? _selected;
                if (node == null) return "Survolez la carte pour voir chaque fichier. Double clic : entrer dans le dossier.";
                return node.FullPath + "  ·  " + ValueFormat.Bytes(node.Size);
            }
        }

        public bool HasSelection => _selected != null;
        public string SelectedName => _selected?.Name ?? string.Empty;
        public string SelectedPath => _selected?.Parent?.FullPath ?? string.Empty;
        public string SelectedSize => _selected == null ? string.Empty : ValueFormat.Bytes(_selected.Size);

        public string SelectedDetail
        {
            get
            {
                if (_selected == null || _tree == null) return string.Empty;
                var parts = new List<string>();
                if (_selected.IsDirectory) parts.Add(ValueFormat.Number(_selected.Files) + " fichiers");
                else
                {
                    var stat = _tree.ExtensionOf(_selected);
                    if (stat != null && stat.Kind.Length > 0) parts.Add(stat.Kind);
                }
                if (_tree.Root.Size > 0) parts.Add(ValueFormat.Percent(100.0 * _selected.Size / _tree.Root.Size) + " de l'analyse");
                if (_selected.LastWrite.HasValue) parts.Add("modifié le " + _selected.LastWrite.Value.ToString("d MMMM yyyy", Formats.French));
                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>Pourquoi l'élément choisi ne peut pas partir à la corbeille, ou null.</summary>
        public string? Refusal => _selected == null ? null : DiskUsageAdvisor.Refusal(_selected);

        public bool HasRefusal => Refusal != null;

        public string SummaryText
        {
            get
            {
                if (_tree == null) return string.Empty;
                var text = ValueFormat.Bytes(_tree.Root.Size) + "  ·  " + ValueFormat.Number(_tree.Root.Files) + " fichiers  ·  "
                           + ValueFormat.Number(_tree.Directories) + " dossiers  ·  lu en " + Seconds(_tree.Elapsed);
                if (_tree.UnreadableDirectories > 0)
                    text += "  ·  " + ValueFormat.Number(_tree.UnreadableDirectories) + " dossier(s) illisible(s)";
                return text;
            }
        }

        /// <summary>Barre du volume : la part analysée, la part hors d'atteinte, le libre.</summary>
        public bool HasVolume => _tree?.VolumeSize > 0;
        public double VolumeScannedShare => HasVolume ? (double)_tree!.Root.Size / _tree.VolumeSize!.Value : 0;
        public double VolumeOtherShare => HasVolume ? (double)_tree!.Unaccounted!.Value / _tree.VolumeSize!.Value : 0;
        public double VolumeFreeShare => HasVolume ? (double)_tree!.VolumeFree!.Value / _tree.VolumeSize!.Value : 0;

        public string VolumeText
        {
            get
            {
                if (!HasVolume) return string.Empty;
                var text = ValueFormat.Bytes(_tree!.VolumeFree!.Value) + " libres sur " + ValueFormat.Bytes(_tree.VolumeSize!.Value);
                var other = _tree.Unaccounted!.Value;
                if (other > 0) text += "  ·  " + ValueFormat.Bytes(other) + " réservés au système ou illisibles";
                return text;
            }
        }

        /// <summary>Lance une analyse : un volume entier, ou un dossier.</summary>
        public async Task ScanAsync(string path)
        {
            if (IsScanning || string.IsNullOrWhiteSpace(path)) return;

            path = Path.GetFullPath(path);
            var isVolume = string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);
            long? volumeSize = null, volumeFree = null;
            if (isVolume)
            {
                try
                {
                    var drive = new DriveInfo(path);
                    volumeSize = drive.TotalSize;
                    volumeFree = drive.TotalFreeSpace;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            }

            _expectedBytes = volumeSize - volumeFree;
            ScanTarget = path;
            ScanFiles = ScanBytes = ScanCurrent = string.Empty;
            ScanShare = 0;
            ScanShareKnown = _expectedBytes > 0;
            Stage = "Scanning";
            Raise(nameof(IsBusy));

            var cancellation = _scan = new CancellationTokenSource();
            var scanner = _scanner = new DiskUsageScanner(FastDirectoryLister.Instance);
            _ticker.Start();
            _logger.Info(Category, "Analyse de " + path);

            try
            {
                var tree = await Task.Run(() => scanner.Scan(path, cancellation.Token)).ConfigureAwait(true);
                tree.VolumeSize = volumeSize;
                tree.VolumeFree = volumeFree;
                _logger.Info(Category, ValueFormat.Bytes(tree.Root.Size) + " en " + tree.Root.Files + " fichiers, lus en " + Seconds(tree.Elapsed));
                Show(tree);
            }
            catch (OperationCanceledException)
            {
                Stage = _tree == null ? "Choose" : "Ready";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                _logger.Warn(Category, "Analyse impossible de " + path, ex);
                Status = "Analyse impossible : " + ex.Message;
                Stage = _tree == null ? "Choose" : "Ready";
            }
            finally
            {
                _ticker.Stop();
                _scanner = null;
                Raise(nameof(IsBusy));
            }
        }

        private void Tick()
        {
            var progress = _scanner?.Progress;
            if (progress == null) return;
            ScanFiles = ValueFormat.Number(progress.Files);
            ScanBytes = ValueFormat.Bytes(progress.Bytes);
            ScanCurrent = progress.Current;
            if (_expectedBytes > 0) ScanShare = Math.Min(1, (double)progress.Bytes / _expectedBytes.Value);
        }

        private void Show(DiskUsageTree tree)
        {
            Tree = tree;
            _expanded.Clear();
            _expanded.Add(tree.Root);
            View = 0;
            MapRoot = tree.Root;
            Selected = null;
            Status = string.Empty;
            Rebuild();
            Stage = "Ready";
        }

        /// <summary>Tout ce qui découle de l'arbre : listes, légende, pistes, chiffres.</summary>
        private void Rebuild()
        {
            var tree = _tree;
            if (tree == null) return;

            Rows.Clear();
            foreach (var row in Flatten(tree.Root, 0)) Rows.Add(row);

            Extensions.Clear();
            var ranked = tree.RankedExtensions();
            for (var index = 0; index < ranked.Count && index < 60; index++) Extensions.Add(new ExtensionRow(ranked[index], tree.Root.Size));

            Largest.Clear();
            foreach (var file in tree.LargestFiles(60))
                Largest.Add(new DiskPick(file, file.Name, file.Parent?.FullPath ?? string.Empty,
                    DiskRow.Brush(TreemapView.PaletteColor(tree.ExtensionOf(file)?.Color ?? -1)), ShowCommand));

            Hints.Clear();
            foreach (var hint in DiskUsageAdvisor.Hints(tree))
                Hints.Add(new DiskPick(hint.Node, hint.Title, hint.Advice, null, ShowCommand));

            Revision++;
            Raise(nameof(HasHints));
            Raise(nameof(HintsLabel));
            Raise(nameof(SummaryText));
            Raise(nameof(HasVolume));
            Raise(nameof(VolumeScannedShare));
            Raise(nameof(VolumeOtherShare));
            Raise(nameof(VolumeFreeShare));
            Raise(nameof(VolumeText));
        }

        private IEnumerable<DiskRow> Flatten(DiskNode node, int depth)
        {
            var tree = _tree!;
            var row = new DiskRow(node, depth, tree);
            yield return row;
            if (!_expanded.Contains(node) || !node.HasChildren) yield break;

            row.IsExpanded = true;
            foreach (var child in Children(node, depth + 1))
            {
                if (child.IsMore) yield return child;
                else foreach (var nested in Flatten(child.Node, depth + 1)) yield return nested;
            }
        }

        /// <summary>Les lignes directes d'un dossier, plafonnées, et la ligne qui résume le reste.</summary>
        private IEnumerable<DiskRow> Children(DiskNode node, int depth)
        {
            var children = node.Children;
            var shown = Math.Min(children.Count, RowsPerFolder);
            for (var index = 0; index < shown; index++) yield return new DiskRow(children[index], depth, _tree!);

            if (children.Count > shown)
            {
                long rest = 0;
                for (var index = shown; index < children.Count; index++) rest += children[index].Size;
                yield return new DiskRow(node, depth, children.Count - shown, rest);
            }
        }

        private void Toggle(DiskRow? row)
        {
            if (row == null || !row.CanExpand) return;
            var index = Rows.IndexOf(row);
            if (index < 0) return;

            if (row.IsExpanded)
            {
                _expanded.Remove(row.Node);
                row.IsExpanded = false;
                while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth) Rows.RemoveAt(index + 1);
                return;
            }

            _expanded.Add(row.Node);
            row.IsExpanded = true;
            var position = index + 1;
            foreach (var child in Children(row.Node, row.Depth + 1)) Rows.Insert(position++, child);
        }

        /// <summary>Déplie l'arborescence jusqu'à l'élément et le sélectionne : la vue fait défiler jusqu'à lui.</summary>
        private void Reveal(DiskNode node)
        {
            var chain = new List<DiskNode>();
            for (var current = node.Parent; current != null; current = current.Parent) chain.Insert(0, current);

            foreach (var ancestor in chain)
            {
                var row = Find(ancestor);
                if (row == null) break;
                if (!row.IsExpanded) Toggle(row);
            }

            var target = Find(node);
            if (target == null)
            {
                // Au-delà du plafond de lignes d'un dossier : la ligne « … et N autres » le représente.
                foreach (var row in Rows)
                    if (row.IsMore && ReferenceEquals(row.Node, node.Parent)) target = row;
            }

            _syncing = true;
            try { SelectedRow = target; }
            finally { _syncing = false; }
        }

        private DiskRow? Find(DiskNode node)
        {
            foreach (var row in Rows)
                if (!row.IsMore && ReferenceEquals(row.Node, node)) return row;
            return null;
        }

        private void RaiseSelection()
        {
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedName));
            Raise(nameof(SelectedPath));
            Raise(nameof(SelectedSize));
            Raise(nameof(SelectedDetail));
            Raise(nameof(Refusal));
            Raise(nameof(HasRefusal));
            Raise(nameof(HoverText));
            RecycleCommand.RaiseCanExecuteChanged();
        }

        private void RefreshDrives()
        {
            Drives.Clear();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { return; }

            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)) continue;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? (drive.DriveType == DriveType.Removable ? "Amovible" : "Disque local") : drive.VolumeLabel;
                    var root = drive.RootDirectory.FullName;
                    Drives.Add(new DiskDriveItem(root, root.TrimEnd('\\') + "  " + label, drive.TotalSize, drive.TotalFreeSpace,
                        new RelayCommand(() => _ = ScanAsync(root))));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private void ChooseFolder()
        {
            var owner = IntPtr.Zero;
            try
            {
                var window = Application.Current?.MainWindow;
                if (window != null) owner = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            }
            catch (InvalidOperationException) { }

            var picked = FolderPicker.Pick(owner, "Dossier à analyser");
            if (picked.Count > 0) _ = ScanAsync(picked[0]);
        }

        private void OpenLocation()
        {
            if (_selected == null) return;
            var path = _selected.FullPath;
            try
            {
                // Un fichier : l'Explorateur s'ouvre sur son dossier, le fichier sélectionné.
                var arguments = _selected.IsDirectory ? "\"" + path + "\"" : "/select,\"" + path + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                _logger.Warn(Category, "Ouverture impossible de " + path, ex);
            }
        }

        private async Task RecycleAsync()
        {
            var node = _selected;
            var tree = _tree;
            if (node == null || tree == null || DiskUsageAdvisor.Refusal(node) != null) return;

            var path = node.FullPath;
            var question = "Mettre à la corbeille « " + node.Name + " » ?\n\n" + ValueFormat.Bytes(node.Size)
                           + (node.IsDirectory ? ", " + ValueFormat.Number(node.Files) + " fichiers" : string.Empty)
                           + "\n" + path + "\n\nIl reste récupérable depuis la corbeille.";
            var owner = Application.Current?.MainWindow;
            var answer = owner == null
                ? MessageBox.Show(question, "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(owner, question, "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                await Task.Run(() => RecycleBin.Send(path, node.IsDirectory)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException
                                       || ex is System.Security.SecurityException)
            {
                _logger.Warn(Category, "Corbeille impossible pour " + path, ex);
                Status = "« " + node.Name + " » n'a pas pu être mis à la corbeille : " + ex.Message;
                return;
            }

            _logger.Info(Category, "Mis à la corbeille : " + path + " (" + ValueFormat.Bytes(node.Size) + ")");
            Status = "« " + node.Name + " » est à la corbeille : " + ValueFormat.Bytes(node.Size) + " libérés une fois la corbeille vidée.";

            var parent = node.Parent;
            tree.Remove(node);
            if (_mapRoot != null && !tree.Root.Contains(_mapRoot)) MapRoot = parent;
            Selected = parent;
            Rebuild();
            if (parent != null) Reveal(parent);
        }

        private static string Seconds(TimeSpan elapsed)
            => elapsed.TotalSeconds < 1 ? "moins d'une seconde"
                : elapsed.TotalSeconds < 10
                ? Math.Round(elapsed.TotalSeconds, 1).ToString(Formats.French) + " s"
                : Math.Round(elapsed.TotalSeconds).ToString(Formats.French) + " s";
    }
}
