using System;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.App.Mvvm;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Ouvre l'écran où se fait une opération, et l'y désigne.
    /// </summary>
    /// <remarks>
    /// Le diagnostic disait quoi faire, « Rendre au fichier hosts son contenu d'origine », sans y
    /// mener : il fallait ouvrir Réparations et chercher la bonne carte parmi quinze. La coquille
    /// renseigne <see cref="Open"/> au démarrage ; chaque recommandation et chaque constat qui
    /// désigne une opération porte un bouton qui y conduit d'un clic.
    /// </remarks>
    public static class ActionNavigator
    {
        /// <summary>Ouvre l'opération désignée par son identifiant. Nul tant que la coquille n'est pas prête.</summary>
        public static Action<string>? Open { get; set; }
    }

    /// <summary>Un bouton qui mène à une opération : son libellé dit où il conduit.</summary>
    public sealed class ActionLink
    {
        public ActionLink(string actionId)
        {
            ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
            Label = LabelOf(actionId);
            OpenCommand = new RelayCommand(() => ActionNavigator.Open?.Invoke(ActionId));
        }

        public string ActionId { get; }

        public string Label { get; }

        public ICommand OpenCommand { get; }

        /// <summary>Nul si l'identifiant est vide : pas de bouton qui ne mène nulle part.</summary>
        public static ActionLink? For(string? actionId)
            => string.IsNullOrWhiteSpace(actionId) ? null : new ActionLink(actionId!);

        internal static string LabelOf(string actionId)
        {
            if (actionId.StartsWith(ActionIds.ToolPrefix, StringComparison.OrdinalIgnoreCase)) return "Ouvrir l'outil";
            if (string.Equals(actionId, ActionIds.Cleanup, StringComparison.OrdinalIgnoreCase)) return "Ouvrir le nettoyage";
            if (string.Equals(actionId, ActionIds.BackupUserData, StringComparison.OrdinalIgnoreCase)) return "Préparer la sauvegarde";
            return "Voir la réparation";
        }
    }
}
