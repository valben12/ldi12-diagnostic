using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Actions.Maintenance
{
    /// <summary>
    /// Relève ce qu'un ensemble de fournisseurs supprimerait, sans rien supprimer.
    /// </summary>
    /// <remarks>
    /// Le relevé est la promesse : l'exécution ne supprime que les fichiers listés ici, et
    /// vérifie pour chacun qu'il n'a pas changé entre-temps. Un fichier apparu après le relevé
    /// n'est pas supprimé, même s'il se trouve dans un dossier balayé : sinon la liste montrée
    /// au technicien ne serait qu'une estimation.
    /// </remarks>
    public sealed class CleanupScanner
    {
        private const string Category = "Cleanup";

        private readonly IFileSystemGateway _files;
        private readonly ILdiLogger _logger;

        public CleanupScanner(IFileSystemGateway files, ILdiLogger logger)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public CleanupPlan Scan(
            IReadOnlyList<CleanupProvider> providers, bool elevated, CancellationToken cancellationToken)
        {
            var groups = new List<CleanupGroup>(providers.Count);
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                groups.Add(provider.Kind == CleanupGroupKind.RecycleBin
                    ? ScanRecycleBin(provider)
                    : ScanFiles(provider, elevated, cancellationToken));
            }

            return new CleanupPlan { Groups = groups };
        }

        private CleanupGroup ScanRecycleBin(CleanupProvider provider)
        {
            var state = _files.ReadRecycleBin();

            if (!state.HasValue)
                return Unavailable(provider, state.Reason ?? "Corbeille illisible.");

            var group = Base(provider, Array.Empty<string>());
            group.ItemCount = (int)Math.Min(int.MaxValue, state.Value.ItemCount);
            group.Bytes = state.Value.SizeBytes;
            return group;
        }

        private CleanupGroup ScanFiles(CleanupProvider provider, bool elevated, CancellationToken cancellationToken)
        {
            if (provider.RequiresElevation && !elevated)
                return Unavailable(provider, "Ce dossier n'est lisible qu'avec des privilèges administrateur.");

            var roots = new List<string>();
            foreach (var pattern in provider.Roots)
                foreach (var root in Expand(pattern))
                {
                    if (!CleanupGuard.IsAcceptableRoot(root, out var refusal))
                    {
                        // Une racine refusée est un défaut du catalogue, pas de la machine :
                        // elle doit se voir dans le journal, pas disparaître en silence.
                        _logger.Warn(Category, "Racine écartée pour « " + provider.Id + " » : " + refusal);
                        continue;
                    }

                    if (_files.DirectoryExists(root)) roots.Add(root);
                }

            if (roots.Count == 0)
                return Unavailable(provider, "Aucun dossier correspondant sur cette machine.");

            var older = provider.MinimumAge.HasValue
                ? DateTime.UtcNow - provider.MinimumAge.Value
                : (DateTime?)null;

            var files = new List<FileEntry>();
            long bytes = 0;
            var inaccessible = 0;
            var reparse = 0;
            var truncated = false;
            var unreadable = 0;

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scan = _files.Scan(
                    new DirectoryScanRequest(root) { OlderThanUtc = older },
                    cancellationToken);

                if (!scan.HasValue)
                {
                    unreadable++;
                    _logger.Debug(Category, "Dossier non balayé : " + root + ", " + scan.Reason);
                    continue;
                }

                files.AddRange(scan.Value.Files);
                bytes += scan.Value.TotalBytes;
                inaccessible += scan.Value.InaccessibleDirectories;
                reparse += scan.Value.SkippedReparsePoints;
                truncated |= scan.Value.Truncated;
            }

            if (unreadable == roots.Count)
                return Unavailable(provider, "Aucun des dossiers concernés n'a pu être lu.");

            var group = Base(provider, roots);
            group.Files = files;
            group.ItemCount = files.Count;
            group.Bytes = bytes;
            group.InaccessibleDirectories = inaccessible;
            group.SkippedReparsePoints = reparse;
            group.Truncated = truncated;
            return group;
        }

        /// <summary>
        /// Développe un unique segment « * », les profils de navigateur, dont le nom varie.
        /// </summary>
        private IEnumerable<string> Expand(string pattern)
        {
            var star = pattern.IndexOf('*');
            if (star < 0)
            {
                yield return pattern;
                yield break;
            }

            var separator = pattern.LastIndexOf(Path.DirectorySeparatorChar, star);
            var next = pattern.IndexOf(Path.DirectorySeparatorChar, star);
            if (separator < 0) yield break;

            var parent = pattern.Substring(0, separator);
            var suffix = next < 0 ? string.Empty : pattern.Substring(next + 1);

            foreach (var directory in _files.EnumerateDirectories(parent))
                yield return suffix.Length == 0 ? directory : Path.Combine(directory, suffix);
        }

        private static CleanupGroup Base(CleanupProvider provider, IReadOnlyList<string> roots)
            => new CleanupGroup
            {
                ProviderId = provider.Id,
                Title = provider.Title,
                Explanation = provider.Explanation,
                Consequence = provider.Consequence,
                Kind = provider.Kind,
                ContainsUserData = provider.ContainsUserData,
                Roots = roots,
            };

        private static CleanupGroup Unavailable(CleanupProvider provider, string reason)
        {
            var group = Base(provider, Array.Empty<string>());
            group.Unavailable = reason;
            return group;
        }
    }
}
