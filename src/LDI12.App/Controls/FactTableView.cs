using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LDI12.Reports.Facts;

namespace LDI12.App.Controls
{
    /// <summary>
    /// Tableau technique à colonnes variables : attributs SMART, volumes, périphériques, journaux.
    /// </summary>
    /// <remarks>
    /// Construit en code plutôt qu'en XAML parce que le nombre et la nature des colonnes ne sont
    /// connus qu'à l'exécution. Un <c>DataGrid</c> ferait l'affaire, mais il apporte le tri,
    /// l'édition, la sélection et le redimensionnement : quatre fonctions dont aucune n'est
    /// souhaitée ici, pour un coût de première ouverture qui se voit sur une machine lente.
    ///
    /// La colonne la plus verbeuse est élastique, les autres sont ajustées au contenu : sans
    /// cela, un extrait de journal de deux cents caractères écraserait les six colonnes de
    /// chiffres qui l'accompagnent.
    /// </remarks>
    public sealed class FactTableView : ContentControl
    {
        /// <summary>
        /// Au-delà, on n'affiche plus. Ces tableaux ne sont pas virtualisés (ils ne contiennent
        /// que des anomalies, donc quelques dizaines de lignes) mais une machine très abîmée
        /// pourrait en produire des centaines, et figer l'interface au moment précis où le
        /// technicien en a le plus besoin. Le rapport HTML, lui, les contient toutes.
        /// </summary>
        private const int MaxRows = 120;

        /// <summary>Largeur maximale d'une colonne ajustée au contenu.</summary>
        private const double MaxFixedColumnWidth = 300;

        /// <summary>
        /// Place laissée à la colonne élastique avant de renoncer à tout montrer d'un coup.
        /// </summary>
        /// <remarks>
        /// En dessous, la colonne verbeuse revient à la ligne tous les deux mots et la ligne
        /// devient plus haute que large. C'est le seuil à partir duquel le tableau préfère
        /// défiler plutôt que de se comprimer davantage.
        /// <para>
        /// La valeur est basse à dessein. Rien n'est perdu quand cette colonne se resserre (son
        /// texte revient à la ligne) alors que le défilement, lui, se paie d'un geste. Mesuré sur
        /// la machine d'essai à 1220 points de large : à 220, le tableau des logiciels installés
        /// affichait une barre de défilement pour vingt-cinq points, sans qu'une seule ligne soit
        /// coupée.
        /// </para>
        /// </remarks>
        private const double MinElasticColumnWidth = 160;

        /// <summary>Place rendue à la colonne élastique quand le tableau défile de toute façon.</summary>
        private const double RelaxedElasticGain = MaxFixedColumnWidth - MinElasticColumnWidth;

        public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
            nameof(Table), typeof(FactTable), typeof(FactTableView),
            new PropertyMetadata(null, OnTableChanged));

        public FactTable? Table
        {
            get => (FactTable?)GetValue(TableProperty);
            set => SetValue(TableProperty, value);
        }

        private static void OnTableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((FactTableView)d).Rebuild();

        private void Rebuild()
        {
            var table = Table;
            if (table == null)
            {
                Content = null;
                return;
            }

            if (!table.HasRows)
            {
                Content = new TextBlock
                {
                    Text = table.EmptyMessage,
                    Style = (Style)FindResource("Text.Muted"),
                    Margin = new Thickness(0, 10, 0, 0),
                };
                return;
            }

            var elastic = WidestColumn(table);
            var shown = Math.Min(table.Rows.Count, MaxRows);
            var grid = new TableGrid(elastic) { Margin = new Thickness(0, 12, 0, 0) };

            for (var column = 0; column < table.Columns.Count; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = column == elastic ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,

                    // Une colonne ajustée au contenu suit son plus long texte sans limite : un
                    // chemin de cent caractères y pousserait les colonnes suivantes hors de
                    // l'écran. Au-delà de cette largeur, le texte se coupe et le tableau reste
                    // entier.
                    MaxWidth = column == elastic ? double.PositiveInfinity : MaxFixedColumnWidth,
                });

            for (var row = 0; row <= shown; row++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddHeader(grid, table);

            for (var row = 0; row < shown; row++)
                AddRow(grid, table, row, elastic);

            var frame = Scrollable(grid);

            if (table.Rows.Count <= MaxRows)
            {
                Content = frame;
                return;
            }

            var panel = new StackPanel();
            panel.Children.Add(frame);
            panel.Children.Add(new TextBlock
            {
                Text = (table.Rows.Count - MaxRows) + " ligne(s) supplémentaire(s) ne sont pas affichées ici. " +
                       "Le rapport exporté les contient toutes.",
                Style = (Style)FindResource("Text.Muted"),
                Margin = new Thickness(0, 10, 0, 0),
            });
            Content = panel;
        }

        /// <summary>
        /// Encadre le tableau d'un défilement horizontal, qui n'apparaît que s'il sert.
        /// </summary>
        /// <remarks>
        /// Un tableau à six colonnes (les tâches planifiées, les journaux d'événements) ne tient
        /// pas dans une fenêtre étroite. Jusqu'ici, la grille était comprimée jusqu'à ce que ses
        /// dernières colonnes sortent du cadre : elles n'étaient ni affichées, ni annoncées, et
        /// rien ne disait au technicien qu'il manquait deux colonnes. Elles restent désormais
        /// atteignables, au prix d'un geste qui, lui, se voit.
        /// </remarks>
        private FrameworkElement Scrollable(TableGrid grid)
        {
            var scroll = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };

            // La grille ne reçoit plus de largeur du cadre : c'est lui qui la lui donne.
            scroll.SizeChanged += (_, e) => grid.SetVisibleWidth(e.NewSize.Width);
            scroll.PreviewMouseWheel += OnTableWheel;

            return scroll;
        }

        /// <summary>
        /// La molette continue de faire défiler la page.
        /// </summary>
        /// <remarks>
        /// Un cadre de défilement imbriqué retient la molette même lorsqu'il n'a rien à en faire :
        /// sans ce renvoi, la page se figerait dès que le pointeur passe sur un tableau. Le geste
        /// n'est gardé ici qu'avec Maj enfoncée, et seulement s'il y a réellement de quoi défiler
        /// sur le côté.
        /// </remarks>
        private void OnTableWheel(object sender, MouseWheelEventArgs e)
        {
            var scroll = (ScrollViewer)sender;

            if (Keyboard.Modifiers == ModifierKeys.Shift && scroll.ScrollableWidth > 0)
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }

            e.Handled = true;
            RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this,
            });
        }

        private void AddHeader(Grid grid, FactTable table)
        {
            var rule = new Border
            {
                BorderBrush = (Brush)FindResource("Brush.BorderSubtle"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Grid.SetRow(rule, 0);
            Grid.SetColumnSpan(rule, table.Columns.Count);
            grid.Children.Add(rule);

            for (var column = 0; column < table.Columns.Count; column++)
            {
                var header = new TextBlock
                {
                    Text = table.Columns[column],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Brush.TextTertiary"),
                    Margin = new Thickness(column == 0 ? 0 : 14, 0, 0, 6),
                    TextWrapping = TextWrapping.NoWrap,
                };
                Grid.SetRow(header, 0);
                Grid.SetColumn(header, column);
                grid.Children.Add(header);
            }
        }

        /// <summary>
        /// Une ligne du tableau.
        /// </summary>
        /// <remarks>
        /// Seule la colonne élastique renvoie à la ligne, et c'est la seule qui le puisse : une
        /// colonne ajustée au contenu se mesure sans contrainte de largeur, si bien qu'un retour
        /// à la ligne demandé là n'arrive jamais, sauf quand la grille manque de place, où il
        /// se produit alors caractère par caractère. Le tableau des tâches planifiées l'a montré :
        /// une ligne haute de cinq cents pixels, quatre colonnes hors de l'écran.
        /// </remarks>
        private void AddRow(Grid grid, FactTable table, int index, int elastic)
        {
            var row = table.Rows[index];
            var background = ToneBrush(row.Tone);

            if (background != null)
            {
                var fill = new Border
                {
                    Background = background,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(-6, 0, -6, 0),
                };
                Grid.SetRow(fill, index + 1);
                Grid.SetColumnSpan(fill, table.Columns.Count);
                grid.Children.Add(fill);
            }

            for (var column = 0; column < table.Columns.Count && column < row.Cells.Count; column++)
            {
                var cell = new TextBlock
                {
                    Text = row.Cells[column],
                    FontSize = 12,
                    Foreground = (Brush)FindResource(
                        column == 0 ? "Brush.TextPrimary" : "Brush.TextSecondary"),
                    Margin = new Thickness(column == 0 ? 0 : 14, 3, 0, 3),
                    TextWrapping = column == elastic ? TextWrapping.Wrap : TextWrapping.NoWrap,

                    // La colonne élastique va à la ligne plutôt que de couper : c'est celle qui
                    // porte les chemins et les extraits, dont la fin compte autant que le début.
                    // Les autres se coupent, avec les points de suspension qui le disent.
                    TextTrimming = column == elastic ? TextTrimming.None : TextTrimming.CharacterEllipsis,

                    // La largeur se pose sur le texte et pas seulement sur la colonne : dans une
                    // colonne ajustée au contenu, le bloc de texte est mesuré sans contrainte et
                    // ne sait donc jamais qu'il déborde. La grille le coupait net, sans les
                    // points de suspension qui auraient dit qu'il manquait quelque chose.
                    MaxWidth = column == elastic ? double.PositiveInfinity : MaxFixedColumnWidth,
                };
                TextOptions.SetTextFormattingMode(cell, TextFormattingMode.Display);
                Typography.SetNumeralAlignment(cell, FontNumeralAlignment.Tabular);

                Grid.SetRow(cell, index + 1);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        /// <summary>
        /// Colonne à rendre élastique : celle dont le contenu est le plus long. Mesurer plutôt que
        /// supposer, parce que la colonne verbeuse n'est pas à la même place d'un tableau à
        /// l'autre : dernière pour un journal d'événements, deuxième pour un attribut SMART.
        /// </summary>
        /// <remarks>
        /// Ce sont les valeurs <b>distinctes</b> qui sont pesées, et non toutes les cellules : une
        /// colonne de catégories répète huit libellés sur quarante lignes, et la somme brute la
        /// faisait passer pour la plus verbeuse. La chronologie l'a montré : la colonne « Nature »
        /// prenait toute la place laissée libre, et le sujet se retrouvait plaqué contre le bord
        /// droit, séparé du reste par un vide de trois cents points. Ce qui doit s'étirer est ce
        /// qui varie.
        /// </remarks>
        private static int WidestColumn(FactTable table)
        {
            var totals = new int[table.Columns.Count];
            var sampled = Math.Min(table.Rows.Count, 40);

            var seen = new HashSet<string>[table.Columns.Count];
            for (var column = 0; column < seen.Length; column++)
                seen[column] = new HashSet<string>(StringComparer.Ordinal);

            for (var row = 0; row < sampled; row++)
            {
                var cells = table.Rows[row].Cells;
                for (var column = 0; column < totals.Length && column < cells.Count; column++)
                {
                    var cell = cells[column];
                    if (cell == null || !seen[column].Add(cell)) continue;
                    totals[column] += cell.Length;
                }
            }

            var best = totals.Length - 1;
            for (var column = 0; column < totals.Length; column++)
                if (totals[column] > totals[best])
                    best = column;

            return best;
        }

        /// <summary>
        /// Grille de tableau qui ne se laisse pas comprimer en dessous de sa largeur utile.
        /// </summary>
        /// <remarks>
        /// Placée dans un défilement horizontal, une grille ordinaire ne reçoit plus aucune
        /// largeur : elle est mesurée sans contrainte, sa colonne élastique s'étale sur toute la
        /// longueur de son texte et plus rien ne revient jamais à la ligne. Celle-ci reprend donc
        /// la largeur visible du cadre (la mise en page reste exactement celle d'avant tant
        /// qu'elle suffit) et s'arrête au plancher. C'est là, et seulement là, que la barre de
        /// défilement apparaît.
        /// </remarks>
        private sealed class TableGrid : Grid
        {
            private readonly int _elastic;
            private double _visible;
            private double _floor = double.NaN;

            internal TableGrid(int elastic) => _elastic = elastic;

            internal void SetVisibleWidth(double width)
            {
                if (Math.Abs(_visible - width) < 0.5) return;
                _visible = width;
                InvalidateMeasure();
            }

            protected override Size MeasureOverride(Size constraint)
            {
                if (double.IsNaN(_floor)) _floor = MeasureFloor(constraint.Height);

                var visible = double.IsInfinity(constraint.Width) ? _visible : constraint.Width;

                // Une fois le défilement inévitable, la colonne élastique n'a plus de raison de
                // rester au minimum : elle reçoit le même plafond que les autres. À 160 points,
                // un chemin de programme tenait sur quatre lignes et chaque ligne du tableau
                // devenait un pavé ; le geste de défilement, lui, est le même dans les deux cas.
                var width = visible >= _floor ? visible : _floor + RelaxedElasticGain;

                var size = base.MeasureOverride(new Size(width, constraint.Height));
                return new Size(Math.Max(size.Width, width), size.Height);
            }

            /// <summary>
            /// Largeur en dessous de laquelle le tableau perdrait des colonnes.
            /// </summary>
            /// <remarks>
            /// Mesurée, pas devinée : la colonne élastique est bridée le temps d'une mesure sans
            /// contrainte, ce qui donne exactement la somme des colonnes ajustées au contenu et de
            /// la place minimale laissée à la colonne verbeuse. La valeur ne dépend donc ni de la
            /// police, ni de l'échelle de l'écran, ni du nombre de colonnes du tableau.
            /// </remarks>
            private double MeasureFloor(double height)
            {
                if (_elastic < 0 || _elastic >= ColumnDefinitions.Count) return 0;

                var elastic = ColumnDefinitions[_elastic];
                var previous = elastic.MaxWidth;

                elastic.MaxWidth = MinElasticColumnWidth;
                var natural = base.MeasureOverride(new Size(double.PositiveInfinity, height));
                elastic.MaxWidth = previous;

                return natural.Width;
            }
        }

        private Brush? ToneBrush(FactTone tone) => tone switch
        {
            FactTone.Warning => (Brush)FindResource("Brush.StatusWarningSoft"),
            FactTone.Bad => (Brush)FindResource("Brush.StatusCriticalSoft"),
            _ => null,
        };
    }
}
