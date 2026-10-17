using System;
using System.Collections.Generic;
using System.Threading;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Un rectangle de la carte, en pixels.</summary>
    public readonly struct TreemapRect
    {
        public TreemapRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public double Area => Width * Height;

        public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

        public override string ToString() => X + "," + Y + " " + Width + "×" + Height;
    }

    /// <summary>Un élément dessiné sur la carte, et la place qu'il y occupe.</summary>
    public readonly struct TreemapItem
    {
        public TreemapItem(DiskNode node, TreemapRect bounds)
        {
            Node = node;
            Bounds = bounds;
        }

        public DiskNode Node { get; }

        public TreemapRect Bounds { get; }
    }

    /// <summary>Le rendu d'une carte : les pixels, et de quoi retrouver l'élément sous la souris.</summary>
    public sealed class TreemapImage
    {
        private readonly int[] _owner;
        private readonly List<TreemapItem> _items;
        private Dictionary<DiskNode, int>? _index;

        internal TreemapImage(int width, int height, int[] pixels, int[] owner, List<TreemapItem> items)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
            _owner = owner;
            _items = items;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Pixels BGRA 32 bits, ligne par ligne.</summary>
        public int[] Pixels { get; }

        public IReadOnlyList<TreemapItem> Items => _items;

        /// <summary>Le fichier (ou le dossier trop petit pour être détaillé) sous ce point.</summary>
        public DiskNode? HitTest(double x, double y)
        {
            var px = (int)x;
            var py = (int)y;
            if (px < 0 || py < 0 || px >= Width || py >= Height) return null;
            var item = _owner[py * Width + px];
            return item < 0 ? null : _items[item].Node;
        }

        /// <summary>
        /// La place d'un élément sur la carte ; pour un élément trop petit pour y figurer, celle de
        /// son plus proche parent dessiné.
        /// </summary>
        public bool TryGetBounds(DiskNode? node, out TreemapItem item)
        {
            if (_index == null)
            {
                var index = new Dictionary<DiskNode, int>(_items.Count);
                for (var position = 0; position < _items.Count; position++) index[_items[position].Node] = position;
                _index = index;
            }

            for (var current = node; current != null; current = current.Parent)
            {
                if (_index.TryGetValue(current, out var position))
                {
                    item = _items[position];
                    return true;
                }
            }

            item = default;
            return false;
        }
    }

    /// <summary>Le réglage du rendu : couleurs du thème et relief des coussins.</summary>
    public sealed class TreemapStyle
    {
        /// <summary>Couleur de chaque rang de type de fichier, en 0xRRGGBB.</summary>
        public int[] Palette { get; set; } =
        {
            // Teintes franches mais pas criardes : distinctes entre elles, lisibles sur fond sombre
            // comme sur fond clair, et le rouge de la marque gardé pour la sélection.
            0x4C6FD8, 0x3BA676, 0xE8853D, 0x9064C8, 0x2AA3A0, 0xE6B84A,
            0xCF5C98, 0x3D8FD6, 0x7FA34A, 0xB98A62, 0x6C7BB5, 0xD8705F,
        };

        /// <summary>Les types au-delà de la palette, et les dossiers trop petits pour être détaillés.</summary>
        public int Other { get; set; } = 0x8E8F99;

        /// <summary>Le fond, visible dans les liserés entre dossiers.</summary>
        public int Background { get; set; } = 0x121217;

        /// <summary>Hauteur du coussin du premier niveau, en proportion de sa taille.</summary>
        public double Height { get; set; } = 0.30;

        /// <summary>Un niveau de plus, un coussin plus plat de ce facteur.</summary>
        public double Falloff { get; set; } = 0.88;

        /// <summary>Lumière ambiante, le reste vient de la source orientée en haut à gauche.</summary>
        public double Ambient { get; set; } = 0.30;

        /// <summary>Éclat général : au-dessus de 1, les faces exposées tirent vers le clair.</summary>
        public double Brightness { get; set; } = 1.05;

        /// <summary>Les dossiers des premiers niveaux sont séparés par un liseré, tant qu'il reste de la place.</summary>
        public int GapDepth { get; set; } = 2;
    }

    /// <summary>
    /// La carte du disque : chaque fichier, un rectangle proportionnel à sa taille, coloré selon son type.
    /// </summary>
    /// <remarks>
    /// Découpage « squarifié » (Bruls, Huizing, van Wijk) : des rectangles aussi carrés que
    /// possible, donc lisibles et faciles à viser. Relief en coussins (van Wijk, van de
    /// Wetering) : chaque dossier ajoute une bosse à la surface, et la lumière fait apparaître
    /// l'arborescence sans un seul trait. C'est le rendu de WinDirStat, adouci : bosses plus
    /// basses, liserés entre les grands dossiers, couleurs du thème.
    /// </remarks>
    public static class TreemapRenderer
    {
        // Source de lumière en haut à gauche, normalisée.
        private const double LightX = -1.0 / 10.1;
        private const double LightY = -1.0 / 10.1;
        private const double LightZ = 10.0 / 10.1;

        public static TreemapImage Render(
            DiskNode root, DiskUsageTree? tree, int width, int height, TreemapStyle? style = null,
            CancellationToken cancellationToken = default)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            style ??= new TreemapStyle();
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var pixels = new int[width * height];
            var owner = new int[width * height];
            var background = Opaque(style.Background);
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = background;
                owner[index] = -1;
            }

            var renderer = new Pass(tree, width, height, pixels, owner, style, cancellationToken);
            if (root.Size > 0)
                renderer.Draw(root, new TreemapRect(0, 0, width, height), new double[4], style.Height, 0, -1);

            return new TreemapImage(width, height, pixels, owner, renderer.Items);
        }

        /// <summary>
        /// Découpe <paramref name="bounds"/> entre les enfants de <paramref name="node"/>, déjà
        /// triés du plus lourd au plus léger ; les enfants vides n'ont pas de place.
        /// </summary>
        public static List<TreemapItem> Squarify(IReadOnlyList<DiskNode> children, TreemapRect bounds)
        {
            var result = new List<TreemapItem>(children.Count);
            long total = 0;
            var count = 0;
            foreach (var child in children)
            {
                if (child.Size <= 0) break;
                total += child.Size;
                count++;
            }
            if (count == 0 || bounds.Area <= 0) return result;

            var scale = bounds.Area / total;
            double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
            var start = 0;

            while (start < count)
            {
                var side = Math.Min(w, h);
                var rowArea = children[start].Size * scale;
                var worst = Worst(children[start].Size * scale, rowArea, rowArea, side);
                var end = start + 1;

                while (end < count)
                {
                    var area = children[end].Size * scale;
                    var candidate = Worst(children[start].Size * scale, area, rowArea + area, side);
                    if (candidate > worst) break;
                    worst = candidate;
                    rowArea += area;
                    end++;
                }

                // Dernière rangée : elle prend tout ce qui reste, pour ne pas laisser de jour au bord.
                var last = end == count;

                if (w >= h)
                {
                    // Une colonne à gauche.
                    var columnWidth = last ? w : rowArea / h;
                    var offset = y;
                    for (var index = start; index < end; index++)
                    {
                        var itemHeight = index == end - 1 ? y + h - offset : children[index].Size * scale / columnWidth;
                        result.Add(new TreemapItem(children[index], new TreemapRect(x, offset, columnWidth, itemHeight)));
                        offset += itemHeight;
                    }
                    x += columnWidth;
                    w -= columnWidth;
                }
                else
                {
                    // Une rangée en haut.
                    var rowHeight = last ? h : rowArea / w;
                    var offset = x;
                    for (var index = start; index < end; index++)
                    {
                        var itemWidth = index == end - 1 ? x + w - offset : children[index].Size * scale / rowHeight;
                        result.Add(new TreemapItem(children[index], new TreemapRect(offset, y, itemWidth, rowHeight)));
                        offset += itemWidth;
                    }
                    y += rowHeight;
                    h -= rowHeight;
                }

                start = end;
            }

            return result;
        }

        /// <summary>Le pire rapport d'aspect d'une rangée : le plus grand élément est le premier, le plus petit le dernier ajouté.</summary>
        private static double Worst(double largest, double smallest, double rowArea, double side)
        {
            var side2 = side * side;
            var row2 = rowArea * rowArea;
            return Math.Max(side2 * largest / row2, row2 / (side2 * smallest));
        }

        private static int Opaque(int rgb) => unchecked((int)0xFF000000) | (rgb & 0xFFFFFF);

        private sealed class Pass
        {
            private readonly DiskUsageTree? _tree;
            private readonly int _width;
            private readonly int _height;
            private readonly int[] _pixels;
            private readonly int[] _owner;
            private readonly TreemapStyle _style;
            private readonly CancellationToken _cancellation;
            private int _drawn;

            public Pass(DiskUsageTree? tree, int width, int height, int[] pixels, int[] owner, TreemapStyle style, CancellationToken cancellation)
            {
                _tree = tree;
                _width = width;
                _height = height;
                _pixels = pixels;
                _owner = owner;
                _style = style;
                _cancellation = cancellation;
            }

            public List<TreemapItem> Items { get; } = new List<TreemapItem>();

            public void Draw(DiskNode node, TreemapRect rect, double[] parentSurface, double height, int depth, int parent)
            {
                if ((++_drawn & 0xFFF) == 0) _cancellation.ThrowIfCancellationRequested();

                // Trop fin pour être visé : il colore quand même son trait de pixel, au compte de son dossier,
                // sans quoi un dossier de milliers de petits fichiers apparaîtrait vide.
                if (parent >= 0 && (rect.Width < 0.5 || rect.Height < 0.5))
                {
                    Fill(rect, parentSurface, ColorOf(node), parent);
                    return;
                }

                var item = Items.Count;
                Items.Add(new TreemapItem(node, rect));

                var surface = (double[])parentSurface.Clone();
                AddRidge(surface, rect, height);

                if (node.IsDirectory && node.HasChildren && rect.Width >= 2 && rect.Height >= 2)
                {
                    var inner = rect;
                    if (depth < _style.GapDepth && depth > 0 && rect.Width > 12 && rect.Height > 12)
                        inner = new TreemapRect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);

                    foreach (var child in Squarify(node.Children, inner))
                        Draw(child.Node, child.Bounds, surface, height * _style.Falloff, depth + 1, item);
                    return;
                }

                Fill(rect, surface, ColorOf(node), item);
            }

            private int ColorOf(DiskNode node)
            {
                if (node.IsDirectory || _tree == null) return _style.Other;
                var stat = _tree.ExtensionOf(node);
                return stat != null && stat.Color >= 0 && stat.Color < _style.Palette.Length ? _style.Palette[stat.Color] : _style.Other;
            }

            /// <summary>Une bosse parabolique sur chaque axe : la pente fait la lumière.</summary>
            private static void AddRidge(double[] surface, TreemapRect rect, double height)
            {
                if (rect.Width > 0)
                {
                    surface[0] += 4 * height * (rect.Right + rect.X) / rect.Width;
                    surface[1] -= 4 * height / rect.Width;
                }
                if (rect.Height > 0)
                {
                    surface[2] += 4 * height * (rect.Bottom + rect.Y) / rect.Height;
                    surface[3] -= 4 * height / rect.Height;
                }
            }

            private void Fill(TreemapRect rect, double[] surface, int rgb, int item)
            {
                var left = Clamp((int)Math.Round(rect.X), _width);
                var right = Clamp((int)Math.Round(rect.Right), _width);
                var top = Clamp((int)Math.Round(rect.Y), _height);
                var bottom = Clamp((int)Math.Round(rect.Bottom), _height);

                // Un fichier de moins d'un pixel garde au moins le sien : sinon les fichiers minuscules disparaîtraient de la carte.
                if (right == left && left < _width) right = left + 1;
                if (bottom == top && top < _height) bottom = top + 1;

                double red = (rgb >> 16) & 0xFF, green = (rgb >> 8) & 0xFF, blue = rgb & 0xFF;
                var ambient = _style.Ambient;
                var diffuse = (1 - ambient) * _style.Brightness;

                for (var py = top; py < bottom; py++)
                {
                    var ny = -(2 * surface[3] * (py + 0.5) + surface[2]);
                    var row = py * _width;
                    for (var px = left; px < right; px++)
                    {
                        var nx = -(2 * surface[1] * (px + 0.5) + surface[0]);
                        var cosine = (nx * LightX + ny * LightY + LightZ) / Math.Sqrt(nx * nx + ny * ny + 1);
                        var light = ambient + diffuse * Math.Max(0, cosine);

                        var r = Math.Min(255, (int)(red * light));
                        var g = Math.Min(255, (int)(green * light));
                        var b = Math.Min(255, (int)(blue * light));
                        _pixels[row + px] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                        _owner[row + px] = item;
                    }
                }
            }

            private static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;
        }
    }
}
