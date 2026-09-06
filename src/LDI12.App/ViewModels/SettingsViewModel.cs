using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Footprint;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Engine.Profile;

namespace LDI12.App.ViewModels
{
    /// <summary>Un module du catalogue, coché ou non.</summary>
    public sealed class ModuleChoiceItem : ObservableObject
    {
        private readonly Action _changed;
        private bool _isSelected = true;

        public ModuleChoiceItem(Services.ModuleChoice source, Action changed)
        {
            Source = source;
            _changed = changed;
        }

        public Services.ModuleChoice Source { get; }

        public string Name => Source.Name;

        public string CategoryLabel => Labels.Describe(Source.Category);

        /// <summary>Durée annoncée par la sonde, en secondes. Sert à estimer la sélection.</summary>
        public double Seconds => Source.EstimatedDuration.TotalSeconds;

        /// <summary>
        /// Renseigné pour les modules absents d'une analyse rapide.
        /// </summary>
        /// <remarks>
        /// Un module coché ne coûte pas la même chose selon qu'il lit le registre ou qu'il lance
        /// <c>sfc</c> : le dire évite de composer sans le savoir une sélection de quatre minutes.
        /// </remarks>
        public string? Note => Source.FullScanOnly
            ? "Lance un outil externe ou un test réseau, absent d'une analyse rapide"
            : null;

        public bool HasNote => Note != null;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (Set(ref _isSelected, value)) _changed(); }
        }

        /// <summary>Change l'état sans repasser par le rafraîchissement, pour les sélections en bloc.</summary>
        internal void SetSelected(bool selected) => Set(ref _isSelected, selected, nameof(IsSelected));
    }

    /// <summary>
    /// Un seuil à l'écran : sa valeur en cours d'édition, ce qu'il change, et sa valeur d'origine.
    /// </summary>
    /// <remarks>
    /// La saisie est validée à la frappe mais n'est jamais corrigée sous les doigts du technicien :
    /// un champ qui se réécrit tout seul pendant qu'on tape est insupportable. La valeur est
    /// ramenée dans ses bornes au moment de l'enregistrement, et l'écart est signalé avant.
    /// </remarks>
    public sealed class ThresholdItem : ObservableObject
    {
        private readonly Action _changed;
        private string _text;

        public ThresholdItem(ThresholdDescriptor descriptor, double value, Action changed)
        {
            Descriptor = descriptor;
            _changed = changed;
            _text = Format(value / Scale);
        }

        public ThresholdDescriptor Descriptor { get; }

        public string Key => Descriptor.Key;
        public string Label => Descriptor.Label;
        public string Effect => Descriptor.Effect;

        /// <summary>
        /// Facteur entre l'unité du barème et celle de la saisie.
        /// </summary>
        /// <remarks>
        /// Le barème compte les mémoires en octets ; demander à un technicien de taper
        /// 8 589 934 592 pour dire « 8 Go » serait une invitation à la faute de frappe, sur un
        /// champ dont l'erreur ne se verrait qu'au score suivant.
        /// </remarks>
        private double Scale => Descriptor.Unit == "octets" ? 1024d * 1024 * 1024 : 1d;

        public string Unit => Descriptor.Unit == "octets" ? "Go" : Descriptor.Unit;

        /// <summary>
        /// Bornes et valeur d'origine.
        /// </summary>
        /// <remarks>
        /// L'unité n'est écrite qu'une fois, à la fin de l'intervalle : les unités du catalogue
        /// sont au pluriel, et « origine : 1 secteurs » se lit mal dans un logiciel dont tout le
        /// reste est rédigé.
        /// </remarks>
        public string RangeLabel =>
            "de " + Format(Descriptor.Minimum / Scale) + " à " + Format(Descriptor.Maximum / Scale) + " " + Unit +
            ", origine : " + Format(Descriptor.Default / Scale);

        public string Text
        {
            get => _text;
            set
            {
                if (!Set(ref _text, value)) return;
                Raise(nameof(IsValid));
                Raise(nameof(IsModified));
                Raise(nameof(Problem));
                Raise(nameof(HasProblem));
                _changed();
            }
        }

        public bool IsValid => TryParse(out _);

        /// <summary>Vrai quand la valeur s'écarte du barème d'origine : l'écran le montre.</summary>
        public bool IsModified => TryParse(out var value) && Math.Abs(value * Scale - Descriptor.Default) > 0.5;

        public string? Problem
        {
            get
            {
                if (!TryParse(out var value)) return "Saisir un nombre.";

                var raw = value * Scale;
                if (raw < Descriptor.Minimum || raw > Descriptor.Maximum)
                    return "Hors des bornes : la valeur sera ramenée à " +
                           Format(ThresholdCatalog.Clamp(Key, raw) / Scale) + " " + Unit + ".";

                return null;
            }
        }

        public bool HasProblem => Problem != null;

        /// <summary>Valeur retenue à l'enregistrement, dans l'unité du barème et bornes appliquées.</summary>
        public double Value => TryParse(out var value)
            ? ThresholdCatalog.Clamp(Key, value * Scale)
            : Descriptor.Default;

        public void Reload(double value) => Text = Format(value / Scale);

        private bool TryParse(out double value)
            => double.TryParse(_text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    public sealed class ThresholdGroup
    {
        public ThresholdGroup(string title, IReadOnlyList<ThresholdItem> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }
        public IReadOnlyList<ThresholdItem> Items { get; }
    }

    /// <summary>
    /// Écran de réglages : le barème, et rien d'autre qui touche au diagnostic.
    /// </summary>
    /// <remarks>
    /// Tous les seuils du moteur y figurent, sans exception, un test le vérifie. Cacher les
    /// seuils « délicats » reviendrait à décider à la place du technicien quels chiffres il a le
    /// droit de comprendre, alors que c'est lui qui répond au client.
    /// <para>
    /// Chaque seuil porte l'effet de son déplacement plutôt que sa seule unité. « Espace libre :
    /// 15 % » n'apprend rien ; « en deçà, le volume de Windows est signalé à surveiller » se
    /// décide.
    /// </para>
    /// </remarks>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly Services.SettingsService _settings;
        private readonly ILdiLogger _logger;

        private string _status = string.Empty;
        private bool _isDirty;

        private readonly Action<bool> _applySensors;
        private readonly Action<IReadOnlyCollection<string>>? _runSelection;

        // ------------------------------------------------------------------ empreinte

        private readonly Func<Task<ActionRunner?>>? _runner;
        private readonly RemoveFootprintAction _footprintAction = new RemoveFootprintAction();
        private ActionPreview? _footprintPreview;

        private string _footprintSummary =
            "Relevé non fait : ce logiciel n'écrit que dans un dossier de votre profil.";
        private bool _isErasing;

        /// <summary>Ce que ce logiciel a écrit sur cette machine, en une ligne.</summary>
        public string FootprintSummary
        {
            get => _footprintSummary;
            private set => Set(ref _footprintSummary, value);
        }

        /// <summary>Le détail, emplacement par emplacement, tel qu'il sera supprimé.</summary>
        public ObservableCollection<string> FootprintLines { get; } = new ObservableCollection<string>();

        public bool HasFootprintPreview => _footprintPreview != null;

        public bool CanRemoveFootprint
            => _footprintPreview != null && _footprintPreview.Outcome == PreviewOutcome.Ready && !IsErasing;

        public bool IsErasing
        {
            get => _isErasing;
            private set
            {
                if (!Set(ref _isErasing, value)) return;
                MeasureFootprintCommand.RaiseCanExecuteChanged();
                RemoveFootprintCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand MeasureFootprintCommand { get; }
        public RelayCommand RemoveFootprintCommand { get; }

        /// <summary>
        /// Établit ce qui serait supprimé, sans rien supprimer.
        /// </summary>
        /// <remarks>
        /// Passe par l'action et non par le relevé direct : c'est le typage de
        /// <c>IRepairAction</c> qui garantit qu'aucune suppression ne peut avoir lieu sans que ce
        /// relevé ait été établi, et c'est lui qui consigne l'opération au journal d'intervention.
        /// </remarks>
        private async Task MeasureFootprintAsync()
        {
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (runner == null)
            {
                FootprintSummary = "Le relevé n'est pas disponible dans cet état de l'application.";
                return;
            }

            FootprintLines.Clear();
            _footprintPreview = await runner
                .PreviewAsync(_footprintAction, null, CancellationToken.None)
                .ConfigureAwait(true);

            FootprintSummary = _footprintPreview.Summary;
            FootprintLines.Add(_footprintPreview.Summary);
            foreach (var line in _footprintPreview.WillDo) FootprintLines.Add(line);
            foreach (var line in _footprintPreview.WillNotDo) FootprintLines.Add(line);

            Raise(nameof(HasFootprintPreview));
            Raise(nameof(CanRemoveFootprint));
            RemoveFootprintCommand.RaiseCanExecuteChanged();
        }

        private async Task RemoveFootprintAsync()
        {
            var preview = _footprintPreview;
            var runner = _runner == null ? null : await _runner().ConfigureAwait(true);
            if (preview == null || runner == null) return;

            IsErasing = true;
            try
            {
                var outcome = await runner
                    .ExecuteAsync(_footprintAction, preview, null, null, CancellationToken.None)
                    .ConfigureAwait(true);

                FootprintLines.Clear();
                FootprintLines.Add(outcome.Summary);
                foreach (var detail in outcome.Details) FootprintLines.Add(detail);
                FootprintSummary = outcome.Summary;
            }
            finally
            {
                // Le relevé est consommé : il décrivait un état qui n'existe plus.
                _footprintPreview = null;
                IsErasing = false;

                    Raise(nameof(HasFootprintPreview));
                Raise(nameof(CanRemoveFootprint));
            }
        }

        public SettingsViewModel(
            Services.SettingsService settings, ILdiLogger logger, Action<bool> applySensors,
            IReadOnlyList<Services.ModuleChoice>? catalogue = null,
            Action<IReadOnlyCollection<string>>? runSelection = null,
            Func<Task<ActionRunner?>>? runner = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _applySensors = applySensors ?? throw new ArgumentNullException(nameof(applySensors));
            _runSelection = runSelection;
            _runner = runner;

            MeasureFootprintCommand = new RelayCommand(() => _ = MeasureFootprintAsync(), () => !IsErasing);
            RemoveFootprintCommand = new RelayCommand(() => _ = RemoveFootprintAsync(), () => CanRemoveFootprint);

            SaveCommand = new RelayCommand(Save, () => IsDirty);
            ResetCommand = new RelayCommand(Reset);
            RevertCommand = new RelayCommand(Reload, () => IsDirty);

            SelectAllCommand = new RelayCommand(() => SelectWhere(_ => true));
            SelectNoneCommand = new RelayCommand(() => SelectWhere(_ => false));
            SelectQuickCommand = new RelayCommand(() => SelectWhere(module => !module.FullScanOnly));
            RunSelectionCommand = new RelayCommand(RunSelection, () => SelectedModuleCount > 0);

            BuildCatalogue(catalogue);
            Build();
            Describe();
        }

        /// <summary>
        /// Modules du catalogue, à cocher.
        /// </summary>
        /// <remarks>
        /// <c>RunMode.Custom</c> était déclaré depuis la phase 0 (« sélection manuelle du
        /// technicien ») et n'avait jamais rien à sélectionner. Deux besoins d'atelier le
        /// justifiaient : écarter un module qui cale sur une machine malade, et rejouer un seul
        /// domaine après une réparation sans refaire les quatre minutes du diagnostic complet.
        /// </remarks>
        public ObservableCollection<ModuleChoiceItem> Modules { get; } =
            new ObservableCollection<ModuleChoiceItem>();

        public ICommand SelectAllCommand { get; }
        public ICommand SelectNoneCommand { get; }
        public ICommand SelectQuickCommand { get; }
        public ICommand RunSelectionCommand { get; }

        public int SelectedModuleCount
        {
            get
            {
                var count = 0;
                foreach (var module in Modules) if (module.IsSelected) count++;
                return count;
            }
        }

        /// <summary>
        /// Ce que la sélection va coûter, et ce qu'elle va coûter à la note.
        /// </summary>
        /// <remarks>
        /// La seconde phrase est la raison d'être de ce résumé. Une analyse partielle produit une
        /// note calculée sur les seules règles qui ont pu conclure : elle est plus haute qu'un
        /// diagnostic complet dès qu'un domaine problématique n'a pas été regardé. La confiance
        /// du score le dit déjà en petit ; ici, on le dit avant de lancer.
        /// </remarks>
        public string SelectionSummary
        {
            get
            {
                var selected = SelectedModuleCount;
                if (selected == 0) return "Aucun module retenu : il n'y a rien à analyser.";

                var seconds = 0d;
                foreach (var module in Modules)
                    if (module.IsSelected) seconds += module.Seconds;

                var summary = selected + " module" + (selected > 1 ? "s" : string.Empty) + " sur " +
                              Modules.Count + " : environ " +
                              ValueFormat.Duration(TimeSpan.FromSeconds(Math.Max(1, Math.Round(seconds)))) + ".";

                return selected == Modules.Count
                    ? summary + " C'est l'équivalent d'un diagnostic complet."
                    : summary + " La note qui en sortira ne se compare pas à celle d'un diagnostic " +
                      "complet : elle ne portera que sur ce qui aura été regardé.";
            }
        }

        private void BuildCatalogue(IReadOnlyList<Services.ModuleChoice>? catalogue)
        {
            if (catalogue == null) return;

            foreach (var module in catalogue)
            {
                var item = new ModuleChoiceItem(module, RefreshSelection);
                Modules.Add(item);
            }
        }

        private void SelectWhere(Func<Services.ModuleChoice, bool> predicate)
        {
            foreach (var module in Modules) module.SetSelected(predicate(module.Source));
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            Raise(nameof(SelectedModuleCount));
            Raise(nameof(SelectionSummary));
            (RunSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RunSelection()
        {
            var selected = new List<string>();
            foreach (var module in Modules) if (module.IsSelected) selected.Add(module.Source.Id);

            if (selected.Count == 0) return;

            _logger.Info("Settings", "Analyse personnalisée demandée sur " + selected.Count + " module(s).");
            _runSelection?.Invoke(selected);
        }

        /// <summary>
        /// Autorise le chargement du pilote de capteurs matériels.
        /// </summary>
        /// <remarks>
        /// Le seul réglage du logiciel qui installe quelque chose sur la machine. Il est donc
        /// éteint par défaut, et l'écran dit ce qu'il engage plutôt que de se contenter d'un
        /// libellé : c'est la différence entre un choix et un piège.
        /// </remarks>
        public bool AdvancedSensors
        {
            get => _settings.AdvancedSensors;
            set
            {
                if (_settings.AdvancedSensors == value) return;

                _settings.SetAdvancedSensors(value);
                _applySensors(value);
                Raise(nameof(AdvancedSensors));

                SensorStatus = value
                    ? "Capteurs matériels autorisés. Relancez une analyse : le pilote sera chargé à ce moment-là."
                    : "Capteurs matériels désactivés. Aucun pilote ne sera chargé.";
                Raise(nameof(SensorStatus));
            }
        }

        /// <summary>
        /// Vérifier les mises à jour au démarrage.
        /// </summary>
        /// <remarks>
        /// La seule sortie réseau du logiciel en dehors des sondes réseau, et le seul réglage
        /// qui voyage avec la clé USB : un technicien qui a répondu une fois ne doit pas se voir
        /// reposer la question sur chacun des postes où il la branche.
        /// </remarks>
        public bool CheckUpdatesOnStartup
        {
            get => _settings.Updates.Consent == true;
            set
            {
                if ((_settings.Updates.Consent == true) == value) return;

                _settings.SaveUpdates(_settings.Updates.With(consent: value));
                Raise(nameof(CheckUpdatesOnStartup));

                UpdateStatus = value
                    ? "Le logiciel interrogera ldi12.fr au lancement, une fois par jour au plus."
                    : "Aucune vérification automatique. Le bouton ci-dessous reste disponible.";
                Raise(nameof(UpdateStatus));
            }
        }

        /// <summary>Emplacement du fichier qui porte ce réglage, sur la clé, ou sur la machine.</summary>
        public string UpdatesPath => _settings.UpdatesPath ?? "emplacement indéterminé";

        public string UpdateStatus { get; private set; } =
            "Rien ne part sur le réseau tant que ce réglage est éteint.";

        public string SensorStatus { get; private set; } =
            "Aucun pilote n'est chargé tant que ce réglage est éteint.";

        public ObservableCollection<ThresholdGroup> Groups { get; } = new ObservableCollection<ThresholdGroup>();

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand RevertCommand { get; }

        public string Status { get => _status; private set => Set(ref _status, value); }

        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (!Set(ref _isDirty, value)) return;
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RevertCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string ProfilePath => _settings.Path ?? "emplacement indéterminé";

        /// <summary>Averti quand l'enregistrement change : l'analyse suivante devra en tenir compte.</summary>
        public event EventHandler? ProfileChanged;

        private void Build()
        {
            Groups.Clear();

            foreach (var category in new[]
            {
                DiagnosticCategory.Storage,
                DiagnosticCategory.Hardware,
                DiagnosticCategory.Windows,
                DiagnosticCategory.Performance,
                DiagnosticCategory.Network,
                DiagnosticCategory.Security,
            })
            {
                var items = new List<ThresholdItem>();
                foreach (var descriptor in ThresholdCatalog.For(category))
                    items.Add(new ThresholdItem(
                        descriptor,
                        ThresholdCatalog.Read(_settings.Active.Limits, descriptor.Key),
                        () => IsDirty = true));

                if (items.Count > 0) Groups.Add(new ThresholdGroup(Labels.Describe(category), items));
            }
        }

        private void Describe()
        {
            if (_settings.LoadError != null)
            {
                Status = _settings.LoadError;
                return;
            }

            Status = _settings.IsCustom
                ? "Barème ajusté en vigueur. Les analyses suivantes l'utilisent."
                : "Barème d'origine. Aucun seuil n'a été modifié sur ce poste.";
        }

        private void Save()
        {
            var limits = _settings.Active.Limits;
            var changed = 0;

            foreach (var group in Groups)
                foreach (var item in group.Items)
                {
                    if (Math.Abs(ThresholdCatalog.Read(limits, item.Key) - item.Value) < 0.0001) continue;
                    limits = ThresholdCatalog.With(limits, item.Key, item.Value);
                    changed++;
                }

            if (changed == 0)
            {
                Status = "Aucune modification à enregistrer.";
                IsDirty = false;
                return;
            }

            // Le barème complet est réécrit, pas seulement les seuils : pénalités, poids et
            // plafonds voyagent ensemble, et un fichier partiel donnerait un barème incohérent.
            var profile = new DiagnosticProfile
            {
                Version = _settings.Active.Version,
                Weights = _settings.Active.Weights,
                DefaultPenalties = _settings.Active.DefaultPenalties,
                PenaltyOverrides = _settings.Active.PenaltyOverrides,
                FamilyCaps = _settings.Active.FamilyCaps,
                SeverityCaps = _settings.Active.SeverityCaps,
                GlobalCriticalCap = _settings.Active.GlobalCriticalCap,
                Limits = limits,
            };

            Status = _settings.Save(profile)
                ? changed + " seuil(s) enregistré(s). L'analyse suivante utilisera ce barème."
                : _settings.LoadError ?? "Le barème n'a pas pu être enregistré.";

            IsDirty = false;
            Reload();
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Reset()
        {
            _settings.Reset();
            Reload();
            Describe();
            _logger.Info("Settings", "Retour au barème d'origine.");
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Reload()
        {
            foreach (var group in Groups)
                foreach (var item in group.Items)
                    item.Reload(ThresholdCatalog.Read(_settings.Active.Limits, item.Key));

            IsDirty = false;
        }
    }
}
