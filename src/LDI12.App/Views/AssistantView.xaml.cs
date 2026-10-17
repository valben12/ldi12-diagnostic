using System.Windows;
using System.Windows.Controls;
using LDI12.App.ViewModels;

namespace LDI12.App.Views
{
    public partial class AssistantView : UserControl
    {
        public AssistantView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is AssistantViewModel old) old.PropertyChanged -= OnModelChanged;
                if (e.NewValue is AssistantViewModel model) model.PropertyChanged += OnModelChanged;
            };
        }

        /// <summary>La clé enregistrée ou oubliée : le champ ne la garde pas.</summary>
        private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AssistantViewModel.KeyInput) && sender is AssistantViewModel model
                && model.KeyInput.Length == 0 && KeyBox.Password.Length > 0)
                KeyBox.Clear();
        }

        /// <summary>
        /// Le champ masqué ne se lie pas : WPF garde le mot de passe hors des liaisons, à raison.
        /// La saisie est relayée à la main, et le champ vidé dès que la clé est enregistrée.
        /// </summary>
        private void OnKeyChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is AssistantViewModel model) model.KeyInput = KeyBox.Password;
        }
    }
}
