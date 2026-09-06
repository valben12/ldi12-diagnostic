using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Monitoring;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Source d'échantillons de la surveillance en direct.
    /// </summary>
    /// <remarks>
    /// <b>Une surveillance doit peser moins que ce qu'elle observe.</b> Un relevé par seconde qui
    /// coûterait cent millisecondes de processeur fausserait sa propre mesure et ralentirait la
    /// machine d'un client déjà venu se plaindre de lenteur. Chaque source est donc choisie pour
    /// son coût autant que pour sa justesse :
    /// <list type="bullet">
    /// <item>processeur, mémoire et processus par les API natives déjà en place ;</item>
    /// <item>fréquence par <c>CallNtPowerInformation</c>, quelques microsecondes ;</item>
    /// <item>réseau par les compteurs d'interface, la liste des cartes n'étant réénumérée que de
    /// loin en loin ;</item>
    /// <item>température par la couche de capteurs matériels, <b>une fois toutes les cinq
    /// secondes</b> : elle rouvre la bibliothèque à chaque lecture, ce qui est parfaitement
    /// acceptable pendant une analyse et beaucoup trop cher à la seconde.</item>
    /// </list>
    /// <para>
    /// Les tours où la température n'est pas relevée sont marqués « non collecté » et non
    /// « manquant » : le premier est une cadence, le second serait un capteur en panne, et les
    /// confondre ferait apparaître une machine sans sonde là où il n'y a qu'un rythme.
    /// </para>
    /// </remarks>
    public sealed class LiveMetricSource : ILiveMetricSource
    {
        private const string Category = "Surveillance";

        /// <summary>Un relevé de température tous les cinq tours.</summary>
        private const int TemperatureEvery = 5;

        /// <summary>Catégorie de compteurs des zones thermiques ACPI. Absente avant Windows 8.</summary>
        private const string ThermalCategory = "Thermal Zone Information";

        /// <summary>Bornes au-delà desquelles une zone ne mesure manifestement pas un composant.</summary>
        private const double PlausibleMinimum = 20;
        private const double PlausibleMaximum = 110;

        /// <summary>La liste des cartes réseau est réénumérée toutes les trente mesures.</summary>
        private const int InterfaceRefreshEvery = 30;

        /// <summary>Au-delà, la liste cesse d'aider et devient un gestionnaire des tâches.</summary>
        private const int BusiestCount = 5;

        private readonly INativeSystemApi _native;
        private readonly IAdvancedSensorApi _sensors;
        private readonly ILdiLogger _logger;
        private readonly int _selfProcessId;

        private NetworkInterface[]? _interfaces;
        private List<PerformanceCounter>? _thermalCounters;
        private int _tick;

        public LiveMetricSource(INativeSystemApi native, IAdvancedSensorApi sensors, ILdiLogger logger)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
            _sensors = sensors ?? throw new ArgumentNullException(nameof(sensors));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _selfProcessId = SafeSelfProcessId();
        }

        public async Task<LiveSample> SampleAsync(TimeSpan window, CancellationToken cancellationToken)
        {
            var tick = _tick++;

            // Les compteurs réseau sont lus avant et après la fenêtre : un débit est une
            // différence entre deux instants, jamais une valeur qu'on lit.
            var openingBytes = ReadTotalBytes();
            var openingAt = DateTimeOffset.Now;

            var cpuTask = _native.ReadCpuUsagePercentAsync(window, cancellationToken);
            var processTask = _native.ReadProcessUsageAsync(window, cancellationToken);

            await Task.WhenAll(cpuTask, processTask).ConfigureAwait(false);

            var cpu = await cpuTask.ConfigureAwait(false);
            var processes = await processTask.ConfigureAwait(false);
            var memory = _native.ReadMemoryStatus();

            var network = Throughput(openingBytes, openingAt);
            var frequency = ReadFrequency(out var rated);

            return new LiveSample
            {
                At = DateTimeOffset.Now,
                CpuPercent = cpu,
                MemoryPercent = memory.Map(status => status.UsagePercent),
                CommitRatioPercent = CommitRatio(memory),
                CpuCelsius = tick % TemperatureEvery == 0
                    ? ReadTemperature()
                    : Measured.NotCollected<double>(),
                CpuMegahertz = frequency,
                CpuMaxMegahertz = rated,
                NetworkBytesPerSecond = network,
                Busiest = Busiest(processes),
            };
        }

        private static Measured<double> CommitRatio(Measured<MemoryStatus> memory)
        {
            if (!memory.HasValue)
                return Measured.Missing<double>(
                    memory.Reason ?? "L'état de la mémoire n'a pas pu être lu.", DataSource.NativeApi);

            var status = memory.Value;
            if (status.TotalBytes <= 0)
                return Measured.Missing<double>(
                    "La mémoire installée n'est pas connue : le rapport de validation est incalculable.",
                    DataSource.NativeApi);

            return Measured.Ok(
                Math.Round(100d * status.CommittedBytes / status.TotalBytes, 1), DataSource.NativeApi);
        }

        /// <summary>
        /// Les processus les plus gourmands du tour, le nôtre marqué.
        /// </summary>
        /// <remarks>
        /// Notre propre processus n'est ni masqué ni exclu du classement : la charge qu'il produit
        /// est réelle, et un technicien qui verrait la liste sans lui se demanderait où sont
        /// passés ses pourcentages. Il est marqué, et c'est la session de surveillance qui
        /// l'écarte de ses conclusions.
        /// <para>
        /// « Nous », c'est aussi le processus d'isolation des sondes : quand une analyse tourne
        /// pendant la surveillance, la charge qu'il produit est la nôtre, pas celle du client.
        /// </para>
        /// </remarks>
        private IReadOnlyList<LiveProcessLoad> Busiest(Measured<IReadOnlyList<ProcessUsage>> processes)
        {
            if (!processes.HasValue) return Array.Empty<LiveProcessLoad>();

            var sorted = new List<ProcessUsage>(processes.Value);
            sorted.Sort((a, b) => b.CpuPercent.Or(0d).CompareTo(a.CpuPercent.Or(0d)));

            var kept = Math.Min(BusiestCount, sorted.Count);
            var busiest = new List<LiveProcessLoad>(kept);

            for (var index = 0; index < kept; index++)
            {
                var process = sorted[index];
                busiest.Add(new LiveProcessLoad
                {
                    Name = process.Name,
                    ProcessId = process.ProcessId,
                    CpuPercent = process.CpuPercent,
                    WorkingSetBytes = process.WorkingSetBytes,
                    IsSelf = IsOurs(process),
                });
            }

            return busiest;
        }

        private bool IsOurs(ProcessUsage process)
            => process.ProcessId == _selfProcessId ||
               process.Name.StartsWith("LDI12", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Le paquet processeur si les capteurs matériels sont actifs, sinon la zone la plus chaude.
        /// </summary>
        /// <remarks>
        /// La solution de repli n'est pas la même mesure et ne prétend pas l'être : une zone du
        /// firmware suit le processeur, le chipset ou l'air d'admission selon la carte mère, et
        /// personne ne dit lequel. Elle reste utile (une courbe qui monte sous charge et
        /// redescend à l'arrêt raconte quelque chose du refroidissement) à condition d'être
        /// nommée pour ce qu'elle est. Sans elle, la surveillance n'avait aucune température à
        /// montrer dès que le pilote n'était pas chargé, c'est-à-dire presque toujours.
        /// </remarks>
        private Measured<double> ReadTemperature()
        {
            var readings = _sensors.Read();
            if (!readings.HasValue) return ReadThermalZone(readings.Reason);

            // Le paquet processeur, et lui seul : les sondes de carte mère mesurent l'air ambiant
            // ou le chipset, et les mélanger produirait une courbe qui ne suit rien.
            var hottest = double.MinValue;
            foreach (var reading in readings.Value)
            {
                if (!reading.IsCpu || reading.Kind != SensorKind.Temperature) continue;
                if (reading.Value.HasValue && reading.Value.Value > hottest) hottest = reading.Value.Value;
            }

            return hottest > double.MinValue
                ? Measured.Ok(Math.Round(hottest, 1), DataSource.NativeApi)
                : ReadThermalZone("Les capteurs répondent, mais aucun ne concerne le processeur.");
        }

        /// <summary>
        /// La plus chaude des zones thermiques du firmware, par compteur de performance.
        /// </summary>
        /// <remarks>
        /// Par le compteur et non par WMI : la lecture est synchrone, elle revient toutes les cinq
        /// secondes, et ouvrir une requête WMI à ce rythme coûterait plus que la mesure. Les
        /// instances sont retenues d'un tour sur l'autre, leur énumération étant la partie lente.
        /// </remarks>
        private Measured<double> ReadThermalZone(string? sensorReason)
        {
            var counters = ThermalCounters();
            if (counters.Count == 0)
                return Measured.Missing<double>(
                    sensorReason ?? "Cette machine n'expose aucune zone thermique.",
                    DataSource.PerformanceCounter);

            var hottest = double.MinValue;
            foreach (var counter in counters)
            {
                try
                {
                    // Le compteur rend des kelvins entiers ; la conversion est celle d'ACPI.
                    var celsius = counter.NextValue() - 273.15;
                    if (celsius > PlausibleMinimum && celsius < PlausibleMaximum && celsius > hottest)
                        hottest = celsius;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException)
                {
                    // Une zone disparaît quand le firmware la retire : un portable qu'on débranche
                    // en est capable. Les autres restent lisibles.
                }
            }

            return hottest > double.MinValue
                ? Measured.Ok(Math.Round(hottest, 1), DataSource.PerformanceCounter)
                : Measured.Missing<double>(
                    "Aucune zone thermique ne rend une valeur crédible sur cette machine.",
                    DataSource.PerformanceCounter);
        }

        private IReadOnlyList<PerformanceCounter> ThermalCounters()
        {
            if (_thermalCounters != null) return _thermalCounters;

            var counters = new List<PerformanceCounter>();

            try
            {
                // La catégorie n'existe pas avant Windows 8 : il n'y a alors rien à lire, et cela
                // se constate une fois plutôt qu'à chaque tour.
                if (PerformanceCounterCategory.Exists(ThermalCategory))
                    foreach (var instance in new PerformanceCounterCategory(ThermalCategory).GetInstanceNames())
                        counters.Add(new PerformanceCounter(ThermalCategory, "Temperature", instance, readOnly: true));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
                _logger.Warn(Category, "Les zones thermiques n'ont pas pu être énumérées.", ex);
            }

            return _thermalCounters = counters;
        }

        private Measured<int> ReadFrequency(out Measured<int> rated)
        {
            try
            {
                if (PowerNative.TryReadFrequency(out var current, out var maximum))
                {
                    rated = maximum > 0
                        ? Measured.Ok(maximum, DataSource.NativeApi)
                        : Measured.Missing<int>("Fréquence maximale non renseignée.", DataSource.NativeApi);

                    return Measured.Ok(current, DataSource.NativeApi);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _logger.For(Category).Warn("La fréquence du processeur n'est pas lisible : " + ex.Message);
            }

            rated = Measured.Missing<int>(
                "Cette machine n'expose pas sa fréquence processeur.", DataSource.NativeApi);

            return Measured.Missing<int>(
                "Cette machine n'expose pas sa fréquence processeur.", DataSource.NativeApi);
        }

        /// <summary>Débit sur la fenêtre écoulée, ou rien tant qu'il n'y a qu'un seul point.</summary>
        private Measured<double> Throughput(long openingBytes, DateTimeOffset openingAt)
        {
            var closingBytes = ReadTotalBytes();
            var closingAt = DateTimeOffset.Now;

            if (openingBytes < 0 || closingBytes < 0)
                return Measured.Missing<double>(
                    "Les compteurs des cartes réseau n'ont pas répondu.", DataSource.NativeApi);

            var seconds = (closingAt - openingAt).TotalSeconds;
            if (seconds <= 0) return Measured.NotCollected<double>();

            // Un compteur qui recule signale une carte réapparue ou remise à zéro : le tour est
            // perdu, ce qui vaut mieux qu'un pic inventé.
            var delta = closingBytes - openingBytes;
            if (delta < 0) return Measured.NotCollected<double>();

            return Measured.Ok(Math.Round(delta / seconds, 0), DataSource.NativeApi);
        }

        /// <summary>
        /// Octets émis et reçus, toutes cartes opérationnelles confondues.
        /// </summary>
        /// <remarks>
        /// La liste des cartes est conservée d'un tour à l'autre : l'énumérer coûte bien plus
        /// cher que lire ses compteurs, et une carte qui apparaît ou disparaît pendant une
        /// surveillance est assez rare pour qu'un rafraîchissement périodique suffise.
        /// </remarks>
        private long ReadTotalBytes()
        {
            try
            {
                if (_interfaces == null || _tick % InterfaceRefreshEvery == 0)
                    _interfaces = NetworkInterface.GetAllNetworkInterfaces();

                long total = 0;
                foreach (var adapter in _interfaces)
                {
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;

                    var statistics = adapter.GetIPv4Statistics();
                    total += statistics.BytesReceived + statistics.BytesSent;
                }

                return total;
            }
            catch (Exception ex) when (ex is NetworkInformationException || ex is PlatformNotSupportedException)
            {
                // Une carte retirée pendant la surveillance fait échouer la lecture de ses
                // compteurs : la liste est jetée pour être réénumérée au tour suivant.
                _interfaces = null;
                _logger.For(Category).Warn("Compteurs réseau indisponibles : " + ex.Message);
                return -1;
            }
        }

        private static int SafeSelfProcessId()
        {
            try
            {
                using (var process = Process.GetCurrentProcess()) return process.Id;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }
}
