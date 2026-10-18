using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LDI12.Actions.Backup;
using LDI12.Core.Drivers;
using Microsoft.Win32;

namespace LDI12.App.Services
{
    /// <summary>
    /// Le magasin de pilotes tel que Driver Store Explorer le montre : chaque paquet ajouté
    /// (oemNN.inf), son dossier et sa taille, et les périphériques qui s'en servent.
    /// </summary>
    /// <remarks>
    /// Tout se lit sans privilèges : les copies des INF dans Windows\INF, l'index du magasin dans
    /// la ruche DRIVERS (Windows 8 et plus), et le pilote de chaque périphérique dans la clé Class.
    /// Sous Windows 7, sans index, le dossier se retrouve en comparant l'INF à ceux du magasin.
    /// </remarks>
    public static class DriverStoreReader
    {
        public static IReadOnlyList<DriverPackage> Read()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? Path.Combine(windows, "Sysnative") : Path.Combine(windows, "System32");
            var repository = Path.Combine(system, @"DriverStore\FileRepository");
            var used = UsedBy();

            string[] infs;
            try { infs = Directory.GetFiles(Path.Combine(windows, "INF"), "oem*.inf"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<DriverPackage>(); }

            Dictionary<string, string>? legacy = null;
            var packages = new List<DriverPackage>();
            foreach (var inf in infs)
            {
                var published = Path.GetFileName(inf).ToLowerInvariant();
                if (!DriverBackup.IsThirdParty(published)) continue;
                var summary = OfflineDrivers.Parse(ReadText(inf));

                var folderName = ActivePackage(published);
                if (folderName == null)
                {
                    legacy ??= Index(repository);
                    legacy.TryGetValue(Key(summary), out folderName);
                }
                var folder = folderName == null ? null : Path.Combine(repository, folderName);
                if (folder != null && !Directory.Exists(folder)) folder = null;

                packages.Add(new DriverPackage
                {
                    PublishedName = published,
                    OriginalName = folderName == null ? null : OriginalName(folderName),
                    Folder = folder,
                    Provider = summary.Provider,
                    DeviceClass = summary.DeviceClass,
                    Version = summary.Version,
                    Date = summary.Date,
                    Device = summary.Device,
                    SizeBytes = folder == null ? 0 : Size(folder),
                    UsedBy = used.TryGetValue(published, out var devices) ? devices : (IReadOnlyList<string>)Array.Empty<string>(),
                });
            }

            DriverStoreAnalyzer.Classify(packages);
            return packages;
        }

        /// <summary>« nvlddmkm.inf_amd64_6b3e… » donne « nvlddmkm.inf ».</summary>
        internal static string OriginalName(string folder)
        {
            var at = folder.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? folder.Substring(0, at + 4).ToLowerInvariant() : folder.ToLowerInvariant();
        }

        /// <summary>Le paquet en service pour ce nom publié, d'après l'index du magasin.</summary>
        private static string? ActivePackage(string published)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"DRIVERS\DriverDatabase\DriverInfFiles\" + published);
                if (key == null) return null;
                if (key.GetValue("Active") is string active && active.Length > 0) return active;
                return key.GetValue(string.Empty) is string[] listed && listed.Length > 0 ? listed[listed.Length - 1] : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return null;
            }
        }

        /// <summary>Le pilote de chaque périphérique, noté dans sa clé Class : « oem12.inf » → les périphériques.</summary>
        private static Dictionary<string, IReadOnlyList<string>> UsedBy()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
                if (classes == null) return new Dictionary<string, IReadOnlyList<string>>();
                foreach (var guid in classes.GetSubKeyNames())
                {
                    try
                    {
                        using var @class = classes.OpenSubKey(guid);
                        if (@class == null) continue;
                        foreach (var instance in @class.GetSubKeyNames())
                        {
                            if (instance.Length != 4 || !instance.All(char.IsDigit)) continue;
                            try
                            {
                                using var device = @class.OpenSubKey(instance);
                                if (!(device?.GetValue("InfPath") is string inf) || !inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) continue;
                                var name = device.GetValue("DriverDesc") as string ?? inf;
                                if (!map.TryGetValue(inf, out var list)) map[inf] = list = new List<string>();
                                if (!list.Contains(name)) list.Add(name);
                            }
                            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { }
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return map.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Windows 7 : chaque dossier du magasin, reconnu à ce que son INF déclare.</summary>
        private static Dictionary<string, string> Index(string repository)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var folder in Directory.GetDirectories(repository))
                {
                    var name = Path.GetFileName(folder);
                    var inf = Path.Combine(folder, OriginalName(name));
                    if (!File.Exists(inf)) continue;
                    var key = Key(OfflineDrivers.Parse(ReadText(inf)));
                    if (!index.ContainsKey(key)) index[key] = name;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return index;
        }

        private static string Key(InfSummary summary)
            => (summary.Provider + "|" + summary.DeviceClass + "|" + summary.Version + "|" + summary.Date?.ToString("yyyyMMdd") + "|" + summary.CatalogFile).ToLowerInvariant();

        private static string ReadText(string path)
        {
            try { return File.ReadAllText(path, System.Text.Encoding.Default); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return string.Empty; }
        }

        private static long Size(string folder)
        {
            try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }
    }
}
