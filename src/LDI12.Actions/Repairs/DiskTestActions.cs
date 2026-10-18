using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Disks;
using LDI12.Core.Execution;
using Newtonsoft.Json;

namespace LDI12.Actions.Repairs
{
    /// <summary>Le disque désigné par l'écran Disques, borné à ce qui existe.</summary>
    internal static class DiskParameter
    {
        public const string Disk = "disk";

        public static int? Index(ActionContext context)
            => int.TryParse(context.Parameter(Disk), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < 64 ? index : (int?)null;
    }

    /// <summary>
    /// L'autotest intégré au disque, comme GSmartControl le lance : court (deux minutes) ou long
    /// (une à plusieurs heures). Le disque le mène seul ; le logiciel relit ensuite son journal.
    /// </summary>
    public sealed class DiskSelfTestAction : IRepairAction
    {
        public const string ModeParameter = "mode";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.DiskSelfTest,
            DisplayName = "Autotest du disque",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Lance l'autotest intégré au disque (court ou long), ou relit son état et son journal.",
            PlainPurpose = "Le disque se vérifie lui-même et dit s'il a trouvé des secteurs défectueux.",
            TypicalDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromMinutes(1),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve les commandes SMART aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(DiskParameter.Index(context) == null
                ? NativeOutcome.Block("Disque non désigné.")
                : new ActionPreview
                {
                    Outcome = PreviewOutcome.Ready,
                    Summary = context.Parameter(ModeParameter) == "status" ? "L'état de l'autotest va être relu." : "L'autotest du disque va être lancé.",
                    WillDo = new[] { "Demander au disque de se vérifier lui-même, ou relire son journal d'autotests." },
                    WillNotDo = new[] { "N'écrit rien sur le disque ; le PC reste utilisable pendant le test." },
                });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var disk = DiskParameter.Index(context)!.Value;
            var mode = context.Parameter(ModeParameter) ?? "status";

            if (mode == "short" || mode == "extended")
            {
                var started = context.Control.StartDiskSelfTest(disk, mode == "extended");
                if (!started.Succeeded) return Task.FromResult(NativeOutcome.From(started, clock.Elapsed));
            }

            var state = context.Control.ReadDiskSelfTest(disk);
            return Task.FromResult(state == null
                ? ActionOutcome.Simple(ActionStatus.Failed, "Ce disque ne répond pas aux commandes SMART (USB, RAID ou NVMe).")
                : new ActionOutcome
                {
                    Status = ActionStatus.Succeeded,
                    Summary = state.Running ? "Autotest en cours : " + (100 - state.RemainingPercent) + " % fait." : "Dernier autotest : " + state.LastResult + ".",
                    RawOutput = JsonConvert.SerializeObject(state),
                    Duration = clock.Elapsed,
                });
        }
    }

    /// <summary>
    /// Le test de surface de HDDScan : chaque bloc lu et chronométré, la carte des temps de
    /// réponse, les blocs illisibles. En lecture seule, jamais d'écriture ni d'effacement.
    /// </summary>
    public sealed class DiskSurfaceTestAction : IRepairAction
    {
        public const string FullParameter = "full";

        /// <summary>Préfixe du détail de progression qui porte la carte en cours, encodée en base 64.</summary>
        public const string MapPrefix = "carte:";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.DiskSurfaceTest,
            DisplayName = "Test de surface",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Lit toute la surface du disque, ou des échantillons répartis, et chronomètre chaque bloc.",
            PlainPurpose = "On voit les zones lentes ou illisibles du disque avant qu'elles ne fassent perdre des données.",
            TypicalDuration = TimeSpan.FromMinutes(2),
            HardTimeout = TimeSpan.FromHours(24),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve la lecture directe d'un disque aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(DiskParameter.Index(context) == null
                ? NativeOutcome.Block("Disque non désigné.")
                : new ActionPreview
                {
                    Outcome = PreviewOutcome.Ready,
                    Summary = context.Parameter(FullParameter) == "1" ? "Toute la surface va être lue." : "Des échantillons répartis sur tout le disque vont être lus.",
                    WillDo = new[] { "Lire le disque bloc par bloc et chronométrer chaque lecture." },
                    WillNotDo = new[] { "N'écrit rien, n'efface rien : le test est en lecture seule." },
                });

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var disk = DiskParameter.Index(context)!.Value;
            var full = context.Parameter(FullParameter) == "1";
            // Relais immédiat : la carte est encodée tant que la lecture est arrêtée sur ce bloc,
            // jamais pendant que le test continue de la remplir.
            var relay = progress == null ? null : new Relay(p =>
                progress.Report(new ActionProgress(
                    ValueFormat.Bytes(p.Partial.BytesRead) + " lus · " + p.Partial.SlowBlocks + " lent(s) · " + p.Partial.Errors + " illisible(s)", p.Fraction)
                {
                    Detail = MapPrefix + Convert.ToBase64String(p.Partial.Cells),
                    Elapsed = p.Partial.Duration,
                }));

            var scan = await context.Control.ScanDiskSurfaceAsync(disk, full, relay, cancellationToken).ConfigureAwait(false);
            if (scan == null) return ActionOutcome.Simple(ActionStatus.Failed, "Le disque n'a pas pu être ouvert en lecture directe.");

            return new ActionOutcome
            {
                Status = scan.Cancelled ? ActionStatus.Cancelled : ActionStatus.Succeeded,
                Summary = scan.Verdict(),
                RawOutput = JsonConvert.SerializeObject(scan),
                Duration = clock.Elapsed,
            };
        }

        private sealed class Relay : IProgress<(double Fraction, SurfaceScan Partial)>
        {
            private readonly Action<(double Fraction, SurfaceScan Partial)> _report;
            public Relay(Action<(double Fraction, SurfaceScan Partial)> report) => _report = report;
            public void Report((double Fraction, SurfaceScan Partial) value) => _report(value);
        }
    }

    /// <summary>
    /// Optimise un volume avec l'outil de Windows, comme « Optimiser les lecteurs » : TRIM pour
    /// un SSD, défragmentation et consolidation pour un disque dur. Windows choisit selon le média.
    /// </summary>
    public sealed class DiskOptimizeAction : IRepairAction
    {
        public const string VolumeParameter = "volume";

        private static readonly Regex Letter = new Regex(@"^[A-Za-z]:$", RegexOptions.CultureInvariant);

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.DiskOptimize,
            DisplayName = "Optimiser le volume",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Low,
            Purpose = "Lance l'optimisation de Windows : TRIM pour un SSD, défragmentation pour un disque dur.",
            PlainPurpose = "Le disque retrouve ses performances.",
            TypicalDuration = TimeSpan.FromMinutes(5),
            HardTimeout = TimeSpan.FromHours(6),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve l'optimisation des volumes aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var volume = context.Parameter(VolumeParameter) ?? string.Empty;
            if (!Letter.IsMatch(volume)) return Task.FromResult(NativeOutcome.Block("Volume non désigné."));
            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = volume.ToUpperInvariant() + " va être optimisé par Windows.",
                WillDo = new[] { "TRIM sur un SSD ; défragmentation et consolidation de l'espace libre sur un disque dur." },
                WillNotDo = new[] { "Ne défragmente jamais un SSD ; aucun fichier n'est supprimé." },
                Measurements = new[] { new PreviewLine("Pendant l'opération", "le disque est très sollicité", PreviewLineKind.Caution) },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var volume = context.Parameter(VolumeParameter)!.ToUpperInvariant();
            progress?.Report(new ActionProgress("Optimisation de " + volume + "…"));
            var result = await context.Processes.RunAsync(
                new ProcessRequest("defrag.exe", volume + " /O /V") { Timeout = Descriptor.HardTimeout, OutputEncoding = ConsoleOutputEncoding.OemCodePage },
                cancellationToken).ConfigureAwait(false);

            var ok = !result.LaunchFailed && !result.TimedOut && result.ExitCode == 0;
            var lines = result.StandardOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok ? volume + " optimisé." : "L'optimisation n'a pas abouti (code " + result.ExitCode + ").",
                Details = lines.Skip(Math.Max(0, lines.Count - 12)).ToList(),
                RawOutput = result.StandardOutput,
                Duration = result.Duration,
            };
        }
    }
}
