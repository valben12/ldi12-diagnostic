using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LDI12.App
{
    /// <summary>
    /// Résout une clé de ressource en géométrie d'icône.
    /// </summary>
    /// <remarks>
    /// Les géométries ne dépendent pas du thème : contrairement aux brosses, elles peuvent donc
    /// passer par un convertisseur sans casser la bascule sombre / clair.
    /// </remarks>
    public sealed class IconKeyConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string key ? Application.Current?.TryFindResource(key) as Geometry : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Masque une section vide : un intertitre sans contenu est du bruit.</summary>
    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Arrondit une valeur continue en entier affichable.
    /// </summary>
    /// <remarks>
    /// Sert au décompte du score : le nombre affiché est lié à la valeur animée de l'arc, pas à
    /// la valeur finale. Une seule grandeur est animée, l'aiguille et le chiffre ne peuvent donc
    /// pas se désynchroniser.
    /// </remarks>
    public sealed class RoundedTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double number
                ? ((int)Math.Round(number, MidpointRounding.AwayFromZero)).ToString(culture)
                : "-";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Masque un élément quand le texte associé est vide : typiquement la raison
    /// d'une mesure absente, qui n'existe que sur les lignes concernées.</summary>
    public sealed class TextToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Masque un élément quand la fenêtre descend sous une largeur donnée.
    /// </summary>
    /// <remarks>
    /// Une information tronquée à « Windows… » n'apprend rien et occupe la place d'un blanc. Le
    /// cas se produit réellement : sur un écran de portable de 1366 points affiché à 125 %, la
    /// barre supérieure ne dispose plus que d'un millier de points une fois les boutons posés,
    /// et la version de Windows y disparaissait derrière une ellipse.
    /// <para>
    /// Le seuil se déclare dans le balisage, à côté de ce qu'il gouverne. Une propriété du modèle
    /// de vue l'aurait éloigné de la mise en page qui, seule, sait de combien de place elle
    /// dispose.
    /// </para>
    /// </remarks>
    public sealed class WiderThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is double width)) return Visibility.Visible;

            var threshold = 0d;
            if (parameter != null)
                double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold);

            return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Masque un élément quand la valeur booléenne est vraie.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
