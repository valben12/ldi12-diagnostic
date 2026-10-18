using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Network;
using Newtonsoft.Json;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Écoute le trafic de ce PC, comme Wireshark : les paquets défilent à l'écran pendant
    /// l'écoute, les statistiques se mettent à jour, et le fichier pcap écrit en même temps
    /// s'ouvre dans Wireshark. De quelques secondes à quatre heures, ou jusqu'à l'arrêt.
    /// </summary>
    /// <remarks>
    /// Lecture seule. La prise de capture exige d'être administrateur : l'opération passe par
    /// l'hôte élevé, qui relaie les paquets par lots, deux fois par seconde.
    /// </remarks>
    public sealed class TrafficCaptureAction : IRepairAction
    {
        public const string AddressParameter = "address";
        public const string SecondsParameter = "seconds";
        public const string PcapParameter = "pcap";

        /// <summary>Préfixe du détail de progression qui porte un lot de paquets et le résumé, en JSON.</summary>
        public const string LivePrefix = "trafic:";

        /// <summary>La plus longue écoute : de quoi attraper un problème qui revient dans l'après-midi.</summary>
        public const int MaxSeconds = 4 * 3600;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.TrafficCapture,
            DisplayName = "Capturer le trafic réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Écoute le trafic IP de ce PC quelques secondes : qui il joint, par quel service, quels noms il demande.",
            PlainPurpose = "On voit à qui le PC parle, et pourquoi la connexion est lente.",
            TypicalDuration = TimeSpan.FromSeconds(60),
            HardTimeout = TimeSpan.FromSeconds(MaxSeconds) + TimeSpan.FromMinutes(5),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve la capture de trafic aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var address = context.Parameter(AddressParameter);
            if (string.IsNullOrWhiteSpace(address)) return Task.FromResult(NativeOutcome.Block("Aucune carte réseau désignée."));
            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le trafic de " + address + " va être écouté " + Describe(Seconds(context)) + ".",
                WillDo = new[] { "Relever les paquets IPv4 entrants et sortants, et les écrire dans un fichier pcap pour Wireshark." },
                WillNotDo = new[] { "Ne modifie ni ne bloque rien, et n'envoie rien hors de ce PC." },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var seconds = Seconds(context);
            progress?.Report(new ActionProgress("Écoute en cours…"));
            var relay = progress == null ? null : new Relay(live =>
                progress.Report(new ActionProgress(live.Summary.Packets + " paquets", Math.Min(1, clock.Elapsed.TotalSeconds / seconds))
                {
                    Detail = LivePrefix + JsonConvert.SerializeObject(live),
                    Elapsed = clock.Elapsed,
                }));
            var summary = await context.Control.CaptureTrafficAsync(
                context.Parameter(AddressParameter)!, TimeSpan.FromSeconds(seconds), context.Parameter(PcapParameter), relay, cancellationToken).ConfigureAwait(false);

            return summary == null
                ? ActionOutcome.Simple(ActionStatus.Failed, "La capture n'a pas pu démarrer sur cette carte réseau.")
                : new ActionOutcome
                {
                    Status = ActionStatus.Succeeded,
                    Summary = summary.Packets + " paquets relevés.",
                    RawOutput = JsonConvert.SerializeObject(summary),
                    Duration = clock.Elapsed,
                };
        }

        private static int Seconds(ActionContext context)
            => int.TryParse(context.Parameter(SecondsParameter), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Max(5, Math.Min(MaxSeconds, seconds))
                : 60;

        private static string Describe(int seconds)
            => seconds >= MaxSeconds ? "jusqu'à l'arrêt (quatre heures au plus)"
               : seconds >= 3600 ? seconds / 3600 + " h"
               : seconds >= 60 ? seconds / 60 + " min"
               : seconds + " secondes";

        /// <summary>Relais immédiat : chaque lot part tel qu'il arrive, sans contexte de synchronisation.</summary>
        private sealed class Relay : IProgress<TrafficLive>
        {
            private readonly Action<TrafficLive> _report;
            public Relay(Action<TrafficLive> report) => _report = report;
            public void Report(TrafficLive value) => _report(value);
        }
    }
}
