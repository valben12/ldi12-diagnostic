using System;
using System.Collections.Generic;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Le poids d'un type de fichier sur l'ensemble analysé.</summary>
    public sealed class ExtensionStat
    {
        internal ExtensionStat(string extension)
        {
            Extension = extension;
            Kind = FileKinds.Describe(extension);
        }

        /// <summary>« .mp4 », ou vide pour les fichiers sans extension.</summary>
        public string Extension { get; }

        /// <summary>« Vidéo », « Image », « Programme »… vide quand le type n'est pas connu.</summary>
        public string Kind { get; }

        public long Bytes { get; internal set; }

        public long Files { get; internal set; }

        /// <summary>Rang dans la palette, -1 au-delà des couleurs disponibles.</summary>
        public int Color { get; internal set; } = -1;

        public override string ToString() => Extension + " (" + Bytes + ")";
    }

    /// <summary>
    /// Le résultat d'une analyse : l'arbre pesé et trié, et la répartition par type de fichier.
    /// </summary>
    public sealed class DiskUsageTree
    {
        /// <summary>Le nombre de types qui reçoivent leur propre couleur, les suivants partagent un gris.</summary>
        public const int PaletteSize = 12;

        private readonly List<ExtensionStat> _extensions = new List<ExtensionStat>();

        internal DiskUsageTree(DiskNode root, long unreadable, TimeSpan elapsed)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            UnreadableDirectories = unreadable;
            Elapsed = elapsed;
            Aggregate();
        }

        public DiskNode Root { get; }

        public TimeSpan Elapsed { get; }

        public long UnreadableDirectories { get; }

        public long Directories { get; private set; }

        /// <summary>Types de fichiers, dans l'ordre de leur indice : voir <see cref="RankedExtensions"/> pour le classement.</summary>
        public IReadOnlyList<ExtensionStat> Extensions => _extensions;

        /// <summary>Taille du volume, quand c'est un volume entier qui est analysé.</summary>
        public long? VolumeSize { get; set; }

        public long? VolumeFree { get; set; }

        /// <summary>Occupé sur le volume mais hors de portée de l'analyse : fichiers système, dossiers refusés.</summary>
        public long? Unaccounted
            => VolumeSize.HasValue && VolumeFree.HasValue
                ? Math.Max(0, VolumeSize.Value - VolumeFree.Value - Root.Size)
                : (long?)null;

        public ExtensionStat? ExtensionOf(DiskNode node)
            => node.Extension >= 0 && node.Extension < _extensions.Count ? _extensions[node.Extension] : null;

        /// <summary>Les plus gros fichiers, du plus lourd au plus léger.</summary>
        public IReadOnlyList<DiskNode> LargestFiles(int count, DiskNode? under = null)
        {
            var heap = new SortedSet<DiskNode>(Comparer<DiskNode>.Create((a, b) =>
            {
                var bySize = a.Size.CompareTo(b.Size);
                return bySize != 0 ? bySize : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a)
                    .CompareTo(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b));
            }));

            var stack = new Stack<DiskNode>();
            stack.Push(under ?? Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var child in node.Children)
                {
                    if (child.IsDirectory)
                    {
                        // Trié par taille : un dossier plus léger que le plus petit retenu n'apportera rien.
                        if (heap.Count < count || child.Size > heap.Min!.Size) stack.Push(child);
                        continue;
                    }
                    if (heap.Count < count) heap.Add(child);
                    else if (child.Size > heap.Min!.Size)
                    {
                        heap.Remove(heap.Min);
                        heap.Add(child);
                    }
                }
            }

            var result = new List<DiskNode>(heap);
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Retire un élément de l'arbre, une fois mis à la corbeille : les totaux de toute sa
        /// lignée et ceux des types de fichiers suivent.
        /// </summary>
        public bool Remove(DiskNode node)
        {
            if (node == null || node.Parent == null) return false;

            var stack = new Stack<DiskNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.IsDirectory)
                {
                    Directories--;
                    foreach (var child in current.Children) stack.Push(child);
                }
                else if (current.Extension >= 0)
                {
                    var stat = _extensions[current.Extension];
                    stat.Bytes -= current.Size;
                    stat.Files--;
                }
            }

            var parent = node.Parent;
            if (!parent.Remove(node)) return false;
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.Parent?.SortChildren();
            parent.SortChildren();
            return true;
        }

        /// <summary>
        /// Totaux, tris et types, en une passe après l'exploration : parcours en profondeur sans
        /// récursion, une arborescence de plusieurs milliers de niveaux ne doit pas faire déborder la pile.
        /// </summary>
        private void Aggregate()
        {
            var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(DiskNode Node, bool Visited)>();
            stack.Push((Root, false));

            while (stack.Count > 0)
            {
                var (node, visited) = stack.Pop();
                if (!visited)
                {
                    stack.Push((node, true));
                    foreach (var child in node.Children)
                        if (child.IsDirectory) stack.Push((child, false));
                    continue;
                }

                Directories++;
                long size = 0, files = 0, last = node.LastWriteUtcTicks;
                foreach (var child in node.Children)
                {
                    if (!child.IsDirectory)
                    {
                        var extension = ExtensionOf(child.Name);
                        if (!indexes.TryGetValue(extension, out var index))
                        {
                            index = _extensions.Count;
                            indexes.Add(extension, index);
                            _extensions.Add(new ExtensionStat(extension.ToLowerInvariant()));
                        }
                        child.Extension = index;
                        _extensions[index].Bytes += child.Size;
                        _extensions[index].Files++;
                    }
                    size += child.Size;
                    files += child.Files;
                    if (child.LastWriteUtcTicks > last) last = child.LastWriteUtcTicks;
                }
                node.Size = size;
                node.Files = files;
                node.LastWriteUtcTicks = last;
                node.SortChildren();
            }

            // Les couleurs suivent le rang : les types qui pèsent le plus sont les plus faciles à repérer.
            var ranked = new List<ExtensionStat>(_extensions);
            ranked.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            for (var rank = 0; rank < ranked.Count && rank < PaletteSize; rank++) ranked[rank].Color = rank;
        }

        /// <summary>« .mp4 » pour « film.MP4 » ; vide pour « LISEZMOI » comme pour « .gitignore ».</summary>
        public static string ExtensionOf(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot <= 0 || dot == name.Length - 1 ? string.Empty : name.Substring(dot);
        }

        /// <summary>Les types de fichiers, triés du plus lourd au plus léger, sans les vides.</summary>
        public IReadOnlyList<ExtensionStat> RankedExtensions()
        {
            var ranked = new List<ExtensionStat>();
            foreach (var stat in _extensions)
                if (stat.Files > 0) ranked.Add(stat);
            ranked.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return ranked;
        }
    }
}
