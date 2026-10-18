using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using Newtonsoft.Json;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Écoute le trafic de ce PC quelques secondes, comme Wireshark, et en rend les statistiques ;
    /// le fichier pcap écrit en même temps s'ouvre dans Wireshark.
    /// </summary>
    /// <remarks>
    /// Lecture seule. La prise de capture exige d'être administrateur : l'opération passe par
    /// l'hôte élevé, qui ne renvoie que le résumé.
    /// </remarks>
    public sealed class TrafficCaptureAction : IRepairAction
    {
        public const string AddressParameter = "address";
        public const string SecondsParameter = "seconds";
        public const string PcapParameter = "pcap";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.TrafficCapture,
            DisplayName = "Capturer le trafic réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Écoute le trafic IP de ce PC quelques secondes : qui il joint, par quel service, quels noms il demande.",
            PlainPurpose = "On voit à qui le PC parle, et pourquoi la connexion est lente.",
            TypicalDuration = TimeSpan.FromSeconds(30),
            HardTimeout = TimeSpan.FromMinutes(6),
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
                Summary = "Le trafic de " + address + " va être écouté " + Seconds(context) + " secondes.",
                WillDo = new[] { "Relever les paquets IPv4 entrants et sortants, et les écrire dans un fichier pcap pour Wireshark." },
                WillNotDo = new[] { "Ne modifie ni ne bloque rien, et n'envoie rien hors de ce PC." },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new ActionProgress("Écoute en cours…"));
            var summary = await context.Control.CaptureTrafficAsync(
                context.Parameter(AddressParameter)!, TimeSpan.FromSeconds(Seconds(context)), context.Parameter(PcapParameter), cancellationToken).ConfigureAwait(false);

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
                ? Math.Max(5, Math.Min(300, seconds))
                : 30;
    }
}
