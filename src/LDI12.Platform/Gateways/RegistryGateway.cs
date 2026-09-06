using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using Microsoft.Win32;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Lecture du registre, sans exception : une clé absente, un refus d'accès ou une ruche
    /// corrompue renvoient <c>null</c>, jamais une exception qui remonterait dans un module.
    /// </summary>
    public sealed class RegistryGateway : IRegistryGateway
    {
        private const string Category = "Platform.Registry";

        private readonly IScopedLogger _log;

        public RegistryGateway(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
        {
            var value = ReadValue(hive, subKey, valueName, view);
            if (value == null) return null;
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }

        public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
        {
            var value = ReadValue(hive, subKey, valueName, view);
            if (value == null) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return null;
            }
        }

        public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
        {
            var value = ReadValue(hive, subKey, valueName, view);
            if (value == null) return null;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return null;
            }
        }

        public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
            => ReadValue(hive, subKey, valueName, view) as string[];

        public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
            => ReadValue(hive, subKey, valueName, view) as byte[];

        public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
            => WithKey(hive, subKey, view, key => key != null, false);

        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
            => WithKey(hive, subKey, view,
                key => key == null ? Array.Empty<string>() : (IReadOnlyList<string>)key.GetSubKeyNames(),
                Array.Empty<string>());

        public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
            => WithKey(hive, subKey, view,
                key => key == null ? Array.Empty<string>() : (IReadOnlyList<string>)key.GetValueNames(),
                Array.Empty<string>());

        private object? ReadValue(RegistryHive hive, string subKey, string valueName, RegistryView view)
            => WithKey(hive, subKey, view, key => key?.GetValue(valueName), null);

        private T WithKey<T>(RegistryHive hive, string subKey, RegistryView view, Func<RegistryKey?, T> read, T fallback)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey, writable: false);
                return read(key);
            }
            catch (Exception ex) when (
                ex is SecurityException ||
                ex is UnauthorizedAccessException ||
                ex is System.IO.IOException ||
                ex is ObjectDisposedException)
            {
                _log.Debug("Lecture impossible de " + hive + "\\" + subKey + " (" + ex.GetType().Name + ").");
                return fallback;
            }
        }
    }
}
