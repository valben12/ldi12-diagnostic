using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Repairs
{
    /// <summary>Le paramètre commun aux gestes sur une sortie audio.</summary>
    internal static class AudioParameter
    {
        internal const string Endpoint = "endpoint";

        internal static AudioOutputState? Find(ActionContext context, string? id)
            => id == null ? null : context.Control.AudioEndpoints().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        internal static AudioOutputState? Default(ActionContext context)
            => context.Control.AudioEndpoints().FirstOrDefault(e => e.IsDefault && !e.IsCapture);

        internal static string Describe(AudioOutputState endpoint)
            => endpoint.Name + " (" + endpoint.State
               + (endpoint.VolumePercent.HasValue ? ", volume " + endpoint.VolumePercent + " %" : string.Empty)
               + (endpoint.Muted == true ? ", coupée" : string.Empty) + ")";
    }

    /// <summary>
    /// Fait d'une sortie audio la sortie par défaut : le son partait vers l'écran HDMI ou un casque
    /// éteint, il revient aux haut-parleurs.
    /// </summary>
    public sealed class AudioDefaultOutputAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.AudioDefaultOutput,
            DisplayName = "Choisir la sortie audio",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Fait d'une sortie (haut-parleurs, casque, HDMI) la sortie par défaut de Windows, pour tous les usages.",
            PlainPurpose = "Le son repart par le bon appareil.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var target = AudioParameter.Find(context, context.Parameter(AudioParameter.Endpoint));
            if (target == null || target.IsCapture) return Task.FromResult(NativeOutcome.Block("Cette sortie audio n'existe pas sur ce PC."));
            if (target.State != "active") return Task.FromResult(NativeOutcome.Block("« " + target.Name + " » n'est pas active (" + target.State + ") : branchez-la ou activez-la d'abord."));
            if (target.IsDefault && target.IsDefaultCommunications)
                return Task.FromResult(new ActionPreview { Outcome = PreviewOutcome.NothingToDo, Summary = "« " + target.Name + " » est déjà la sortie par défaut." });

            var current = AudioParameter.Default(context);
            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le son va partir par « " + target.Name + " ».",
                WillDo = new[] { "En faire la sortie par défaut, pour le son ordinaire comme pour les appels." },
                WillNotDo = new[] { "Ne désactive aucune autre sortie et ne touche pas aux pilotes." },
                Measurements = new[] { new PreviewLine("Sortie actuelle", current == null ? "aucune" : AudioParameter.Describe(current)) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.SetDefaultAudioEndpoint(context.Parameter(AudioParameter.Endpoint)!), clock.Elapsed));
        }
    }

    /// <summary>Lève la coupure du son et remonte un volume resté à zéro.</summary>
    public sealed class AudioRestoreVolumeAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.AudioRestoreVolume,
            DisplayName = "Rétablir le son",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Lève la coupure du son et remonte le volume à 50 % s'il était presque nul.",
            PlainPurpose = "Le son coupé ou au minimum revient.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var id = context.Parameter(AudioParameter.Endpoint);
            var target = id == null ? AudioParameter.Default(context) : AudioParameter.Find(context, id);
            if (target == null) return Task.FromResult(NativeOutcome.Block("Aucune sortie audio active."));
            if (target.Muted == false && target.VolumePercent >= 10)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "« " + target.Name + " » n'est ni coupée ni à zéro (" + target.VolumePercent + " %).",
                });

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le son de « " + target.Name + " » va être rétabli.",
                WillDo = new[] { "Lever la coupure du son.", "Remonter le volume à 50 % s'il est sous 10 %." },
                WillNotDo = new[] { "Ne baisse jamais un volume, et ne touche pas aux autres sorties." },
                Measurements = new[] { new PreviewLine("Sortie", AudioParameter.Describe(target)) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.RestoreAudioVolume(context.Parameter(AudioParameter.Endpoint)), clock.Elapsed));
        }
    }

    /// <summary>
    /// Un son de test sur la sortie par défaut : la seule preuve qui compte est l'oreille du technicien.
    /// </summary>
    public sealed class AudioTestSoundAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.AudioTestSound,
            DisplayName = "Jouer un son de test",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Joue une note d'une seconde et demie sur la sortie audio par défaut.",
            PlainPurpose = "Une note retentit : on sait aussitôt si le son sort.",
            TypicalDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var output = AudioParameter.Default(context);
            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Une note va être jouée" + (output == null ? "." : " sur « " + output.Name + " »."),
                WillDo = new[] { "Jouer une note de 440 Hz pendant une seconde et demie, à volume modéré." },
                WillNotDo = new[] { "Ne change ni la sortie ni le volume." },
                Measurements = output == null ? Array.Empty<PreviewLine>() : new[] { new PreviewLine("Sortie", AudioParameter.Describe(output)) },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Écoutez…"));
            var clock = Stopwatch.StartNew();
            // Hors du fil de l'interface : la note dure une seconde et demie.
            var result = await Task.Run(() => context.Control.PlayTestSound(), cancellationToken).ConfigureAwait(false);
            var output = AudioParameter.Default(context);
            return NativeOutcome.From(result, clock.Elapsed,
                "Note jouée" + (output == null ? "." : " sur « " + output.Name + " » (" + (output.Muted == true ? "coupée" : output.VolumePercent + " %") + ")."));
        }
    }

    /// <summary>Relance les deux services du son de Windows, dans l'ordre où ils dépendent l'un de l'autre.</summary>
    public sealed class AudioServicesRestartAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.AudioServicesRestart,
            DisplayName = "Relancer les services audio",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Redémarre « Générateur de points de terminaison audio » et « Audio Windows ».",
            PlainPurpose = "Le moteur du son de Windows repart à neuf.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve l'arrêt des services audio aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Les services audio vont être relancés.",
                WillDo = new[] { "Arrêter puis relancer Audiosrv et AudioEndpointBuilder." },
                WillNotDo = new[] { "Ne touche ni aux pilotes ni aux réglages de son." },
                Measurements = new[]
                {
                    new PreviewLine("Audio Windows", context.Control.ServiceStatus("Audiosrv") ?? "absent"),
                    new PreviewLine("Pendant l'opération", "le son se coupe quelques secondes", PreviewLineKind.Caution),
                },
            });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(40);
            progress?.Report(new ActionProgress("Arrêt des services audio…"));
            // Arrêter le générateur arrête aussi Audiosrv, qui en dépend.
            var stopped = context.Control.StopService("AudioEndpointBuilder", timeout, out var dependents);
            if (!stopped.Succeeded) return Task.FromResult(NativeOutcome.From(stopped, clock.Elapsed));

            progress?.Report(new ActionProgress("Relance…"));
            var started = context.Control.StartService("AudioEndpointBuilder", timeout);
            var failures = started.Succeeded ? 0 : 1;
            foreach (var name in new[] { "Audiosrv" }.Concat(dependents).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!context.Control.StartService(name, timeout).Succeeded) failures++;

            return Task.FromResult(new ActionOutcome
            {
                Status = failures == 0 ? ActionStatus.Succeeded : ActionStatus.PartiallySucceeded,
                Summary = failures == 0 ? "Services audio relancés." : "Relancés en partie : un redémarrage du PC terminera.",
                RestartRequired = failures > 0,
                Duration = clock.Elapsed,
            });
        }
    }
}
