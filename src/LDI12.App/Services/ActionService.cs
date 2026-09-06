using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Journal;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Elevation;

namespace LDI12.App.Services
{
    /// <summary>
    /// Construit et tient l'orchestrateur d'actions, le journal d'intervention et le canal élevé.
    /// </summary>
    /// <remarks>
    /// Un seul journal et un seul canal pour toute la session : c'est ce qui fait qu'une
    /// intervention en trois étapes ne demande qu'une invite UAC, et se relit ensuite d'un seul
    /// tenant. Le canal n'est pas ouvert ici : il ne l'est qu'à la première action qui en a
    /// réellement besoin.
    /// </remarks>
    public sealed class ActionService : IDisposable
    {
        private readonly DiagnosticService _diagnostics;
        private readonly ILdiLogger _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private ElevatedChannel? _channel;
        private ActionRunner? _runner;
        private SystemSnapshot? _snapshot;

        public ActionService(DiagnosticService diagnostics, ILdiLogger logger)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Journal = new InterventionJournal(logger);
        }

        public InterventionJournal Journal { get; }

        public async Task<ActionRunner> GetRunnerAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var services = await _diagnostics.GetPlatformAsync(cancellationToken).ConfigureAwait(false);

                if (_runner != null) return _runner;

                _channel ??= new ElevatedChannel(services.Platform, services.Launcher, _logger);

                var context = new ActionContext(
                    services.Platform, services.Processes, services.Launcher, services.Files,
                    services.Registry, services.Restore, _logger, _snapshot);

                return _runner = new ActionRunner(context, Journal, _logger, _channel);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Reconstruit le contexte sur le nouveau diagnostic.
        /// </summary>
        /// <remarks>
        /// Le journal et le canal, eux, survivent : ce qui a été fait avant une seconde analyse
        /// reste dans le compte rendu, et l'élévation déjà accordée n'est pas redemandée.
        /// </remarks>
        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;

            var previous = _runner;
            if (previous == null) return;

            _runner = new ActionRunner(previous.Context.With(snapshot), Journal, _logger, _channel);
        }

        public void Dispose()
        {
            _channel?.Dispose();
            _gate.Dispose();
        }
    }
}
