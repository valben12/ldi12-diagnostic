using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Journal;
using LDI12.Core.Benchmarks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;

namespace LDI12.Actions.Benchmarks
{
    /// <summary>Ce que le technicien a demandé de mesurer, et dans quelles conditions.</summary>
    public sealed class BenchmarkRequest
    {
        /// <summary>Plan du disque. Nul quand le disque n'est pas mesuré.</summary>
        public StorageBenchmarkPlan? Storage { get; init; }

        public bool Processor { get; init; } = true;

        public bool Memory { get; init; } = true;

        /// <summary>Type de média déclaré par le disque : sert à confronter la mesure à l'annonce.</summary>
        public StorageMediaType DeclaredMedia { get; init; }

        public bool SystemVolume { get; init; }

        /// <summary>Charge du processeur relevée juste avant. Sert aux réserves, jamais aux conclusions.</summary>
        public Measured<double> BackgroundCpuPercent { get; init; }

        /// <summary>Machine sur batterie : Windows y bride volontairement processeur et disque.</summary>
        public Measured<bool> OnBattery { get; init; }
    }

    /// <summary>
    /// Exécute les mesures de performance.
    /// </summary>
    /// <remarks>
    /// <b>La seule partie du logiciel qui sollicite volontairement la machine du client.</b> Tout
    /// le reste observe ; celle-ci charge le processeur à fond et écrit deux cent cinquante
    /// mégaoctets sur le disque. Elle est donc soumise aux mêmes règles qu'une réparation : ce qui
    /// va se passer est montré avant, rien ne démarre sans un geste, tout est annulable, et le
    /// journal d'intervention en garde la trace.
    /// <para>
    /// Les réserves sont relevées <b>au moment de la mesure</b> et non écrites après coup : une
    /// machine déjà occupée ou un portable sur batterie suffisent à faire d'un mauvais chiffre un
    /// artefact, et personne ne s'en souviendra en relisant le journal trois semaines plus tard.
    /// </para>
    /// </remarks>
    public sealed class BenchmarkRunner
    {
        private const string Category = "Mesures";

        /// <summary>Volume écrit par la mesure du disque.</summary>
        public const long DefaultStorageBytes = 256L * 1024 * 1024;

        /// <summary>Au-delà, la machine travaillait déjà pour quelqu'un d'autre.</summary>
        private const double BusyBackgroundPercent = 20;

        private const double Megabyte = 1024 * 1024;

        private readonly IStorageBenchmarkGateway _storage;
        private readonly InterventionJournal _journal;
        private readonly ILdiLogger _logger;

        public BenchmarkRunner(
            IStorageBenchmarkGateway storage, InterventionJournal journal, ILdiLogger logger)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Établit ce qui serait écrit sur un volume, sans rien écrire.</summary>
        public StorageBenchmarkPlan Plan(string volumeRoot, long bytes = DefaultStorageBytes)
            => _storage.Plan(volumeRoot, bytes);

        public async Task<BenchmarkRun> RunAsync(
            BenchmarkRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var stopwatch = Stopwatch.StartNew();
            var measures = new List<BenchmarkMeasure>();
            var verdicts = new List<BenchmarkVerdict>();
            var caveats = new List<string>();

            Conditions(request, caveats);

            // Hors du fil d'interface : ces boucles occupent tous les cœurs pendant plusieurs
            // secondes, et une fenêtre figée pendant une mesure ferait croire à un plantage.
            await Task.Run(() =>
            {
                if (request.Storage != null) Storage(request, measures, verdicts, caveats, progress, cancellationToken);
                if (request.Processor) Processor(measures, verdicts, progress, cancellationToken);
                if (request.Memory) MemoryBandwidth(measures, progress, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            var run = new BenchmarkRun
            {
                Duration = stopwatch.Elapsed,
                Measures = measures,
                Verdicts = verdicts,
                Caveats = caveats,
                Headline = Headline(measures, verdicts),
            };

            Record(request, run);
            return run;
        }

        /// <summary>
        /// Les conditions de la mesure, relevées avant elle.
        /// </summary>
        /// <remarks>
        /// Ce ne sont pas des précautions de style : une machine à 60 % de charge au départ rendra
        /// des chiffres de processeur faux de moitié, et un portable sur batterie peut afficher le
        /// tiers de ce dont il est capable. Sans ces deux phrases, la mesure serait un piège.
        /// </remarks>
        private static void Conditions(BenchmarkRequest request, ICollection<string> caveats)
        {
            var background = request.BackgroundCpuPercent;
            if (background.HasValue && background.Value >= BusyBackgroundPercent)
            {
                caveats.Add(
                    "La machine était déjà occupée à " +
                    background.Value.ToString("0", CultureInfo.CurrentCulture) +
                    " % au lancement : ces chiffres sont un plancher, pas ce dont la machine est capable.");
            }

            if (request.OnBattery.Or(false))
            {
                caveats.Add(
                    "Machine sur batterie : Windows y bride volontairement le processeur et les " +
                    "accès disque. Refaire la mesure branchée sur le secteur donnera d'autres chiffres.");
            }
        }

        private void Storage(
            BenchmarkRequest request, ICollection<BenchmarkMeasure> measures,
            ICollection<BenchmarkVerdict> verdicts, ICollection<string> caveats,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var plan = request.Storage!;
            if (!plan.CanRun)
            {
                caveats.Add(plan.Refusal!);
                return;
            }

            var throughput = _storage.Measure(plan, progress, cancellationToken);

            measures.Add(Rate(BenchmarkKind.SequentialWrite, "Écriture continue",
                "ce que le disque encaisse sur un gros fichier", throughput.WriteBytesPerSecond));

            measures.Add(Rate(BenchmarkKind.SequentialRead, "Lecture continue",
                "ce que le disque rend sur un gros fichier", throughput.ReadBytesPerSecond));

            measures.Add(new BenchmarkMeasure
            {
                Kind = BenchmarkKind.RandomRead,
                Label = "Accès isolé",
                Meaning = "le temps d'aller chercher quatre kilo-octets au hasard",
                Value = throughput.RandomReadMilliseconds,
                Display = throughput.RandomReadMilliseconds.HasValue
                    ? BenchmarkVerdicts.Milliseconds(throughput.RandomReadMilliseconds.Value)
                    : "-",
            });

            foreach (var verdict in BenchmarkVerdicts.Describe(
                         throughput, request.DeclaredMedia, request.SystemVolume))
                verdicts.Add(verdict);

            if (throughput.Limitation != null) caveats.Add(throughput.Limitation);

            caveats.Add(
                "L'écriture porte sur " + ValueFormat.Bytes(plan.Bytes) + " : assez pour mesurer un " +
                "disque, pas assez pour épuiser la mémoire tampon d'un disque à mémoire flash. Une " +
                "copie de plusieurs gigaoctets serait plus lente que ce chiffre.");
        }

        private void Processor(
            ICollection<BenchmarkMeasure> measures, ICollection<BenchmarkVerdict> verdicts,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report("Calcul sur un seul cœur…");
            var single = ComputeBenchmark.SingleThread(cancellationToken);

            progress?.Report("Calcul sur tous les cœurs…");
            var parallel = ComputeBenchmark.MultiThread(cancellationToken);

            measures.Add(Rate(BenchmarkKind.Cpu, "Calcul, un cœur",
                "ce qu'un seul cœur traite, ce que ressent un logiciel qui n'en utilise qu'un",
                Measured.Ok(single, DataSource.Inferred)));

            measures.Add(Rate(BenchmarkKind.CpuParallel, "Calcul, tous les cœurs",
                "le même travail réparti sur " + Environment.ProcessorCount + " cœurs logiques",
                Measured.Ok(parallel, DataSource.Inferred)));

            if (single <= 0) return;

            var verdict = BenchmarkVerdicts.DescribeParallelGain(parallel / single, Environment.ProcessorCount);
            if (verdict != null) verdicts.Add(verdict);
        }

        private void MemoryBandwidth(
            ICollection<BenchmarkMeasure> measures, IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("Copie en mémoire…");
            var bandwidth = ComputeBenchmark.MemoryBandwidth(cancellationToken);

            measures.Add(Rate(BenchmarkKind.Memory, "Mémoire vive",
                "débit d'une copie en mémoire, lecture et écriture confondues",
                Measured.Ok(bandwidth, DataSource.Inferred)));
        }

        private static BenchmarkMeasure Rate(
            BenchmarkKind kind, string label, string meaning, Measured<double> bytesPerSecond)
            => new BenchmarkMeasure
            {
                Kind = kind,
                Label = label,
                Meaning = meaning,
                Value = bytesPerSecond,
                Display = bytesPerSecond.HasValue
                    ? BenchmarkVerdicts.Rate(bytesPerSecond.Value / Megabyte)
                    : "-",
            };

        private static string Headline(
            IReadOnlyList<BenchmarkMeasure> measures, IReadOnlyList<BenchmarkVerdict> verdicts)
        {
            if (measures.Count == 0) return "Aucune mesure n'a pu être menée.";

            foreach (var verdict in verdicts)
                if (verdict.Severity >= Severity.Warning) return verdict.Statement;

            return measures.Count + " mesures relevées. Elles décrivent cette machine, et se " +
                   "comparent à celles de la même machine, pas à un classement.";
        }

        /// <summary>
        /// Consigne la campagne dans le journal d'intervention.
        /// </summary>
        /// <remarks>
        /// En « exécution » quand le disque a été mesuré, en « prévisualisation » sinon : la
        /// mesure du disque écrit réellement un fichier sur la machine du client, même s'il
        /// disparaît aussitôt. La distinction du journal porte sur ce que la machine a subi, pas
        /// sur ce qu'il en reste.
        /// </remarks>
        private void Record(BenchmarkRequest request, BenchmarkRun run)
        {
            var details = new List<string>();
            foreach (var measure in run.Measures) details.Add(measure.Label + " : " + measure.Display);
            foreach (var verdict in run.Verdicts) details.Add(verdict.Statement);
            foreach (var caveat in run.Caveats) details.Add("Réserve : " + caveat);

            var wroteToDisk = request.Storage != null && request.Storage.CanRun;

            _journal.Record(
                wroteToDisk ? InterventionKind.Execution : InterventionKind.Preview,
                ActionIds.Benchmark,
                "Mesures de performance",
                run.Measures.Count > 0 ? "Terminées" : "Sans résultat",
                run.Headline,
                duration: run.Duration,
                details: details);

            _logger.For(Category).Info(
                "Campagne de mesures terminée en " + (int)run.Duration.TotalSeconds + " s : " +
                run.Measures.Count + " mesures, " + run.Verdicts.Count + " conclusions.");
        }
    }
}
