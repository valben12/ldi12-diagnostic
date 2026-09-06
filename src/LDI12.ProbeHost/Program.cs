using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine;
using LDI12.Engine.Orchestration;
using LDI12.Engine.Profile;
using LDI12.Platform;
using LDI12.Platform.Isolation;
using LDI12.Platform.Logging;
using LDI12.Reports;
using LDI12.Reports.Html;
using LDI12.Reports.Json;

namespace LDI12.ProbeHost
{
    /// <summary>
    /// Hôte de sondes : exécutable satellite de LDI12 Diagnostic.
    /// </summary>
    /// <remarks>
    /// Trois rôles, dont deux implémentés :
    /// <list type="number">
    /// <item>mode headless : collecte, analyse et écriture du diagnostic (phases 0 à 2) ;</item>
    /// <item>isolation des sondes bloquantes, dans un processus que l'application peut tuer ;</item>
    /// <item>exécution élevée à la demande, une seule invite UAC par session (phase 5).</item>
    /// </list>
    /// </remarks>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitError = 1;
        private const int ExitUnsupportedPlatform = 2;

        private static int Main(string[] args)
        {
            // Le compte rendu console et les rapports produits ici sont en français : leurs
            // chiffres et leurs dates doivent l'être aussi, quelle que soit la machine.
            Formats.Apply();

            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Erreur inattendue : " + ex.Message);
                return ExitError;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var options = CommandLine.Parse(args);
            if (options.ShowHelp)
            {
                CommandLine.PrintUsage();
                return ExitOk;
            }

            // Mode hôte isolé : le processus n'existe que pour exécuter des sondes à la demande
            // du processus principal, et pour pouvoir être tué s'il se bloque.
            if (options.Isolated && options.ElevatedPipe != null)
            {
                using var isolatedLogger = RollingFileLogger.CreateDefault(LogLevel.Debug);
                return await IsolatedProbeHost
                    .RunAsync(options.ElevatedPipe, options.AdvancedSensors, isolatedLogger, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Mode hôte élevé, sans collecte ni analyse : le processus ne sert qu'à exécuter
            // les actions privilégiées demandées par l'application, puis il meurt.
            if (options.ElevatedPipe != null)
            {
                using var elevatedLogger = RollingFileLogger.CreateDefault(LogLevel.Debug);
                return await ElevatedHost
                    .RunAsync(options.ElevatedPipe, elevatedLogger, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var profile = LoadProfile(options);
            if (profile == null) return ExitError;

            if (options.ExportProfilePath != null)
            {
                ProfileSerializer.Save(profile, options.ExportProfilePath);
                Console.WriteLine();
                Console.WriteLine("  Barème exporté : " + Path.GetFullPath(options.ExportProfilePath));
                Console.WriteLine("  Modifier les seuils puis relancer avec --profile pour l'utiliser.");
                Console.WriteLine();
                return ExitOk;
            }

            var fileLogger = RollingFileLogger.CreateDefault(options.Verbose ? LogLevel.Trace : LogLevel.Debug);
            using var logger = new CompositeLogger(
                fileLogger,
                new ConsoleLogger(options.Verbose ? LogLevel.Debug : LogLevel.Warning));

            Console.WriteLine();
            Console.WriteLine("LDI12 Diagnostic : hôte de sondes " + SnapshotBuilder.ToolVersion());
            Console.WriteLine();

            // Mode relecture : analyser un diagnostic déjà collecté, sans toucher à la machine.
            if (options.AnalyzePath != null)
                return AnalyzeExisting(options, profile, logger, fileLogger);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.Error.WriteLine();
                Console.Error.WriteLine("Annulation demandée, arrêt en cours…");
                cancellation.Cancel();
            };

            using var services = await PlatformServices.CreateAsync(logger, cancellation.Token).ConfigureAwait(false);
            var platform = services.Platform.Profile;

            // Jamais par défaut : activer les capteurs charge un pilote noyau sur la machine.
            // En ligne de commande, cela se demande explicitement, comme dans l'écran de réglages.
            if (options.AdvancedSensors) services.Sensors.Enable(true);

            PrintPlatform(services);

            if (platform.Level == CompatibilityLevel.Unsupported)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Plateforme non prise en charge.");
                Console.Error.WriteLine("  " + platform.LevelReason);
                Console.Error.WriteLine();
                return ExitUnsupportedPlatform;
            }

            if (options.ShowFeatures) PrintFeatures(services);

            var draft = new SnapshotDraft();

            using var isolation = options.NoIsolation
                ? null
                : new ProbeIsolationHost(services.Launcher, new SnapshotCodec(), services.Sensors.Enabled, logger);

            var runner = new DiagnosticRunner(new ProbeExecutionPolicy(logger, isolation), logger);
            var progress = options.Verbose ? null : new ConsoleProgress();
            var collection = await runner
                .RunAsync(
                    CollectorCatalog.CreateAll(logger),
                    services.CreateProbeContext(draft, options.Mode),
                    options.Mode, progress, cancellation.Token)
                .ConfigureAwait(false);
            progress?.Clear();

            PrintModules(collection);

            var collected = SnapshotBuilder.Build(services, draft, collection, options.Mode);
            var analyzed = new AnalysisEngine(logger).Analyze(collected, profile);

            AnalysisRenderer.Render(analyzed, options.Detailed);
            SnapshotSerializer.Save(analyzed, options.OutputPath);
            PrintFooter(options.OutputPath, fileLogger.FilePath);
            ExportReports(analyzed, options);

            return ExitOk;
        }

        private static int AnalyzeExisting(
            CommandLine options, DiagnosticProfile profile, ILdiLogger logger, RollingFileLogger fileLogger)
        {
            SystemSnapshot snapshot;
            try
            {
                snapshot = SnapshotSerializer.Load(options.AnalyzePath!);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("  Le diagnostic n'a pas pu être relu : " + ex.Message);
                return ExitError;
            }

            Console.WriteLine("  Diagnostic relu : " + Path.GetFullPath(options.AnalyzePath!));
            Console.WriteLine("  Machine .......... " + snapshot.Machine.MachineName);
            Console.WriteLine("  Windows .......... " + snapshot.Platform.Windows.DisplayName);
            Console.WriteLine("  Collecté le ...... " + snapshot.Metadata.CreatedAt.ToString("g", CultureInfo.CurrentCulture));

            var analyzed = new AnalysisEngine(logger).Analyze(snapshot, profile);
            AnalysisRenderer.Render(analyzed, options.Detailed);

            if (options.OutputExplicit)
            {
                SnapshotSerializer.Save(analyzed, options.OutputPath);
                PrintFooter(options.OutputPath, fileLogger.FilePath);
            }
            else
            {
                Console.WriteLine();
            }

            ExportReports(analyzed, options);
            return ExitOk;
        }

        /// <summary>
        /// Écrit les trois rapports quand <c>--report</c> est demandé.
        /// </summary>
        /// <remarks>
        /// Disponible aussi bien après une collecte qu'après <c>--analyze</c> : un diagnostic
        /// archivé doit pouvoir être réédité au nom d'un autre technicien sans être refait, sinon
        /// « rejouer un dossier » signifierait retourner chez le client.
        /// </remarks>
        private static void ExportReports(SystemSnapshot snapshot, CommandLine options)
        {
            if (options.ReportDirectory == null) return;

            var context = new ReportContext
            {
                Technician = options.Technician,
                ClientReference = options.ClientReference,
            };

            Console.WriteLine("  Rapports :");
            foreach (var kind in new[] { ReportKind.Technician, ReportKind.Client, ReportKind.Json })
            {
                try
                {
                    var exported = ReportExporter.Export(snapshot, kind, options.ReportDirectory, context);
                    Console.WriteLine("    " + Label(kind).PadRight(20) + Path.GetFullPath(exported.Path));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("    " + Label(kind).PadRight(20) + "échec : " + ex.Message);
                }
            }
            Console.WriteLine();
        }

        private static string Label(ReportKind kind) => kind switch
        {
            ReportKind.Technician => "Technicien (HTML)",
            ReportKind.Client => "Client (HTML)",
            _ => "Données (JSON)",
        };

        private static DiagnosticProfile? LoadProfile(CommandLine options)
        {
            if (options.ProfilePath == null) return DiagnosticProfile.Default;

            try
            {
                return ProfileSerializer.Load<DiagnosticProfile>(options.ProfilePath);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException)
            {
                Console.Error.WriteLine("  Le barème n'a pas pu être chargé : " + ex.Message);
                return null;
            }
        }

        private static void PrintFooter(string outputPath, string logPath)
        {
            var size = new FileInfo(outputPath).Length;
            Console.WriteLine();
            Console.WriteLine("  Diagnostic écrit : " + Path.GetFullPath(outputPath));
            Console.WriteLine("                     " + FormatSize(size) + " : schéma v" + SystemSnapshot.CurrentSchemaVersion);
            Console.WriteLine("  Journal          : " + logPath);
            Console.WriteLine();
        }

        private static void PrintPlatform(PlatformServices services)
        {
            var profile = services.Platform.Profile;
            Console.WriteLine("  Machine .......... " + Environment.MachineName);
            Console.WriteLine("  Windows .......... " + profile.DisplayName);
            Console.WriteLine("  Compatibilité .... " + Describe(profile.Level) + " : " + profile.LevelReason);
            Console.WriteLine("  Élévation ........ " + Describe(services.Platform.Elevation));
        }

        private static void PrintFeatures(PlatformServices services)
        {
            var features = services.Platform.Features.All;
            Console.WriteLine();
            Console.WriteLine("  Fonctionnalités système (" + features.Count + ")");

            foreach (var feature in features)
            {
                Console.WriteLine("    " + Marker(feature.Availability) + " " + Pad(feature.DisplayName, 44) + " " +
                                  Describe(feature.Availability));
                if (feature.Availability != Availability.Available)
                {
                    Console.WriteLine("           " + feature.Reason);
                    if (!string.IsNullOrEmpty(feature.Workaround))
                        Console.WriteLine("           → " + feature.Workaround);
                }
            }
        }

        private static void PrintModules(CollectionResult collection)
        {
            var failures = 0;
            foreach (var report in collection.Reports)
                if (report.Status != Core.Probes.ProbeStatus.Ok) failures++;

            Console.WriteLine();
            Console.WriteLine("  Collecte : " + collection.Reports.Count + " module(s) en " +
                              collection.DurationMs + " ms" +
                              (failures > 0 ? ", " + failures + " module(s) partiels ou en échec" : string.Empty));

            foreach (var report in collection.Reports)
            {
                if (report.Status == Core.Probes.ProbeStatus.Ok) continue;
                Console.WriteLine("    " + Pad(report.DisplayName, 34) + report.Status);
                if (!string.IsNullOrEmpty(report.Message)) Console.WriteLine("        " + report.Message);
            }
        }

        /// <summary>Progression sur une seule ligne réécrite : lisible même dans une console 80 colonnes.</summary>
        private sealed class ConsoleProgress : IProgress<DiagnosticProgress>
        {
            private int _lastLength;

            public void Report(DiagnosticProgress value)
            {
                var text = "  [" + value.Completed.ToString(CultureInfo.InvariantCulture) + "/" +
                           value.Total.ToString(CultureInfo.InvariantCulture) + "] " + value.CurrentProbe;
                if (text.Length > 76) text = text.Substring(0, 76);
                Console.Write("\r" + text.PadRight(Math.Max(_lastLength, text.Length)));
                _lastLength = text.Length;
            }

            public void Clear()
            {
                if (_lastLength == 0) return;
                Console.Write("\r" + new string(' ', _lastLength) + "\r");
                _lastLength = 0;
            }
        }

        private static string Marker(Availability availability) => availability switch
        {
            Availability.Available => "[+]",
            Availability.Partial => "[~]",
            Availability.RequiresElevation => "[!]",
            Availability.Unavailable => "[-]",
            _ => "[?]",
        };

        private static string Describe(Availability availability) => availability switch
        {
            Availability.Available => "disponible",
            Availability.Partial => "partielle",
            Availability.RequiresElevation => "élévation requise",
            Availability.Unavailable => "indisponible",
            _ => "indéterminée",
        };

        private static string Describe(CompatibilityLevel level) => level switch
        {
            CompatibilityLevel.Full => "complète",
            CompatibilityLevel.Reduced => "réduite",
            CompatibilityLevel.Minimal => "minimale",
            _ => "non prise en charge",
        };

        private static string Describe(ElevationState state) => state switch
        {
            ElevationState.NotElevated => "utilisateur standard",
            ElevationState.Elevated => "administrateur (élevé)",
            ElevationState.ElevatedByDefault => "administrateur sans élévation (UAC désactivé ?)",
            _ => "indéterminée",
        };

        private static string Pad(string text, int width)
            => text.Length >= width ? text + " " : text + new string('.', width - text.Length);

        private static string FormatSize(long bytes)
            => bytes < 1024
                ? bytes + " octets"
                : (bytes / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " Ko";
    }

    internal sealed class CommandLine
    {
        public string OutputPath { get; private set; } = "snapshot.json";
        public bool OutputExplicit { get; private set; }
        public string? AnalyzePath { get; private set; }
        public string? ProfilePath { get; private set; }
        public string? ExportProfilePath { get; private set; }
        public RunMode Mode { get; private set; } = RunMode.Full;
        public bool Verbose { get; private set; }
        public bool Detailed { get; private set; }
        public bool ShowFeatures { get; private set; }
        public bool ShowHelp { get; private set; }
        public string? ReportDirectory { get; private set; }
        public string? Technician { get; private set; }
        public string? ClientReference { get; private set; }

        /// <summary>Nom du tube nommé sur lequel se connecter en mode hôte élevé.</summary>
        public string? ElevatedPipe { get; private set; }

        /// <summary>Autorise le chargement du pilote de capteurs matériels pour cette exécution.</summary>
        public bool AdvancedSensors { get; private set; }

        /// <summary>Mode hôte isolé : exécute des sondes à la demande, sans élévation.</summary>
        public bool Isolated { get; private set; }

        /// <summary>
        /// Exécute tout dans ce processus, sans hôte isolé.
        /// </summary>
        /// <remarks>
        /// Sert à comparer : un diagnostic doit rendre le même résultat avec et sans isolation,
        /// et c'est la seule façon de le vérifier sur une machine réelle plutôt que de l'affirmer.
        /// </remarks>
        public bool NoIsolation { get; private set; }

        public static CommandLine Parse(string[] args)
        {
            var options = new CommandLine();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--dump":
                    case "-o":
                        if (i + 1 < args.Length) { options.OutputPath = args[++i]; options.OutputExplicit = true; }
                        break;
                    case "--analyze":
                        if (i + 1 < args.Length) options.AnalyzePath = args[++i];
                        break;
                    case "--profile":
                        if (i + 1 < args.Length) options.ProfilePath = args[++i];
                        break;
                    case "--export-profile":
                        options.ExportProfilePath = i + 1 < args.Length ? args[++i] : "profil-diagnostic.json";
                        break;
                    case "--report":
                        options.ReportDirectory = i + 1 < args.Length ? args[++i] : ".";
                        break;
                    case "--technician":
                        if (i + 1 < args.Length) options.Technician = args[++i];
                        break;
                    case "--client-ref":
                        if (i + 1 < args.Length) options.ClientReference = args[++i];
                        break;
                    case "--elevated":
                        // Le nom du tube est obligatoire : sans lui, ce mode n'a aucun sens et
                        // un hôte élevé sans interlocuteur serait un processus administrateur
                        // orphelin.
                        break;
                    case "--pipe":
                        if (i + 1 < args.Length) options.ElevatedPipe = args[++i];
                        break;
                    case "--no-isolation":
                        options.NoIsolation = true;
                        break;
                    case "--isolate":
                        // Comme le mode élevé, ce mode n'a de sens qu'avec un tube : un hôte
                        // isolé sans interlocuteur serait un processus orphelin.
                        options.Isolated = true;
                        break;
                    case "--sensors":
                        options.AdvancedSensors = true;
                        break;
                    case "--quick":
                        options.Mode = RunMode.Quick;
                        break;
                    case "--details":
                        options.Detailed = true;
                        break;
                    case "--features":
                        options.ShowFeatures = true;
                        break;
                    case "--verbose":
                    case "-v":
                        options.Verbose = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                }
            }
            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("LDI12 Diagnostic : hôte de sondes " + SnapshotBuilder.ToolVersion());
            Console.WriteLine();
            Console.WriteLine("  LDI12.ProbeHost.exe [options]");
            Console.WriteLine();
            Console.WriteLine("  --dump <fichier>       Écrit le diagnostic analysé en JSON (défaut : snapshot.json)");
            Console.WriteLine("  --analyze <fichier>    Relit et analyse un diagnostic existant, sans toucher à la machine");
            Console.WriteLine("  --report <dossier>     Exporte le rapport technicien, le bilan client et le JSON");
            Console.WriteLine("  --technician <nom>     Nom porté en signature des rapports");
            Console.WriteLine("  --client-ref <ref>     Référence de dossier client portée sur les rapports");
            Console.WriteLine("  --quick                Analyse rapide : lecture pure, aucun outil externe");
            Console.WriteLine("  --sensors              Charge le pilote de capteurs matériels : température du");
            Console.WriteLine("                         processeur et sondes de la carte mère (administrateur requis)");
            Console.WriteLine("  --details              Affiche le grand livre des pénalités du score");
            Console.WriteLine("  --features             Affiche l'état détaillé des fonctionnalités système");
            Console.WriteLine("  --profile <fichier>    Utilise un barème personnalisé");
            Console.WriteLine("  --export-profile [f]   Exporte le barème par défaut pour le personnaliser");
            Console.WriteLine("  --elevated --pipe <n>  Mode hôte élevé : exécute les actions privilégiées");
            Console.WriteLine("                         demandées par l'application sur le tube nommé <n>");
            Console.WriteLine("  --verbose              Journalisation détaillée sur la console");
            Console.WriteLine("  --help                 Affiche cette aide");
            Console.WriteLine();
        }
    }
}
