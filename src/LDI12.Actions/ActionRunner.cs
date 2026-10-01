using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Elevation;
using LDI12.Actions.Journal;
using LDI12.Actions.Tools;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Actions
{
    /// <summary>
    /// Orchestre les actions : décide où elles s'exécutent, impose la prévisualisation, et
    /// consigne tout dans le journal d'intervention.
    /// </summary>
    /// <remarks>
    /// Une action qui exige des privilèges ne s'exécute pas dans l'application : celle-ci reste
    /// volontairement en session utilisateur. Elle part par le canal vers l'hôte élevé, ouvert
    /// une seule fois. Le technicien voit donc une invite UAC lors de sa première action
    /// privilégiée, et aucune ensuite.
    /// </remarks>
    public sealed class ActionRunner
    {
        private const string Category = "Actions";

        /// <summary>Marge ajoutée au délai de l'action pour l'aller-retour par le canal.</summary>
        private static readonly TimeSpan ChannelMargin = TimeSpan.FromSeconds(30);

        private readonly ActionContext _context;
        private readonly IElevatedChannel? _channel;
        private readonly ILdiLogger _logger;

        public ActionRunner(
            ActionContext context, InterventionJournal journal, ILdiLogger logger, IElevatedChannel? channel = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _channel = channel;
        }

        public InterventionJournal Journal { get; }

        public ActionContext Context => _context;

        /// <summary>Vrai si une élévation est possible : sinon les actions privilégiées sont grisées.</summary>
        public bool CanElevate => _context.Platform.IsElevated || _channel != null;

        /// <summary>
        /// Disponibilité affichée. Une action qui demande des privilèges reste proposée quand un
        /// canal existe : elle est faisable, moyennant une invite.
        /// </summary>
        public ActionReadiness Readiness(IRepairAction action, IReadOnlyDictionary<string, string>? parameters = null)
        {
            var readiness = action.CheckReadiness(Scoped(parameters));
            if (readiness.Availability != ActionAvailability.NeedsElevation) return readiness;

            return CanElevate
                ? readiness
                : ActionReadiness.No(
                    "Privilèges administrateur requis, et l'hôte élevé n'est pas disponible.",
                    "Relancer LDI12 Diagnostic en tant qu'administrateur.");
        }

        public async Task<ActionPreview> PreviewAsync(
            IRepairAction action, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var context = Scoped(parameters);
            var readiness = action.CheckReadiness(context);

            if (readiness.Availability == ActionAvailability.Unavailable)
                return Blocked(readiness.Reason ?? "Action indisponible sur cette machine.", readiness.Workaround);

            ActionPreview preview;
            if (readiness.Availability == ActionAvailability.NeedsElevation && !context.Platform.IsElevated)
            {
                var elevated = await OpenChannelAsync(cancellationToken).ConfigureAwait(false);
                if (elevated != null) return elevated;

                preview = await RemotePreviewAsync(action, parameters, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                preview = await OffInterfaceThread(() => action.PreviewAsync(context, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            Journal.Record(
                InterventionKind.Preview, action.Descriptor.Id, action.Descriptor.DisplayName,
                preview.Outcome.ToString(), preview.Summary,
                elevated: preview.RemoteToken != null || context.Platform.IsElevated);

            return preview;
        }

        public async Task<ActionOutcome> ExecuteAsync(
            IRepairAction action, ActionPreview preview, IReadOnlyDictionary<string, string>? parameters,
            IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            // Garde-fou du contrat : on n'exécute que ce qui a été établi et montré. Le type
            // impose de passer une prévisualisation ; il reste à vérifier qu'elle concluait
            // qu'il y avait quelque chose à faire.
            if (!preview.CanExecute)
                return ActionOutcome.Simple(
                    ActionStatus.Failed,
                    preview.Blocker ?? "Rien n'a été exécuté : la prévisualisation ne concluait pas à une action.");

            var context = Scoped(parameters);
            ActionOutcome outcome;

            try
            {
                if (preview.RemoteToken != null)
                    outcome = await RemoteExecuteAsync(action, preview.RemoteToken, progress, cancellationToken)
                        .ConfigureAwait(false);
                else
                    outcome = await OffInterfaceThread(
                            () => action.ExecuteAsync(context, preview, progress, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = ActionOutcome.Simple(
                    ActionStatus.Cancelled, "Opération interrompue par le technicien.");
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "L'action " + action.Descriptor.Id + " a levé une exception.", ex);
                outcome = ActionOutcome.Simple(
                    ActionStatus.Failed, "L'action n'a pas pu être menée à son terme : " + ex.Message);
            }

            Journal.Record(
                InterventionKind.Execution, action.Descriptor.Id, action.Descriptor.DisplayName,
                outcome.Status.ToString(), outcome.Summary,
                elevated: preview.RemoteToken != null || context.Platform.IsElevated,
                duration: outcome.Duration, freedBytes: outcome.FreedBytes, details: outcome.Details);

            return outcome;
        }

        /// <summary>
        /// Crée un point de restauration avant intervention.
        /// </summary>
        /// <remarks>
        /// Rendu obligatoire par personne : c'est une case que le technicien coche. L'imposer
        /// ferait perdre plusieurs minutes avant un vidage de cache DNS, et une protection qu'on
        /// contourne systématiquement finit par ne plus protéger.
        /// </remarks>
        public async Task<RestorePointResult> CreateRestorePointAsync(
            string description, CancellationToken cancellationToken)
        {
            RestorePointResult result;

            if (_context.Platform.IsElevated)
            {
                result = await _context.Restore.CreateAsync(description, cancellationToken).ConfigureAwait(false);
            }
            else if (_channel == null)
            {
                result = new RestorePointResult
                {
                    State = RestorePointState.RequiresElevation,
                    Message = "Un point de restauration ne peut être créé qu'avec des privilèges administrateur.",
                };
            }
            else
            {
                result = await RemoteRestorePointAsync(description, cancellationToken).ConfigureAwait(false);
            }

            Journal.Record(
                InterventionKind.RestorePoint, "RESTORE-POINT", "Point de restauration",
                result.State.ToString(), result.Message, elevated: true);

            return result;
        }

        /// <summary>Ouvre une console Windows. Consigné : le technicien a bien pu y toucher.</summary>
        public LaunchResult LaunchTool(WindowsToolState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            if (!state.Available)
            {
                var refused = LaunchResult.Error(state.Reason ?? "Console indisponible.");
                Journal.Record(
                    InterventionKind.ToolLaunch, state.Tool.Id, state.Tool.Title, "Unavailable",
                    refused.Reason ?? string.Empty);
                return refused;
            }

            var result = _context.Launcher.Launch(
                new LaunchRequest(state.Path ?? state.Tool.FileName, state.Tool.Arguments));

            Journal.Record(
                InterventionKind.ToolLaunch, state.Tool.Id, state.Tool.Title,
                result.Started ? "Started" : "Failed",
                result.Started
                    ? state.Tool.Title + " a été ouvert."
                    : "Ouverture impossible : " + (result.Reason ?? "cause inconnue."));

            return result;
        }

        private ActionContext Scoped(IReadOnlyDictionary<string, string>? parameters)
            => parameters == null ? _context : _context.With(parameters);

        /// <summary>
        /// Fait le travail d'une action locale sur un fil à elle, jamais sur celui de l'appelant.
        /// </summary>
        /// <remarks>
        /// <b>Les actions sont écrites de façon synchrone</b>, et c'est normal : copier quatre
        /// cent mille fichiers est une boucle, pas une suite d'attentes. Mais l'appelant est
        /// l'interface, et jusqu'à la version 1.23.0 cette boucle tournait sur son fil. La fenêtre
        /// gelait pendant toute la copie, la progression ne pouvait pas s'afficher, et Windows
        /// finissait par déclarer le logiciel « ne répond pas » au bout d'une sauvegarde qui, elle,
        /// avançait. Le relevé qui précède la copie gelait de la même façon.
        /// <para>
        /// Un fil dédié plutôt que le pool : une sauvegarde dure une heure, et elle ne doit priver
        /// de fil ni le canal élevé ni l'interface. En appartement STA, comme le fil d'interface
        /// qui portait ces actions jusque-là : le vidage de la corbeille passe par le shell de
        /// Windows, et il ne doit voir aucune différence.
        /// </para>
        /// </remarks>
        internal static Task<T> OffInterfaceThread<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(work().GetAwaiter().GetResult());
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "LDI12 : action",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return completion.Task;
        }

        private static ActionPreview Blocked(string reason, string? workaround = null)
            => new ActionPreview
            {
                Outcome = PreviewOutcome.Blocked,
                Summary = reason,
                Blocker = reason,
                WillDo = workaround == null ? Array.Empty<string>() : new[] { workaround },
            };

        /// <summary>Retourne une prévisualisation bloquée si le canal n'a pas pu s'ouvrir, sinon nul.</summary>
        private async Task<ActionPreview?> OpenChannelAsync(CancellationToken cancellationToken)
        {
            if (_channel == null)
                return Blocked(
                    "Privilèges administrateur requis, et l'hôte élevé n'est pas disponible.",
                    "Relancer LDI12 Diagnostic en tant qu'administrateur.");

            var state = await _channel.EnsureAsync(cancellationToken).ConfigureAwait(false);
            if (state == ElevatedChannelState.Available || state == ElevatedChannelState.AlreadyElevated) return null;

            return Blocked(state == ElevatedChannelState.Refused
                ? "L'élévation a été refusée. Rien n'a été fait."
                : "L'hôte élevé n'a pas pu être lancé : " + (_channel.Reason ?? "cause inconnue."));
        }

        private async Task<ActionPreview> RemotePreviewAsync(
            IRepairAction action, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            var request = new ElevatedRequest
            {
                Op = ElevationProtocol.OpPreview,
                Action = action.Descriptor.Id,
                Parameters = Copy(parameters),
            };

            var message = await ExchangeAsync(request, null, action.Descriptor.HardTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (message.Type == ElevatedMessage.TypePreview && message.Preview != null)
                return message.Preview.ToPreview();

            return Blocked(message.Message ?? "L'hôte élevé n'a pas rendu de prévisualisation exploitable.");
        }

        private async Task<ActionOutcome> RemoteExecuteAsync(
            IRepairAction action, string token, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var request = new ElevatedRequest
            {
                Op = ElevationProtocol.OpExecute,
                Action = action.Descriptor.Id,
                Token = token,
            };

            // Arrêter une action élevée, c'est le lui demander : l'échange continue d'attendre sa
            // réponse, que l'hôte rend dès que l'action s'est arrêtée entre deux fichiers.
            using var stop = cancellationToken.Register(() =>
                _ = _channel?.SignalAsync(ElevationProtocol.Write(new ElevatedRequest { Op = ElevationProtocol.OpCancel })));

            var message = await ExchangeAsync(
                    request,
                    line =>
                    {
                        var notification = ElevationProtocol.Read<ElevatedMessage>(line);
                        if (notification == null || notification.Type != ElevatedMessage.TypeProgress)
                            return false;

                        if (notification.Text != null)
                            progress?.Report(new ActionProgress(notification.Text, notification.Fraction)
                            {
                                Detail = notification.Detail,
                                Remaining = notification.RemainingSeconds.HasValue
                                    ? TimeSpan.FromSeconds(notification.RemainingSeconds.Value)
                                    : (TimeSpan?)null,
                                Elapsed = notification.ElapsedSeconds.HasValue
                                    ? TimeSpan.FromSeconds(notification.ElapsedSeconds.Value)
                                    : (TimeSpan?)null,
                            });

                        return true;
                    },
                    action.Descriptor.HardTimeout, CancellationToken.None)
                .ConfigureAwait(false);

            if (message.Type == ElevatedMessage.TypeOutcome && message.Outcome != null)
                return message.Outcome.ToOutcome();

            if (cancellationToken.IsCancellationRequested)
                return ActionOutcome.Simple(ActionStatus.Cancelled, "Opération interrompue par le technicien.");

            return ActionOutcome.Simple(
                ActionStatus.Failed,
                message.Message ?? "L'hôte élevé n'a rendu aucun compte rendu exploitable.");
        }

        private async Task<RestorePointResult> RemoteRestorePointAsync(
            string description, CancellationToken cancellationToken)
        {
            var opened = await OpenChannelAsync(cancellationToken).ConfigureAwait(false);
            if (opened != null)
                return new RestorePointResult
                {
                    State = RestorePointState.RequiresElevation,
                    Message = opened.Summary,
                };

            var message = await ExchangeAsync(
                    new ElevatedRequest { Op = ElevationProtocol.OpRestorePoint, Description = description },
                    null, TimeSpan.FromMinutes(4), cancellationToken)
                .ConfigureAwait(false);

            if (message.Type == ElevatedMessage.TypeRestore && message.RestoreState.HasValue)
                return new RestorePointResult
                {
                    State = message.RestoreState.Value,
                    Message = message.Message ?? string.Empty,
                };

            return new RestorePointResult
            {
                State = RestorePointState.Failed,
                Message = message.Message ?? "L'hôte élevé n'a pas répondu.",
            };
        }

        private async Task<ElevatedMessage> ExchangeAsync(
            ElevatedRequest request, Func<string, bool>? onNotification, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_channel == null)
                return new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeError,
                    Message = "Aucun canal élevé n'est ouvert.",
                };

            try
            {
                var line = await _channel
                    .SendAsync(ElevationProtocol.Write(request), onNotification, timeout + ChannelMargin, cancellationToken)
                    .ConfigureAwait(false);

                return ElevationProtocol.Read<ElevatedMessage>(line) ?? new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeError,
                    Message = "Réponse illisible de l'hôte élevé.",
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Échange interrompu avec l'hôte élevé.", ex);
                return new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeError,
                    Message = "La communication avec l'hôte élevé a été interrompue : " + ex.Message,
                };
            }
        }

        private static Dictionary<string, string>? Copy(IReadOnlyDictionary<string, string>? parameters)
        {
            if (parameters == null || parameters.Count == 0) return null;

            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parameters) copy[pair.Key] = pair.Value;
            return copy;
        }
    }
}
