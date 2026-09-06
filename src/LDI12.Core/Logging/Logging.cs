using System;

namespace LDI12.Core.Logging
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
    }

    /// <summary>
    /// Journal interne. Volontairement minimal : sur un logiciel qui doit démarrer vite
    /// sur un Core 2 Duo, une dépendance de journalisation complète ne se justifie pas.
    /// L'interface permet de la substituer plus tard sans toucher aux modules.
    /// </summary>
    public interface ILdiLogger
    {
        bool IsEnabled(LogLevel level);

        void Log(LogLevel level, string category, string message, Exception? exception = null);
    }

    public static class LoggerExtensions
    {
        public static void Trace(this ILdiLogger log, string category, string message)
            => log.Log(LogLevel.Trace, category, message);

        public static void Debug(this ILdiLogger log, string category, string message)
            => log.Log(LogLevel.Debug, category, message);

        public static void Info(this ILdiLogger log, string category, string message)
            => log.Log(LogLevel.Info, category, message);

        public static void Warn(this ILdiLogger log, string category, string message, Exception? ex = null)
            => log.Log(LogLevel.Warning, category, message, ex);

        public static void Error(this ILdiLogger log, string category, string message, Exception? ex = null)
            => log.Log(LogLevel.Error, category, message, ex);

        /// <summary>Journal pré-catégorisé, pour éviter de répéter la catégorie à chaque appel.</summary>
        public static IScopedLogger For(this ILdiLogger log, string category) => new ScopedLogger(log, category);
    }

    public interface IScopedLogger
    {
        void Trace(string message);
        void Debug(string message);
        void Info(string message);
        void Warn(string message, Exception? ex = null);
        void Error(string message, Exception? ex = null);
    }

    internal sealed class ScopedLogger : IScopedLogger
    {
        private readonly ILdiLogger _log;
        private readonly string _category;

        public ScopedLogger(ILdiLogger log, string category)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _category = category;
        }

        public void Trace(string message) => _log.Log(LogLevel.Trace, _category, message);
        public void Debug(string message) => _log.Log(LogLevel.Debug, _category, message);
        public void Info(string message) => _log.Log(LogLevel.Info, _category, message);
        public void Warn(string message, Exception? ex = null) => _log.Log(LogLevel.Warning, _category, message, ex);
        public void Error(string message, Exception? ex = null) => _log.Log(LogLevel.Error, _category, message, ex);
    }

    /// <summary>Journal qui n'écrit rien. Utile en test et comme valeur par défaut sûre.</summary>
    public sealed class NullLogger : ILdiLogger
    {
        public static readonly NullLogger Instance = new NullLogger();

        private NullLogger() { }

        public bool IsEnabled(LogLevel level) => false;

        public void Log(LogLevel level, string category, string message, Exception? exception = null) { }
    }
}
