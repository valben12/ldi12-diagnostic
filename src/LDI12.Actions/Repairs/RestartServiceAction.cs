using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Arrête puis relance un service Windows.
    /// </summary>
    /// <remarks>
    /// Passe par <c>sc.exe</c> plutôt que par <c>net stop</c> : la sortie de <c>net</c> est
    /// traduite dans la langue de Windows, alors que <c>sc query</c> rend un état numérique
    /// stable (1 arrêté, 4 démarré) quelle que soit la langue. Un outil de dépannage qui ne
    /// fonctionne que sur un Windows français n'est pas un outil de dépannage.
    /// </remarks>
    public sealed class RestartServiceAction : IRepairAction
    {
        private const int StateStopped = 1;
        private const int StateRunning = 4;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestartService,
            DisplayName = "Redémarrer un service Windows",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Moderate,
            Purpose = "Arrête puis relance le service choisi, et vérifie qu'il est bien reparti.",
            PlainPurpose = "Un composant interne de Windows est arrêté puis relancé, comme on redémarre " +
                           "un appareil qui ne répond plus.",
            TypicalDuration = TimeSpan.FromSeconds(20),
            HardTimeout = TimeSpan.FromMinutes(3),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (context.Parameter("service") == null)
                return ActionReadiness.No(
                    "Aucun service désigné.",
                    "Choisir le service à relancer dans la liste des services de l'écran Windows.");

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("le gestionnaire de services refuse un arrêt sans privilèges administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var name = context.Parameter("service");
            if (name == null)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucun service désigné.",
                    Blocker = "Cette action a besoin de savoir quel service relancer.",
                });

            var known = Find(context.Snapshot, name);
            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Service", name),
            };

            if (known != null)
            {
                measurements.Add(new PreviewLine("Nom affiché", known.DisplayName));
                measurements.Add(new PreviewLine("État au diagnostic", known.State));
                measurements.Add(new PreviewLine("Démarrage", known.StartMode));
                if (known.IsEssential)
                    measurements.Add(new PreviewLine(
                        "Service essentiel",
                        "son arrêt momentané se voit : impression, réseau ou mises à jour peuvent " +
                        "être indisponibles quelques secondes",
                        PreviewLineKind.Caution));
            }

            measurements.Add(new PreviewLine(
                "Services dépendants",
                "ceux qui dépendent de ce service sont arrêtés avec lui et ne sont pas relancés " +
                "automatiquement : leur état est à vérifier après l'opération",
                PreviewLineKind.Caution));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le service « " + (known?.DisplayName ?? name) + " » va être arrêté puis relancé.",
                WillDo = new[]
                {
                    "Demander l'arrêt du service " + name + " et attendre qu'il soit effectivement arrêté.",
                    "Le redémarrer et vérifier qu'il tourne réellement avant de conclure.",
                },
                WillNotDo = new[]
                {
                    "Ne modifie pas son type de démarrage : un service désactivé le restera.",
                    "Ne touche à aucun fichier ni à aucun réglage.",
                },
                Measurements = measurements,
                Plan = name,
            });
        }

        private static ServiceInfo? Find(SystemSnapshot? snapshot, string name)
        {
            foreach (var service in snapshot?.Windows.Services ?? Array.Empty<ServiceInfo>())
                if (string.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase)) return service;
            return null;
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var name = preview.Plan as string ?? context.Parameter("service");
            if (name == null)
                return ActionOutcome.Simple(ActionStatus.Failed, "Aucun service désigné.");

            var stopwatch = Stopwatch.StartNew();
            var output = new StringBuilder();

            progress?.Report(new ActionProgress("Arrêt du service " + name + "…"));
            await RunAsync(context, "stop " + Quote(name), output, cancellationToken).ConfigureAwait(false);

            var stopped = await WaitForStateAsync(context, name, StateStopped, output, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ActionProgress("Redémarrage du service " + name + "…"));
            await RunAsync(context, "start " + Quote(name), output, cancellationToken).ConfigureAwait(false);

            var running = await WaitForStateAsync(context, name, StateRunning, output, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (running)
                return new ActionOutcome
                {
                    Status = ActionStatus.Succeeded,
                    Summary = "Le service " + name + " tourne de nouveau.",
                    Details = stopped
                        ? Array.Empty<string>()
                        : new[] { "Le service n'était pas démarré au moment de l'opération ; il l'est maintenant." },
                    Duration = stopwatch.Elapsed,
                    RawOutput = output.ToString(),
                };

            return new ActionOutcome
            {
                Status = ActionStatus.Failed,
                Summary = "Le service " + name + " n'est pas reparti. Un service qui refuse de démarrer " +
                          "signale presque toujours autre chose : dépendance arrêtée, fichier manquant " +
                          "ou type de démarrage désactivé.",
                Duration = stopwatch.Elapsed,
                RawOutput = output.ToString(),
            };
        }

        private static string Quote(string name) => "\"" + name.Replace("\"", string.Empty) + "\"";

        private static async Task<ProcessResult> RunAsync(
            ActionContext context, string arguments, StringBuilder output, CancellationToken cancellationToken)
        {
            var result = await context.Processes.RunAsync(
                new ProcessRequest("sc.exe", arguments)
                {
                    Timeout = TimeSpan.FromSeconds(45),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            output.AppendLine("sc " + arguments).AppendLine(result.StandardOutput).AppendLine();
            return result;
        }

        /// <summary>
        /// Un service ne s'arrête ni ne démarre à l'instant où on le demande : <c>sc</c> rend la
        /// main tout de suite. Conclure sans attendre l'état réel reviendrait à affirmer un
        /// résultat qu'on n'a pas constaté.
        /// </summary>
        private static async Task<bool> WaitForStateAsync(
            ActionContext context, string name, int expected, StringBuilder output, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 15; attempt++)
            {
                var result = await RunAsync(context, "query " + Quote(name), output, cancellationToken)
                    .ConfigureAwait(false);

                if (ReadState(result.StandardOutput) == expected) return true;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>Lit l'état numérique de la ligne STATE, seule partie non traduite de la sortie.</summary>
        internal static int? ReadState(string? output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            foreach (var line in output!.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;

                var key = line.Substring(0, colon).Trim();
                if (key.IndexOf("STATE", StringComparison.OrdinalIgnoreCase) < 0 &&
                    key.IndexOf("ÉTAT", StringComparison.OrdinalIgnoreCase) < 0 &&
                    key.IndexOf("ETAT", StringComparison.OrdinalIgnoreCase) < 0) continue;

                foreach (var token in line.Substring(colon + 1).Split(' ', '\t', '\r'))
                    if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var state))
                        return state;
            }

            return null;
        }
    }
}
