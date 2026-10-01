using System;
using System.Windows;
using System.Windows.Controls;

namespace LDI12.App.Controls
{
    /// <summary>
    /// Disposition en colonnes de paires libellé / valeur.
    /// </summary>
    /// <remarks>
    /// Écrit parce qu'aucun panneau standard ne convient. <c>UniformGrid</c> impose à toutes les
    /// cellules la hauteur de la plus haute : une seule ligne portant la raison d'une mesure
    /// absente (« la température nécessite des capteurs désactivés par défaut ») étirait les
    /// quinze autres lignes de la carte, qui devenait deux fois trop haute et illisible.
    ///
    /// Ici, la hauteur est calculée <b>par rangée</b> : les deux éléments d'une même rangée
    /// s'alignent, ce qui est nécessaire pour lire en balayant, mais une rangée verbeuse n'impose
    /// rien aux autres.
    ///
    /// Le nombre de colonnes retombe à un quand la largeur ne suffit plus. C'est la même règle
    /// que le reste de l'application : la mise en page suit la fenêtre.
    /// </remarks>
    public sealed class PairGrid : Panel
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(PairGrid),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap), typeof(double), typeof(PairGrid),
            new FrameworkPropertyMetadata(28d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>En deçà, on repasse à une colonne plutôt que de tronquer les valeurs.</summary>
        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
            nameof(MinColumnWidth), typeof(double), typeof(PairGrid),
            new FrameworkPropertyMetadata(300d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// Vrai : les éléments d'une même rangée prennent la hauteur du plus haut. Pour des cartes
        /// posées côte à côte, dont les bas inégaux dessinaient un escalier.
        /// </summary>
        public static readonly DependencyProperty StretchRowsProperty = DependencyProperty.Register(
            nameof(StretchRows), typeof(bool), typeof(PairGrid),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        public bool StretchRows
        {
            get => (bool)GetValue(StretchRowsProperty);
            set => SetValue(StretchRowsProperty, value);
        }

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        public double ColumnGap
        {
            get => (double)GetValue(ColumnGapProperty);
            set => SetValue(ColumnGapProperty, value);
        }

        public double MinColumnWidth
        {
            get => (double)GetValue(MinColumnWidthProperty);
            set => SetValue(MinColumnWidthProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = ResolveColumns(availableSize.Width);
            var columnWidth = ColumnWidth(availableSize.Width, columns);
            var height = 0d;
            var rowHeight = 0d;

            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

                if ((i + 1) % columns != 0) continue;

                height += rowHeight;
                rowHeight = 0d;
            }

            height += rowHeight;

            var width = double.IsInfinity(availableSize.Width)
                ? (columnWidth * columns) + (ColumnGap * (columns - 1))
                : availableSize.Width;

            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = ResolveColumns(finalSize.Width);
            var columnWidth = ColumnWidth(finalSize.Width, columns);
            var top = 0d;
            var rowHeight = 0d;

            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                var column = i % columns;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

                var height = child.DesiredSize.Height;
                if (StretchRows)
                {
                    // La hauteur de la rangée entière, connue en relisant ses voisins.
                    var first = i - column;
                    for (var j = first; j < Math.Min(first + columns, InternalChildren.Count); j++)
                        height = Math.Max(height, InternalChildren[j].DesiredSize.Height);
                }

                child.Arrange(new Rect(
                    column * (columnWidth + ColumnGap), top, columnWidth, height));

                if ((i + 1) % columns != 0) continue;

                top += rowHeight;
                rowHeight = 0d;
            }

            return finalSize;
        }

        private int ResolveColumns(double availableWidth)
        {
            var requested = Math.Max(1, Columns);
            if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)) return requested;

            while (requested > 1 && ColumnWidth(availableWidth, requested) < MinColumnWidth) requested--;
            return requested;
        }

        private double ColumnWidth(double availableWidth, int columns)
        {
            if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)) return MinColumnWidth;
            return Math.Max(0, (availableWidth - (ColumnGap * (columns - 1))) / columns);
        }
    }
}
