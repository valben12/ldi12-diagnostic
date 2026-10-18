using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LDI12.App.Theming;
using LDI12.Core.Diagnostics;
using LDI12.Core.DiskUsage;

namespace LDI12.App.Controls
{
    /// <summary>
    /// La carte du disque à l'écran : rendu en arrière-plan, fondu d'une image à l'autre, cadre
    /// de sélection qui glisse vers l'élément choisi, zoom à la molette et déplacement à la main,
    /// et une bulle qui dit ce qu'il y a sous la souris.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Le rendu se fait en pixels réels de l'écran, mise à l'échelle comprise : une carte rendue
    /// à 96 points puis agrandie à 150 % serait floue, et ses petits fichiers illisibles.
    /// Chaque nouveau rendu annule le précédent : redimensionner la fenêtre n'empile pas les calculs.
    /// </para>
    /// <para>
    /// Le zoom ne grossit pas des pixels : la carte entière est remise en page à la taille
    /// agrandie et seule la partie visible est dessinée, si bien que les fichiers trop petits
    /// pour la vue d'ensemble apparaissent en approchant. Pendant le geste, l'image en place est
    /// simplement étirée, pour suivre la molette sans attendre ; le rendu net la remplace dès que
    /// la main s'arrête.
    /// </para>
    /// </remarks>
    public sealed class TreemapView : Grid
    {
        public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
            nameof(Root), typeof(DiskNode), typeof(TreemapView), new PropertyMetadata(null, (d, _) => ((TreemapView)d).OnRootChanged()));

        public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
            nameof(Tree), typeof(DiskUsageTree), typeof(TreemapView), new PropertyMetadata(null, (d, _) => ((TreemapView)d).Schedule()));

        /// <summary>Change à chaque modification de l'arbre (mise à la corbeille) : la carte se redessine.</summary>
        public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
            nameof(Revision), typeof(int), typeof(TreemapView), new PropertyMetadata(0, (d, _) => ((TreemapView)d).Schedule()));

        public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
            nameof(Selected), typeof(DiskNode), typeof(TreemapView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((TreemapView)d).PlaceSelection(animate: true)));

        public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(
            nameof(Hovered), typeof(DiskNode), typeof(TreemapView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        /// <summary>Double clic : la commande reçoit le dossier à agrandir.</summary>
        public static readonly DependencyProperty ZoomCommandProperty = DependencyProperty.Register(
            nameof(ZoomCommand), typeof(ICommand), typeof(TreemapView));

        private static readonly DependencyPropertyKey ZoomTextKey = DependencyProperty.RegisterReadOnly(
            nameof(ZoomText), typeof(string), typeof(TreemapView), new PropertyMetadata("100 %"));

        /// <summary>Le grossissement en cours, « 100 % » pour la carte entière.</summary>
        public static readonly DependencyProperty ZoomTextProperty = ZoomTextKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsZoomedKey = DependencyProperty.RegisterReadOnly(
            nameof(IsZoomed), typeof(bool), typeof(TreemapView), new PropertyMetadata(false));

        public static readonly DependencyProperty IsZoomedProperty = IsZoomedKey.DependencyProperty;

        private const double MaxZoom = 64;
        private const double WheelStep = 1.25;

        private static readonly Duration Glide = new Duration(TimeSpan.FromMilliseconds(170));
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        private readonly Image _back = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        private readonly Image _front = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        private readonly Canvas _tipLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        private readonly Border _hover;
        private readonly Border _selection;
        private readonly Border _tip;
        private readonly TextBlock _tipName = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        private readonly TextBlock _tipSize = new TextBlock { Margin = new Thickness(0, 3, 0, 0) };
        private readonly TextBlock _tipKind = new TextBlock { Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        private readonly TextBlock _tipPath = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        private readonly TextBlock _tipHint = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        private readonly MatrixTransform _stretch = new MatrixTransform();
        private readonly DispatcherTimer _debounce;
        private CancellationTokenSource? _rendering;
        private bool _pending;

        /// <summary>Cartes en attente de rendu : les captures d'écran attendent qu'il n'y en ait plus.</summary>
        public static int Pending { get; private set; }
        private TreemapImage? _image;
        private double _scaleX = 1, _scaleY = 1;

        // La vue demandée, en points d'écran de la carte agrandie, et celle de l'image affichée.
        private double _zoom = 1, _offsetX, _offsetY;
        private double _shownZoom = 1, _shownX, _shownY;

        // Le geste de la main : un appui qui bouge déplace la carte ; un appui immobile sélectionne.
        private Point? _pressed;
        private Point _pressedOffset;
        private bool _panning;

        public TreemapView()
        {
            ClipToBounds = true;
            Background = Brushes.Transparent;
            Focusable = true;
            FocusVisualStyle = null;
            RenderOptions.SetBitmapScalingMode(_back, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(_front, BitmapScalingMode.NearestNeighbor);
            _back.RenderTransform = _stretch;
            _front.RenderTransform = _stretch;
            _overlay.RenderTransform = _stretch;

            _hover = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(2),
                Visibility = Visibility.Collapsed,
            };
            _selection = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.White,
                CornerRadius = new CornerRadius(3),
                Visibility = Visibility.Collapsed,
                Child = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    CornerRadius = new CornerRadius(2),
                },
            };
            _overlay.Children.Add(_hover);
            _overlay.Children.Add(_selection);

            _tip = BuildTip();
            _tipLayer.Children.Add(_tip);

            Children.Add(_back);
            Children.Add(_front);
            Children.Add(_overlay);
            Children.Add(_tipLayer);

            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
            _debounce.Tick += (_, __) =>
            {
                _debounce.Stop();
                Render();
            };

            SizeChanged += (_, __) =>
            {
                Clamp();
                Schedule();
            };
            Loaded += (_, __) =>
            {
                ThemeManager.Changed += OnThemeChanged;
                Schedule();
            };
            Unloaded += (_, __) =>
            {
                ThemeManager.Changed -= OnThemeChanged;
                _debounce.Stop();
                SetPending(false);
            };
            Cursor = Cursors.Hand;
        }

        public DiskNode? Root
        {
            get => (DiskNode?)GetValue(RootProperty);
            set => SetValue(RootProperty, value);
        }

        public DiskUsageTree? Tree
        {
            get => (DiskUsageTree?)GetValue(TreeProperty);
            set => SetValue(TreeProperty, value);
        }

        public int Revision
        {
            get => (int)GetValue(RevisionProperty);
            set => SetValue(RevisionProperty, value);
        }

        public DiskNode? Selected
        {
            get => (DiskNode?)GetValue(SelectedProperty);
            set => SetValue(SelectedProperty, value);
        }

        public DiskNode? Hovered
        {
            get => (DiskNode?)GetValue(HoveredProperty);
            set => SetValue(HoveredProperty, value);
        }

        public ICommand? ZoomCommand
        {
            get => (ICommand?)GetValue(ZoomCommandProperty);
            set => SetValue(ZoomCommandProperty, value);
        }

        public string ZoomText => (string)GetValue(ZoomTextProperty);

        public bool IsZoomed => (bool)GetValue(IsZoomedProperty);

        /// <summary>La couleur de chaque rang de type de fichier : la légende la reprend.</summary>
        public static Color PaletteColor(int rank)
        {
            var style = new TreemapStyle();
            var rgb = rank >= 0 && rank < style.Palette.Length ? style.Palette[rank] : style.Other;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        // ------------------------------------------------------------------ zoom

        /// <summary>Approche, autour du centre de la carte (boutons et clavier).</summary>
        public void ZoomIn() => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), WheelStep * WheelStep);

        public void ZoomOut() => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / (WheelStep * WheelStep));

        /// <summary>Toute la carte, à nouveau.</summary>
        public void ResetZoom()
        {
            _zoom = 1;
            _offsetX = _offsetY = 0;
            ViewChanged();
        }

        private void ZoomAt(Point anchor, double factor)
        {
            var zoom = Math.Max(1, Math.Min(MaxZoom, _zoom * factor));
            if (Math.Abs(zoom - _zoom) < 1e-6) return;
            // Le point sous la souris reste sous la souris.
            var ratio = zoom / _zoom;
            _offsetX = (_offsetX + anchor.X) * ratio - anchor.X;
            _offsetY = (_offsetY + anchor.Y) * ratio - anchor.Y;
            _zoom = zoom;
            ViewChanged();
        }

        private void Clamp()
        {
            _offsetX = Math.Max(0, Math.Min(_offsetX, ActualWidth * (_zoom - 1)));
            _offsetY = Math.Max(0, Math.Min(_offsetY, ActualHeight * (_zoom - 1)));
        }

        private void ViewChanged()
        {
            Clamp();
            SetValue(ZoomTextKey, Math.Round(_zoom * 100).ToString("N0", Formats.French) + " %");
            SetValue(IsZoomedKey, _zoom > 1.0001);
            ApplyStretch();
            HideTip();
            Schedule();
        }

        /// <summary>
        /// Étire l'image affichée vers la vue demandée : un point de l'image rendue se place là où
        /// la nouvelle vue le mettra, en attendant le rendu net.
        /// </summary>
        private void ApplyStretch()
        {
            var scale = _zoom / _shownZoom;
            _stretch.Matrix = new Matrix(scale, 0, 0, scale, _shownX * scale - _offsetX, _shownY * scale - _offsetY);
        }

        private void OnRootChanged()
        {
            // Entrer dans un dossier, c'est le voir en entier.
            _zoom = 1;
            _offsetX = _offsetY = 0;
            SetValue(ZoomTextKey, "100 %");
            SetValue(IsZoomedKey, false);
            Schedule();
        }

        private void OnThemeChanged(object? sender, EventArgs e) => Schedule();

        private void Schedule()
        {
            SetPending(true);
            _debounce.Stop();
            _debounce.Start();
        }

        private void SetPending(bool pending)
        {
            if (_pending == pending) return;
            _pending = pending;
            Pending += pending ? 1 : -1;
        }

        private async void Render()
        {
            _rendering?.Cancel();
            var root = Root;
            var tree = Tree;
            if (root == null || ActualWidth < 4 || ActualHeight < 4)
            {
                SetPending(false);
                _image = null;
                _front.Source = null;
                _back.Source = null;
                _hover.Visibility = _selection.Visibility = Visibility.Collapsed;
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            _scaleX = dpi.DpiScaleX;
            _scaleY = dpi.DpiScaleY;
            var width = (int)Math.Round(ActualWidth * _scaleX);
            var height = (int)Math.Round(ActualHeight * _scaleY);

            var zoom = _zoom;
            var x = _offsetX;
            var y = _offsetY;
            var viewport = new TreemapViewport(zoom, x * _scaleX, y * _scaleY);
            var sameView = Math.Abs(zoom - _shownZoom) < 1e-6 && Math.Abs(x - _shownX) < 0.01 && Math.Abs(y - _shownY) < 0.01;

            var style = new TreemapStyle { Background = BackgroundColor() };
            var cancellation = _rendering = new CancellationTokenSource();

            TreemapImage image;
            try
            {
                image = await Task.Run(() => TreemapRenderer.Render(root, tree, width, height, viewport, style, cancellation.Token), cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cancellation.IsCancellationRequested) return;

            var bitmap = new WriteableBitmap(image.Width, image.Height, 96 * _scaleX, 96 * _scaleY, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), image.Pixels, image.Width * 4, 0);
            bitmap.Freeze();

            _shownZoom = zoom;
            _shownX = x;
            _shownY = y;
            ApplyStretch();

            if (sameView && Motion.Enabled)
            {
                // Fondu enchaîné : l'ancienne carte reste dessous le temps que la nouvelle apparaisse.
                _back.Source = _front.Source;
                _front.Source = bitmap;
                _front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = Ease });
            }
            else
            {
                // Pendant un zoom, l'image nette remplace l'image étirée sans fondu : elle est au même endroit.
                _front.BeginAnimation(OpacityProperty, null);
                _back.Source = null;
                _front.Source = bitmap;
            }

            _image = image;
            _hover.Visibility = Visibility.Collapsed;
            PlaceSelection(animate: false);
            SetPending(false);
        }

        private int BackgroundColor()
        {
            if (TryFindResource("Brush.Ground") is SolidColorBrush brush)
                return (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
            return new TreemapStyle().Background;
        }

        // ------------------------------------------------------------------ sélection et survol

        private void PlaceSelection(bool animate)
        {
            if (_image == null || Selected == null || !_image.TryGetBounds(Selected, out var item))
            {
                _selection.Visibility = Visibility.Collapsed;
                return;
            }

            var target = ToScreen(item.Bounds, inflate: 1);
            if (!animate || _selection.Visibility != Visibility.Visible)
            {
                StopGlide();
                Canvas.SetLeft(_selection, target.X);
                Canvas.SetTop(_selection, target.Y);
                _selection.Width = target.Width;
                _selection.Height = target.Height;
                _selection.Visibility = Visibility.Visible;
                if (Motion.Enabled) _selection.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Glide));
                return;
            }

            if (!Motion.Enabled)
            {
                StopGlide();
                Canvas.SetLeft(_selection, target.X);
                Canvas.SetTop(_selection, target.Y);
                _selection.Width = target.Width;
                _selection.Height = target.Height;
                return;
            }

            GlideTo(Canvas.LeftProperty, target.X);
            GlideTo(Canvas.TopProperty, target.Y);
            GlideTo(WidthProperty, target.Width);
            GlideTo(HeightProperty, target.Height);
        }

        private void GlideTo(DependencyProperty property, double value)
            => _selection.BeginAnimation(property, new DoubleAnimation(value, Glide) { EasingFunction = Ease }, HandoffBehavior.SnapshotAndReplace);

        private void StopGlide()
        {
            _selection.BeginAnimation(Canvas.LeftProperty, null);
            _selection.BeginAnimation(Canvas.TopProperty, null);
            _selection.BeginAnimation(WidthProperty, null);
            _selection.BeginAnimation(HeightProperty, null);
        }

        /// <summary>Un rectangle de l'image rendue, en points de l'image (avant étirement).</summary>
        private Rect ToScreen(TreemapRect bounds, double inflate)
        {
            var rect = new Rect(bounds.X / _scaleX, bounds.Y / _scaleY, bounds.Width / _scaleX, bounds.Height / _scaleY);
            rect.Inflate(inflate, inflate);
            return new Rect(rect.X, rect.Y, Math.Max(4, rect.Width), Math.Max(4, rect.Height));
        }

        private DiskNode? HitTest(Point point)
        {
            if (_image == null) return null;
            // Le point d'écran, ramené dans l'image rendue : l'étirement en cours est défait.
            var inverse = _stretch.Matrix;
            if (!inverse.HasInverse) return null;
            inverse.Invert();
            var source = inverse.Transform(point);
            return _image.HitTest(source.X * _scaleX, source.Y * _scaleY);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = e.GetPosition(this);

            if (_pressed.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var moved = point - _pressed.Value;
                if (!_panning && _zoom > 1.0001 && (Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4))
                {
                    _panning = true;
                    Cursor = Cursors.SizeAll;
                    HideTip();
                    _hover.Visibility = Visibility.Collapsed;
                }
                if (_panning)
                {
                    _offsetX = _pressedOffset.X - moved.X;
                    _offsetY = _pressedOffset.Y - moved.Y;
                    Clamp();
                    ApplyStretch();
                    Schedule();
                    return;
                }
            }

            var node = HitTest(point);
            if (!ReferenceEquals(node, Hovered))
            {
                Hovered = node;
                if (node == null || _image == null || !_image.TryGetBounds(node, out var item))
                {
                    _hover.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var rect = ToScreen(item.Bounds, inflate: 0);
                    Canvas.SetLeft(_hover, rect.X);
                    Canvas.SetTop(_hover, rect.Y);
                    _hover.Width = rect.Width;
                    _hover.Height = rect.Height;
                    _hover.Visibility = Visibility.Visible;
                }
                FillTip(node);
            }
            PlaceTip(point);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Hovered = null;
            _hover.Visibility = Visibility.Collapsed;
            HideTip();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var point = e.GetPosition(this);

            if (e.ClickCount >= 2)
            {
                // Double clic : on entre dans le dossier (celui du fichier, pour un fichier).
                var node = HitTest(point);
                var folder = node == null ? null : node.IsDirectory ? node : node.Parent;
                if (folder != null && ZoomCommand?.CanExecute(folder) == true) ZoomCommand.Execute(folder);
                _pressed = null;
                e.Handled = true;
                return;
            }

            _pressed = point;
            _pressedOffset = new Point(_offsetX, _offsetY);
            _panning = false;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_pressed.HasValue && !_panning)
            {
                var node = HitTest(e.GetPosition(this));
                if (node != null) Selected = node;
            }
            _pressed = null;
            _panning = false;
            Cursor = Cursors.Hand;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.GetPosition(this), e.Delta > 0 ? WheelStep : 1 / WheelStep);
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.Key)
            {
                case Key.Add:
                case Key.OemPlus:
                    ZoomIn();
                    e.Handled = true;
                    break;
                case Key.Subtract:
                case Key.OemMinus:
                    ZoomOut();
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                case Key.Escape when IsZoomed:
                    ResetZoom();
                    e.Handled = true;
                    break;
            }
        }

        // ------------------------------------------------------------------ bulle

        private Border BuildTip()
        {
            var panel = new StackPanel();
            foreach (var text in new[] { _tipName, _tipSize, _tipKind, _tipPath, _tipHint })
            {
                text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
                text.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Small");
                text.TextWrapping = TextWrapping.NoWrap;
                panel.Children.Add(text);
            }
            _tipName.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Body");
            _tipName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            _tipSize.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            _tipKind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            _tipPath.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
            _tipHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");

            var tip = new Border
            {
                Padding = new Thickness(12, 9, 12, 10),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Child = panel,
                Visibility = Visibility.Collapsed,
                MaxWidth = 380,
            };
            tip.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            tip.SetResourceReference(Border.BorderBrushProperty, "Brush.CardEdge");
            return tip;
        }

        private void FillTip(DiskNode? node)
        {
            if (node == null)
            {
                HideTip();
                return;
            }

            var root = Root;
            var share = root != null && root.Size > 0 ? (double)node.Size / root.Size : 0;
            _tipName.Text = node.Parent == null ? node.Name.TrimEnd('\\') : node.Name;
            _tipSize.Text = ValueFormat.Bytes(node.Size) + (share > 0 ? "  ·  " + (share * 100).ToString(share < 0.01 ? "0.0" : "0", Formats.French) + " % de la carte" : string.Empty);

            if (node.IsDirectory)
            {
                _tipKind.Text = "Dossier · " + node.Files.ToString("N0", Formats.French) + " fichier(s)";
                _tipHint.Text = "Double clic : entrer dans le dossier · molette : zoomer";
            }
            else
            {
                var stat = Tree?.ExtensionOf(node);
                var kind = stat == null ? "Fichier" : (stat.Kind.Length > 0 ? stat.Kind : "Fichier") + (stat.Extension.Length > 0 ? " " + stat.Extension : string.Empty);
                _tipKind.Text = kind + (node.LastWrite.HasValue ? " · modifié le " + node.LastWrite.Value.ToString("d MMM yyyy", Formats.French) : string.Empty);
                _tipHint.Text = "Clic : sélectionner · double clic : ouvrir son dossier";
            }
            _tipPath.Text = node.Parent?.FullPath ?? node.FullPath;
            _tip.Visibility = Visibility.Visible;
        }

        /// <summary>La bulle suit la souris, et passe de l'autre côté plutôt que de sortir de la carte.</summary>
        private void PlaceTip(Point point)
        {
            if (_tip.Visibility != Visibility.Visible) return;
            _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _tip.DesiredSize;
            var x = point.X + 16;
            var y = point.Y + 18;
            if (x + size.Width > ActualWidth - 4) x = Math.Max(4, point.X - size.Width - 12);
            if (y + size.Height > ActualHeight - 4) y = Math.Max(4, point.Y - size.Height - 12);
            Canvas.SetLeft(_tip, x);
            Canvas.SetTop(_tip, y);
        }

        private void HideTip() => _tip.Visibility = Visibility.Collapsed;
    }
}
