using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Platform;
using LDI12.Platform.Isolation;
using LDI12.Reports.Json;

namespace LDI12.ProbeHost
{
    /// <summary>
    /// Face isolée du canal : exécute une sonde à la demande, et rend ce qu'elle a écrit.
    /// </summary>
    /// <remarks>
    /// Ce processus existe pour être <b>tué</b>. Il ne détient rien qui ne puisse disparaître
    /// avec lui : pas de journal d'intervention, pas d'action, pas d'élévation. Le parent lui
    /// envoie un identifiant de sonde de son propre catalogue et les sections déjà collectées
    /// dont elle peut dépendre ; il rend le compte rendu et les sections écrites.
    /// <para>
    /// Il meurt dès que le tube se ferme, et de lui-même après cinq minutes sans demande : un
    /// processus orphelin qui survivrait à l'application serait exactement le genre de trace
    /// qu'un outil de diagnostic ne doit pas laisser sur la machine d'un client.
    /// </para>
    /// </remarks>
    internal static class IsolatedProbeHost
    {
        private const string Category = "Isolated";

        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

        public static async Task<int> RunAsync(
            string pipeName, bool sensorsEnabled, ILdiLogger logger, CancellationToken cancellationToken)
        {
            var log = logger.For(Category);

            using var services = await PlatformServices.CreateAsync(logger, cancellationToken).ConfigureAwait(false);
            if (sensorsEnabled) services.Sensors.Enable(true);

            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);

            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException)
            {
                log.Error("Le canal d'isolation n'a pas pu être rejoint.", ex);
                return 1;
            }

            var codec = new SnapshotCodec();
            var probes = Index(CollectorCatalog.CreateAll(logger));
            var draft = new SnapshotDraft();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };

            log.Info("Hôte isolé connecté : " + probes.Count + " module(s) disponibles.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    log.Info("Canal fermé : arrêt de l'hôte isolé.");
                    return 0;
                }

                var request = codec.Deserialize(line, typeof(IsolationRequest)) as IsolationRequest;
                if (request == null || string.Equals(request.Op, IsolationProtocol.OpClose, StringComparison.Ordinal))
                    return 0;

                var response = await HandleAsync(request, probes, draft, services, codec, log, cancellationToken)
                    .ConfigureAwait(false);

                await writer.WriteLineAsync(codec.Serialize(response)).ConfigureAwait(false);
            }

            return 0;
        }

        private static async Task<IsolationResponse> HandleAsync(
            IsolationRequest request, IReadOnlyDictionary<string, IDiagnosticProbe> probes,
            SnapshotDraft draft, PlatformServices services, SnapshotCodec codec,
            IScopedLogger log, CancellationToken cancellationToken)
        {
            if (request.Probe == null || !probes.TryGetValue(request.Probe, out var probe))
            {
                // Le parent ne devrait jamais demander autre chose ; le dire plutôt que de se
                // taire évite de chercher longtemps le jour où les deux catalogues divergent.
                return Failed("Module inconnu de l'hôte isolé : " + (request.Probe ?? "(sans nom)") + ".");
            }

            // Les sections reçues garnissent le relevé pour que la sonde retrouve ce dont elle
            // dépend, la liste des disques pour la lecture SMART, par exemple.
            foreach (var section in request.Input ?? new List<IsolationSection>())
            {
                var type = DraftSections.TypeOf(section.Name);
                if (type == null) continue;

                var value = codec.Deserialize(section.Json, type);
                if (value != null) draft.Apply(section.Name, value);
            }

            var mode = Enum.TryParse<RunMode>(request.Mode, out var parsed) ? parsed : RunMode.Full;
            var context = services.CreateProbeContext(draft, mode);

            // Seul ce que la sonde écrit maintenant doit remonter : le reste appartient au parent.
            draft.ClearWrites();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var outcome = await probe.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                log.Debug(request.Probe + " → " + outcome.Status + " en " + stopwatch.ElapsedMilliseconds + " ms.");

                return new IsolationResponse
                {
                    Status = outcome.Status.ToString(),
                    Message = outcome.Message,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Exception = outcome.Exception == null
                        ? null
                        : outcome.Exception.GetType().Name + " : " + outcome.Exception.Message,
                    Writes = Pack(draft.Writes, codec),
                };
            }
            catch (OperationCanceledException)
            {
                return new IsolationResponse { Status = ProbeStatus.Cancelled.ToString() };
            }
            catch (Exception ex)
            {
                // Même filet que dans le processus principal : une sonde qui lève rend un compte
                // rendu en échec, et l'hôte reste debout pour la suivante.
                log.Error(request.Probe + " a levé une exception non gérée.", ex);
                return new IsolationResponse
                {
                    Status = ProbeStatus.Failed.ToString(),
                    Message = "Le module a rencontré une erreur inattendue. Le reste du diagnostic n'est pas affecté.",
                    Exception = ex.GetType().Name + " : " + ex.Message,
                };
            }
        }

        private static List<IsolationSection> Pack(IReadOnlyList<DraftSection> sections, SnapshotCodec codec)
        {
            var packed = new List<IsolationSection>(sections.Count);
            foreach (var section in sections)
                packed.Add(new IsolationSection { Name = section.Name, Json = codec.Serialize(section.Value) });
            return packed;
        }

        private static IsolationResponse Failed(string message)
            => new IsolationResponse { Status = ProbeStatus.Failed.ToString(), Message = message };

        private static IReadOnlyDictionary<string, IDiagnosticProbe> Index(IReadOnlyList<IDiagnosticProbe> probes)
        {
            var map = new Dictionary<string, IDiagnosticProbe>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in probes) map[probe.Descriptor.Id] = probe;
            return map;
        }

        /// <summary>
        /// Attend une demande, sans attendre indéfiniment.
        /// </summary>
        /// <remarks>
        /// Si l'application disparaît sans refermer proprement le tube, la lecture ne rendrait
        /// jamais la main : ce processus resterait sur la machine du client. Au bout de cinq
        /// minutes sans demande, il s'en va.
        /// </remarks>
        private static async Task<string?> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var read = reader.ReadLineAsync();
            var idle = Task.Delay(IdleTimeout, cancellationToken);

            return await Task.WhenAny(read, idle).ConfigureAwait(false) == read
                ? await read.ConfigureAwait(false)
                : null;
        }
    }
}
