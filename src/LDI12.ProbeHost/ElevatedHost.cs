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
using LDI12.Platform.Gateways;

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

            // Lire en mode sauvegarde, comme robocopy /B : c'est ce qui permet de copier les comptes
            // d'un autre Windows, protégés par des droits qui ne connaissent pas le technicien. Rien
            // n'est modifié sur les fichiers ; seules les lectures passent outre leurs droits.
            log.Info(FileSystemGateway.EnableBackupSemantics()
                ? "Privilège de sauvegarde actif : lecture des fichiers protégés possible."
                : "Privilège de sauvegarde refusé : les fichiers protégés seront comptés comme refusés.");

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

            // Une ligne à la fois sur le tube. Les notifications de progression partent au fil de
            // l'eau depuis l'action, la réponse à la fin : sans ce verrou, deux lignes pouvaient
            // s'entremêler.
            var writeLock = new object();
            void Send(ElevatedMessage message)
            {
                var text = ElevationProtocol.Write(message);
                lock (writeLock) writer.WriteLine(text);
            }

            // Le tube est lu en permanence, y compris pendant une opération : c'est ce qui permet
            // de recevoir l'ordre d'arrêt d'une copie qui dure des heures. Une seule lecture est en
            // attente à la fois.
            var lineTask = reader.ReadLineAsync();
            Task<ElevatedMessage>? running = null;
            CancellationTokenSource? operation = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Sans opération en cours, un hôte oublié ne doit pas rester en vie indéfiniment.
                var waitFor = running ?? (Task)Task.Delay(IdleTimeout, cancellationToken);
                var done = await Task.WhenAny(lineTask, waitFor).ConfigureAwait(false);

                if (done != lineTask)
                {
                    if (running == null)
                    {
                        log.Info("Canal inactif : l'hôte élevé se referme.");
                        break;
                    }

                    Send(await running.ConfigureAwait(false));
                    running = null;
                    operation?.Dispose();
                    operation = null;
                    continue;
                }

                var line = await lineTask.ConfigureAwait(false);
                if (line == null)
                {
                    log.Info("Canal refermé par l'application.");
                    operation?.Cancel();
                    break;
                }

                lineTask = reader.ReadLineAsync();

                var request = ElevationProtocol.Read<ElevatedRequest>(line);
                if (request != null && string.Equals(request.Op, ElevationProtocol.OpCancel, StringComparison.OrdinalIgnoreCase))
                {
                    log.Info("Arrêt demandé par le technicien.");
                    operation?.Cancel();
                    continue;
                }

                if (request != null && string.Equals(request.Op, ElevationProtocol.OpClose, StringComparison.OrdinalIgnoreCase))
                {
                    operation?.Cancel();
                    break;
                }

                // L'application n'envoie une requête qu'après la réponse à la précédente : une
                // requête pendant une opération ne peut être qu'un signal inconnu, qui n'a pas de
                // réponse à attendre. En répondre une serait la prendre pour celle de l'opération.
                if (running != null)
                {
                    log.Warn("Requête reçue pendant une opération, ignorée : " + (request?.Op ?? "illisible"));
                    continue;
                }

                if (request == null)
                {
                    Send(new ElevatedMessage { Type = ElevatedMessage.TypeError, Message = "Requête illisible." });
                    continue;
                }

                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                running = session.HandleAsync(request, Send, operation.Token);
            }

            if (running != null)
            {
                try { await running.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException) { }
            }

            log.Info("Hôte élevé terminé.");
            return 0;
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

                // Rapportée sur-le-champ, et non par Progress<T> qui passe par le pool : une
                // notification pouvait sinon partir après la réponse, et être prise pour la
                // réponse de la requête suivante.
                var progress = new ImmediateProgress(report => notify(new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeProgress,
                    Text = report.Text,
                    Fraction = report.Fraction,
                    Detail = report.Detail,
                    RemainingSeconds = report.Remaining?.TotalSeconds,
                    ElapsedSeconds = report.Elapsed?.TotalSeconds,
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
                    _services.Registry, _services.Restore, _logger, null, parameters, new DpapiSecretProtector());

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
    
        /// <summary>Une progression rapportée sur le fil de l'action, dans l'ordre.</summary>
        private sealed class ImmediateProgress : IProgress<ActionProgress>
        {
            private readonly Action<ActionProgress> _report;

            public ImmediateProgress(Action<ActionProgress> report) => _report = report;

            public void Report(ActionProgress value) => _report(value);
        }
    }
}
