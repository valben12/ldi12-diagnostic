using System;
using System.Windows;
using System.Windows.Threading;

namespace LDI12.App
{
    /// <summary>
    /// Amène un élément à l'écran quand il devient l'objet de l'attention.
    /// </summary>
    /// <remarks>
    /// Un clic sur « Voir la réparation » ouvre l'écran des réparations : sans ceci, la carte
    /// visée pouvait être la douzième, sous le bord de la fenêtre, et le technicien devait encore
    /// la chercher. L'écran peut ne pas être construit au moment du clic : l'élément est alors
    /// amené à l'écran dès son chargement.
    /// </remarks>
    public static class Reveal
    {
        public static readonly DependencyProperty WhenProperty = DependencyProperty.RegisterAttached(
            "When", typeof(bool), typeof(Reveal), new PropertyMetadata(false, OnWhenChanged));

        public static bool GetWhen(DependencyObject element) => (bool)element.GetValue(WhenProperty);

        public static void SetWhen(DependencyObject element, bool value) => element.SetValue(WhenProperty, value);

        private static void OnWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || !(bool)e.NewValue) return;

            if (element.IsLoaded) Schedule(element);
            else element.Loaded += OnLoaded;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            element.Loaded -= OnLoaded;
            if (GetWhen(element)) Schedule(element);
        }

        /// <summary>Après la mise en page : avant, l'élément n'a ni taille ni place à montrer.</summary>
        private static void Schedule(FrameworkElement element)
            => element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                // Le haut de la carte, et un peu d'air au-dessus : son titre est ce qu'on cherche.
                var height = Math.Min(element.ActualHeight, 320);
                element.BringIntoView(new Rect(0, -40, element.ActualWidth, height + 40));
            }));
    }
}
