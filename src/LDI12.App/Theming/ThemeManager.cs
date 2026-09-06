using System;
using System.Windows;
using Microsoft.Win32;

namespace LDI12.App.Theming
{
    public enum AppTheme
    {
        Dark = 0,
        Light = 1,
    }

    /// <summary>
    /// Bascule sombre / clair par échange de dictionnaire de jetons.
    /// </summary>
    /// <remarks>
    /// Les deux dictionnaires déclarent exactement les mêmes clés : aucune vue n'a de couleur en
    /// dur, et l'échange suffit à repeindre toute l'application. C'est ce qui permet d'ajouter un
    /// écran sans se soucier du thème.
    /// </remarks>
    public static class ThemeManager
    {
        private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static readonly Uri DarkTokens = new Uri("Themes/Tokens.Dark.xaml", UriKind.Relative);
        private static readonly Uri LightTokens = new Uri("Themes/Tokens.Light.xaml", UriKind.Relative);

        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        /// <summary>
        /// Levé après l'échange du dictionnaire. Permet à la fenêtre d'accompagner la bascule
        /// d'un fondu : sans lui, toutes les surfaces changent dans la même image.
        /// </summary>
        public static event EventHandler? Changed;

        public static void Apply(AppTheme theme)
        {
            var application = Application.Current;
            if (application == null) return;

            var dictionaries = application.Resources.MergedDictionaries;
            var replacement = new ResourceDictionary { Source = theme == AppTheme.Light ? LightTokens : DarkTokens };

            // Les jetons occupent toujours la première place : les styles suivants s'y réfèrent
            // en DynamicResource et se réévaluent tout seuls.
            if (dictionaries.Count == 0) dictionaries.Add(replacement);
            else dictionaries[0] = replacement;

            Current = theme;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

        /// <summary>
        /// Préférence système. La clé n'existe qu'à partir de Windows 10 : sur Windows 7 et 8.1,
        /// on garde le thème sombre, qui est l'identité de LDI12.
        /// </summary>
        public static AppTheme DetectSystemPreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int light) return light == 1 ? AppTheme.Light : AppTheme.Dark;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                // Préférence illisible : le thème sombre reste le défaut.
            }

            return AppTheme.Dark;
        }
    }
}
