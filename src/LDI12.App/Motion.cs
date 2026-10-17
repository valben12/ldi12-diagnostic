using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LDI12.App
{
    /// <summary>
    /// Politique d'animation de l'application.
    /// </summary>
    /// <remarks>
    /// Deux catégories d'animation, traitées différemment :
    ///
    /// - les micro-interactions (survol d'un bouton, pastille de navigation, chevron) portent sur
    ///   quelques dizaines de pixels et restent actives partout ; elles sont décrites en XAML et
    ///   plafonnées par la fréquence d'images imposée dans <c>App.ApplyRenderingProfile</c> ;
    ///
    /// - les animations de grande surface (ouverture de la fenêtre, changement d'écran, balayage
    ///   de l'arc de score, entrée en cascade des cartes) repeignent une zone entière à chaque
    ///   image. Elles passent toutes par <see cref="Enabled"/> et disparaissent en rendu logiciel,
    ///   c'est-à-dire en bureau à distance, en machine virtuelle et sur GPU sans pilote, le parc
    ///   exact que ce logiciel doit servir.
    ///
    /// Le mode capture les désactive aussi : une image prise au milieu d'un fondu ne prouve rien.
    /// </remarks>
    public static class Motion
    {
        /// <summary>Animations de grande surface. Faux en rendu logiciel et en mode capture.</summary>
        public static bool Enabled { get; set; } = true;

        public static readonly Duration Fast = new Duration(TimeSpan.FromMilliseconds(120));
        public static readonly Duration Normal = new Duration(TimeSpan.FromMilliseconds(240));
        public static readonly Duration Sweep = new Duration(TimeSpan.FromMilliseconds(680));

        /// <summary>Décélération franche : le mouvement démarre vite et se pose, jamais l'inverse.</summary>
        public static IEasingFunction EaseOut { get; } = CreateEase(EasingMode.EaseOut, 3);

        private static IEasingFunction CreateEase(EasingMode mode, double power)
        {
            var ease = new PowerEase { EasingMode = mode, Power = power };
            ease.Freeze();
            return ease;
        }

        // ---------------------------------------------------------------- entrée en cascade

        /// <summary>
        /// Fait entrer l'élément en fondu et en léger glissement vertical, décalé selon son rang
        /// dans la liste qui le contient. Posé sur la racine d'un gabarit d'élément.
        /// </summary>
        /// <summary>
        /// Soulève l'élément de deux points au survol, et le repose en sortie.
        /// </summary>
        /// <remarks>
        /// Ce qui se clique doit le dire avant le clic. Deux points suffisent, en 140 ms : au-delà
        /// la grille paraît flotter. Une micro-interaction, active même en rendu logiciel : elle ne
        /// déplace qu'une tuile. Partage la translation posée par <see cref="EntranceProperty"/>.
        /// </remarks>
        public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached(
            "Lift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLiftChanged));

        public static bool GetLift(DependencyObject element) => (bool)element.GetValue(LiftProperty);
        public static void SetLift(DependencyObject element, bool value) => element.SetValue(LiftProperty, value);

        private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element)) return;

            element.MouseEnter -= OnLiftEnter;
            element.MouseLeave -= OnLiftLeave;
            if (e.NewValue is bool on && on)
            {
                element.MouseEnter += OnLiftEnter;
                element.MouseLeave += OnLiftLeave;
            }
        }

        private static void OnLiftEnter(object sender, MouseEventArgs e) => LiftTo((UIElement)sender, -2);

        private static void OnLiftLeave(object sender, MouseEventArgs e) => LiftTo((UIElement)sender, 0);

        private static void LiftTo(UIElement element, double y)
        {
            if (!(element.RenderTransform is TranslateTransform slide) || slide.IsFrozen)
            {
                slide = new TranslateTransform();
                element.RenderTransform = slide;
            }

            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = y,
                Duration = Fast,
                EasingFunction = EaseOut,
            });
        }

        public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
            "Entrance", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnEntranceChanged));

        public static bool GetEntrance(DependencyObject element) => (bool)element.GetValue(EntranceProperty);
        public static void SetEntrance(DependencyObject element, bool value) => element.SetValue(EntranceProperty, value);

        private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element)) return;

            element.Loaded -= OnEntranceLoaded;
            if (e.NewValue is bool on && on) element.Loaded += OnEntranceLoaded;
        }

        private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;

            if (!Enabled)
            {
                element.Opacity = 1;
                return;
            }

            // Le décalage est plafonné : au-delà de huit éléments, attendre une demi-seconde que
            // la liste finisse d'apparaître devient une gêne, pas un effet.
            var delay = TimeSpan.FromMilliseconds(Math.Min(IndexInParent(element), 7) * 45);

            var slide = new TranslateTransform(0, 10);
            element.RenderTransform = slide;
            element.Opacity = 0;

            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                BeginTime = delay,
                EasingFunction = EaseOut,
            });

            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 10,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                BeginTime = delay,
                EasingFunction = EaseOut,
            });
        }

        /// <summary>
        /// Rang de l'élément dans le panneau qui le contient. On remonte l'arbre visuel jusqu'au
        /// premier panneau : entre le gabarit et le panneau, le générateur d'éléments intercale un
        /// <c>ContentPresenter</c> dont le rang est celui que l'on cherche.
        /// </summary>
        private static int IndexInParent(DependencyObject element)
        {
            var child = element;
            var parent = VisualTreeHelper.GetParent(child);

            while (parent != null && !(parent is Panel))
            {
                child = parent;
                parent = VisualTreeHelper.GetParent(parent);
            }

            if (parent is Panel panel)
                for (var i = 0; i < panel.Children.Count; i++)
                    if (ReferenceEquals(panel.Children[i], child))
                        return i;

            return 0;
        }

        // ---------------------------------------------------------------- fondu simple

        /// <summary>Fondu d'apparition, sans transformation. Retourne sans rien faire si les
        /// animations de grande surface sont coupées.</summary>
        public static void FadeIn(UIElement element, double from, Duration duration)
        {
            if (element == null) return;

            if (!Enabled)
            {
                element.Opacity = 1;
                return;
            }

            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = from,
                To = 1,
                Duration = duration,
                EasingFunction = EaseOut,
            });
        }
            // ---------------------------------------------------------------- défilement doux

        /// <summary>
        /// Défilement à la molette qui glisse vers sa position au lieu d'y sauter.
        /// </summary>
        /// <remarks>
        /// WPF fait défiler par sauts secs de quarante-huit points : l'œil perd la ligne qu'il
        /// lisait à chaque cran. La page glisse désormais vers sa cible en quelques images,
        /// comme dans un navigateur. Les crans rapides s'additionnent à la cible au lieu de
        /// repartir de la position courante : la page suit la molette sans traîner.
        /// <para>
        /// Une liste intérieure qui défile elle-même (pilotes, applications) garde la main quand
        /// la souris est dessus. Les listes virtualisées, qui défilent par élément et non par
        /// point, ne sont pas concernées. En rendu logiciel, le défilement reste celui de WPF.
        /// </para>
        /// </remarks>
        public static readonly DependencyProperty SmoothScrollProperty = DependencyProperty.RegisterAttached(
            "SmoothScroll", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothScrollChanged));

        public static bool GetSmoothScroll(DependencyObject element) => (bool)element.GetValue(SmoothScrollProperty);
        public static void SetSmoothScroll(DependencyObject element, bool value) => element.SetValue(SmoothScrollProperty, value);

        /// <summary>Points parcourus par cran de molette : un peu plus que WPF, la glissade le rend lisible.</summary>
        private const double PointsPerNotch = 72;

        private static readonly Dictionary<ScrollViewer, double> Targets = new Dictionary<ScrollViewer, double>();
        private static bool _rendering;

        private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ScrollViewer viewer)) return;

            viewer.PreviewMouseWheel -= OnWheel;
            if (e.NewValue is bool on && on) viewer.PreviewMouseWheel += OnWheel;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = (ScrollViewer)sender;
            if (!Enabled || e.Handled || viewer.CanContentScroll || viewer.ScrollableHeight <= 0) return;
            if (InnerScrollerUnder(e.OriginalSource as DependencyObject, viewer)) return;

            var from = Targets.TryGetValue(viewer, out var pending) ? pending : viewer.VerticalOffset;
            var target = Math.Max(0, Math.Min(viewer.ScrollableHeight, from - e.Delta / 120.0 * PointsPerNotch));
            Targets[viewer] = target;
            e.Handled = true;

            if (_rendering) return;
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            var done = new List<ScrollViewer>();
            foreach (var pair in Targets)
            {
                var viewer = pair.Key;
                var current = viewer.VerticalOffset;
                var target = Math.Min(pair.Value, viewer.ScrollableHeight);

                // Un quart du chemin restant à chaque image : rapide au départ, posé à l'arrivée.
                var next = current + (target - current) * 0.24;
                if (Math.Abs(target - next) < 0.6 || !viewer.IsLoaded)
                {
                    next = target;
                    done.Add(viewer);
                }

                viewer.ScrollToVerticalOffset(next);
            }

            foreach (var viewer in done) Targets.Remove(viewer);
            if (Targets.Count > 0) return;

            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        /// <summary>Vrai si la souris est sur une zone qui défile elle-même, à l'intérieur de celle-ci.</summary>
        private static bool InnerScrollerUnder(DependencyObject? source, ScrollViewer outer)
        {
            var node = source;
            while (node != null && !ReferenceEquals(node, outer))
            {
                if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        // ---------------------------------------------------------------- barres qui se remplissent

        /// <summary>
        /// La valeur d'une barre, atteinte en glissant plutôt qu'affichée d'un coup : la barre se
        /// remplit à l'arrivée sur l'écran et suit chaque changement.
        /// </summary>
        /// <remarks>
        /// À poser à la place de <c>Value</c>. Une barre de quelques pixels de haut : l'animation
        /// reste légère, mais suit <see cref="Enabled"/> comme les autres, puisqu'un écran en
        /// compte souvent des dizaines.
        /// </remarks>
        public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
            "Fill", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnFillChanged));

        public static double GetFill(DependencyObject element) => (double)element.GetValue(FillProperty);

        public static void SetFill(DependencyObject element, double value) => element.SetValue(FillProperty, value);

        private static void OnFillChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (!(element is System.Windows.Controls.Primitives.RangeBase bar)) return;
            var target = (double)e.NewValue;

            if (!Enabled)
            {
                bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
                bar.Value = target;
                return;
            }

            bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(target, Sweep) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
        }

        // ---------------------------------------------------------------- compteurs

        /// <summary>
        /// Un nombre qui défile jusqu'à sa valeur au lieu d'apparaître d'un coup : posé à la
        /// place de <c>Text</c> sur un TextBlock.
        /// </summary>
        public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
            "Count", typeof(int), typeof(Motion), new PropertyMetadata(0, OnCountChanged));

        /// <summary>La valeur intermédiaire, animée ; le texte en est l'arrondi.</summary>
        private static readonly DependencyProperty CountingProperty = DependencyProperty.RegisterAttached(
            "Counting", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnCountingChanged));

        public static int GetCount(DependencyObject element) => (int)element.GetValue(CountProperty);

        public static void SetCount(DependencyObject element, int value) => element.SetValue(CountProperty, value);

        private static void OnCountChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (!(element is TextBlock text)) return;
            var target = (int)e.NewValue;

            if (!Enabled)
            {
                text.BeginAnimation(CountingProperty, null);
                text.SetValue(CountingProperty, (double)target);
                text.Text = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }

            text.BeginAnimation(CountingProperty,
                new DoubleAnimation(target, Sweep) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
        }

        private static void OnCountingChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is TextBlock text)
                text.Text = ((int)Math.Round((double)e.NewValue)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
