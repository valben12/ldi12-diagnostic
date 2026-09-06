using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Actions.Maintenance
{
    public enum CleanupGroupKind
    {
        /// <summary>Fichiers relevés un par un, montrés avant suppression.</summary>
        Files = 0,

        /// <summary>
        /// Corbeille : Windows n'en donne que le nombre d'éléments et le volume.
        /// </summary>
        /// <remarks>
        /// Impossible d'en lister les noms sans passer par le shell. On montre donc ce qu'on
        /// sait (combien, et quelle place) et on dit qu'on ne sait pas le reste, plutôt que
        /// de présenter une liste vide comme si la corbeille l'était.
        /// </remarks>
        RecycleBin = 1,
    }

    /// <summary>
    /// Une source de fichiers récupérables, avec ce qu'il faut dire au client à son sujet.
    /// </summary>
    public sealed class CleanupProvider
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        /// <summary>Ce que c'est, en français ordinaire. Affiché tel quel dans l'écran.</summary>
        public string Explanation { get; init; } = string.Empty;

        /// <summary>Ce que la suppression coûte réellement à l'utilisateur, quand elle coûte quelque chose.</summary>
        public string? Consequence { get; init; }

        public CleanupGroupKind Kind { get; init; }

        /// <summary>Dossiers à balayer. Un segment peut valoir « * » : il est développé au balayage.</summary>
        public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

        public bool RequiresElevation { get; init; }

        /// <summary>
        /// Contient des éléments produits par l'utilisateur.
        /// </summary>
        /// <remarks>
        /// Ces sources ne sont jamais cochées d'avance. La consigne est explicite : ne jamais
        /// supprimer automatiquement des données personnelles. Une case déjà cochée dans un écran
        /// qu'on valide vite est une suppression automatique déguisée.
        /// </remarks>
        public bool ContainsUserData { get; init; }

        public bool SelectedByDefault => !ContainsUserData;

        /// <summary>
        /// Âge minimal des fichiers retenus.
        /// </summary>
        /// <remarks>
        /// Un fichier temporaire écrit il y a dix minutes appartient probablement à un logiciel
        /// encore ouvert. Le laisser coûte quelques mégaoctets ; le supprimer peut faire perdre
        /// un document en cours.
        /// </remarks>
        public TimeSpan? MinimumAge { get; init; }
    }

    /// <summary>
    /// Les sources de nettoyage, résolues sur cette machine.
    /// </summary>
    /// <remarks>
    /// Aucun nettoyage de dossier système « profond » (WinSxS, points de restauration, anciennes
    /// installations de Windows) : ce sont des opérations que Windows sait faire lui-même par
    /// l'outil de nettoyage de disque, et dont les effets de bord dépassent ce qu'un écran peut
    /// honnêtement prévisualiser.
    /// </remarks>
    public static class CleanupCatalog
    {
        public static IReadOnlyList<CleanupProvider> Create() => Create(DefaultResolver);

        public static IReadOnlyList<CleanupProvider> Create(Func<string, string?> resolve)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));

            var windows = resolve("Windows");
            var localAppData = resolve("LocalApplicationData");
            var programData = resolve("CommonApplicationData");
            var temp = resolve("Temp");

            var providers = new List<CleanupProvider>();

            Add(providers, new CleanupProvider
            {
                Id = "TEMP-USER",
                Title = "Fichiers temporaires de la session",
                Explanation = "Les fichiers de travail que les logiciels créent puis oublient de supprimer.",
                Kind = CleanupGroupKind.Files,
                Roots = One(temp),
                MinimumAge = TimeSpan.FromHours(24),
            });

            Add(providers, new CleanupProvider
            {
                Id = "TEMP-SYSTEM",
                Title = "Fichiers temporaires de Windows",
                Explanation = "Les mêmes, côté système : restes d'installations et de mises à jour.",
                Kind = CleanupGroupKind.Files,
                Roots = One(Combine(windows, "Temp")),
                RequiresElevation = true,
                MinimumAge = TimeSpan.FromHours(24),
            });

            Add(providers, new CleanupProvider
            {
                Id = "UPDATE-CACHE",
                Title = "Cache des mises à jour Windows",
                Explanation = "Les paquets de mise à jour déjà installés, conservés au cas où.",
                Consequence = "Windows les retéléchargera s'il en a de nouveau besoin.",
                Kind = CleanupGroupKind.Files,
                Roots = One(Combine(windows, "SoftwareDistribution", "Download")),
                RequiresElevation = true,
            });

            Add(providers, new CleanupProvider
            {
                Id = "WER-REPORTS",
                Title = "Rapports d'erreurs Windows",
                Explanation = "Les comptes rendus que Windows garde après le plantage d'un logiciel.",
                Consequence = "Ils ne servent qu'à un diagnostic ultérieur du même plantage.",
                Kind = CleanupGroupKind.Files,
                Roots = Many(
                    Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive")),
                RequiresElevation = true,
            });

            Add(providers, new CleanupProvider
            {
                Id = "THUMBNAILS",
                Title = "Cache des miniatures",
                Explanation = "Les aperçus d'images et de vidéos que l'explorateur garde en réserve.",
                Consequence = "Les dossiers d'images mettront un instant de plus à s'afficher, une fois.",
                Kind = CleanupGroupKind.Files,
                Roots = One(Combine(localAppData, "Microsoft", "Windows", "Explorer")),
            });

            Add(providers, new CleanupProvider
            {
                Id = "BROWSER-CACHE",
                Title = "Caches des navigateurs",
                Explanation = "Les copies de pages et d'images que Chrome, Edge et Firefox gardent " +
                              "pour recharger plus vite.",
                Consequence = "Les premiers chargements seront un peu plus lents. Ni les favoris, " +
                              "ni l'historique, ni les mots de passe ne sont concernés.",
                Kind = CleanupGroupKind.Files,
                Roots = Many(
                    Combine(localAppData, "Google", "Chrome", "User Data", "*", "Cache"),
                    Combine(localAppData, "Google", "Chrome", "User Data", "*", "Code Cache"),
                    Combine(localAppData, "Google", "Chrome", "User Data", "*", "GPUCache"),
                    Combine(localAppData, "Microsoft", "Edge", "User Data", "*", "Cache"),
                    Combine(localAppData, "Microsoft", "Edge", "User Data", "*", "Code Cache"),
                    Combine(localAppData, "Microsoft", "Edge", "User Data", "*", "GPUCache"),
                    Combine(localAppData, "Mozilla", "Firefox", "Profiles", "*", "cache2")),
            });

            Add(providers, new CleanupProvider
            {
                Id = "RECYCLE-BIN",
                Title = "Corbeille",
                Explanation = "Les fichiers que l'utilisateur a supprimés et qui sont encore récupérables.",
                // La consigne « à ne cocher qu'après avoir demandé » est portée une seule fois,
                // par la mention de données personnelles : la répéter ici la banaliserait.
                Consequence = "Après vidage, ces fichiers ne sont plus récupérables.",
                Kind = CleanupGroupKind.RecycleBin,
                ContainsUserData = true,
            });

            return providers;
        }

        /// <summary>Un fournisseur dont aucune racine n'a pu être résolue n'entre pas au catalogue.</summary>
        private static void Add(List<CleanupProvider> providers, CleanupProvider provider)
        {
            if (provider.Kind == CleanupGroupKind.Files && provider.Roots.Count == 0) return;
            providers.Add(provider);
        }

        private static IReadOnlyList<string> One(string? root)
            => root == null ? Array.Empty<string>() : new[] { root };

        private static IReadOnlyList<string> Many(params string?[] roots)
        {
            var kept = new List<string>();
            foreach (var root in roots)
                if (root != null) kept.Add(root);
            return kept;
        }

        private static string? Combine(string? head, params string[] parts)
        {
            if (string.IsNullOrEmpty(head)) return null;

            var path = head!;
            foreach (var part in parts) path = Path.Combine(path, part);
            return path;
        }

        private static string? DefaultResolver(string key) => key switch
        {
            "Temp" => SafePath(Path.GetTempPath()),
            "Windows" => SafeFolder(Environment.SpecialFolder.Windows),
            "LocalApplicationData" => SafeFolder(Environment.SpecialFolder.LocalApplicationData),
            "CommonApplicationData" => SafeFolder(Environment.SpecialFolder.CommonApplicationData),
            _ => null,
        };

        private static string? SafeFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return SafePath(Environment.GetFolderPath(folder));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string? SafePath(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? null
                : path!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
