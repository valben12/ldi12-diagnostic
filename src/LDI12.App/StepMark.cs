using System.Windows;

namespace LDI12.App
{
    /// <summary>
    /// Une étape franchie d'un formulaire : sa pastille numérotée se coche en vert.
    /// </summary>
    /// <remarks>
    /// Le formulaire de sauvegarde fait plusieurs écrans de haut. Remonté au milieu, le
    /// technicien voit d'un coup d'œil ce qui est réglé et ce qui reste à faire, sans relire
    /// chaque étape.
    /// </remarks>
    public static class StepMark
    {
        public static readonly DependencyProperty DoneProperty = DependencyProperty.RegisterAttached(
            "Done", typeof(bool), typeof(StepMark), new PropertyMetadata(false));

        public static bool GetDone(DependencyObject element) => (bool)element.GetValue(DoneProperty);

        public static void SetDone(DependencyObject element, bool value) => element.SetValue(DoneProperty, value);
    }
}
