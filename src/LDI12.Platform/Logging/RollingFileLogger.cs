using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Logging;

namespace LDI12.Platform.Logging
{
    /// <summary>
    /// Journal fichier rotatif, sans dépendance externe.
    /// </summary>
    /// <remarks>
    /// C'est le journal que le technicien ouvre quand un module a échoué chez un client et que la
    /// machine n'est plus sous la main. Il doit donc être écrit même si tout le reste échoue :
    /// une erreur d'écriture n'est jamais propagée, elle dégrade simplement la journalisation.
    /// </remarks>
    public sealed class RollingFileLogger : ILdiLogger, IDisposable
    {
        private const long MaxFileBytes = 4L * 1024 * 1024;
        private const int MaxArchivedFiles = 5;

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly LogLevel _minimumLevel;
        private StreamWriter? _writer;
        private bool _writeFailed;

        public RollingFileLogger(string path, LogLevel minimumLevel = LogLevel.Debug)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _minimumLevel = minimumLevel;
        }

        /// <summary>%LOCALAPPDATA%\LDI12\Diagnostic\logs\ldi12-aaaaMMjj.log</summary>
        public static string DefaultPath()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LDI12", "Diagnostic", "logs");
            var name = "ldi12-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
            return Path.Combine(directory, name);
        }

        public static RollingFileLogger CreateDefault(LogLevel minimumLevel = LogLevel.Debug)
            => new RollingFileLogger(DefaultPath(), minimumLevel);

        /// <summary>Chemin du fichier courant, affiché dans l'interface pour ouvrir le journal.</summary>
        public string FilePath => _path;

        public bool IsEnabled(LogLevel level) => !_writeFailed && level >= _minimumLevel;

        public void Log(LogLevel level, string category, string message, Exception? exception = null)
        {
            if (!IsEnabled(level)) return;

            var line = new StringBuilder(160)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Label(level))
                .Append(" [").Append(category).Append("] ")
                .Append(message);

            if (exception != null)
            {
                line.AppendLine()
                    .Append("    ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
                if (exception.StackTrace != null)
                    line.AppendLine().Append("    ").Append(exception.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "    "));
            }

            lock (_gate)
            {
                try
                {
                    EnsureWriter();
                    _writer!.WriteLine(line.ToString());
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    // Journaliser ne doit jamais faire échouer un diagnostic. On abandonne
                    // silencieusement l'écriture pour le reste de la session.
                    _writeFailed = true;
                    SafeDisposeWriter();
                }
            }
        }

        private void EnsureWriter()
        {
            if (_writer != null)
            {
                if (_writer.BaseStream.Length < MaxFileBytes) return;
                SafeDisposeWriter();
                Roll();
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        private void Roll()
        {
            try
            {
                var oldest = _path + "." + MaxArchivedFiles;
                if (File.Exists(oldest)) File.Delete(oldest);

                for (var i = MaxArchivedFiles - 1; i >= 1; i--)
                {
                    var source = _path + "." + i;
                    if (File.Exists(source)) File.Move(source, _path + "." + (i + 1));
                }

                if (File.Exists(_path)) File.Move(_path, _path + ".1");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Rotation impossible : on continue d'écrire dans le fichier courant.
            }
        }

        private void SafeDisposeWriter()
        {
            try { _writer?.Dispose(); }
            catch (IOException) { }
            _writer = null;
        }

        private static string Label(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "???",
        };

        public void Dispose()
        {
            lock (_gate) SafeDisposeWriter();
        }
    }

    /// <summary>Journal console, utilisé par LDI12.ProbeHost en mode headless.</summary>
    public sealed class ConsoleLogger : ILdiLogger
    {
        private readonly LogLevel _minimumLevel;
        private readonly object _gate = new object();

        public ConsoleLogger(LogLevel minimumLevel = LogLevel.Info) => _minimumLevel = minimumLevel;

        public bool IsEnabled(LogLevel level) => level >= _minimumLevel;

        public void Log(LogLevel level, string category, string message, Exception? exception = null)
        {
            if (!IsEnabled(level)) return;
            lock (_gate)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warning => ConsoleColor.Yellow,
                    LogLevel.Info => previous,
                    _ => ConsoleColor.DarkGray,
                };
                Console.Error.WriteLine("[" + category + "] " + message);
                if (exception != null) Console.Error.WriteLine("    " + exception.GetType().Name + ": " + exception.Message);
                Console.ForegroundColor = previous;
            }
        }
    }

    /// <summary>Diffuse vers plusieurs journaux. Un journal défaillant n'empêche pas les autres.</summary>
    public sealed class CompositeLogger : ILdiLogger, IDisposable
    {
        private readonly IReadOnlyList<ILdiLogger> _loggers;

        public CompositeLogger(params ILdiLogger[] loggers)
            => _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));

        public bool IsEnabled(LogLevel level)
        {
            for (var i = 0; i < _loggers.Count; i++)
                if (_loggers[i].IsEnabled(level)) return true;
            return false;
        }

        public void Log(LogLevel level, string category, string message, Exception? exception = null)
        {
            for (var i = 0; i < _loggers.Count; i++)
            {
                try { _loggers[i].Log(level, category, message, exception); }
                catch (Exception) { /* un journal défaillant ne doit rien interrompre */ }
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < _loggers.Count; i++)
                (_loggers[i] as IDisposable)?.Dispose();
        }
    }
}
