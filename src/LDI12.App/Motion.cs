using System;
using System.Windows;
using System.Windows.Controls;
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
    }
}
