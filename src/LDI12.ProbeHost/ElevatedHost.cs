using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Elevation;
using LDI12.Actions.Journal;
using LDI12.Actions.Maintenance;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform;

namespace LDI12.ProbeHost
{
    /// <summary>
    /// Face élevée du canal : exécute les actions privilégiées demandées par l'application.
    /// </summary>
    /// <remarks>
    /// Trois propriétés le rendent acceptable :
    /// <list type="number">
    /// <item>il n'exécute que des actions de son propre catalogue, désignées par identifiant,
    /// jamais une commande reçue sur le tube ;</item>
    /// <item>il se connecte en <c>Identification</c> : le processus qui tient l'autre bout ne
    /// peut pas se servir de cette connexion pour usurper son jeton administrateur ;</item>
    /// <item>il meurt dès que le tube se ferme, et de lui-même après un quart d'heure sans
    /// demande, les privilèges ne survivent pas à la session de travail.</item>
    /// </list>
    /// </remarks>
    internal static class ElevatedHost
    {
        private const string Category = "Elevated";

        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Chemins transmis pour affichage. Le relevé complet reste ici.</summary>
        private const int DisplayedFilesPerGroup = CleanupAction.DisplayedFilesPerGroup;

        public static async Task<int> RunAsync(string pipeName, ILdiLogger logger, CancellationToken cancellationToken)
        {
            var log = logger.For(Category);

            if (!IsElevated())
            {
                log.Error("Lancé sans privilèges administrateur : rien à faire ici.");
                return 1;
            }

            using var services = await PlatformServices.CreateAsync(logger, cancellationToken).ConfigureAwait(false);

            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                // Identification et non Impersonation : l'application, qui tourne en session
                // utilisateur, ne doit en aucun cas pouvoir emprunter ce jeton élevé.
                TokenImpersonationLevel.Identification);

            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException)
            {
                log.Error("Le canal n'a pas pu être rejoint.", ex);
                return 1;
            }

            log.Info("Canal élevé ouvert.");

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var session = new ElevatedSession(services, logger);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    log.Info("Canal refermé par l'application.");
                    break;
                }

                var request = ElevationProtocol.Read<ElevatedRequest>(line);
                if (request == null)
                {
                    await writer.WriteLineAsync(ElevationProtocol.Write(new ElevatedMessage
                    {
                        Type = ElevatedMessage.TypeError,
                        Message = "Requête illisible.",
                    })).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(request.Op, ElevationProtocol.OpClose, StringComparison.OrdinalIgnoreCase)) break;

                var response = await session
                    .HandleAsync(request, notification => writer.WriteLine(ElevationProtocol.Write(notification)), cancellationToken)
                    .ConfigureAwait(false);

                await writer.WriteLineAsync(ElevationProtocol.Write(response)).ConfigureAwait(false);
            }

            log.Info("Hôte élevé terminé.");
            return 0;
        }

        /// <summary>Lecture bornée : un hôte élevé oublié ne doit pas rester en vie indéfiniment.</summary>
        private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var read = reader.ReadLineAsync();
            var timeout = Task.Delay(IdleTimeout, cancellationToken);

            var finished = await Task.WhenAny(read, timeout).ConfigureAwait(false);
            return finished == read ? await read.ConfigureAwait(false) : null;
        }

        private static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>
        /// État de la session élevée : le catalogue d'actions et les relevés en attente
        /// d'exécution.
        /// </summary>
        private sealed class ElevatedSession
        {
            private readonly Dictionary<string, Pending> _pending =
                new Dictionary<string, Pending>(StringComparer.Ordinal);

            private readonly PlatformServices _services;
            private readonly ILdiLogger _logger;
            private readonly IScopedLogger _log;
            private readonly InterventionJournal _journal;

            public ElevatedSession(PlatformServices services, ILdiLogger logger)
            {
                _services = services;
                _logger = logger;
                _log = logger.For(Category);

                // Journal distinct de celui de l'application : le côté élevé consigne ce qu'il a
                // fait lui-même, et le fichier reste lisible même si l'application est fermée
                // brutalement.
                _journal = new InterventionJournal(logger);
            }

            public async Task<ElevatedMessage> HandleAsync(
                ElevatedRequest request, Action<ElevatedMessage> notify, CancellationToken cancellationToken)
            {
                try
                {
                    return request.Op?.ToLowerInvariant() switch
                    {
                        ElevationProtocol.OpPing => new ElevatedMessage { Type = ElevatedMessage.TypePong },
                        ElevationProtocol.OpPreview => await PreviewAsync(request, cancellationToken).ConfigureAwait(false),
                        ElevationProtocol.OpExecute => await ExecuteAsync(request, notify, cancellationToken).ConfigureAwait(false),
                        ElevationProtocol.OpRestorePoint => await RestorePointAsync(request, cancellationToken).ConfigureAwait(false),
                        _ => Error("Opération inconnue : " + request.Op),
                    };
                }
                catch (OperationCanceledException)
                {
                    return Error("Opération interrompue.");
                }
                catch (Exception ex)
                {
                    _log.Error("L'opération « " + request.Op + " » a échoué.", ex);
                    return Error("L'hôte élevé a rencontré une erreur : " + ex.Message);
                }
            }

            private async Task<ElevatedMessage> PreviewAsync(ElevatedRequest request, CancellationToken cancellationToken)
            {
                var action = Resolve(request.Action);
                if (action == null) return Error("Action inconnue : " + request.Action);

                var context = CreateContext(request.Parameters);
                var preview = await action.PreviewAsync(context, cancellationToken).ConfigureAwait(false);

                var token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending(action, preview, context);

                _journal.Record(
                    InterventionKind.Preview, action.Descriptor.Id, action.Descriptor.DisplayName,
                    preview.Outcome.ToString(), preview.Summary, elevated: true);

                return new ElevatedMessage
                {
                    Type = ElevatedMessage.TypePreview,
                    Preview = PreviewPayload.From(preview, token, DisplayedFilesPerGroup),
                };
            }

            private async Task<ElevatedMessage> ExecuteAsync(
                ElevatedRequest request, Action<ElevatedMessage> notify, CancellationToken cancellationToken)
            {
                if (request.Token == null || !_pending.TryGetValue(request.Token, out var pending))
                    return Error("Aucun relevé ne correspond : rien n'a été exécuté. " +
                                 "Refaire la prévisualisation avant d'exécuter.");

                // Un relevé ne sert qu'une fois : le réutiliser supprimerait une seconde fois
                // ce qui a déjà été montré et traité.
                _pending.Remove(request.Token);

                if (!string.Equals(pending.Action.Descriptor.Id, request.Action, StringComparison.OrdinalIgnoreCase))
                    return Error("Le relevé ne correspond pas à l'action demandée.");

                var progress = new Progress<ActionProgress>(report => notify(new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeProgress,
                    Text = report.Text,
                    Fraction = report.Fraction,
                }));

                var outcome = await pending.Action
                    .ExecuteAsync(pending.Context, pending.Preview, progress, cancellationToken)
                    .ConfigureAwait(false);

                _journal.Record(
                    InterventionKind.Execution, pending.Action.Descriptor.Id, pending.Action.Descriptor.DisplayName,
                    outcome.Status.ToString(), outcome.Summary, elevated: true,
                    duration: outcome.Duration, freedBytes: outcome.FreedBytes, details: outcome.Details);

                return new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeOutcome,
                    Outcome = OutcomePayload.From(outcome),
                };
            }

            private async Task<ElevatedMessage> RestorePointAsync(
                ElevatedRequest request, CancellationToken cancellationToken)
            {
                var result = await _services.Restore
                    .CreateAsync(request.Description ?? "LDI12 Diagnostic : avant intervention", cancellationToken)
                    .ConfigureAwait(false);

                _journal.Record(
                    InterventionKind.RestorePoint, "RESTORE-POINT", "Point de restauration",
                    result.State.ToString(), result.Message, elevated: true);

                return new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeRestore,
                    RestoreState = result.State,
                    Message = result.Message,
                };
            }

            private IRepairAction? Resolve(string? id)
                => id == null ? null : ActionCatalog.Find(id, _logger);

            private ActionContext CreateContext(Dictionary<string, string>? parameters)
                => new ActionContext(
                    _services.Platform, _services.Processes, _services.Launcher, _services.Files,
                    _services.Registry, _services.Restore, _logger, null, parameters);

            private static ElevatedMessage Error(string message)
                => new ElevatedMessage { Type = ElevatedMessage.TypeError, Message = message };

            private sealed class Pending
            {
                public Pending(IRepairAction action, ActionPreview preview, ActionContext context)
                {
                    Action = action;
                    Preview = preview;
                    Context = context;
                }

                public IRepairAction Action { get; }
                public ActionPreview Preview { get; }
                public ActionContext Context { get; }
            }
        }
    }
}
