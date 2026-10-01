using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LDI12.App.Controls
{
    /// <summary>
    /// Porte-contenu qui enchaîne les écrans par un fondu court accompagné d'un léger glissement.
    /// </summary>
    /// <remarks>
    /// Le passage d'un écran à l'autre est instantané en WPF : le contenu change d'un coup, et
    /// l'œil perd le fil de ce qu'il regardait. Un fondu de deux dixièmes de seconde suffit à
    /// rétablir la continuité, sans donner l'impression d'attendre.
    ///
    /// Aucune superposition des deux écrans : on n'anime que l'entrant. Faire coexister l'ancien
    /// et le nouveau imposerait de garder deux arbres visuels vivants, coûteux, et inutile ici.
    /// </remarks>
    public sealed class TransitionPresenter : ContentControl
    {
        private readonly TranslateTransform _slide = new TranslateTransform();

        /// <summary>Les écrans déjà construits, par modèle de vue.</summary>
        private readonly System.Collections.Generic.Dictionary<object, FrameworkElement> _views =
            new System.Collections.Generic.Dictionary<object, FrameworkElement>();

        public TransitionPresenter()
        {
            RenderTransform = _slide;
        }

        /// <summary>
        /// Le modèle de l'écran à afficher.
        /// </summary>
        /// <remarks>
        /// <b>Chaque écran n'est construit qu'une fois.</b> Lié directement au contenu, un écran
        /// était reconstruit à chaque passage : des centaines de contrôles pour celui des données,
        /// un à-coup au clic, et la page qui remontait en haut à chaque retour. Gardé, il revient
        /// tel qu'on l'a laissé, défilement compris. Les modèles de vue vivent aussi longtemps que
        /// la fenêtre : garder leurs vues ne retient rien de plus.
        /// </remarks>
        public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
            nameof(Page), typeof(object), typeof(TransitionPresenter),
            new PropertyMetadata(null, (d, e) => ((TransitionPresenter)d).Show(e.NewValue)));

        public object? Page
        {
            get => GetValue(PageProperty);
            set => SetValue(PageProperty, value);
        }

        private void Show(object? page)
        {
            if (page == null)
            {
                Content = null;
                return;
            }

            if (!_views.TryGetValue(page, out var view))
            {
                // Sans gabarit déclaré pour ce modèle, le contenu est confié tel quel à WPF.
                if (!(TryFindResource(new DataTemplateKey(page.GetType())) is DataTemplate template) ||
                    !(template.LoadContent() is FrameworkElement built))
                {
                    Content = page;
                    return;
                }

                built.DataContext = page;
                _views[page] = view = built;
            }

            Content = view;
        }

        protected override void OnContentChanged(object oldContent, object newContent)
        {
            base.OnContentChanged(oldContent, newContent);

            // Rien à enchaîner au premier affichage : l'ouverture de la fenêtre s'en charge.
            if (oldContent == null || newContent == null) return;

            if (!Motion.Enabled)
            {
                Opacity = 1;
                return;
            }

            BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(190)),
                EasingFunction = Motion.EaseOut,
            });

            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(260)),
                EasingFunction = Motion.EaseOut,
            });
        }
    }
}
