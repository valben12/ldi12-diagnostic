using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using LDI12.Core.Crashes;
using Microsoft.Win32;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Ce que les journaux de Windows disent des plantages : écrans bleus notés au redémarrage,
    /// arrêts brutaux, programmes qui plantent ou se figent. Lisible sans privilèges.
    /// </summary>
    public static class CrashEventsReader
    {
        private const int MaxEvents = 600;

        public static (IReadOnlyList<BugCheckEvent> BugChecks, int Shutdowns, IReadOnlyList<AppCrash> Apps) Read(int days)
        {
            var window = ((long)days * 86_400_000L).ToString(CultureInfo.InvariantCulture);
            return (BugChecks(window), Shutdowns(window), Apps(window));
        }

        private static List<BugCheckEvent> BugChecks(string window)
        {
            var found = new List<BugCheckEvent>();
            Each("System", "*[System[Provider[@Name='BugCheck'] and (EventID=1001) and TimeCreated[timediff(@SystemTime) <= " + window + "]]]", record =>
            {
                // Les propriétés ne sont pas traduites : « 0x0000000a (0x…, …) », puis le chemin du vidage.
                var first = Property(record, 0);
                found.Add(new BugCheckEvent
                {
                    TimeUtc = (record.TimeCreated ?? DateTime.Now).ToUniversalTime(),
                    BugCheckCode = Code(first),
                    DumpPath = Property(record, 1) is { Length: > 0 } path && path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ? path : null,
                });
            });
            return found;
        }

        /// <summary>Les arrêts brutaux sans écran bleu : Kernel-Power 41 dont le code d'arrêt est nul.</summary>
        private static int Shutdowns(string window)
        {
            var count = 0;
            Each("System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= " + window + "]]]", record =>
            {
                var code = record.Properties.Count > 0 ? record.Properties[0].Value : null;
                if (code == null || Convert.ToUInt64(code, CultureInfo.InvariantCulture) == 0) count++;
            });
            return count;
        }

        private static List<AppCrash> Apps(string window)
        {
            var groups = new Dictionary<string, AppCrash>(StringComparer.OrdinalIgnoreCase);
            Each("Application", "*[System[(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)][TimeCreated[timediff(@SystemTime) <= " + window + "]]]", record =>
            {
                var hang = record.Id == 1002;
                var application = Property(record, 0);
                if (string.IsNullOrWhiteSpace(application)) return;
                var module = hang ? null : Property(record, 3);
                var key = (hang ? "hang|" : "crash|") + application + "|" + module;
                var time = (record.TimeCreated ?? DateTime.Now).ToUniversalTime();

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new AppCrash
                    {
                        Application = application!,
                        Version = Property(record, 1),
                        Module = string.IsNullOrWhiteSpace(module) ? null : module,
                        ExceptionCode = hang ? null : Hex(Property(record, 6)),
                        IsHang = hang,
                        Path = hang ? null : Property(record, 10),
                        LastUtc = time,
                    };
                    groups[key] = group;
                }
                group.Count++;
                if (time > group.LastUtc) group.LastUtc = time;
            });
            return groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.LastUtc).ToList();
        }

        private static void Each(string log, string xpath, Action<EventRecord> handle)
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                for (var read = 0; read < MaxEvents; read++)
                {
                    var record = reader.ReadEvent();
                    if (record == null) break;
                    using (record)
                    {
                        try { handle(record); }
                        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is EventLogException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is InvalidOperationException)
            {
                // Journal absent ou refusé : la section reste vide, le reste de l'écran s'affiche.
            }
        }

        private static string? Property(EventRecord record, int index)
            => index < record.Properties.Count ? record.Properties[index].Value?.ToString() : null;

        internal static uint? Code(string? text)
        {
            if (text == null) return null;
            var start = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            var end = start + 2;
            while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
            var digits = text.Substring(start + 2, end - start - 2);
            if (digits.Length > 8) digits = digits.Substring(digits.Length - 8);
            return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? code : (uint?)null;
        }

        private static string? Hex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value!.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return value;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? "0x" + ((uint)number).ToString("X8", CultureInfo.InvariantCulture)
                : value;
        }

        /// <summary>Le réglage de Windows : ce qu'un écran bleu laisse, et où.</summary>
        public static DumpSettings Settings()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
                var mode = key?.GetValue("CrashDumpEnabled") is int value ? value : (int?)null;
                var folder = key?.GetValue("MinidumpDir") as string;
                var memory = key?.GetValue("DumpFile") as string;
                return new DumpSettings
                {
                    Mode = mode,
                    MinidumpFolder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(windows, "Minidump") : Environment.ExpandEnvironmentVariables(folder),
                    MemoryDump = string.IsNullOrWhiteSpace(memory) ? Path.Combine(windows, "MEMORY.DMP") : Environment.ExpandEnvironmentVariables(memory),
                };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return new DumpSettings { MinidumpFolder = Path.Combine(windows, "Minidump"), MemoryDump = Path.Combine(windows, "MEMORY.DMP") };
            }
        }
    }

    /// <summary>Retrouve un pilote sur le disque, et ce que son fichier dit de lui.</summary>
    public sealed class ModuleFileSource : IModuleFileSource
    {
        private readonly ConcurrentDictionary<string, ModuleFile?> _cache = new ConcurrentDictionary<string, ModuleFile?>(StringComparer.OrdinalIgnoreCase);

        public ModuleFile? Find(string moduleName)
            => string.IsNullOrWhiteSpace(moduleName) ? null : _cache.GetOrAdd(moduleName, Locate);

        private static ModuleFile? Locate(string name)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // Un processus 32 bits sur Windows 64 bits voit SysWOW64 à la place de System32 : Sysnative est le vrai.
            var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? Path.Combine(windows, "Sysnative")
                : Path.Combine(windows, "System32");

            foreach (var candidate in new[] { Path.Combine(system, "drivers", name), Path.Combine(system, name), Path.Combine(windows, name) })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var info = FileVersionInfo.GetVersionInfo(candidate);
                    return new ModuleFile
                    {
                        Path = candidate,
                        Company = Clean(info.CompanyName),
                        Description = Clean(info.FileDescription),
                        Product = Clean(info.ProductName),
                        Version = Clean(info.FileVersion),
                        FileDateUtc = File.GetLastWriteTimeUtc(candidate),
                    };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                }
            }
            return null;
        }

        private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }
}
