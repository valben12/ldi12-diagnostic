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
using LDI12.Core.DiskUsage;

namespace LDI12.App.Controls
{
    /// <summary>
    /// La carte du disque à l'écran : rendu en arrière-plan, fondu d'une image à l'autre, cadre
    /// de sélection qui glisse vers l'élément choisi.
    /// </summary>
    /// <remarks>
    /// Le rendu se fait en pixels réels de l'écran, mise à l'échelle comprise : une carte rendue
    /// à 96 points puis agrandie à 150 % serait floue, et ses petits fichiers illisibles.
    /// Chaque nouveau rendu annule le précédent : redimensionner la fenêtre n'empile pas les calculs.
    /// </remarks>
    public sealed class TreemapView : Grid
    {
        public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
            nameof(Root), typeof(DiskNode), typeof(TreemapView), new PropertyMetadata(null, (d, _) => ((TreemapView)d).Schedule()));

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

        private static readonly Duration Glide = new Duration(TimeSpan.FromMilliseconds(170));
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        private readonly Image _back = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        private readonly Image _front = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        private readonly Border _hover;
        private readonly Border _selection;
        private readonly DispatcherTimer _debounce;
        private CancellationTokenSource? _rendering;
        private TreemapImage? _image;
        private double _scaleX = 1, _scaleY = 1;

        public TreemapView()
        {
            ClipToBounds = true;
            Background = Brushes.Transparent;
            RenderOptions.SetBitmapScalingMode(_back, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(_front, BitmapScalingMode.NearestNeighbor);

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

            Children.Add(_back);
            Children.Add(_front);
            Children.Add(_overlay);

            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
            _debounce.Tick += (_, __) =>
            {
                _debounce.Stop();
                Render();
            };

            SizeChanged += (_, __) => Schedule();
            Loaded += (_, __) =>
            {
                ThemeManager.Changed += OnThemeChanged;
                Schedule();
            };
            Unloaded += (_, __) => ThemeManager.Changed -= OnThemeChanged;
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

        /// <summary>La couleur de chaque rang de type de fichier : la légende la reprend.</summary>
        public static Color PaletteColor(int rank)
        {
            var style = new TreemapStyle();
            var rgb = rank >= 0 && rank < style.Palette.Length ? style.Palette[rank] : style.Other;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private void OnThemeChanged(object? sender, EventArgs e) => Schedule();

        private void Schedule()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private async void Render()
        {
            _rendering?.Cancel();
            var root = Root;
            var tree = Tree;
            if (root == null || ActualWidth < 4 || ActualHeight < 4)
            {
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

            var style = new TreemapStyle { Background = BackgroundColor() };
            var cancellation = _rendering = new CancellationTokenSource();

            TreemapImage image;
            try
            {
                image = await Task.Run(() => TreemapRenderer.Render(root, tree, width, height, style, cancellation.Token), cancellation.Token)
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

            // Fondu enchaîné : l'ancienne carte reste dessous le temps que la nouvelle apparaisse.
            _back.Source = _front.Source;
            _front.Source = bitmap;
            if (Motion.Enabled)
                _front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = Ease });
            else
                _front.BeginAnimation(OpacityProperty, null);

            _image = image;
            _hover.Visibility = Visibility.Collapsed;
            PlaceSelection(animate: false);
        }

        private int BackgroundColor()
        {
            if (TryFindResource("Brush.Ground") is SolidColorBrush brush)
                return (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
            return new TreemapStyle().Background;
        }

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

        private Rect ToScreen(TreemapRect bounds, double inflate)
        {
            var rect = new Rect(bounds.X / _scaleX, bounds.Y / _scaleY, bounds.Width / _scaleX, bounds.Height / _scaleY);
            rect.Inflate(inflate, inflate);
            return new Rect(rect.X, rect.Y, Math.Max(4, rect.Width), Math.Max(4, rect.Height));
        }

        private DiskNode? HitTest(MouseEventArgs e)
        {
            if (_image == null) return null;
            var point = e.GetPosition(this);
            return _image.HitTest(point.X * _scaleX, point.Y * _scaleY);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var node = HitTest(e);
            if (ReferenceEquals(node, Hovered)) return;
            Hovered = node;

            if (node == null || _image == null || !_image.TryGetBounds(node, out var item))
            {
                _hover.Visibility = Visibility.Collapsed;
                return;
            }

            var rect = ToScreen(item.Bounds, inflate: 0);
            Canvas.SetLeft(_hover, rect.X);
            Canvas.SetTop(_hover, rect.Y);
            _hover.Width = rect.Width;
            _hover.Height = rect.Height;
            _hover.Visibility = Visibility.Visible;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Hovered = null;
            _hover.Visibility = Visibility.Collapsed;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var node = HitTest(e);
            if (node == null) return;

            if (e.ClickCount >= 2)
            {
                // Double clic sur un fichier : on entre dans son dossier.
                var folder = node.IsDirectory ? node : node.Parent;
                if (folder != null && ZoomCommand?.CanExecute(folder) == true) ZoomCommand.Execute(folder);
            }
            else
            {
                Selected = node;
            }
            e.Handled = true;
        }
    }
}
