using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Core.Runtime;

namespace LDI12.Platform.Isolation
{
    /// <summary>
    /// Exécute les sondes déclarées isolées dans un processus enfant, que l'on peut tuer.
    /// </summary>
    /// <remarks>
    /// <b>Un seul processus, réutilisé, et les demandes se suivent.</b> Un processus par sonde
    /// coûterait dix lancements sur un diagnostic complet ; un processus permanent en coûte un.
    /// La contrepartie est que les sondes isolées ne s'exécutent plus en parallèle entre elles :
    /// elles continuent en revanche de tourner pendant que les autres travaillent, et leur somme
    /// pèse quelques secondes sur une analyse qui en dure des dizaines.
    /// <para>
    /// <b>Le dépassement de délai tue l'hôte.</b> C'est tout l'objet de la manœuvre : le délai
    /// maximal seul abandonne une tâche mais laisse le fil bloqué pour la durée de la session,
    /// avec le dépôt WMI ou le disque qu'il tient encore. La demande suivante relance un hôte
    /// neuf.
    /// </para>
    /// <para>
    /// Si l'hôte est introuvable ou refuse de démarrer, la méthode rend <c>null</c> et
    /// l'appelant exécute la sonde chez lui : une isolation indisponible ne doit priver personne
    /// d'un module qui fonctionne.
    /// </para>
    /// </remarks>
    public sealed class ProbeIsolationHost : IProbeIsolationHost, IDisposable
    {
        private const string Category = "Platform.Isolation";
        private const string HostFileName = "LDI12.ProbeHost.exe";

        /// <summary>Temps laissé à l'hôte pour se connecter au tube.</summary>
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Marge accordée au-delà du délai de la sonde, pour l'aller-retour lui-même.</summary>
        private static readonly TimeSpan Overhead = TimeSpan.FromSeconds(5);

        private readonly IProcessLauncher _launcher;
        private readonly ISnapshotCodec _codec;
        private readonly IScopedLogger _log;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly bool _sensorsEnabled;

        private NamedPipeServerStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private int? _processId;
        private string? _temporaryDirectory;
        private bool _unavailable;
        private bool _disposed;

        public ProbeIsolationHost(
            IProcessLauncher launcher, ISnapshotCodec codec, bool sensorsEnabled, ILdiLogger logger)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _sensorsEnabled = sensorsEnabled;
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public async Task<IsolatedOutcome?> RunAsync(
            ProbeDescriptor descriptor,
            RunMode mode,
            IReadOnlyList<DraftSection> input,
            CancellationToken cancellationToken)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (_disposed || _unavailable) return null;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_unavailable) return null;
                if (!await EnsureHostAsync(cancellationToken).ConfigureAwait(false)) return null;

                var request = new IsolationRequest
                {
                    Op = IsolationProtocol.OpRun,
                    Probe = descriptor.Id,
                    Mode = mode.ToString(),
                    Input = Pack(input),
                };

                return await ExchangeAsync(descriptor, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                _log.Warn("L'hôte isolé s'est interrompu : " + descriptor.Id + " sera exécuté sur place.");
                Kill();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<IsolatedOutcome?> ExchangeAsync(
            ProbeDescriptor descriptor, IsolationRequest request, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(descriptor.HardTimeout + Overhead);

            await _writer!.WriteLineAsync(_codec.Serialize(request)).ConfigureAwait(false);

            var read = _reader!.ReadLineAsync();
            var abandon = Task.Delay(Timeout.Infinite, deadline.Token);

            if (await Task.WhenAny(read, abandon).ConfigureAwait(false) != read)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Kill();
                    throw new OperationCanceledException(cancellationToken);
                }

                // Le fil bloqué part avec le processus : c'est exactement ce que l'isolation
                // apporte, et que le seul délai maximal ne pouvait pas faire.
                _log.Warn(descriptor.Id + " n'a pas répondu : l'hôte isolé est arrêté.");
                Kill();

                return new IsolatedOutcome
                {
                    Status = ProbeStatus.TimedOut,
                    DurationMs = (long)descriptor.HardTimeout.TotalMilliseconds,
                    Message = "Le module n'a pas répondu dans le délai de " +
                              descriptor.HardTimeout.TotalSeconds.ToString("0.#") +
                              " s. Il s'exécutait dans un processus séparé, qui a été arrêté.",
                };
            }

            var line = await read.ConfigureAwait(false);
            if (line == null)
            {
                _log.Warn("L'hôte isolé a fermé le canal pendant " + descriptor.Id + ".");
                Kill();
                return null;
            }

            var response = _codec.Deserialize(line, typeof(IsolationResponse)) as IsolationResponse;
            if (response == null)
            {
                _log.Warn("Réponse illisible de l'hôte isolé pour " + descriptor.Id + ".");
                return null;
            }

            return new IsolatedOutcome
            {
                Status = Enum.TryParse<ProbeStatus>(response.Status, out var status) ? status : ProbeStatus.Failed,
                Message = response.Message,
                ExceptionSummary = response.Exception,
                DurationMs = response.DurationMs,
                Writes = Unpack(response.Writes),
            };
        }

        // ------------------------------------------------------------------ transport

        private List<IsolationSection> Pack(IReadOnlyList<DraftSection> sections)
        {
            var packed = new List<IsolationSection>(sections.Count);
            foreach (var section in sections)
                packed.Add(new IsolationSection { Name = section.Name, Json = _codec.Serialize(section.Value) });
            return packed;
        }

        private IReadOnlyList<DraftSection> Unpack(IReadOnlyList<IsolationSection>? sections)
        {
            if (sections == null) return Array.Empty<DraftSection>();

            var unpacked = new List<DraftSection>(sections.Count);
            foreach (var section in sections)
            {
                // Le type vient du noyau, jamais du message : rien dans le flux ne décide de ce
                // qui sera instancié ici.
                var type = DraftSections.TypeOf(section.Name);
                if (type == null)
                {
                    _log.Warn("Section inconnue rendue par l'hôte isolé : " + section.Name + ".");
                    continue;
                }

                var value = _codec.Deserialize(section.Json, type);
                if (value != null) unpacked.Add(new DraftSection(section.Name, value));
            }

            return unpacked;
        }

        // ------------------------------------------------------------------ processus

        private async Task<bool> EnsureHostAsync(CancellationToken cancellationToken)
        {
            if (_writer != null && _reader != null && IsAlive()) return true;

            Close();

            var host = ResolveHostPath();
            if (host == null)
            {
                _log.Warn(HostFileName + " est introuvable : les modules isolés s'exécuteront sur place.");
                _unavailable = true;
                return false;
            }

            var name = "LDI12-iso-" + Guid.NewGuid().ToString("N");

            try
            {
                _pipe = Create(name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.Error("Le canal d'isolation n'a pas pu être créé.", ex);
                _unavailable = true;
                return false;
            }

            var arguments = IsolationProtocol.HostArgument + " --pipe " + name + (_sensorsEnabled ? " --sensors" : string.Empty);
            // Sans fenêtre : l'hôte est une application console, et le premier module isolé
            // d'une analyse faisait sinon surgir une fenêtre noire au lancement du logiciel.
            var launch = _launcher.Launch(new LaunchRequest(host, arguments) { Hidden = true });

            if (!launch.Started)
            {
                _log.Warn("L'hôte isolé n'a pas pu être lancé : " + (launch.Reason ?? "raison inconnue") + ".");
                Close();
                _unavailable = true;
                return false;
            }

            _processId = launch.ProcessId;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionTimeout);

            try
            {
                await _pipe!.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn("L'hôte isolé ne s'est pas connecté dans le délai imparti.");
                Kill();
                _unavailable = true;
                return false;
            }

            _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };

            _log.Info("Hôte isolé prêt (processus " + (_processId?.ToString() ?? "?") + ").");
            return true;
        }

        /// <summary>
        /// Tube accessible au seul utilisateur courant.
        /// </summary>
        /// <remarks>
        /// L'hôte isolé tourne sous le même compte et sans élévation : une autorisation nominative
        /// suffit, et exclut tout autre compte de la machine.
        /// </remarks>
        private static NamedPipeServerStream Create(string name)
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();

            security.AddAccessRule(new PipeAccessRule(
                identity.User ?? (IdentityReference)new NTAccount(Environment.UserName),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 256 * 1024, 256 * 1024, security);
        }

        private bool IsAlive()
        {
            if (_pipe == null || !_pipe.IsConnected) return false;
            if (_processId == null) return true;

            try
            {
                using var process = Process.GetProcessById(_processId.Value);
                return !process.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        private void Kill()
        {
            if (_processId != null)
            {
                try
                {
                    using var process = Process.GetProcessById(_processId.Value);
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception ex) when (
                    ex is ArgumentException || ex is InvalidOperationException ||
                    ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    // Déjà parti, ou impossible à arrêter : le canal est refermé de toute façon.
                }
            }

            Close();
        }

        private void Close()
        {
            try { _writer?.Dispose(); } catch (IOException) { }
            try { _reader?.Dispose(); } catch (IOException) { }
            try { _pipe?.Dispose(); } catch (IOException) { }

            _writer = null;
            _reader = null;
            _pipe = null;
            _processId = null;
        }

        /// <summary>
        /// Cherche l'hôte à côté de l'exécutable courant, puis dans les ressources embarquées.
        /// </summary>
        /// <remarks>
        /// Même ordre que pour le canal élevé : un fichier posé à côté de l'exécutable l'emporte,
        /// l'hôte embarqué prend le relais pour une version publiée en exécutable unique.
        /// <para>
        /// <b>Une différence, et elle compte.</b> Le canal élevé écrit l'hôte dans le profil de
        /// l'utilisateur et l'y laisse : le technicien l'a demandé, et le fichier doit survivre à
        /// l'invite UAC. L'isolation, elle, se déclenche à chaque analyse sans que personne ne la
        /// demande. Elle écrit donc dans un dossier temporaire, qu'elle efface en partant : un
        /// outil de diagnostic n'a pas à déposer cinq mégaoctets sur la machine d'un client pour
        /// son propre confort.
        /// </para>
        /// </remarks>
        private string? ResolveHostPath()
        {
            // Un hôte tué laisse son fichier derrière lui : on l'efface avant d'en écrire un
            // autre, sans quoi une analyse qui redémarre l'hôte trois fois en laisserait trois.
            RemoveTemporaryHost();

            var directory = Path.GetDirectoryName(
                System.Reflection.Assembly.GetEntryAssembly()?.Location ??
                AppDomain.CurrentDomain.BaseDirectory);

            if (directory != null)
            {
                var beside = Path.Combine(directory, HostFileName);
                if (File.Exists(beside)) return beside;
            }

            var temporary = BundledFiles.ExtractTemporary(HostFileName);
            if (temporary != null) _temporaryDirectory = Path.GetDirectoryName(temporary);
            return temporary;
        }

        /// <summary>
        /// Efface l'hôte écrit pour cette analyse.
        /// </summary>
        /// <remarks>
        /// Le processus vient d'être arrêté : Windows peut mettre un instant à relâcher le
        /// fichier, d'où les quelques tentatives. Un échec n'a aucune conséquence (le dossier
        /// est dans le répertoire temporaire, que Windows nettoie de lui-même) et surtout il ne
        /// doit rien interrompre.
        /// </remarks>
        private void RemoveTemporaryHost()
        {
            var directory = _temporaryDirectory;
            if (directory == null) return;
            _temporaryDirectory = null;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory)) return;
                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
                catch (Exception ex) when (ex is ArgumentException)
                {
                    return;
                }
            }

            _log.Debug("L'hôte isolé temporaire n'a pas pu être effacé : " + directory + ".");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_writer != null)
                    _writer.WriteLine(_codec.Serialize(new IsolationRequest { Op = IsolationProtocol.OpClose }));
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // L'hôte est déjà parti : rien à lui dire.
            }

            Close();
            RemoveTemporaryHost();
            _gate.Dispose();
        }
    }
}
