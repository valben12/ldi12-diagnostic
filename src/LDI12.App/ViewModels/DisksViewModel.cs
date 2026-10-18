using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Disks;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using Newtonsoft.Json;

namespace LDI12.App.ViewModels
{
    /// <summary>Un disque proposé en segment.</summary>
    public sealed class DiskChoice : ObservableObject
    {
        private readonly DisksViewModel _owner;

        public DiskChoice(PhysicalDiskInfo disk, DisksViewModel owner)
        {
            Disk = disk;
            _owner = owner;
        }

        public PhysicalDiskInfo Disk { get; }

        public string Label => "Disque " + Disk.Index + " · " + (Disk.Model.ValueOrDefault ?? "modèle inconnu")
                               + (Disk.CapacityBytes.HasValue ? " · " + ValueFormat.Bytes(Disk.CapacityBytes.Value) : string.Empty);

        public bool IsSelected
        {
            get => ReferenceEquals(_owner.Selected, this);
            set { if (value) _owner.Selected = this; }
        }

        internal void Changed() => Raise(nameof(IsSelected));
    }

    /// <summary>Une ligne du tableau SMART.</summary>
    public sealed class SmartRow
    {
        public SmartRow(SmartAttribute attribute)
        {
            var info = SmartCatalog.Describe(attribute.Id, attribute.Name);
            Id = attribute.Id.ToString("X2", CultureInfo.InvariantCulture);
            Name = info.Name;
            Values = attribute.Current + " / " + attribute.Worst + " / " + (attribute.Threshold > 0 ? attribute.Threshold.ToString(CultureInfo.InvariantCulture) : "–");
            Raw = attribute.Raw.ToString("N0", Formats.French);
            Meaning = info.Meaning ?? string.Empty;
            StatusKey = attribute.ThresholdExceeded ? "Bad"
                : (info.Critical || attribute.IsCritical) && attribute.Raw > 0 && attribute.Id != 194 && attribute.Id != 190 ? "Warning"
                : attribute.Id == 199 && attribute.Raw > 0 ? "Warning"
                : "Good";
        }

        public string Id { get; }
        public string Name { get; }
        public string Values { get; }
        public string Raw { get; }
        public string Meaning { get; }
        public bool HasMeaning => Meaning.Length > 0 && StatusKey != "Good";
        public string StatusKey { get; }
    }

    /// <summary>Une métrique en tuile : température, heures, usure ; ou une classe de temps de lecture, avec sa couleur.</summary>
    public sealed class DiskMetric
    {
        public DiskMetric(string value, string label, Brush? swatch = null)
        {
            Value = value;
            Label = label;
            Swatch = swatch;
        }

        public string Value { get; }
        public string Label { get; }
        public Brush? Swatch { get; }
        public bool HasSwatch => Swatch != null;
    }

    /// <summary>Une ligne du journal d'autotests, mise en mots.</summary>
    public sealed class SelfTestRow
    {
        public SelfTestRow(SelfTestEntry entry)
        {
            Title = entry.Kind + " · " + entry.Result;
            Passed = entry.Passed;
            Detail = "à " + entry.LifetimeHours.ToString("N0", Formats.French) + " h de fonctionnement"
                     + (entry.FailingLba.HasValue ? " · premier secteur en échec : LBA " + entry.FailingLba.Value.ToString("N0", Formats.French) : string.Empty);
        }

        public string Title { get; }
        public string Detail { get; }
        public bool Passed { get; }
    }

    /// <summary>Un volume du disque, avec son bouton d'optimisation.</summary>
    public sealed class DiskVolumeRow
    {
        public DiskVolumeRow(string letter, string detail, AsyncRelayCommand optimize)
        {
            Letter = letter;
            Detail = detail;
            OptimizeCommand = optimize;
        }

        public string Letter { get; }
        public string Detail { get; }
        public AsyncRelayCommand OptimizeCommand { get; }
    }

    /// <summary>
    /// L'écran Disques : l'état SMART complet et commenté (GSmartControl), l'autotest du disque,
    /// le test de surface chronométré avec sa carte (HDDScan), et l'optimisation de Windows.
    /// </summary>
    public sealed class DisksViewModel : ObservableObject
    {
        private const string Category = "Disques";

        /// <summary>Les couleurs de la carte, de « non lu » à « illisible » : celles qu'utilise HDDScan.</summary>
        private static readonly uint[] Palette = { 0x40808080, 0xFF3FB97A, 0xFF8BCB5A, 0xFFC9CF45, 0xFFF2B33D, 0xFFF07A2E, 0xFFE2373D, 0xFF8E3FD4 };

        private const int Columns = 100;
        private const int Rows = 40;
        private const int Cell = 7;

        private readonly RepairsViewModel _repairs;
        private readonly ILdiLogger _logger;
        private DiskChoice? _selected;
        private DiskHealth? _health;
        private SelfTestState? _selfTest;
        private SurfaceScan? _scan;
        private CancellationTokenSource? _scanning;
        private WriteableBitmap? _map;
        private bool _fullScan, _isScanning, _isBusy, _demo;
        private double _scanProgress;
        private string _status = string.Empty, _scanText = string.Empty, _selfTestText = string.Empty, _optimizeText = string.Empty;

        public DisksViewModel(RepairsViewModel repairs, ILdiLogger logger)
        {
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ShortTestCommand = new AsyncRelayCommand(() => SelfTestAsync("short"), () => CanSelfTest && !_isBusy);
            LongTestCommand = new AsyncRelayCommand(() => SelfTestAsync("extended"), () => CanSelfTest && !_isBusy);
            ReadTestCommand = new AsyncRelayCommand(() => SelfTestAsync("status"), () => CanSelfTest && !_isBusy);
            ScanCommand = new AsyncRelayCommand(ScanAsync, () => _selected != null && !_isScanning);
            StopScanCommand = new RelayCommand(() => _scanning?.Cancel(), () => _isScanning);
        }

        public ObservableCollection<DiskChoice> Disks { get; } = new ObservableCollection<DiskChoice>();
        public ObservableCollection<SmartRow> Attributes { get; } = new ObservableCollection<SmartRow>();
        public ObservableCollection<DiskMetric> Metrics { get; } = new ObservableCollection<DiskMetric>();
        public ObservableCollection<string> HealthReasons { get; } = new ObservableCollection<string>();
        public ObservableCollection<SelfTestRow> SelfTestLog { get; } = new ObservableCollection<SelfTestEntry>();
        public ObservableCollection<DiskVolumeRow> Volumes { get; } = new ObservableCollection<DiskVolumeRow>();
        public ObservableCollection<DiskMetric> Histogram { get; } = new ObservableCollection<DiskMetric>();

        public AsyncRelayCommand ShortTestCommand { get; }
        public AsyncRelayCommand LongTestCommand { get; }
        public AsyncRelayCommand ReadTestCommand { get; }
        public AsyncRelayCommand ScanCommand { get; }
        public RelayCommand StopScanCommand { get; }

        public bool HasDisks => Disks.Count > 0;
        public string Status { get => _status; private set => Set(ref _status, value); }

        private SystemSnapshot? _snapshot;

        public DiskChoice? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                foreach (var disk in Disks) disk.Changed();
                ShowDisk();
            }
        }

        // ------------------------------------------------------------------ état SMART

        public string HealthTitle => _health?.Title ?? string.Empty;
        public string HealthTone => _health?.Tone ?? "Unknown";
        public bool HasAttributes => Attributes.Count > 0;

        public void Update(SystemSnapshot snapshot)
        {
            if (_demo) return;
            _snapshot = snapshot;
            var previous = _selected?.Disk.Index;
            Disks.Clear();
            foreach (var disk in snapshot.Storage.PhysicalDisks.OrderBy(d => d.Index)) Disks.Add(new DiskChoice(disk, this));
            Raise(nameof(HasDisks));
            _selected = null;
            Selected = Disks.FirstOrDefault(d => d.Disk.Index == previous) ?? Disks.FirstOrDefault();
        }

        private void ShowDisk()
        {
            var disk = _selected?.Disk;
            Attributes.Clear();
            Metrics.Clear();
            HealthReasons.Clear();
            Volumes.Clear();
            SelfTestLog.Clear();
            _selfTest = null;
            _health = disk == null ? null : DiskHealth.Of(disk);

            if (disk != null)
            {
                foreach (var attribute in disk.Smart.Attributes.OrderBy(a => a.Id)) Attributes.Add(new SmartRow(attribute));
                foreach (var reason in _health!.Reasons) HealthReasons.Add(reason);

                var smart = disk.Smart;
                if (smart.TemperatureCelsius.HasValue) Metrics.Add(new DiskMetric(smart.TemperatureCelsius.Value + " °C", "température"));
                if (smart.PowerOnHours.HasValue) Metrics.Add(new DiskMetric(smart.PowerOnHours.Value.ToString("N0", Formats.French) + " h", "de fonctionnement (" + (smart.PowerOnHours.Value / 24 / 365.0).ToString("0.0", Formats.French) + " ans)"));
                if (smart.PowerCycles.HasValue) Metrics.Add(new DiskMetric(smart.PowerCycles.Value.ToString("N0", Formats.French), "démarrages"));
                if (smart.WearPercent.HasValue) Metrics.Add(new DiskMetric(smart.WearPercent.Value.ToString("0", Formats.French) + " %", "d'usure de la mémoire"));
                if (smart.TotalBytesWritten.HasValue) Metrics.Add(new DiskMetric(ValueFormat.Bytes(smart.TotalBytesWritten.Value), "écrits depuis la mise en service"));
                Metrics.Add(new DiskMetric(Media(disk), Bus(disk)));

                foreach (var volume in _snapshot?.Storage.Volumes.Where(v => v.DiskIndex.HasValue && v.DiskIndex.Value == disk.Index && v.DriveLetter.HasValue) ?? Enumerable.Empty<VolumeInfo>())
                {
                    var letter = volume.DriveLetter.Value.TrimEnd('\\');
                    var detail = (volume.Label.ValueOrDefault is { Length: > 0 } label ? label + " · " : string.Empty)
                                 + (volume.FileSystem.ValueOrDefault ?? string.Empty)
                                 + (volume.TotalBytes.HasValue ? " · " + ValueFormat.Bytes(volume.TotalBytes.Value) : string.Empty);
                    Volumes.Add(new DiskVolumeRow(letter, detail, new AsyncRelayCommand(() => OptimizeAsync(letter), () => !_isBusy)));
                }
            }

            SelfTestText = !CanSelfTest
                ? "L'autotest intégré se lance sur les disques SATA. Ce disque (" + (disk == null ? "?" : Bus(disk)) + ") ne le permet pas d'ici : le test de surface, lui, fonctionne."
                : "Le disque se vérifie lui-même : l'autotest court dure environ deux minutes, le long peut prendre plusieurs heures. Le PC reste utilisable.";
            OptimizeText = disk == null ? string.Empty
                : IsSolid(disk) ? "SSD : Windows lance un TRIM, qui lui rend sa vitesse d'écriture. Un SSD ne se défragmente jamais."
                : "Disque dur : Windows défragmente les fichiers et regroupe l'espace libre.";

            ResetScan();
            foreach (var name in new[] { nameof(HealthTitle), nameof(HealthTone), nameof(HasAttributes), nameof(CanSelfTest), nameof(SelfTestRunning), nameof(SelfTestResult), nameof(HasVolumes) }) Raise(name);
            ShortTestCommand.RaiseCanExecuteChanged();
            LongTestCommand.RaiseCanExecuteChanged();
            ReadTestCommand.RaiseCanExecuteChanged();
            ScanCommand.RaiseCanExecuteChanged();
        }

        private static bool IsSolid(PhysicalDiskInfo disk) => disk.MediaType.ValueOrDefault == StorageMediaType.Ssd || disk.MediaType.ValueOrDefault == StorageMediaType.Nvme;

        private static string Media(PhysicalDiskInfo disk) => disk.MediaType.ValueOrDefault switch
        {
            StorageMediaType.Hdd => "Disque dur",
            StorageMediaType.Ssd => "SSD",
            StorageMediaType.Nvme => "SSD NVMe",
            StorageMediaType.Removable => "Amovible",
            StorageMediaType.Virtual => "Virtuel",
            _ => "Média inconnu",
        };

        private static string Bus(PhysicalDiskInfo disk) => disk.BusType.ValueOrDefault switch
        {
            StorageBusType.Sata => "branché en SATA",
            StorageBusType.Ata => "branché en ATA",
            StorageBusType.Nvme => "branché en NVMe",
            StorageBusType.Usb => "branché en USB",
            StorageBusType.Raid => "derrière un contrôleur RAID",
            StorageBusType.Virtual => "disque virtuel",
            _ => "bus inconnu",
        };

        // ------------------------------------------------------------------ autotest

        public bool CanSelfTest => _selected != null &&
                                   (_selected.Disk.BusType.ValueOrDefault == StorageBusType.Sata || _selected.Disk.BusType.ValueOrDefault == StorageBusType.Ata) &&
                                   _selected.Disk.Smart.OverallStatus.ValueOrDefault != SmartOverallStatus.NotSupported;

        public string SelfTestText { get => _selfTestText; private set => Set(ref _selfTestText, value); }
        public bool SelfTestRunning => _selfTest?.Running == true;
        public double SelfTestProgress => _selfTest == null ? 0 : (100 - _selfTest.RemainingPercent) / 100.0;
        public string SelfTestResult => _selfTest == null ? string.Empty
            : _selfTest.Running ? "Autotest en cours : " + (100 - _selfTest.RemainingPercent) + " % fait. Relisez dans quelques minutes."
            : "Dernier autotest : " + _selfTest.LastResult + "." + (_selfTest.ExtendedMinutes > 0 ? " Durée annoncée du test long : " + _selfTest.ExtendedMinutes + " min." : string.Empty);

        private async Task SelfTestAsync(string mode)
        {
            var disk = _selected?.Disk;
            if (disk == null) return;
            Busy(true);
            try
            {
                var outcome = await _repairs.RunQuietAsync(ActionIds.DiskSelfTest, new Dictionary<string, string>
                {
                    ["disk"] = disk.Index.ToString(CultureInfo.InvariantCulture),
                    [DiskSelfTestAction.ModeParameter] = mode,
                }, null, CancellationToken.None).ConfigureAwait(true);

                _selfTest = outcome.Succeeded && outcome.RawOutput != null ? JsonConvert.DeserializeObject<SelfTestState>(outcome.RawOutput) : null;
                SelfTestLog.Clear();
                if (_selfTest != null) foreach (var entry in _selfTest.Log) SelfTestLog.Add(new SelfTestRow(entry));
                Status = outcome.Summary;
                foreach (var name in new[] { nameof(SelfTestRunning), nameof(SelfTestProgress), nameof(SelfTestResult), nameof(HasSelfTestLog) }) Raise(name);
            }
            catch (JsonException ex)
            {
                Status = "Réponse illisible : " + ex.Message;
            }
            finally
            {
                Busy(false);
            }
        }

        public bool HasSelfTestLog => SelfTestLog.Count > 0;

        // ------------------------------------------------------------------ surface

        public bool IsQuickScan { get => !_fullScan; set { if (value) { _fullScan = false; Raise(nameof(IsFullScan)); Raise(); } } }
        public bool IsFullScan { get => _fullScan; set { if (value) { _fullScan = true; Raise(nameof(IsQuickScan)); Raise(); } } }

        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (!Set(ref _isScanning, value)) return;
                ScanCommand.RaiseCanExecuteChanged();
                StopScanCommand.RaiseCanExecuteChanged();
            }
        }

        public double ScanProgress { get => _scanProgress; private set => Set(ref _scanProgress, value); }
        public string ScanText { get => _scanText; private set => Set(ref _scanText, value); }
        public ImageSource? Map => _map;
        public bool HasScan => _scan != null || _isScanning;

        private void ResetScan()
        {
            _scan = null;
            _map = null;
            Histogram.Clear();
            ScanText = "Rapide : 2 400 échantillons répartis sur tout le disque, une à deux minutes. Complet : chaque bloc, plusieurs heures sur un grand disque dur.";
            Raise(nameof(Map));
            Raise(nameof(HasScan));
        }

        private async Task ScanAsync()
        {
            var disk = _selected?.Disk;
            if (disk == null) return;
            IsScanning = true;
            ResetScan();
            Raise(nameof(HasScan));
            var cancellation = _scanning = new CancellationTokenSource();
            var progress = new Progress<ActionProgress>(p =>
            {
                ScanProgress = p.Fraction ?? 0;
                ScanText = p.Text + (p.Elapsed.HasValue ? " · " + (int)p.Elapsed.Value.TotalMinutes + " min " + p.Elapsed.Value.Seconds + " s" : string.Empty);
                if (p.Detail != null && p.Detail.StartsWith(DiskSurfaceTestAction.MapPrefix, StringComparison.Ordinal))
                {
                    try { Draw(Convert.FromBase64String(p.Detail.Substring(DiskSurfaceTestAction.MapPrefix.Length))); }
                    catch (FormatException) { }
                }
            });

            try
            {
                var outcome = await _repairs.RunQuietAsync(ActionIds.DiskSurfaceTest, new Dictionary<string, string>
                {
                    ["disk"] = disk.Index.ToString(CultureInfo.InvariantCulture),
                    [DiskSurfaceTestAction.FullParameter] = _fullScan ? "1" : "0",
                }, progress, cancellation.Token).ConfigureAwait(true);

                var scan = outcome.RawOutput != null ? JsonConvert.DeserializeObject<SurfaceScan>(outcome.RawOutput) : null;
                if (scan != null) ShowScan(scan);
                else ScanText = outcome.Summary;
                _logger.Info(Category, "Surface du disque " + disk.Index + " : " + outcome.Summary);
            }
            catch (JsonException ex)
            {
                ScanText = "Résultat illisible : " + ex.Message;
            }
            finally
            {
                IsScanning = false;
                ScanProgress = 0;
                Raise(nameof(HasScan));
            }
        }

        private void ShowScan(SurfaceScan scan)
        {
            _scan = scan;
            Draw(scan.Cells);
            Histogram.Clear();
            string[] labels = { "< 5 ms", "< 20 ms", "< 50 ms", "< 150 ms", "< 500 ms", "≥ 500 ms", "illisibles" };
            for (var index = 0; index < labels.Length && index < scan.Histogram.Length; index++)
                Histogram.Add(new DiskMetric(scan.Histogram[index].ToString("N0", Formats.French), labels[index], Swatch(index + 1)));
            ScanText = (scan.Cancelled ? "Test interrompu. " : string.Empty) + scan.Verdict() + " "
                       + ValueFormat.Bytes(scan.BytesRead) + " lus en " + (int)scan.Duration.TotalMinutes + " min " + scan.Duration.Seconds + " s.";
            Raise(nameof(HasScan));
        }

        private static Brush Swatch(int cell)
        {
            var argb = Palette[cell];
            var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        /// <summary>La carte : une case par zone du disque, la pire lecture l'emporte.</summary>
        private void Draw(byte[] cells)
        {
            var width = Columns * Cell;
            var height = Rows * Cell;
            _map ??= new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new uint[width * height];
            for (var index = 0; index < Columns * Rows; index++)
            {
                var source = (int)((long)index * cells.Length / (Columns * Rows));
                var value = source < cells.Length ? cells[source] : (byte)0;
                var color = Palette[Math.Min(value, (byte)(Palette.Length - 1))];
                var x0 = index % Columns * Cell;
                var y0 = index / Columns * Cell;
                for (var y = 0; y < Cell - 1; y++)
                    for (var x = 0; x < Cell - 1; x++)
                        pixels[(y0 + y) * width + x0 + x] = color;
            }
            _map.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            Raise(nameof(Map));
        }

        // ------------------------------------------------------------------ optimisation

        public string OptimizeText { get => _optimizeText; private set => Set(ref _optimizeText, value); }
        public bool HasVolumes => Volumes.Count > 0;

        private async Task OptimizeAsync(string letter)
        {
            Busy(true);
            Status = "Optimisation de " + letter + "…";
            try
            {
                var outcome = await _repairs.RunQuietAsync(ActionIds.DiskOptimize, new Dictionary<string, string> { [DiskOptimizeAction.VolumeParameter] = letter },
                    new Progress<ActionProgress>(p => Status = p.Text), CancellationToken.None).ConfigureAwait(true);
                Status = outcome.Summary + (outcome.Details.Count > 0 ? " " + outcome.Details[outcome.Details.Count - 1] : string.Empty);
            }
            finally
            {
                Busy(false);
            }
        }

        private void Busy(bool busy)
        {
            _isBusy = busy;
            ShortTestCommand.RaiseCanExecuteChanged();
            LongTestCommand.RaiseCanExecuteChanged();
            ReadTestCommand.RaiseCanExecuteChanged();
            foreach (var volume in Volumes) volume.OptimizeCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Un disque d'exemple, pour les captures d'écran seulement.</summary>
        public void ShowDemo()
        {
            _demo = true;
            SmartAttribute A(int id, int current, int worst, int threshold, long raw) => new SmartAttribute { Id = id, Current = current, Worst = worst, Threshold = threshold, Raw = raw, IsCritical = id == 5 || id == 197 || id == 198 };
            var disk = new PhysicalDiskInfo
            {
                Index = 0,
                Model = Measured.Ok("WDC WD10EZEX-08WN4A0", DataSource.NativeApi),
                CapacityBytes = Measured.Ok(1_000_204_886_016L, DataSource.NativeApi),
                BusType = Measured.Ok(StorageBusType.Sata, DataSource.NativeApi),
                MediaType = Measured.Ok(StorageMediaType.Hdd, DataSource.NativeApi),
                Smart = new SmartData
                {
                    OverallStatus = Measured.Ok(SmartOverallStatus.Warning, DataSource.NativeApi),
                    TemperatureCelsius = Measured.Ok(41, DataSource.NativeApi),
                    PowerOnHours = Measured.Ok(28_416L, DataSource.NativeApi),
                    PowerCycles = Measured.Ok(3_912L, DataSource.NativeApi),
                    ReallocatedSectors = Measured.Ok(24L, DataSource.NativeApi),
                    PendingSectors = Measured.Ok(0L, DataSource.NativeApi),
                    UncorrectableErrors = Measured.Ok(0L, DataSource.NativeApi),
                    Attributes = new[]
                    {
                        A(1, 200, 200, 51, 3), A(3, 172, 170, 21, 4375), A(4, 97, 97, 0, 3961), A(5, 199, 199, 140, 24), A(7, 200, 200, 0, 0),
                        A(9, 62, 62, 0, 28416), A(10, 100, 100, 0, 0), A(12, 97, 97, 0, 3912), A(192, 200, 200, 0, 211), A(193, 199, 199, 0, 4105),
                        A(194, 106, 92, 0, 41), A(196, 200, 200, 0, 0), A(197, 200, 200, 0, 0), A(198, 100, 253, 0, 0), A(199, 200, 200, 0, 7), A(200, 100, 253, 0, 0),
                    },
                },
            };
            Disks.Clear();
            Disks.Add(new DiskChoice(disk, this));
            Raise(nameof(HasDisks));
            _selected = null;
            Selected = Disks[0];

            _selfTest = new SelfTestState
            {
                LastResult = "réussi, aucune erreur", ShortMinutes = 2, ExtendedMinutes = 110,
                Log = new List<SelfTestEntry>
                {
                    new SelfTestEntry { Kind = "Court", Result = "réussi, aucune erreur", Passed = true, LifetimeHours = 28_410 },
                    new SelfTestEntry { Kind = "Long", Result = "échec de lecture", Passed = false, LifetimeHours = 27_904, FailingLba = 1_245_771_264 },
                },
            };
            foreach (var entry in _selfTest.Log) SelfTestLog.Add(new SelfTestRow(entry));
            foreach (var name in new[] { nameof(SelfTestRunning), nameof(SelfTestProgress), nameof(SelfTestResult), nameof(HasSelfTestLog) }) Raise(name);

            var random = new Random(12);
            var scan = new SurfaceScan(4000) { DiskBytes = 1_000_204_886_016L, BytesRead = 2_516_582_400L, Duration = TimeSpan.FromSeconds(84) };
            for (var index = 0; index < 2400; index++)
            {
                var position = index / 2400.0;
                var latency = position > 0.62 && position < 0.64 ? 180 + random.Next(600) : 6 + random.Next(30) + (random.Next(60) == 0 ? 90 : 0);
                scan.Add(position, latency, position > 0.631 && position < 0.6325, (long)(position * scan.DiskBytes));
            }
            ShowScan(scan);
        }
    }
}
