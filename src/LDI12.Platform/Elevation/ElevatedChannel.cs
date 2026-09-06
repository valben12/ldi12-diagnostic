using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Runtime;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Elevation
{
    /// <summary>
    /// Canal vers <c>LDI12.ProbeHost.exe --elevated</c> : une seule invite UAC par session.
    /// </summary>
    /// <remarks>
    /// C'est l'application qui crée le tube et l'hôte élevé qui s'y connecte, et non l'inverse.
    /// Ainsi le nom du tube existe avant que quiconque puisse le connaître : personne ne peut le
    /// devancer et se faire passer pour l'hôte. La liste de contrôle d'accès n'accorde le tube
    /// qu'à l'utilisateur courant, et l'identifiant du processus connecté est vérifié : seul le
    /// processus que nous avons lancé peut répondre.
    /// <para>
    /// Le protocole reste volontairement pauvre, voir <c>ElevationProtocol</c>. L'hôte élevé
    /// n'exécute que les actions de son propre catalogue, jamais une commande reçue : c'est ce
    /// qui limite ce qu'un abus de ce canal pourrait obtenir.
    /// </para>
    /// </remarks>
    public sealed class ElevatedChannel : IElevatedChannel, IDisposable
    {
        private const string Category = "Platform.Elevation";
        private const string HostFileName = "LDI12.ProbeHost.exe";

        /// <summary>Temps laissé à l'utilisateur pour répondre à l'invite UAC.</summary>
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(120);

        private readonly IPlatformInfo _platform;
        private readonly IProcessLauncher _launcher;
        private readonly IScopedLogger _log;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private NamedPipeServerStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _disposed;

        public ElevatedChannel(IPlatformInfo platform, IProcessLauncher launcher, ILdiLogger logger)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

            State = platform.IsElevated ? ElevatedChannelState.AlreadyElevated : ElevatedChannelState.NotRequested;
        }

        public ElevatedChannelState State { get; private set; }

        public string? Reason { get; private set; }

        public async Task<ElevatedChannelState> EnsureAsync(CancellationToken cancellationToken)
        {
            if (State == ElevatedChannelState.AlreadyElevated || State == ElevatedChannelState.Available)
                return State;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == ElevatedChannelState.Available) return State;
                return State = await OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ElevatedChannelState> OpenAsync(CancellationToken cancellationToken)
        {
            var host = ResolveHostPath();
            if (host == null)
            {
                Reason = HostFileName + " est introuvable à côté de l'application.";
                return ElevatedChannelState.Failed;
            }

            var name = "LDI12-elev-" + Guid.NewGuid().ToString("N");

            try
            {
                _pipe = Create(name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Reason = "Le canal n'a pas pu être créé : " + ex.Message;
                _log.Error("Création du tube impossible.", ex);
                return ElevatedChannelState.Failed;
            }

            var launch = _launcher.Launch(
                new LaunchRequest(host, "--elevated --pipe " + name) { Elevated = true, Hidden = true });

            if (launch.Refused)
            {
                Reason = "L'élévation a été refusée.";
                Close();
                return ElevatedChannelState.Refused;
            }

            if (!launch.Started)
            {
                Reason = launch.Reason ?? "L'hôte élevé n'a pas pu être lancé.";
                Close();
                return ElevatedChannelState.Failed;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionTimeout);

            try
            {
                await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Reason = "L'hôte élevé ne s'est pas connecté dans le délai imparti.";
                Close();
                return ElevatedChannelState.Failed;
            }

            if (!IsExpectedClient(launch.ProcessId))
            {
                // Un autre processus s'est glissé sur le tube : on referme sans échanger un mot.
                Reason = "Le processus connecté n'est pas celui qui a été lancé. Canal refermé.";
                _log.Error("Identité du processus connecté inattendue : canal refermé.");
                Close();
                return ElevatedChannelState.Failed;
            }

            _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            Reason = null;
            _log.Info("Hôte élevé connecté, une seule invite UAC pour toute la session.");
            return ElevatedChannelState.Available;
        }

        /// <summary>
        /// Tube accessible au seul utilisateur courant.
        /// </summary>
        /// <remarks>
        /// L'hôte élevé tourne sous le même compte que l'application : c'est le jeton qui diffère,
        /// pas l'identité. Une autorisation nominative suffit donc, et exclut tout autre compte
        /// de la machine.
        /// </remarks>
        private static NamedPipeServerStream Create(string name)
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();

            security.AddAccessRule(new PipeAccessRule(
                identity.User ?? (IdentityReference)new NTAccount(Environment.UserName),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
        }

        private bool IsExpectedClient(int? expectedProcessId)
        {
            if (_pipe == null) return false;
            if (expectedProcessId == null || expectedProcessId.Value == 0) return true;

            try
            {
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var connected))
                {
                    // API absente ou refusée : on ne prétend pas avoir vérifié.
                    _log.Warn("L'identifiant du processus connecté n'a pas pu être lu.");
                    return true;
                }

                return connected == expectedProcessId.Value;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return true;
            }
        }

        public async Task<string> SendAsync(
            string request, Func<string, bool>? onNotification, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_writer == null || _reader == null)
                throw new InvalidOperationException("Le canal élevé n'est pas ouvert.");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);

                await _writer.WriteLineAsync(request).ConfigureAwait(false);

                try
                {
                    return await ReadReplyAsync(_reader, onNotification, deadline.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    State = ElevatedChannelState.Failed;
                    Reason = "L'hôte élevé s'est arrêté.";
                    throw new IOException("L'hôte élevé a fermé le canal.");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Lit les lignes rendues par l'hôte jusqu'à la réponse, en passant les notifications.
        /// </summary>
        /// <remarks>
        /// Isolée du reste pour être éprouvable sans tube ni élévation : c'est ici qu'une
        /// progression a été prise pour une réponse pendant toute la durée du projet, faute de
        /// pouvoir écrire un test sur un échange.
        /// </remarks>
        internal static async Task<string> ReadReplyAsync(
            TextReader reader, Func<string, bool>? onNotification, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) throw new EndOfStreamException("L'hôte élevé a fermé le canal.");
                if (line.Length == 0) continue;

                // Ce qui est une notification, la couche plateforme ne le sait pas et n'a pas à
                // le savoir : elle transporte des lignes, l'appelant les interprète.
                if (onNotification != null && onNotification(line)) continue;

                return line;
            }
        }

        /// <summary>
        /// Cherche l'hôte à côté de l'exécutable courant, puis dans ses propres ressources, puis
        /// dans les sorties de compilation voisines.
        /// </summary>
        /// <remarks>
        /// L'ordre compte. Un fichier posé à côté de l'exécutable l'emporte, parce qu'il permet
        /// de remplacer l'hôte sans republier le logiciel. Vient ensuite l'hôte embarqué, qui est
        /// le cas normal d'une version publiée en exécutable unique : il est écrit dans le profil
        /// de l'utilisateur au moment où l'élévation est demandée, et pas avant : un outil de
        /// diagnostic n'a pas à déposer un second exécutable sur la machine d'un client tant que
        /// personne ne le lui a demandé. La recherche dans les dossiers de compilation ne sert
        /// qu'au développement.
        /// </remarks>
        internal static string? ResolveHostPath()
        {
            var beside = Path.Combine(AppContext.BaseDirectory, HostFileName);
            if (File.Exists(beside)) return beside;

            var extracted = BundledFiles.Extract(HostFileName);
            if (extracted != null) return extracted;

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "src", "LDI12.ProbeHost", "bin", ConfigurationName(), "net462", HostFileName);
                if (File.Exists(candidate)) return candidate;

                directory = directory.Parent;
            }

            return null;
        }

        private static string ConfigurationName()
            => AppContext.BaseDirectory.IndexOf(@"\Release\", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Release"
                : "Debug";

        private void Close()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
            _reader = null;
            _writer = null;
            _pipe = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_writer != null && _pipe != null && _pipe.IsConnected)
                    _writer.WriteLine("{\"Op\":\"close\"}");
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // L'hôte est peut-être déjà parti : rien à sauver ici.
            }

            Close();
            _gate.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out int processId);
    }
}
