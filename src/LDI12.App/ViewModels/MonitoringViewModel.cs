using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LDI12.Actions;
using LDI12.Actions.Journal;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Monitoring;
using LDI12.Engine.Monitoring;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Une mesure suivie, telle qu'elle s'affiche : sa courbe et ses trois chiffres.
    /// </summary>
    /// <remarks>
    /// La courbe est tracée dans un repère fixe de cent sur trente-six, mis à l'échelle par la
    /// vue. Les bornes verticales sont celles de la grandeur (zéro à cent pour un pourcentage)
    /// et non celles des valeurs relevées : une échelle qui se recale sur ses propres données
    /// transformerait une machine parfaitement stable entre 31 et 32 % en montagne russe.
    /// </remarks>
    public sealed class MetricCard : ObservableObject
    {
        private const double PlotWidth = 100;
        private const double PlotHeight = 36;

        /// <summary>Points tracés : quatre minutes d'historique à un relevé par seconde.</summary>
        private const int PlottedPoints = 240;

        private readonly double _floor;
        private readonly double _ceiling;
        private readonly Func<double, string> _format;
        private readonly bool _autoScale;

        private PointCollection _points = new PointCollection();
        private string _value = "-";
        private string _average = string.Empty;
        private string _extreme = string.Empty;
        private string? _reason;
        private bool _hasData;

        public MetricCard(
            string title, string subtitle, double floor, double ceiling,
            Func<double, string> format, bool autoScale = false)
        {
            Title = title;
            Subtitle = subtitle;
            _floor = floor;
            _ceiling = ceiling;
            _format = format;
            _autoScale = autoScale;
        }

        public string Title { get; }

        /// <summary>Ce que la mesure décrit, en une ligne. Un chiffre sans légende ne se lit pas.</summary>
        public string Subtitle { get; }

        public PointCollection Points { get => _points; private set => Set(ref _points, value); }
        public string Value { get => _value; private set => Set(ref _value, value); }
        public string Average { get => _average; private set => Set(ref _average, value); }
        public string Extreme { get => _extreme; private set => Set(ref _extreme, value); }
        public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

        /// <summary>Renseignée si et seulement si la mesure a manqué. Jamais une courbe plate muette.</summary>
        public string? Reason { get => _reason; private set => Set(ref _reason, value); }

        public bool HasReason => Reason != null;

        /// <summary>
        /// Reprend la carte sur l'état courant de la mesure.
        /// </summary>
        /// <param name="note">
        /// Réserve propre à cette mesure, quand elle en a une. Sert à la fréquence : une courbe
        /// parfaitement plate à 3801 MHz sous un titre qui promet « ce que le processeur tient
        /// réellement » se contredirait toute seule, la réserve étant plus bas dans la page.
        /// </param>
        public void Update(WatchTrack track, string? note = null)
        {
            HasData = track.HasData;
            Reason = note ?? (track.Missing > 0 ? track.Reason : null);

            if (!track.HasData)
            {
                Value = "-";
                Average = string.Empty;
                Extreme = string.Empty;
                Points = new PointCollection();
                Raise(nameof(HasReason));
                return;
            }

            Value = _format(track.Last);
            Average = "moyenne " + _format(track.Average);
            Extreme = "maximum " + _format(track.Max);
            Points = Plot(track);
            Raise(nameof(HasReason));
        }

        private PointCollection Plot(WatchTrack track)
        {
            var values = track.Tail(PlottedPoints);
            var points = new PointCollection();
            if (values.Count == 0) return points;

            // Une échelle automatique n'a de sens que là où la grandeur n'a pas de plafond connu :
            // un débit réseau peut valoir dix octets ou dix mégaoctets par seconde.
            var ceiling = _autoScale ? Math.Max(track.Max, 1d) : _ceiling;
            var span = Math.Max(ceiling - _floor, 0.0001d);
            var step = values.Count > 1 ? PlotWidth / (values.Count - 1) : PlotWidth;

            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                if (double.IsNaN(value)) continue;

                var clamped = Math.Min(Math.Max(value, _floor), ceiling);
                var y = PlotHeight - ((clamped - _floor) / span * PlotHeight);
                points.Add(new Point(index * step, y));
            }

            return points;
        }
    }

    public sealed class ProcessItem
    {
        public string Name { get; init; } = string.Empty;
        public string Load { get; init; } = string.Empty;
        public string Memory { get; init; } = string.Empty;

        /// <summary>Le logiciel de diagnostic lui-même : dit, pour ne pas être imputé au client.</summary>
        public bool IsSelf { get; init; }
    }

    public sealed class ObservationItem
    {
        public string Statement { get; init; } = string.Empty;
        public string Explanation { get; init; } = string.Empty;
        public Severity Severity { get; init; }
    }

    /// <summary>
    /// Écran de surveillance en direct.
    /// </summary>
    /// <remarks>
    /// Le diagnostic décrit une machine à un instant ; cet écran la regarde vivre. C'est la seule
    /// façon d'attraper ce qui ne dure pas : la charge qui monte à l'ouverture d'un logiciel, la
    /// température qui grimpe au bout de dix minutes, le programme qui se réveille toutes les
    /// trente secondes.
    /// <para>
    /// <b>Rien ne démarre tout seul.</b> Une surveillance consomme du processeur sur la machine
    /// qu'elle observe : la lancer d'office fausserait la mesure et pèserait sur une machine
    /// venue se plaindre de lenteur. Elle démarre sur un geste, s'arrête sur un autre, et s'arrête
    /// d'elle-même au bout de deux heures.
    /// </para>
    /// </remarks>
    public sealed class MonitoringViewModel : ObservableObject
    {
        private const string Category = "Surveillance";

        /// <summary>Fenêtre d'un relevé : la cadence de l'écran est celle-ci.</summary>
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private readonly DiagnosticService _diagnostics;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;

        private WatchSession _session = new WatchSession();
        private CancellationTokenSource? _running;
        private bool _isRunning;
        private string _status = "La surveillance n'est pas lancée : rien n'est mesuré tant que vous ne l'avez pas demandé.";
        private string _headline = string.Empty;
        private string _elapsed = string.Empty;
        private bool _hasSamples;

        public MonitoringViewModel(DiagnosticService diagnostics, InterventionJournal journal, ILdiLogger logger)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
            StopCommand = new RelayCommand(() => Stop("Surveillance arrêtée."), () => IsRunning);
            RecordCommand = new RelayCommand(Record, () => !IsRunning && HasSamples);

            Cards = new ObservableCollection<MetricCard>
            {
                Cpu, Memory, Temperature, Frequency, Commit, Network,
            };
        }

        public MetricCard Cpu { get; } = new MetricCard(
            "Processeur", "part du temps où le processeur travaille", 0, 100, Percent);

        public MetricCard Memory { get; } = new MetricCard(
            "Mémoire occupée", "part de la mémoire vive utilisée", 0, 100, Percent);

        public MetricCard Commit { get; } = new MetricCard(
            "Mémoire réclamée", "au-delà de 100 %, Windows compense sur le disque", 0, 200, Percent);

        // Deux sources possibles, et le sous-titre les nomme : la courbe ne veut pas dire la
        // même chose selon que le pilote de capteurs est chargé ou non.
        public MetricCard Temperature { get; } = new MetricCard(
            "Température", "processeur si les capteurs sont actifs, sinon zone thermique la plus chaude",
            20, 105, Celsius);

        public MetricCard Frequency { get; } = new MetricCard(
            "Fréquence du processeur", "ce que le processeur tient réellement", 0, 5000, Megahertz);

        public MetricCard Network { get; } = new MetricCard(
            "Réseau", "émission et réception confondues", 0, 0, Throughput, autoScale: true);

        public ObservableCollection<MetricCard> Cards { get; }

        public ObservableCollection<ProcessItem> Busiest { get; } = new ObservableCollection<ProcessItem>();

        public ObservableCollection<ObservationItem> Observations { get; } = new ObservableCollection<ObservationItem>();

        public ObservableCollection<string> Caveats { get; } = new ObservableCollection<string>();

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RecordCommand { get; }

        public bool IsRunning
        {
            get => _isRunning;
            private set { if (Set(ref _isRunning, value)) RaiseCommandStates(); }
        }

        public bool HasSamples
        {
            get => _hasSamples;
            private set { if (Set(ref _hasSamples, value)) RaiseCommandStates(); }
        }

        public string Status { get => _status; private set => Set(ref _status, value); }
        public string Headline { get => _headline; private set => Set(ref _headline, value); }
        public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

        public bool HasObservations => Observations.Count > 0;
        public bool HasCaveats => Caveats.Count > 0;

        /// <summary>Le journal a reçu quelque chose : l'écran des rapports doit se relire.</summary>
        public event EventHandler? JournalChanged;

        private async Task StartAsync()
        {
            if (IsRunning) return;

            _running?.Dispose();
            _running = new CancellationTokenSource();
            var token = _running.Token;

            ILiveMetricSource source;
            try
            {
                var services = await _diagnostics.GetPlatformAsync(token).ConfigureAwait(true);
                source = services.CreateLiveMetrics();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La surveillance n'a pas pu démarrer.", ex);
                Status = "La surveillance n'a pas pu démarrer : " + ex.Message;
                return;
            }

            // Une session neuve : mélanger deux observations séparées par une intervention
            // produirait une moyenne qui ne décrit ni l'avant ni l'après.
            _session = new WatchSession(WatchLimits.From(_diagnostics.ActiveProfile?.Invoke().Limits!));
            Observations.Clear();
            Caveats.Clear();
            Busiest.Clear();
            HasSamples = false;
            IsRunning = true;
            Status = "Surveillance en cours. Faites travailler la machine : ce qui ne se produit pas ne se mesure pas.";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var sample = await source.SampleAsync(Window, token).ConfigureAwait(true);
                    _session.Add(sample);
                    Refresh(sample);

                    if (!_session.Exhausted) continue;

                    Stop("Surveillance arrêtée d'elle-même au bout de deux heures : une observation " +
                         "oubliée continuerait de peser sur la machine du client.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Arrêt demandé : l'état de la session est conservé, c'est lui qu'on consigne.
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "La surveillance s'est interrompue.", ex);
                Stop("La surveillance s'est interrompue : " + ex.Message);
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>Arrêt à la fermeture de la fenêtre : plus rien ne doit tourner ensuite.</summary>
        public void Shutdown()
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
            IsRunning = false;
        }

        private void Stop(string status)
        {
            _running?.Cancel();
            IsRunning = false;
            Status = status;
        }

        private void Refresh(LiveSample sample)
        {
            Cpu.Update(_session.Cpu);
            Memory.Update(_session.Memory);
            Commit.Update(_session.Commit);
            Temperature.Update(_session.Temperature);
            Frequency.Update(_session.Frequency, _session.Frequency.Constant
                ? "Valeur figée sur toute la session : cette machine rapporte sa fréquence " +
                  "nominale et non celle qu'elle tient."
                : null);
            Network.Update(_session.Network);

            HasSamples = _session.SampleCount > 0;
            Elapsed = ValueFormat.Duration(_session.Duration) + " : " + _session.SampleCount + " relevés";

            Busiest.Clear();
            foreach (var process in sample.Busiest)
            {
                Busiest.Add(new ProcessItem
                {
                    Name = process.IsSelf ? process.Name + " (ce logiciel)" : process.Name,
                    Load = process.CpuPercent.HasValue ? Percent(process.CpuPercent.Value) : "-",
                    Memory = process.WorkingSetBytes.HasValue
                        ? ValueFormat.Bytes(process.WorkingSetBytes.Value)
                        : "-",
                    IsSelf = process.IsSelf,
                });
            }

            var report = _session.Conclude();
            Headline = report.Headline;

            Replace(Observations, report.Observations);
            Replace(Caveats, report.Caveats);
        }

        private void Replace(ObservableCollection<ObservationItem> target, IReadOnlyList<WatchObservation> source)
        {
            // Reconstruction complète à chaque relevé : une conclusion peut disparaître aussi bien
            // qu'apparaître, et une liste qui ne ferait qu'ajouter garderait à l'écran ce qui
            // n'est plus vrai.
            if (target.Count == source.Count)
            {
                var identical = true;
                for (var index = 0; index < source.Count; index++)
                    if (target[index].Statement != source[index].Statement) { identical = false; break; }

                if (identical) return;
            }

            target.Clear();
            foreach (var observation in source)
                target.Add(new ObservationItem
                {
                    Statement = observation.Statement,
                    Explanation = observation.Explanation,
                    Severity = observation.Severity,
                });

            Raise(nameof(HasObservations));
        }

        private void Replace(ObservableCollection<string> target, IReadOnlyList<string> source)
        {
            if (target.Count == source.Count)
            {
                var identical = true;
                for (var index = 0; index < source.Count; index++)
                    if (target[index] != source[index]) { identical = false; break; }

                if (identical) return;
            }

            target.Clear();
            foreach (var caveat in source) target.Add(caveat);
            Raise(nameof(HasCaveats));
        }

        /// <summary>
        /// Consigne la session dans le journal d'intervention.
        /// </summary>
        /// <remarks>
        /// En « prévisualisation » et non en « exécution » : la surveillance n'a rien modifié sur
        /// la machine. Les réserves partent dans le journal avec les conclusions : elles sont ce
        /// qui empêche de relire, trois semaines plus tard, « rien à signaler » comme un
        /// certificat de bonne santé.
        /// </remarks>
        private void Record()
        {
            var report = _session.Conclude();
            var details = new List<string>();

            foreach (var observation in report.Observations)
                details.Add(observation.Statement);

            foreach (var caveat in report.Caveats)
                details.Add("Réserve : " + caveat);

            _journal.Record(
                InterventionKind.Preview, ActionIds.Watch, "Surveillance en direct",
                report.LongEnough ? "Terminée" : "Trop courte pour conclure",
                report.Headline, duration: report.Duration, details: details);

            Status = "Session consignée dans le journal d'intervention.";
            JournalChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseCommandStates()
        {
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static string Percent(double value)
            => value.ToString("0.#", CultureInfo.CurrentCulture) + " %";

        private static string Celsius(double value)
            => value.ToString("0.#", CultureInfo.CurrentCulture) + " °C";

        private static string Megahertz(double value)
            => value.ToString("0", CultureInfo.CurrentCulture) + " MHz";

        private static string Throughput(double value)
            => ValueFormat.Bytes((long)value) + "/s";
    }
}
