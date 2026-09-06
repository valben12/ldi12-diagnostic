using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Logging;
using LDI12.Core.Updates;

namespace LDI12.App.ViewModels
{
    /// <summary>Ce que le bandeau de mise à jour montre à un instant donné.</summary>
    public enum UpdateBanner
    {
        /// <summary>Rien à dire : le bandeau n'existe pas.</summary>
        Hidden,

        /// <summary>La question posée une seule fois, au premier lancement.</summary>
        Asking,

        /// <summary>Une version existe, et le technicien décide.</summary>
        Offering,

        /// <summary>Une version corrige quelque chose : elle s'installe, on le dit pendant.</summary>
        Installing,

        /// <summary>La mise en place a échoué, et le bandeau dit quoi faire.</summary>
        Failed,

        /// <summary>Réponse à un « Vérifier maintenant » qui n'a rien trouvé.</summary>
        Informed,
    }

    /// <summary>
    /// Le bandeau de mise à jour, et ce qu'il déclenche.
    /// </summary>
    /// <remarks>
    /// <b>Deux comportements, et la différence est voulue.</b> Une version ordinaire est
    /// proposée : le technicien peut la prendre, la remettre à plus tard, ou l'écarter
    /// définitivement. Une version marquée obligatoire (celles qui corrigent un constat faux ou
    /// une opération qui abîme) s'installe sans demander, parce qu'un outil de diagnostic qui se
    /// trompe est pire qu'un outil absent. Dans les deux cas, ce qui se passe est visible à
    /// l'écran pendant que cela se passe.
    /// <para>
    /// Rien de tout cela ne se déclenche pendant une analyse : le remplacement de l'exécutable
    /// ferme l'application, et fermer l'application au milieu d'un diagnostic chez un client
    /// serait exactement le mauvais moment.
    /// </para>
    /// </remarks>
    public sealed class UpdateViewModel : ObservableObject
    {
        private const string Category = "Updates";

        private readonly UpdateService _service;
        private readonly ILdiLogger _logger;
        private readonly Func<bool> _isBusy;

        private UpdateBanner _state = UpdateBanner.Hidden;
        private string _title = string.Empty;
        private string _message = string.Empty;
        private double _progress;
        private bool _isMandatory;
        private UpdateOutlook? _pending;
        private IReadOnlyList<string> _highlights = Array.Empty<string>();
        private bool _deferred;

        public UpdateViewModel(UpdateService service, ILdiLogger logger, Func<bool> isBusy)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));

            AcceptCommand = new RelayCommand(() => Answer(true));
            DeclineCommand = new RelayCommand(() => Answer(false));
            InstallCommand = new AsyncRelayCommand(InstallAsync, () => State == UpdateBanner.Offering);
            LaterCommand = new RelayCommand(Dismiss);
            SkipCommand = new RelayCommand(Skip);
            NotesCommand = new RelayCommand(OpenNotes);
            CloseCommand = new RelayCommand(Dismiss);
        }

        public UpdateBanner State
        {
            get => _state;
            private set
            {
                if (!Set(ref _state, value)) return;
                Raise(nameof(IsVisible));
                Raise(nameof(ShowsChoice));
                Raise(nameof(ShowsProgress));
                Raise(nameof(ShowsConsent));

                // Sans cette ligne, le bouton reste grisé : WPF garde le résultat de
                // CanExecute évalué à la construction du bandeau, quand l'état était encore
                // Hidden. Constaté à l'écran, pas supposé.
                InstallCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsVisible => State != UpdateBanner.Hidden;

        public bool ShowsConsent => State == UpdateBanner.Asking;

        public bool ShowsChoice => State == UpdateBanner.Offering;

        public bool ShowsProgress => State == UpdateBanner.Installing;

        public string Title
        {
            get => _title;
            private set => Set(ref _title, value);
        }

        public string Message
        {
            get => _message;
            private set => Set(ref _message, value);
        }

        /// <summary>Trois points marquants au plus : ce qui tient dans un bandeau.</summary>
        public IReadOnlyList<string> Highlights
        {
            get => _highlights;
            private set { if (Set(ref _highlights, value)) Raise(nameof(HasHighlights)); }
        }

        public bool HasHighlights => _highlights.Count > 0;

        public double Progress
        {
            get => _progress;
            private set => Set(ref _progress, value);
        }

        /// <summary>Teinte d'alerte plutôt que d'information.</summary>
        public bool IsMandatory
        {
            get => _isMandatory;
            private set => Set(ref _isMandatory, value);
        }

        public RelayCommand AcceptCommand { get; }
        public RelayCommand DeclineCommand { get; }
        public AsyncRelayCommand InstallCommand { get; }
        public RelayCommand LaterCommand { get; }
        public RelayCommand SkipCommand { get; }
        public RelayCommand NotesCommand { get; }
        public RelayCommand CloseCommand { get; }

        /// <summary>Demandé quand la mise en place est prête et que l'application doit se fermer.</summary>
        public event EventHandler? QuitRequested;

        /// <summary>
        /// Au démarrage : poser la question si elle ne l'a jamais été, sinon vérifier.
        /// </summary>
        /// <remarks>
        /// Appelé une fois la fenêtre à l'écran, jamais avant : la promesse d'un affichage en
        /// moins de 800 ms ne se négocie pas contre une requête réseau.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.Info(Category, _service.NeedsConsent
                ? "Première ouverture : la question de la vérification est posée."
                : "Vérification au démarrage " + (_service.Enabled ? "activée" : "refusée") + ".");

            if (_service.NeedsConsent)
            {
                State = UpdateBanner.Asking;
                Title = "Vérifier les mises à jour au démarrage ?";
                Message = "Le logiciel interrogerait ldi12.fr au lancement pour savoir s'il existe une " +
                          "version plus récente. Rien d'autre ne quitte cette machine : ni son nom, ni " +
                          "aucun résultat d'analyse. Le diagnostic, lui, fonctionne entièrement hors ligne.";
                return;
            }

            await CheckAsync(manual: false, cancellationToken).ConfigureAwait(true);
        }

        /// <summary>Vérification demandée par le technicien, depuis les réglages.</summary>
        public Task CheckNowAsync(CancellationToken cancellationToken) => CheckAsync(true, cancellationToken);

        private async Task CheckAsync(bool manual, CancellationToken cancellationToken)
        {
            UpdateOutlook? outlook;

            try
            {
                outlook = await _service.CheckAsync(manual, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (outlook == null) return;

            switch (outlook.Decision)
            {
                case UpdateDecision.Required:
                    _pending = outlook;
                    IsMandatory = true;
                    await InstallAsync().ConfigureAwait(true);
                    return;

                case UpdateDecision.Offer:
                    _pending = outlook;
                    IsMandatory = false;
                    Highlights = outlook.Release?.Highlights ?? Array.Empty<string>();
                    Title = "Version " + outlook.Release!.Version + " disponible";
                    Message = outlook.Release.Title;
                    State = UpdateBanner.Offering;
                    return;

                default:
                    // Tout le reste (à jour, écartée, Windows trop ancien, pas de réseau) n'a
                    // d'intérêt que si quelqu'un vient de poser la question.
                    if (!manual) return;

                    Highlights = Array.Empty<string>();
                    Title = "Vérification";
                    Message = outlook.Message;
                    State = UpdateBanner.Informed;
                    return;
            }
        }

        private void Answer(bool accepted)
        {
            _service.Answer(accepted);
            State = UpdateBanner.Hidden;

            if (!accepted) return;

            // La première vérification part tout de suite : c'est ce que « oui » veut dire.
            _ = CheckAsync(manual: false, CancellationToken.None);
        }

        private async Task InstallAsync()
        {
            if (_pending == null) return;

            // Fermer l'application au milieu d'une analyse, chez un client, serait le pire
            // moment possible : la mise à jour attend la fin. Et elle l'attend vraiment :
            // Resume() la reprend quand l'analyse se termine, sans quoi ce message promettrait
            // quelque chose que personne ne ferait.
            if (_isBusy())
            {
                _deferred = true;
                Title = "Mise à jour en attente";
                Message = "Une analyse est en cours : la mise à jour sera posée dès qu'elle sera terminée.";
                State = UpdateBanner.Informed;
                return;
            }

            _deferred = false;

            State = UpdateBanner.Installing;
            Progress = 0;
            Title = IsMandatory
                ? "Mise à jour nécessaire vers la version " + _pending.Release!.Version
                : "Installation de la version " + _pending.Release!.Version;
            Message = IsMandatory
                ? "Cette version corrige un défaut qui rend la version installée peu fiable. " +
                  "Le logiciel va redémarrer tout seul."
                : "Téléchargement en cours…";

            var progress = new Progress<double>(fraction => Progress = fraction * 100d);

            InstallOutcome outcome;
            try
            {
                outcome = await _service.InstallAsync(_pending, progress, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(Category, "Installation interrompue.", ex);
                outcome = InstallOutcome.Failed("L'installation s'est interrompue : " + ex.Message);
            }

            if (outcome.ShouldQuit)
            {
                Message = "Le logiciel redémarre…";
                QuitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Title = outcome.Tampered
                ? "Le fichier reçu n'est pas le bon"
                : "La mise à jour n'a pas pu être posée";
            Message = outcome.Message;
            State = UpdateBanner.Failed;
        }

        /// <summary>
        /// Reprend une installation que l'analyse en cours avait fait attendre.
        /// </summary>
        /// <remarks>
        /// Appelé à la fin de chaque analyse. Sans lui, le message « la mise à jour sera posée
        /// dès qu'elle sera terminée » serait une promesse que rien ne tient : le pire défaut
        /// possible pour un logiciel dont toute la valeur tient à ce qu'on puisse le croire.
        /// </remarks>
        public void Resume()
        {
            if (!_deferred || _pending == null) return;

            _deferred = false;
            _ = InstallAsync();
        }

        private void Skip()
        {
            if (_pending?.Release != null) _service.Skip(_pending.Release.Version);
            Dismiss();
        }

        private void Dismiss()
        {
            State = UpdateBanner.Hidden;
            Highlights = Array.Empty<string>();
        }

        private void OpenNotes()
        {
            var url = _pending?.Release?.NotesUrl;
            if (string.IsNullOrWhiteSpace(url)) url = LDI12.Core.Runtime.Vendor.Site;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url!)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                                       ex is InvalidOperationException)
            {
                _logger.Warn(Category, "La page des nouveautés n'a pas pu être ouverte.", ex);
                Clipboard.SetText(url);
                Message = "Le navigateur n'a pas pu être ouvert. L'adresse a été copiée : " + url;
            }
        }
    }
}
