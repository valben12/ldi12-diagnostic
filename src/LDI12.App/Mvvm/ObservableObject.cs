using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LDI12.App.Mvvm
{
    /// <summary>
    /// Socle MVVM minimal.
    /// </summary>
    /// <remarks>
    /// L'architecture prévoyait CommunityToolkit.Mvvm. Ses générateurs de source fonctionnent bien
    /// sur une bibliothèque net462, mais <b>pas</b> pendant la compilation du balisage XAML : celle-ci
    /// passe par un projet temporaire qui n'embarque pas les analyseurs, si bien que toutes les
    /// propriétés générées y sont introuvables et que le projet ne compile plus. Le contournement
    /// documenté (<c>IncludePackageReferencesDuringMarkupCompilation</c>) n'y change rien sur
    /// .NET Framework.
    /// <para>
    /// Ces quatre-vingts lignes remplacent le paquet sans rien perdre de fonctionnel, et
    /// suppriment une dépendance de plus dans l'exécutable unique distribué sur clé USB.
    /// </para>
    /// </remarks>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
            => PropertyChanged?.Invoke(this, e);

        protected void Raise([CallerMemberName] string? propertyName = null)
            => OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

        /// <summary>Affecte et notifie si la valeur change réellement.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Commande asynchrone qui se désactive pendant son exécution.
    /// </summary>
    /// <remarks>
    /// Sans cette garde, un double clic sur « Diagnostic complet » lancerait deux analyses
    /// concurrentes sur la même machine, et fausserait les mesures de charge des deux.
    /// </remarks>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object parameter) => !_isRunning && (_canExecute == null || _canExecute());

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;

            _isRunning = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute().ConfigureAwait(true);
            }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
