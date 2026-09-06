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

        public TransitionPresenter()
        {
            RenderTransform = _slide;
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
