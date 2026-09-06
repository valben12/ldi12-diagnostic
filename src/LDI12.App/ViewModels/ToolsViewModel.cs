using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Tools;
using LDI12.App.Mvvm;
using LDI12.Core.Logging;

namespace LDI12.App.ViewModels
{
    public sealed class ToolItem : ObservableObject
    {
        private readonly ToolsViewModel _owner;
        private readonly WindowsToolState _state;

        public ToolItem(ToolsViewModel owner, WindowsToolState state)
        {
            _owner = owner;
            _state = state;
            LaunchCommand = new RelayCommand(Launch, () => state.Available);
        }

        public string Title => _state.Tool.Title;
        public string Purpose => _state.Tool.Purpose;
        public string CategoryLabel => Labels.Describe(_state.Tool.Category);
        public string FileName => _state.Tool.FileName;

        public bool IsAvailable => _state.Available;

        /// <summary>Renseignée si et seulement si la console est absente. Jamais un grisé muet.</summary>
        public string? Reason => _state.Reason;

        public bool HasReason => Reason != null;

        /// <summary>
        /// L'avertissement d'élévation ne s'affiche que sur une console réellement ouvrable :
        /// annoncer une invite UAC sous une console indisponible se contredit tout seul.
        /// </summary>
        public bool PromptsForElevation => _state.Available && _state.Tool.PromptsForElevation;

        public ICommand LaunchCommand { get; }

        private void Launch() => _owner.Launch(_state);
    }

    /// <summary>
    /// Écran des consoles Windows.
    /// </summary>
    /// <remarks>
    /// Une console absente est expliquée, jamais simplement grisée. La distinction qui compte
    /// pour le client est celle-ci : l'édition Famille de Windows ne fournit pas gpedit.msc, et
    /// ce n'est pas un symptôme de la machine qu'on lui répare. Un outil grisé sans phrase
    /// laisse exactement l'impression inverse.
    /// </remarks>
    public sealed class ToolsViewModel : ObservableObject
    {
        private readonly ILdiLogger _logger;
        private readonly Func<Task<ActionRunner?>> _runner;

        private ActionRunner? _cached;
        private string _status = "Identification des consoles disponibles…";

        public ToolsViewModel(ILdiLogger logger, Func<Task<ActionRunner?>> runner)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public ObservableCollection<ToolItem> Items { get; } = new ObservableCollection<ToolItem>();

        public string Status { get => _status; private set => Set(ref _status, value); }

        public event EventHandler? JournalChanged;

        public async Task InitializeAsync()
        {
            var runner = _cached = await _runner().ConfigureAwait(true);
            if (runner == null)
            {
                Status = "Les consoles Windows n'ont pas pu être inventoriées.";
                return;
            }

            Items.Clear();

            var states = WindowsToolCatalog.Inspect(runner.Context.Platform, runner.Context.Files);
            var missing = 0;
            foreach (var state in states)
            {
                Items.Add(new ToolItem(this, state));
                if (!state.Available) missing++;
            }

            Status = missing == 0
                ? Items.Count + " consoles Windows, toutes présentes sur cette machine."
                : Items.Count + " consoles Windows, dont " + missing +
                  " absente(s) de cette édition : la raison est indiquée sous chacune.";
        }

        internal void Launch(WindowsToolState state)
        {
            // L'orchestrateur a déjà été obtenu par InitializeAsync : le lancement d'une console
            // est synchrone, comme le clic qui le déclenche.
            if (_cached == null) return;

            var result = _cached.LaunchTool(state);
            Status = result.Started
                ? state.Tool.Title + " a été ouvert."
                : "Ouverture impossible : " + (result.Reason ?? "cause inconnue.");

            if (!result.Started) _logger.Warn("Tools", Status);
            JournalChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
