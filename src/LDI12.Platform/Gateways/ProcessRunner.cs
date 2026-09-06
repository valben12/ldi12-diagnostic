using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Exécution d'outils système avec délai maximal, annulation et capture bornée.
    /// </summary>
    /// <remarks>
    /// Aucun module ne lance de processus directement : c'est ici que sont centralisés les trois
    /// pièges qui coûtent cher : le délai maximal (un <c>chkdsk</c> peut ne jamais rendre la main),
    /// la capture bornée (DISM verbeux sur une machine sans mémoire) et surtout l'encodage.
    /// </remarks>
    public sealed class ProcessRunner : IProcessRunner
    {
        private const string Category = "Platform.Process";
        private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan FlushGracePeriod = TimeSpan.FromSeconds(2);

        private readonly IScopedLogger _log;

        public ProcessRunner(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var stopwatch = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo(request.FileName, request.Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            };

            var encoding = ResolveEncoding(request.OutputEncoding);
            if (encoding != null)
            {
                startInfo.StandardOutputEncoding = encoding;
                startInfo.StandardErrorEncoding = encoding;
            }

            var output = new BoundedBuffer(request.MaxOutputChars);
            var error = new BoundedBuffer(request.MaxOutputChars);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(true);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is System.IO.FileNotFoundException)
            {
                _log.Warn("Lancement impossible : " + request, ex);
                return new ProcessResult
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    LaunchFailed = true,
                    Exception = ex,
                    Duration = stopwatch.Elapsed,
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timedOut = false;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(request.Timeout);
                using (deadline.Token.Register(() => exited.TrySetResult(false)))
                {
                    if (!await exited.Task.ConfigureAwait(false))
                    {
                        timedOut = !cancellationToken.IsCancellationRequested;
                        _log.Warn((timedOut ? "Délai dépassé" : "Annulation") + " : " + request +
                                  " : le processus est arrêté après " + stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                        Kill(process);
                    }
                }
            }

            // WaitForExit sans argument après une lecture asynchrone garantit le vidage des
            // tampons de sortie ; on le borne malgré tout pour ne jamais bloquer indéfiniment.
            try { process.WaitForExit((int)FlushGracePeriod.TotalMilliseconds); }
            catch (SystemException) { /* processus déjà disparu */ }

            var exitCode = 0;
            try { exitCode = process.HasExited ? process.ExitCode : -1; }
            catch (InvalidOperationException) { exitCode = -1; }

            stopwatch.Stop();
            _log.Debug(request + " → code " + exitCode + " en " + stopwatch.ElapsedMilliseconds + " ms.");

            return new ProcessResult
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                ExitCode = exitCode,
                StandardOutput = output.ToString(),
                StandardError = error.ToString(),
                Duration = stopwatch.Elapsed,
                TimedOut = timedOut,
                OutputTruncated = output.Truncated || error.Truncated,
            };
        }

        /// <summary>
        /// sfc.exe écrit sa sortie en UTF-16LE sur une console redirigée. Lue en UTF-8 ou en page
        /// de code OEM, elle est illisible : c'est le piège d'encodage à connaître.
        /// </summary>
        private static Encoding? ResolveEncoding(ConsoleOutputEncoding requested) => requested switch
        {
            ConsoleOutputEncoding.Utf8 => new UTF8Encoding(false),
            ConsoleOutputEncoding.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
            ConsoleOutputEncoding.OemCodePage => Encoding.GetEncoding(GetOemCodePage()),
            _ => null,
        };

        private static int GetOemCodePage()
        {
            try { return Console.OutputEncoding.CodePage; }
            catch (Exception ex) when (ex is System.IO.IOException || ex is PlatformNotSupportedException)
            {
                return 850;
            }
        }

        private void Kill(Process process)
        {
            try
            {
                if (process.HasExited) return;
                process.Kill();
                process.WaitForExit((int)KillGracePeriod.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                _log.Warn("Le processus n'a pas pu être arrêté.", ex);
            }
        }

        /// <summary>Accumulateur plafonné : au-delà de la limite, on cesse d'écrire et on le signale.</summary>
        private sealed class BoundedBuffer
        {
            private readonly StringBuilder _builder = new StringBuilder();
            private readonly int _maxChars;

            public BoundedBuffer(int maxChars) => _maxChars = maxChars > 0 ? maxChars : 0;

            public bool Truncated { get; private set; }

            public void AppendLine(string line)
            {
                lock (_builder)
                {
                    if (_builder.Length + line.Length + 2 > _maxChars)
                    {
                        Truncated = true;
                        return;
                    }
                    _builder.AppendLine(line);
                }
            }

            public override string ToString()
            {
                lock (_builder) return _builder.ToString();
            }
        }
    }
}
