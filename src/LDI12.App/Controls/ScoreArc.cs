using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LDI12.App.Controls
{
    /// <summary>
    /// Anneau de score, dessiné directement. Sert aussi d'indicateur pendant l'analyse.
    /// </summary>
    /// <remarks>
    /// Deux arcs et rien d'autre : la valeur et son libellé sont composés par-dessus en XAML, ce
    /// qui les laisse sous le contrôle des styles de typographie. Un arc est incomparablement plus
    /// économique qu'un dégradé ou qu'une ombre : l'élément se redessine sans coût mesurable
    /// même en rendu logiciel.
    ///
    /// <see cref="Value"/> est la valeur réelle ; <see cref="DisplayValue"/> est celle qui est
    /// tracée. La seconde rejoint la première par animation, ce qui donne le balayage de l'arc et,
    /// par la même liaison, le décompte du nombre affiché au centre. Les deux restent donc
    /// synchronisés par construction : il n'y a qu'une valeur animée, pas deux.
    ///
    /// En mode <see cref="IsScanning"/>, le même anneau porte la progression de l'analyse et une
    /// comète qui en fait le tour. La comète entre et sort progressivement aux deux extrémités du
    /// parcours (elle grandit depuis zéro au départ et se résorbe à l'arrivée) de sorte que la
    /// boucle se referme sans saut visible. C'est ce détail qui sépare une animation propre d'une
    /// animation qui « claque » une fois par seconde.
    /// </remarks>
    public sealed class ScoreArc : FrameworkElement
    {
        private const double StartAngle = 135;
        private const double SweepAngle = 270;
        private const double CometLength = 38;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(ScoreArc),
            new FrameworkPropertyMetadata(0d, OnValueChanged));

        /// <summary>Valeur effectivement tracée. Rejoint <see cref="Value"/> par animation.</summary>
        public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
            nameof(DisplayValue), typeof(double), typeof(ScoreArc),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(ScoreArc),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
            nameof(ValueBrush), typeof(Brush), typeof(ScoreArc),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(ScoreArc),
            new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

        // ------------------------------------------------------------------ mode analyse

        public static readonly DependencyProperty IsScanningProperty = DependencyProperty.Register(
            nameof(IsScanning), typeof(bool), typeof(ScoreArc),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnScanningChanged));

        public static readonly DependencyProperty ScanProgressProperty = DependencyProperty.Register(
            nameof(ScanProgress), typeof(double), typeof(ScoreArc),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ScanBrushProperty = DependencyProperty.Register(
            nameof(ScanBrush), typeof(Brush), typeof(ScoreArc),
            new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Position de la tête de comète, en degrés. Animée en boucle pendant l'analyse.</summary>
        private static readonly DependencyProperty CometHeadProperty = DependencyProperty.Register(
            "CometHead", typeof(double), typeof(ScoreArc),
            new FrameworkPropertyMetadata(StartAngle, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double DisplayValue
        {
            get => (double)GetValue(DisplayValueProperty);
            set => SetValue(DisplayValueProperty, value);
        }

        public Brush TrackBrush
        {
            get => (Brush)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public Brush ValueBrush
        {
            get => (Brush)GetValue(ValueBrushProperty);
            set => SetValue(ValueBrushProperty, value);
        }

        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        public bool IsScanning
        {
            get => (bool)GetValue(IsScanningProperty);
            set => SetValue(IsScanningProperty, value);
        }

        public double ScanProgress
        {
            get => (double)GetValue(ScanProgressProperty);
            set => SetValue(ScanProgressProperty, value);
        }

        public Brush ScanBrush
        {
            get => (Brush)GetValue(ScanBrushProperty);
            set => SetValue(ScanBrushProperty, value);
        }

        private double CometHead => (double)GetValue(CometHeadProperty);

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var arc = (ScoreArc)d;
            var target = (double)e.NewValue;

            if (!Motion.Enabled)
            {
                arc.BeginAnimation(DisplayValueProperty, null);
                arc.DisplayValue = target;
                return;
            }

            arc.BeginAnimation(DisplayValueProperty, new DoubleAnimation
            {
                From = arc.DisplayValue,
                To = target,
                Duration = Motion.Sweep,
                EasingFunction = Motion.EaseOut,
            });
        }

        private static void OnScanningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ScoreArc)d).UpdateComet((bool)e.NewValue);

        private void UpdateComet(bool scanning)
        {
            if (!scanning || !Motion.Enabled)
            {
                BeginAnimation(CometHeadProperty, null);
                return;
            }

            // La tête part avant le début du parcours et le dépasse d'une longueur de comète :
            // celle-ci grandit depuis rien, puis se résorbe, et la boucle se referme sans saut.
            BeginAnimation(CometHeadProperty, new DoubleAnimation
            {
                From = StartAngle,
                To = StartAngle + SweepAngle + CometLength,
                Duration = new Duration(TimeSpan.FromMilliseconds(1250)),
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= Thickness * 2) return;

            var radius = (size - Thickness) / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);

            var trackPen = new Pen(TrackBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawGeometry(null, trackPen, BuildArc(center, radius, StartAngle, SweepAngle));

            if (IsScanning) RenderScan(drawingContext, center, radius);
            else RenderScore(drawingContext, center, radius);
        }

        private void RenderScore(DrawingContext drawingContext, Point center, double radius)
        {
            var ratio = Math.Max(0d, Math.Min(100d, DisplayValue)) / 100d;
            if (ratio <= 0) return;

            var pen = new Pen(ValueBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawGeometry(null, pen, BuildArc(center, radius, StartAngle, SweepAngle * ratio));
        }

        private void RenderScan(DrawingContext drawingContext, Point center, double radius)
        {
            var ratio = Math.Max(0d, Math.Min(100d, ScanProgress)) / 100d;

            if (ratio > 0)
            {
                var pen = new Pen(ScanBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                drawingContext.DrawGeometry(null, pen, BuildArc(center, radius, StartAngle, SweepAngle * ratio));
            }

            var head = Math.Min(CometHead, StartAngle + SweepAngle);
            var tail = Math.Max(CometHead - CometLength, StartAngle);
            var span = head - tail;
            if (span < 0.5) return;

            var tailPoint = PointOnCircle(center, radius, tail);
            var headPoint = PointOnCircle(center, radius, head);

            // Dégradé le long de la corde tête-queue : la comète s'éteint derrière elle. Reconstruit
            // à chaque image parce que sa direction change : un objet gelé de quelques dizaines
            // d'octets, sans commune mesure avec le coût d'un nuanceur.
            var comet = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = tailPoint,
                EndPoint = headPoint,
            };
            comet.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
            comet.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 255, 255, 255), 0.55));
            comet.GradientStops.Add(new GradientStop(Color.FromArgb(0xD8, 255, 255, 255), 1));
            comet.Freeze();

            var cometPen = new Pen(comet, Thickness * 0.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawGeometry(null, cometPen, BuildArc(center, radius, tail, span));
        }

        private static Geometry BuildArc(Point center, double radius, double startAngle, double sweep)
        {
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, startAngle + sweep);

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.ArcTo(end, new Size(radius, radius), 0,
                    isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            var radians = degrees * Math.PI / 180d;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
    }
}
