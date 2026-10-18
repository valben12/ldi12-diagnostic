using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Network;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;
using Newtonsoft.Json;

namespace LDI12.App.ViewModels
{
    /// <summary>Un appareil du réseau, avec les gestes qu'il permet.</summary>
    public sealed class LanDeviceRow : ObservableObject
    {
        public LanDeviceRow(LanDevice device, Action<string> status)
        {
            Device = device;
            OpenWebCommand = new RelayCommand(() => Launch((device.OpenPorts.Contains(443) && !device.OpenPorts.Contains(80) ? "https://" : "http://") + device.Address
                                                           + (device.OpenPorts.Contains(80) || device.OpenPorts.Contains(443) ? string.Empty : ":8080")));
            OpenSharesCommand = new RelayCommand(() => Launch(@"\\" + device.Address));
            RemoteDesktopCommand = new RelayCommand(() => Launch("mstsc.exe", "/v:" + device.Address));
            WakeCommand = new RelayCommand(() => status(LanNative.WakeOnLan(device.Mac!)
                ? "Demande de réveil envoyée à " + device.Name + " (carte réseau compatible et allumée en veille nécessaires)."
                : "Réveil impossible : adresse matérielle inconnue."));
            CopyCommand = new RelayCommand(() =>
            {
                try { Clipboard.SetText(device.Address); status(device.Address + " copiée."); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            });
        }

        public LanDevice Device { get; }

        public string Title => Device.Name;
        public string KindText => LanClassifier.Describe(Device.Kind) + (Device.Workgroup != null ? " · groupe " + Device.Workgroup : string.Empty);
        public string KindKey => Device.Kind.ToString();

        public string AddressText => Device.Address + (Device.HostName != null && Device.NetBiosName != null && !Device.HostName.StartsWith(Device.NetBiosName, StringComparison.OrdinalIgnoreCase) ? " · " + Device.HostName : string.Empty);

        public string HardwareText => Device.Mac == null
            ? "adresse matérielle inconnue"
            : Device.Mac + " · " + (Device.Vendor ?? (Device.HasRandomMac ? "adresse aléatoire (téléphone, tablette)" : "fabricant inconnu"));

        public string PortsText => Device.OpenPorts.Count == 0
            ? (Device.RoundTripMs.HasValue ? "répond au ping, aucun service ouvert parmi ceux essayés" : "ne répond pas au ping")
            : string.Join(" · ", Device.OpenPorts.Select(LanClassifier.PortName));

        public string SharesText => Device.Shares.Count == 0 ? string.Empty : "Partages : " + string.Join(", ", Device.Shares);
        public bool HasShares => Device.Shares.Count > 0;

        public string LatencyText => Device.RoundTripMs.HasValue ? Device.RoundTripMs.Value + " ms" : string.Empty;

        public bool HasWeb => Device.OpenPorts.Contains(80) || Device.OpenPorts.Contains(443) || Device.OpenPorts.Contains(8080);
        public bool HasSmb => Device.OpenPorts.Contains(445) && !Device.IsSelf;
        public bool HasRdp => Device.OpenPorts.Contains(3389) && !Device.IsSelf;
        public bool CanWake => Device.Mac != null && !Device.IsSelf && !Device.HasRandomMac;

        public ICommand OpenWebCommand { get; }
        public ICommand OpenSharesCommand { get; }
        public ICommand RemoteDesktopCommand { get; }
        public ICommand WakeCommand { get; }
        public ICommand CopyCommand { get; }

        internal void Refresh()
        {
            foreach (var name in new[] { nameof(Title), nameof(KindText), nameof(KindKey), nameof(AddressText), nameof(HardwareText), nameof(PortsText), nameof(SharesText), nameof(HasShares), nameof(HasWeb), nameof(HasSmb), nameof(HasRdp), nameof(CanWake) })
                Raise(name);
        }

        private static void Launch(string target, string? arguments = null)
        {
            try { Process.Start(new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
        }
    }

    /// <summary>Un réseau du PC, proposé comme un segment : le choix se voit, sans liste déroulante.</summary>
    public sealed class SegmentChoice : ObservableObject
    {
        private readonly LanViewModel _owner;

        public SegmentChoice(LanSegment segment, LanViewModel owner)
        {
            Segment = segment;
            _owner = owner;
        }

        public LanSegment Segment { get; }

        public string Label => Segment.Adapter + " · " + Segment.Address;

        public bool IsSelected
        {
            get => ReferenceEquals(_owner.Segment, Segment);
            set { if (value) _owner.Segment = Segment; }
        }

        internal void Changed() => Raise(nameof(IsSelected));
    }

    /// <summary>Une connexion de ce PC, avec le programme qui la tient.</summary>
    public sealed class ConnectionRow : ObservableObject
    {
        private string? _remoteName;

        public ConnectionRow(ConnectionEntry entry, string process)
        {
            Entry = entry;
            Process = process;
        }

        public ConnectionEntry Entry { get; }
        public string Process { get; }
        public string Key => Entry.Protocol + "|" + Entry.LocalPort + "|" + Entry.RemoteAddress + "|" + Entry.RemotePort + "|" + Entry.ProcessId;

        public string LocalText => Entry.Protocol + " " + Entry.LocalPort;

        public string RemoteText => Entry.RemoteAddress.Length == 0 || Entry.RemoteAddress == "0.0.0.0"
            ? (Entry.Protocol == "UDP" ? "écoute UDP" : "en attente de connexions")
            : (_remoteName ?? Entry.RemoteAddress) + " : " + Entry.RemotePort + (Service(Entry.RemotePort) is { } service ? " (" + service + ")" : string.Empty);

        public string State => Entry.State;
        public bool IsEstablished => Entry.State == "établie";

        public string? RemoteName
        {
            get => _remoteName;
            set { if (Set(ref _remoteName, value)) Raise(nameof(RemoteText)); }
        }

        private static string? Service(int port) => port switch
        {
            443 => "HTTPS", 80 => "HTTP", 53 => "DNS", 445 => "partage", 3389 => "bureau à distance", 993 => "IMAP", 587 => "SMTP", 465 => "SMTP", 995 => "POP3", 22 => "SSH", _ => null,
        };
    }

    /// <summary>Une ligne de statistiques de trafic, avec sa part pour la barre.</summary>
    public sealed class TrafficRow
    {
        public TrafficRow(string title, string? detail, long bytes, long packets, long total)
        {
            Title = title;
            Detail = detail ?? string.Empty;
            Share = total > 0 ? (double)bytes / total : 0;
            Amount = bytes > 0 ? ValueFormat.Bytes(bytes) + " · " + packets + " paquets" : packets + " requête(s)";
        }

        public string Title { get; }
        public string Detail { get; }
        public bool HasDetail => Detail.Length > 0;
        public double Share { get; }
        public string Amount { get; }
    }

    /// <summary>
    /// L'écran Réseau local : les appareils du réseau (Advanced IP Scanner), les connexions de ce
    /// PC par programme, et une écoute du trafic résumée comme les statistiques de Wireshark.
    /// </summary>
    public sealed class LanViewModel : ObservableObject, IScreenActivation
    {
        private const string Category = "Réseau local";
        private const string OuiUrl = "https://standards-oui.ieee.org/oui/oui.csv";

        private readonly RepairsViewModel _repairs;
        private readonly ILdiLogger _logger;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, ConnectionRow> _connections = new Dictionary<string, ConnectionRow>();
        private readonly Dictionary<string, string?> _names = new Dictionary<string, string?>();
        private readonly ListCollectionView _connectionView;
        private OuiDatabase? _vendors;
        private CancellationTokenSource? _scan;
        private string _tab = "Devices", _status = string.Empty, _scanText = "Analysez le réseau : chaque appareil branché, ce qu'il est, ce qu'il partage.";
        private string _filter = string.Empty, _connectionSummary = string.Empty, _trafficText = string.Empty, _trafficHeadline = string.Empty;
        private LanSegment? _segment;
        private double _scanProgress;
        private bool _isScanning, _isCapturing, _establishedOnly = true, _sampling, _demo, _hasTraffic;
        private int _seconds = 30;
        private string? _pcapPath;

        public LanViewModel(RepairsViewModel repairs, ILdiLogger logger)
        {
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionView = new ListCollectionView(Connections) { Filter = KeepConnection };
            _connectionView.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.Process), ListSortDirection.Ascending));
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, __) => SampleConnections();

            ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && _segment != null);
            StopCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
            DownloadVendorsCommand = new AsyncRelayCommand(DownloadVendorsAsync);
            CaptureCommand = new AsyncRelayCommand(CaptureAsync, () => !IsCapturing && _segment != null);
            OpenPcapCommand = new RelayCommand(OpenPcap, () => _pcapPath != null && File.Exists(_pcapPath));
        }

        public ObservableCollection<SegmentChoice> Segments { get; } = new ObservableCollection<SegmentChoice>();
        public ObservableCollection<LanDeviceRow> Devices { get; } = new ObservableCollection<LanDeviceRow>();
        public ObservableCollection<ConnectionRow> Connections { get; } = new ObservableCollection<ConnectionRow>();
        public ObservableCollection<TrafficRow> Protocols { get; } = new ObservableCollection<TrafficRow>();
        public ObservableCollection<TrafficRow> Hosts { get; } = new ObservableCollection<TrafficRow>();
        public ObservableCollection<TrafficRow> Services { get; } = new ObservableCollection<TrafficRow>();
        public ObservableCollection<TrafficRow> DnsQueries { get; } = new ObservableCollection<TrafficRow>();
        public ICollectionView ConnectionView => _connectionView;

        public AsyncRelayCommand ScanCommand { get; }
        public RelayCommand StopCommand { get; }
        public AsyncRelayCommand DownloadVendorsCommand { get; }
        public AsyncRelayCommand CaptureCommand { get; }
        public RelayCommand OpenPcapCommand { get; }

        public string Status { get => _status; private set => Set(ref _status, value); }
        public string ScanText { get => _scanText; private set => Set(ref _scanText, value); }
        public double ScanProgress { get => _scanProgress; private set => Set(ref _scanProgress, value); }

        public bool NeedsVendors => _vendors == null;

        public LanSegment? Segment
        {
            get => _segment;
            set
            {
                if (!Set(ref _segment, value)) return;
                foreach (var choice in Segments) choice.Changed();
                ScanCommand.RaiseCanExecuteChanged();
                CaptureCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (!Set(ref _isScanning, value)) return;
                ScanCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }

        // ------------------------------------------------------------------ onglets

        public bool IsDevicesTab { get => _tab == "Devices"; set { if (value) Tab("Devices"); } }
        public bool IsConnectionsTab { get => _tab == "Connections"; set { if (value) Tab("Connections"); } }
        public bool IsTrafficTab { get => _tab == "Traffic"; set { if (value) Tab("Traffic"); } }

        private void Tab(string tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            foreach (var name in new[] { nameof(IsDevicesTab), nameof(IsConnectionsTab), nameof(IsTrafficTab) }) Raise(name);
            if (tab == "Connections" && !_demo) { SampleConnections(); _timer.Start(); }
            else _timer.Stop();
        }

        public void Activated()
        {
            if (_demo) return;
            if (Segments.Count == 0)
            {
                foreach (var segment in LanScanner.Segments()) Segments.Add(new SegmentChoice(segment, this));
                Segment = Segments.FirstOrDefault()?.Segment;
                if (Segment == null) ScanText = "Aucune carte réseau connectée.";
                LoadVendors();
            }
            if (_tab == "Connections") { SampleConnections(); _timer.Start(); }
        }

        public void Deactivated() => _timer.Stop();

        // ------------------------------------------------------------------ appareils

        private static string VendorsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"LDI12\Diagnostic\oui.csv");

        private void LoadVendors()
        {
            // À côté de l'exécutable d'abord (clé USB), puis dans le profil.
            var portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "oui.csv");
            foreach (var path in new[] { portable, VendorsPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var reader = new StreamReader(path);
                    _vendors = OuiDatabase.Parse(reader);
                    Raise(nameof(NeedsVendors));
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private async Task DownloadVendorsAsync()
        {
            Status = "Téléchargement de la base des fabricants (IEEE)…";
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LDI12-Diagnostic");
                var text = await client.GetStringAsync(OuiUrl).ConfigureAwait(true);
                var database = OuiDatabase.Parse(new StringReader(text));
                if (database.Count < 1000) throw new InvalidDataException("Base incomplète.");
                Directory.CreateDirectory(Path.GetDirectoryName(VendorsPath)!);
                File.WriteAllText(VendorsPath, text);
                _vendors = database;
                Raise(nameof(NeedsVendors));
                foreach (var row in Devices)
                {
                    row.Device.Vendor = _vendors.Find(row.Device.Mac);
                    row.Device.Kind = LanClassifier.Classify(row.Device);
                    row.Refresh();
                }
                Status = database.Count + " fabricants connus.";
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Status = "Téléchargement impossible : " + ex.Message;
            }
        }

        private async Task ScanAsync()
        {
            var segment = _segment;
            if (segment == null) return;
            IsScanning = true;
            Devices.Clear();
            var cancellation = _scan = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            var progress = new Progress<(int Done, int Total, LanDevice? Found)>(p =>
            {
                ScanProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                ScanText = "Interrogation de " + p.Total + " adresses… " + Devices.Count + " appareil(s) trouvé(s).";
                if (p.Found != null) Devices.Add(new LanDeviceRow(p.Found, s => Status = s));
            });

            try
            {
                ScanText = "Interrogation du réseau " + segment.Address + "/" + segment.PrefixLength + "…";
                var devices = await Task.Run(() => LanScanner.ScanAsync(segment, _vendors, progress, cancellation.Token), cancellation.Token).ConfigureAwait(true);
                Devices.Clear();
                foreach (var device in devices) Devices.Add(new LanDeviceRow(device, s => Status = s));
                ScanText = devices.Count + " appareil(s) sur " + segment.Address + "/" + segment.PrefixLength + ", en " + clock.Elapsed.TotalSeconds.ToString("0", Formats.French) + " s.";
                _logger.Info(Category, ScanText);
            }
            catch (OperationCanceledException)
            {
                ScanText = "Analyse interrompue : " + Devices.Count + " appareil(s) trouvé(s).";
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.Error(Category, "Analyse du réseau impossible.", ex);
                ScanText = "Analyse impossible : " + ex.Message;
            }
            finally
            {
                ScanProgress = 0;
                IsScanning = false;
            }
        }

        // ------------------------------------------------------------------ connexions

        public bool EstablishedOnly
        {
            get => _establishedOnly;
            set { if (Set(ref _establishedOnly, value)) { _connectionView.Refresh(); Count(); } }
        }

        public string Filter
        {
            get => _filter;
            set { if (Set(ref _filter, value ?? string.Empty)) { _connectionView.Refresh(); Count(); } }
        }

        public string ConnectionSummary { get => _connectionSummary; private set => Set(ref _connectionSummary, value); }

        private bool KeepConnection(object item)
        {
            var row = (ConnectionRow)item;
            if (_establishedOnly && !row.IsEstablished) return false;
            var filter = _filter.Trim();
            return filter.Length == 0 || row.Process.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   row.RemoteText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || row.LocalText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Count()
        {
            var shown = Connections.Count(c => KeepConnection(c));
            ConnectionSummary = shown + " connexion(s) affichée(s) · " + Connections.Count(c => c.IsEstablished) + " établie(s) · "
                                + Connections.Select(c => c.Process).Distinct().Count() + " programme(s)";
        }

        private async void SampleConnections()
        {
            if (_sampling || _demo) return;
            _sampling = true;
            try
            {
                var (entries, processes) = await Task.Run(() =>
                    (LanNative.Connections(), ProcessNative.Snapshot().GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name))).ConfigureAwait(true);
                if (_demo) return;
                var seen = new HashSet<string>();
                foreach (var entry in entries)
                {
                    var row = new ConnectionRow(entry, processes.TryGetValue(entry.ProcessId, out var name) ? name : "PID " + entry.ProcessId);
                    seen.Add(row.Key);
                    if (_connections.ContainsKey(row.Key)) continue;
                    _connections[row.Key] = row;
                    Connections.Add(row);
                    Resolve(row);
                }
                foreach (var gone in _connections.Where(c => !seen.Contains(c.Key)).ToList())
                {
                    _connections.Remove(gone.Key);
                    Connections.Remove(gone.Value);
                }
                Count();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Status = "Connexions illisibles : " + ex.Message;
            }
            finally
            {
                _sampling = false;
            }
        }

        /// <summary>Le nom de l'adresse distante, résolu une fois en arrière-plan.</summary>
        private void Resolve(ConnectionRow row)
        {
            var address = row.Entry.RemoteAddress;
            if (address.Length == 0 || address == "0.0.0.0" || address == "127.0.0.1") return;
            if (_names.TryGetValue(address, out var known)) { row.RemoteName = known; return; }
            _names[address] = null;
            _ = Task.Run(() =>
            {
                try { return Dns.GetHostEntry(address).HostName; }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is ArgumentException) { return null; }
            }).ContinueWith(task =>
            {
                _names[address] = task.Result;
                foreach (var other in Connections.Where(c => c.Entry.RemoteAddress == address)) other.RemoteName = task.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ trafic

        public bool Is15 { get => _seconds == 15; set { if (value) Duration(15); } }
        public bool Is30 { get => _seconds == 30; set { if (value) Duration(30); } }
        public bool Is60 { get => _seconds == 60; set { if (value) Duration(60); } }

        private void Duration(int seconds)
        {
            _seconds = seconds;
            foreach (var name in new[] { nameof(Is15), nameof(Is30), nameof(Is60) }) Raise(name);
        }

        public bool IsCapturing
        {
            get => _isCapturing;
            private set { if (Set(ref _isCapturing, value)) CaptureCommand.RaiseCanExecuteChanged(); }
        }

        public bool HasTraffic { get => _hasTraffic; private set => Set(ref _hasTraffic, value); }
        public string TrafficText { get => _trafficText; private set => Set(ref _trafficText, value); }
        public string TrafficHeadline { get => _trafficHeadline; private set => Set(ref _trafficHeadline, value); }

        private async Task CaptureAsync()
        {
            var segment = _segment;
            if (segment == null) return;
            IsCapturing = true;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LDI12 Diagnostic", "Captures");
            var pcap = Path.Combine(folder, "trafic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pcap");
            TrafficText = "Écoute de " + segment.Address + " pendant " + _seconds + " secondes : utilisez le PC normalement (ouvrez une page, lancez la synchronisation qui pose problème)…";
            try
            {
                var outcome = await _repairs.RunQuietAsync(ActionIds.TrafficCapture, new Dictionary<string, string>
                {
                    [TrafficCaptureAction.AddressParameter] = segment.Address.ToString(),
                    [TrafficCaptureAction.SecondsParameter] = _seconds.ToString(CultureInfo.InvariantCulture),
                    [TrafficCaptureAction.PcapParameter] = pcap,
                }, null, CancellationToken.None).ConfigureAwait(true);

                var summary = outcome.Succeeded && outcome.RawOutput != null ? JsonConvert.DeserializeObject<TrafficSummary>(outcome.RawOutput) : null;
                if (summary == null)
                {
                    TrafficText = outcome.Summary;
                    return;
                }
                ShowTraffic(summary);
            }
            catch (JsonException ex)
            {
                TrafficText = "Résultat illisible : " + ex.Message;
            }
            finally
            {
                IsCapturing = false;
            }
        }

        private void ShowTraffic(TrafficSummary summary)
        {
            _pcapPath = summary.PcapPath;
            OpenPcapCommand.RaiseCanExecuteChanged();
            var seconds = Math.Max(1, summary.Duration.TotalSeconds);
            TrafficHeadline = ValueFormat.Bytes(summary.Bytes) + " en " + seconds.ToString("0", Formats.French) + " s · "
                              + summary.Packets.ToString("N0", Formats.French) + " paquets · "
                              + ValueFormat.Bytes((long)(summary.Bytes / seconds)) + "/s en moyenne";
            TrafficText = summary.PcapPath != null
                ? "Fichier pour Wireshark : " + summary.PcapPath + (summary.Truncated ? " (tronqué à 200 Mo)" : string.Empty)
                : string.Empty;

            void Fill(ObservableCollection<TrafficRow> rows, IEnumerable<TrafficRow> source)
            {
                rows.Clear();
                foreach (var row in source) rows.Add(row);
            }

            Fill(Protocols, summary.Protocols.Select(p => new TrafficRow(p.Key, null, p.Bytes, p.Packets, summary.Bytes)));
            Fill(Hosts, summary.Hosts.Select(h => new TrafficRow(h.Label ?? h.Key, h.Label != null ? h.Key : null, h.Bytes, h.Packets, summary.Bytes)));
            Fill(Services, summary.Services.Select(s => new TrafficRow(s.Label ?? s.Key, s.Label != null ? s.Key : null, s.Bytes, s.Packets, summary.Bytes)));
            Fill(DnsQueries, summary.DnsQueries.Select(d => new TrafficRow(d.Key, null, 0, d.Packets, 0)));
            HasTraffic = true;
        }

        private void OpenPcap()
        {
            if (_pcapPath == null) return;
            try
            {
                // Ouvert par Wireshark s'il est installé ; sinon le dossier, pour le copier ailleurs.
                Process.Start(new ProcessStartInfo(_pcapPath) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                ProgramsViewModel.Reveal(_pcapPath);
            }
        }

        /// <summary>Un réseau d'exemple, pour les captures d'écran seulement.</summary>
        public void ShowDemo(string tab)
        {
            _demo = true;
            _timer.Stop();
            var segment = new LanSegment { Adapter = "Wi-Fi", Address = IPAddress.Parse("192.168.1.24"), PrefixLength = 24, Gateway = IPAddress.Parse("192.168.1.1") };
            Segments.Clear();
            Segments.Add(new SegmentChoice(segment, this));
            Segment = segment;

            LanDevice D(string ip, string? mac, string? vendor, string? netbios, string? host, int? rtt, params int[] ports)
            {
                var device = new LanDevice { Address = ip, Mac = mac, Vendor = vendor, NetBiosName = netbios, HostName = host, RoundTripMs = rtt, IsSelf = ip == "192.168.1.24", IsGateway = ip == "192.168.1.1" };
                device.OpenPorts.AddRange(ports);
                device.Kind = LanClassifier.Classify(device);
                return device;
            }

            var nas = D("192.168.1.30", "00:11:32:4A:5B:6C", "Synology Incorporated", "DS220", "ds220.home", 2, 80, 443, 445, 5000, 5001);
            nas.Shares.AddRange(new[] { "Photos", "Sauvegardes", "Musique" });
            var office = D("192.168.1.12", "3C:52:82:1A:2B:3C", "Hewlett Packard", "COMPTA-01", "compta-01.home", 1, 445, 139, 3389);
            office.Workgroup = "WORKGROUP";
            office.Shares.Add("Scans");
            Devices.Clear();
            foreach (var device in new[]
            {
                D("192.168.1.1", "F4:CA:E5:11:22:33", "FREEBOX SAS", null, "freebox.home", 1, 80, 443),
                office,
                D("192.168.1.24", "8C:16:45:AA:BB:CC", "LCFC(HeFei) Electronics Technology", "PORTABLE-MARIE", null, 0, 445, 139),
                nas,
                D("192.168.1.41", "30:05:5C:12:34:56", "Brother Industries, Ltd.", null, "BRW30055C123456.home", 3, 80, 443, 631, 9100),
                D("192.168.1.57", "DA:A1:19:5E:6F:70", null, null, "iPhone.home", 24, 62078),
                D("192.168.1.63", "54:60:09:AB:CD:EF", "Google, Inc.", null, null, 6, 8009),
            })
                Devices.Add(new LanDeviceRow(device, s => Status = s));
            ScanText = "7 appareil(s) sur 192.168.1.24/24, en 6 s.";

            Connections.Clear();
            ConnectionRow C(string process, int local, string remote, int port, string? name)
                => new ConnectionRow(new ConnectionEntry { Protocol = "TCP", LocalAddress = "192.168.1.24", LocalPort = local, RemoteAddress = remote, RemotePort = port, State = "établie" }, process) { RemoteName = name };
            foreach (var row in new[]
            {
                C("chrome.exe", 52144, "142.250.75.238", 443, "par10s39-in-f14.1e100.net"),
                C("chrome.exe", 52160, "151.101.1.140", 443, null),
                C("OUTLOOK.EXE", 51022, "52.97.146.162", 443, "outlook.office365.com"),
                C("OneDrive.exe", 50877, "13.107.42.12", 443, "onedrive.live.com"),
                C("updater.exe", 53012, "185.199.110.153", 8080, null),
                C("svchost.exe", 49712, "20.42.65.85", 443, null),
            })
            {
                _connections[row.Key] = row;
                Connections.Add(row);
            }
            Count();

            ShowTraffic(new TrafficSummary
            {
                Duration = TimeSpan.FromSeconds(30), Packets = 18_420, Bytes = 21_400_000, LocalAddress = "192.168.1.24",
                Protocols = new List<TrafficLine> { new TrafficLine { Key = "TCP", Bytes = 20_100_000, Packets = 16_900 }, new TrafficLine { Key = "UDP", Bytes = 1_250_000, Packets = 1_480 }, new TrafficLine { Key = "ICMP", Bytes = 50_000, Packets = 40 } },
                Hosts = new List<TrafficLine>
                {
                    new TrafficLine { Key = "13.107.42.12", Label = "onedrive.live.com", Bytes = 14_800_000, Packets = 11_200 },
                    new TrafficLine { Key = "142.250.75.238", Label = "www.youtube.com", Bytes = 4_100_000, Packets = 3_900 },
                    new TrafficLine { Key = "192.168.1.1", Label = null, Bytes = 610_000, Packets = 1_150 },
                },
                Services = new List<TrafficLine> { new TrafficLine { Key = "TCP 443", Label = "HTTPS", Bytes = 19_800_000, Packets = 16_100 }, new TrafficLine { Key = "UDP 53", Label = "DNS", Bytes = 90_000, Packets = 640 } },
                DnsQueries = new List<TrafficLine> { new TrafficLine { Key = "onedrive.live.com", Packets = 12 }, new TrafficLine { Key = "www.youtube.com", Packets = 8 }, new TrafficLine { Key = "telemetry.updater-cdn.net", Packets = 31 } },
                PcapPath = @"C:\Users\Marie\Documents\LDI12 Diagnostic\Captures\trafic-20261018-101500.pcap",
            });

            if (tab == "connections") IsConnectionsTab = true;
            else if (tab == "traffic") IsTrafficTab = true;
        }
    }
}
