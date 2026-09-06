using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Engine.Orchestration
{
    /// <summary>
    /// Enveloppe unique d'exécution d'une sonde : vérification des prérequis, délai maximal,
    /// capture des exceptions, mesure de durée et compte rendu.
    /// </summary>
    /// <remarks>
    /// <b>Aucune sonde n'implémente sa propre gestion d'erreur.</b> C'est la seule garantie que la
    /// règle « un module qui échoue n'interrompt jamais le diagnostic » soit vraie partout, y
    /// compris dans les modules écrits dans six mois. Une sonde qui lève une exception produit un
    /// compte rendu en échec, et le diagnostic continue.
    /// </remarks>
    public sealed class ProbeExecutionPolicy
    {
        private const string Category = "Engine.Probe";

        private readonly IScopedLogger _log;
        private readonly IProbeIsolationHost? _isolation;

        /// <param name="isolation">
        /// Hôte d'exécution isolée. Nul : les sondes déclarées isolées s'exécutent sur place,
        /// comme elles le faisaient avant que l'isolation n'existe.
        /// </param>
        public ProbeExecutionPolicy(ILdiLogger logger, IProbeIsolationHost? isolation = null)
        {
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
            _isolation = isolation;
        }

        public async Task<ModuleReport> ExecuteAsync(
            IDiagnosticProbe probe, ProbeContext context, CancellationToken cancellationToken)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var descriptor = probe.Descriptor;
            var stopwatch = Stopwatch.StartNew();

            var blocked = await CheckRequirementsAsync(descriptor, context, cancellationToken).ConfigureAwait(false);
            if (blocked != null)
            {
                stopwatch.Stop();
                _log.Debug(descriptor.Id + " écartée : " + blocked.Message);
                return Report(descriptor, blocked.Status, stopwatch.ElapsedMilliseconds, blocked.Message);
            }

            try
            {
                if (descriptor.Isolation == IsolationMode.SeparateProcess && _isolation != null)
                {
                    var isolated = await RunIsolatedAsync(descriptor, context, cancellationToken)
                        .ConfigureAwait(false);

                    if (isolated != null)
                    {
                        stopwatch.Stop();
                        _log.Debug(descriptor.Id + " (isolé) → " + isolated.Status + " en " +
                                   isolated.DurationMs + " ms, " + stopwatch.ElapsedMilliseconds +
                                   " ms avec l'attente.");

                        // La durée rapportée est celle du travail, pas celle du tour d'attente :
                        // les sondes isolées se suivent, et afficher l'attente ferait passer un
                        // module rapide pour un module lent.
                        return Report(descriptor, isolated.Status, isolated.DurationMs,
                            isolated.Message, isolated.ExceptionSummary);
                    }

                    // Isolation indisponible : la sonde s'exécute ici. Une machine sans hôte
                    // isolé doit rendre le même diagnostic, pas un diagnostic amputé.
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(descriptor.HardTimeout);

                // Lancée sur un fil du pool, et non appelée directement : une sonde qui se
                // bloque sur un appel synchrone (une requête WMI sur un dépôt corrompu, un
                // IOCTL vers un disque qui ne répond plus) ne rend jamais la main avant sa
                // première attente. Appelée ici, elle emporterait le délai maximal avec elle,
                // et le module ne serait pas abandonné mais bloqué pour de bon. Vérifié sur la
                // machine de développement : le diagnostic ne se terminait plus du tout.
                var work = Task.Run(() => probe.ExecuteAsync(context, deadline.Token), CancellationToken.None);
                var abandon = Task.Delay(Timeout.Infinite, deadline.Token);

                if (await Task.WhenAny(work, abandon).ConfigureAwait(false) != work)
                {
                    stopwatch.Stop();
                    var cancelled = cancellationToken.IsCancellationRequested;
                    var message = cancelled
                        ? "Diagnostic annulé par le technicien."
                        : "Le module n'a pas répondu dans le délai de " +
                          descriptor.HardTimeout.TotalSeconds.ToString("0.#") + " s et a été abandonné.";

                    if (!cancelled) _log.Warn(descriptor.Id + " : délai dépassé.");
                    return Report(descriptor, cancelled ? ProbeStatus.Cancelled : ProbeStatus.TimedOut,
                        stopwatch.ElapsedMilliseconds, message);
                }

                var outcome = await work.ConfigureAwait(false);
                stopwatch.Stop();
                _log.Debug(descriptor.Id + " → " + outcome.Status + " en " + stopwatch.ElapsedMilliseconds + " ms.");
                return Report(descriptor, outcome.Status, stopwatch.ElapsedMilliseconds, outcome.Message, outcome.Exception);
            }
            catch (OperationCanceledException)
            {
                // Une sonde qui respecte le jeton d'annulation lève ici aussi bien sur une
                // annulation du technicien que sur un dépassement de délai. Confondre les deux
                // ferait afficher « annulé par le technicien » sur un module qui a en réalité
                // calé : c'est le jeton d'origine qui tranche.
                stopwatch.Stop();
                var cancelledByOperator = cancellationToken.IsCancellationRequested;
                if (!cancelledByOperator) _log.Warn(descriptor.Id + " : délai dépassé.");

                return Report(descriptor,
                    cancelledByOperator ? ProbeStatus.Cancelled : ProbeStatus.TimedOut,
                    stopwatch.ElapsedMilliseconds,
                    cancelledByOperator
                        ? "Diagnostic annulé par le technicien."
                        : "Le module n'a pas répondu dans le délai de " +
                          descriptor.HardTimeout.TotalSeconds.ToString("0.#") + " s et a été interrompu.");
            }
            catch (Exception ex)
            {
                // Filet de sécurité volontairement large : c'est ici que se joue la promesse
                // « aucune fonctionnalité ne peut faire planter l'application ».
                stopwatch.Stop();
                _log.Error(descriptor.Id + " a levé une exception non gérée.", ex);
                return Report(descriptor, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                    "Le module a rencontré une erreur inattendue. Le reste du diagnostic n'est pas affecté.", ex);
            }
        }

        private static async Task<Blocked?> CheckRequirementsAsync(
            ProbeDescriptor descriptor, ProbeContext context, CancellationToken cancellationToken)
        {
            var requirements = descriptor.Requirements;
            var profile = context.Platform.Profile;

            if (requirements.MinimumBuild.HasValue && profile.Build < requirements.MinimumBuild.Value)
            {
                return new Blocked(ProbeStatus.Unavailable,
                    "Requiert le build " + requirements.MinimumBuild.Value + " de Windows. Build actuel : " +
                    profile.Build + " (" + WindowsProfile.FamilyLabel(profile.Family) + ").");
            }

            foreach (var featureId in requirements.RequiredFeatures)
            {
                var feature = context.Platform.Features.Get(featureId);
                if (feature.IsUsable) continue;
                return new Blocked(ProbeStatus.Unavailable, feature.Reason);
            }

            if (requirements.RequiresElevation && !context.Platform.IsElevated)
            {
                return new Blocked(ProbeStatus.ElevationRequired,
                    "Ce contrôle nécessite des privilèges administrateur. Relancer l'analyse en tant qu'administrateur pour l'inclure.");
            }

            foreach (var wmiNamespace in requirements.RequiredWmiNamespaces)
            {
                var state = await context.Wmi.ProbeNamespaceAsync(wmiNamespace, cancellationToken).ConfigureAwait(false);
                if (state == WmiNamespaceState.Present) continue;

                // Un refus d'accès n'est pas une absence : le premier se corrige par une
                // élévation, le second non. Le technicien doit voir la différence.
                return state == WmiNamespaceState.AccessDenied
                    ? new Blocked(ProbeStatus.ElevationRequired,
                        "Ce contrôle lit " + wmiNamespace + ", accessible uniquement avec des privilèges administrateur.")
                    : new Blocked(ProbeStatus.Unavailable,
                        "L'espace de noms système " + wmiNamespace + " est absent sur cette version de Windows.");
            }

            return null;
        }

        private static ModuleReport Report(
            ProbeDescriptor descriptor, ProbeStatus status, long durationMs, string? message, Exception? exception = null)
            => Report(descriptor, status, durationMs, message,
                exception == null ? null : exception.GetType().Name + " : " + exception.Message);

        private static ModuleReport Report(
            ProbeDescriptor descriptor, ProbeStatus status, long durationMs, string? message, string? exceptionSummary)
            => new ModuleReport
            {
                ProbeId = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Category = descriptor.Category,
                Status = status,
                DurationMs = durationMs,
                Message = message,
                ExceptionSummary = exceptionSummary,
            };

        /// <summary>
        /// Exécute la sonde dans le processus isolé, et rejoue chez nous ce qu'elle y a écrit.
        /// </summary>
        /// <remarks>
        /// Les sections déjà collectées partent avec la demande : sans elles, la lecture SMART
        /// ne trouverait pas la liste des disques que la sonde d'énumération a produite, et
        /// l'isolation transformerait une dépendance déclarée en donnée manquante.
        /// </remarks>
        private async Task<IsolatedOutcome?> RunIsolatedAsync(
            ProbeDescriptor descriptor, ProbeContext context, CancellationToken cancellationToken)
        {
            IsolatedOutcome? outcome;
            try
            {
                outcome = await _isolation!
                    .RunAsync(descriptor, context.Mode, context.Draft.Writes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // L'isolation est un confort, pas une dépendance : si le canal casse, la sonde
                // s'exécutera sur place plutôt que de disparaître du diagnostic.
                _log.Warn("L'exécution isolée de " + descriptor.Id + " a échoué : exécution sur place.", ex);
                return null;
            }

            if (outcome == null) return null;

            foreach (var section in outcome.Writes)
            {
                if (context.Draft.Apply(section.Name, section.Value)) continue;
                _log.Warn("Section rendue par l'hôte isolé et non rejouée : " + section.Name + ".");
            }

            return outcome;
        }

        private sealed class Blocked
        {
            public Blocked(ProbeStatus status, string message)
            {
                Status = status;
                Message = message;
            }

            public ProbeStatus Status { get; }
            public string Message { get; }
        }
    }
}
