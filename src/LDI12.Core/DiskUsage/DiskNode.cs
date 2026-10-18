using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Core.DiskUsage
{
    public enum DiskNodeKind
    {
        File = 0,
        Directory = 1,
    }

    /// <summary>
    /// Un fichier ou un dossier pesé par l'analyse du disque.
    /// </summary>
    /// <remarks>
    /// Volontairement léger : un volume ordinaire en compte plusieurs centaines de milliers, et
    /// chacun reste en mémoire le temps de l'exploration. Pas de chemin complet stocké, il se
    /// recompose depuis les parents ; l'extension tient dans un indice vers la table des types.
    /// </remarks>
    public sealed class DiskNode
    {
        private static readonly DiskNode[] NoChildren = new DiskNode[0];

        public DiskNode(string name, DiskNodeKind kind, DiskNode? parent)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            Parent = parent;
        }

        public string Name { get; }

        public DiskNodeKind Kind { get; }

        public DiskNode? Parent { get; internal set; }

        /// <summary>Taille en octets : la sienne pour un fichier, la somme de son contenu pour un dossier.</summary>
        public long Size { get; internal set; }

        /// <summary>Nombre de fichiers contenus, lui-même compris pour un fichier.</summary>
        public long Files { get; internal set; }

        /// <summary>Dernière modification, en ticks UTC ; pour un dossier, la plus récente de son contenu.</summary>
        public long LastWriteUtcTicks { get; internal set; }

        /// <summary>Indice dans <see cref="DiskUsageTree.Extensions"/>, -1 pour un dossier.</summary>
        public int Extension { get; internal set; } = -1;

        /// <summary>Le dossier n'a pas pu être lu : accès refusé, chemin disparu.</summary>
        public bool Unreadable { get; internal set; }

        /// <summary>Enfants triés du plus lourd au plus léger.</summary>
        public IReadOnlyList<DiskNode> Children => _children;

        private DiskNode[] _children = NoChildren;

        public bool IsDirectory => Kind == DiskNodeKind.Directory;

        public bool HasChildren => _children.Length > 0;

        public DateTime? LastWrite
            => LastWriteUtcTicks > 0 ? new DateTime(LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;

        /// <summary>Le chemin complet, recomposé depuis la racine.</summary>
        public string FullPath
        {
            get
            {
                if (Parent == null) return Name;

                var parts = new List<string>();
                for (var node = this; node != null; node = node.Parent) parts.Add(node.Name);
                parts.Reverse();
                var path = parts[0];
                for (var index = 1; index < parts.Count; index++) path = Path.Combine(path, parts[index]);
                return path;
            }
        }

        /// <summary>Profondeur sous la racine, la racine valant 0.</summary>
        public int Depth
        {
            get
            {
                var depth = 0;
                for (var node = Parent; node != null; node = node.Parent) depth++;
                return depth;
            }
        }

        /// <summary>Vrai si <paramref name="other"/> est ce nœud ou l'un de ses descendants.</summary>
        public bool Contains(DiskNode? other)
        {
            for (var node = other; node != null; node = node.Parent)
                if (ReferenceEquals(node, this)) return true;
            return false;
        }

        internal void SetChildren(List<DiskNode> children)
            => _children = children.Count == 0 ? NoChildren : children.ToArray();

        internal void SortChildren()
        {
            if (_children.Length > 1) Array.Sort(_children, BySizeDescending);
        }

        /// <summary>Retire un enfant et reporte sa taille sur toute la lignée.</summary>
        internal bool Remove(DiskNode child)
        {
            var index = Array.IndexOf(_children, child);
            if (index < 0) return false;

            var rest = new DiskNode[_children.Length - 1];
            Array.Copy(_children, 0, rest, 0, index);
            Array.Copy(_children, index + 1, rest, index, _children.Length - index - 1);
            _children = rest;
            child.Parent = null;

            for (var node = this; node != null; node = node.Parent)
            {
                node.Size -= child.Size;
                node.Files -= child.Files;
            }
            return true;
        }

        internal static int BySizeDescending(DiskNode a, DiskNode b)
        {
            var bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => Name + " (" + Size + ")";
    }
}
