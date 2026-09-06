using System;
using System.Collections.Generic;
using LDI12.Core.Execution;

namespace LDI12.Actions.Maintenance
{
    /// <summary>
    /// Ce qu'un fournisseur a réellement trouvé sur cette machine.
    /// </summary>
    /// <remarks>
    /// Propriétés en lecture / écriture, contrairement au reste du modèle : ce plan est construit
    /// par étapes pendant le balayage, et il traverse le canal élevé en JSON.
    /// </remarks>
    public sealed class CleanupGroup
    {
        public string ProviderId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public string? Consequence { get; set; }
        public CleanupGroupKind Kind { get; set; }
        public bool ContainsUserData { get; set; }

        /// <summary>Racines effectivement balayées, jokers développés.</summary>
        public IReadOnlyList<string> Roots { get; set; } = Array.Empty<string>();

        /// <summary>Les fichiers qui seront supprimés. Vide pour la corbeille, qui ne se liste pas.</summary>
        public IReadOnlyList<FileEntry> Files { get; set; } = Array.Empty<FileEntry>();

        public int ItemCount { get; set; }
        public long Bytes { get; set; }

        /// <summary>Dossiers non lus faute de droits. Comptés, jamais tus.</summary>
        public int InaccessibleDirectories { get; set; }

        public int SkippedReparsePoints { get; set; }

        /// <summary>Le relevé a atteint son plafond : il y a davantage de fichiers que listés.</summary>
        public bool Truncated { get; set; }

        /// <summary>Renseigné quand le fournisseur n'a pas pu être relevé du tout.</summary>
        public string? Unavailable { get; set; }

        public bool HasContent => Unavailable == null && ItemCount > 0;

        /// <summary>Copie allégée pour l'affichage et pour la traversée du canal élevé.</summary>
        public CleanupGroup Trim(int maxFiles)
        {
            if (Files.Count <= maxFiles) return this;

            var kept = new List<FileEntry>(maxFiles);
            for (var i = 0; i < maxFiles; i++) kept.Add(Files[i]);

            return new CleanupGroup
            {
                ProviderId = ProviderId,
                Title = Title,
                Explanation = Explanation,
                Consequence = Consequence,
                Kind = Kind,
                ContainsUserData = ContainsUserData,
                Roots = Roots,
                Files = kept,
                ItemCount = ItemCount,
                Bytes = Bytes,
                InaccessibleDirectories = InaccessibleDirectories,
                SkippedReparsePoints = SkippedReparsePoints,
                Truncated = true,
                Unavailable = Unavailable,
            };
        }
    }

    public sealed class CleanupPlan
    {
        public IReadOnlyList<CleanupGroup> Groups { get; init; } = Array.Empty<CleanupGroup>();

        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (var group in Groups) total += group.Bytes;
                return total;
            }
        }

        public int TotalItems
        {
            get
            {
                var total = 0;
                foreach (var group in Groups) total += group.ItemCount;
                return total;
            }
        }

        public bool ContainsUserData
        {
            get
            {
                foreach (var group in Groups)
                    if (group.ContainsUserData && group.HasContent) return true;
                return false;
            }
        }

        public CleanupPlan Trim(int maxFilesPerGroup)
        {
            var groups = new List<CleanupGroup>(Groups.Count);
            foreach (var group in Groups) groups.Add(group.Trim(maxFilesPerGroup));
            return new CleanupPlan { Groups = groups };
        }
    }
}
