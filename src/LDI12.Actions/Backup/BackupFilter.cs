using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// Ce que le technicien écarte des dossiers personnels : des types de fichiers, ou ce qui
    /// dépasse une taille.
    /// </summary>
    /// <remarks>
    /// Une image disque oubliée dans Téléchargements, une vidéo de 40 Go : ce sont elles qui font
    /// déborder le support ou durer la copie une heure de plus. Les filtres ne s'appliquent qu'aux
    /// dossiers personnels et aux dossiers ajoutés. Les données d'applications restent entières :
    /// un profil de navigateur amputé d'un fichier serait inutilisable.
    /// </remarks>
    public sealed class BackupFilter
    {
        private readonly HashSet<string> _extensions;

        private BackupFilter(HashSet<string> extensions, long? maxBytes)
        {
            _extensions = extensions;
            MaxBytes = maxBytes;
        }

        public long? MaxBytes { get; }

        public IReadOnlyCollection<string> Extensions => _extensions;

        public bool IsEmpty => _extensions.Count == 0 && MaxBytes == null;

        public int ExcludedFiles { get; private set; }

        public long ExcludedBytes { get; private set; }

        /// <summary>
        /// « iso, .vhdx; mkv » et « 4 » (Go). Une extension illisible est ignorée, une taille
        /// illisible ou nulle aussi.
        /// </summary>
        public static BackupFilter Parse(string? extensions, string? maxGigabytes)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (extensions ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var extension = raw.Trim().TrimStart('*').TrimStart('.');
                if (extension.Length == 0 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                set.Add("." + extension.ToLowerInvariant());
            }

            long? max = null;
            if (double.TryParse((maxGigabytes ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var gigabytes) &&
                gigabytes > 0)
                max = (long)(gigabytes * 1024 * 1024 * 1024);

            return new BackupFilter(set, max);
        }

        /// <summary>Vrai si ce fichier est écarté ; il est alors compté.</summary>
        public bool Excludes(FileEntry file)
        {
            var excluded = (MaxBytes != null && file.SizeBytes > MaxBytes.Value) ||
                           (_extensions.Count > 0 && _extensions.Contains(Path.GetExtension(file.Path)));
            if (!excluded) return false;

            ExcludedFiles++;
            ExcludedBytes += file.SizeBytes;
            return true;
        }

        /// <summary>« les fichiers .iso et .vhdx, et ceux de plus de 4 Go ».</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (_extensions.Count > 0)
            {
                var names = new List<string>(_extensions);
                names.Sort(StringComparer.Ordinal);
                parts.Add("les fichiers " + string.Join(", ", names));
            }

            if (MaxBytes != null) parts.Add("ceux de plus de " + ValueFormat.Bytes(MaxBytes.Value));
            return string.Join(", et ", parts);
        }

        /// <summary>
        /// Les plus gros sous-dossiers de ce qui part, au premier niveau de chaque dossier : ce
        /// qu'on regarde pour décider quoi écarter.
        /// </summary>
        public static IReadOnlyList<(string Name, long Bytes)> Largest(IEnumerable<BackupFolder> folders, int count)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (folder.Application != null) continue;
                var root = folder.Path.TrimEnd('\\') + "\\";
                foreach (var file in folder.Files)
                {
                    if (!file.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = file.Path.Substring(root.Length);
                    var slash = relative.IndexOf('\\');
                    var key = folder.Label + (slash > 0 ? "\\" + relative.Substring(0, slash) : " (fichiers à la racine)");
                    sizes[key] = (sizes.TryGetValue(key, out var size) ? size : 0) + file.SizeBytes;
                }
            }

            var list = new List<(string, long)>();
            foreach (var pair in sizes) list.Add((pair.Key, pair.Value));
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list.Count > count ? list.GetRange(0, count) : list;
        }
    }
}
