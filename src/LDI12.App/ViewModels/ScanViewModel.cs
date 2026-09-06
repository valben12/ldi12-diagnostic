using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Orchestration;

namespace LDI12.App.ViewModels
{
    public enum ScanModuleState
    {
        Pending = 0,
        Running = 1,
        Done = 2,
    }

    /// <summary>Un module dans le plan d'analyse, avec l'état où il en est.</summary>
    public sealed class ScanModuleItem : ObservableObject
    {
        private ScanModuleState _state;

        public ScanModuleItem(string id, string name, DiagnosticCategory category)
        {
            Id = id;
            Name = name;
            Category = category;
        }

        public string Id { get; }
        public string Name { get; }
        public DiagnosticCategory Category { get; }

        public ScanModuleState State
        {
            get => _state;
            set => Set(ref _state, value);
        }
    }

    /// <summary>
    /// Écran d'analyse : ce que l'outil est en train de regarder, pendant qu'il le regarde.
    /// </summary>
    /// <remarks>
    /// Cet écran ne se contente pas de faire patienter. Le plan complet est affiché dès le départ :
    /// le technicien voit ce que l'outil va examiner avant qu'il l'ait examiné, ce qui est aussi
    /// ce qu'il doit pouvoir dire au client qui regarde par-dessus son épaule. Les modules
    /// s'allument au fil de leur exécution réelle : rien n'est simulé, aucun état n'est inféré.
    ///
    /// C'est pour cela que le moteur signale désormais le démarrage d'un module en plus de sa fin
    /// (<see cref="DiagnosticProgress.StartedProbe"/>). Sans cela, il aurait fallu deviner ce qui
    /// travaille à partir de l'ordre du catalogue et du plafond de parallélisme : donner pour
    /// certain quelque chose qu'on infère, ce que ce logiciel s'interdit ailleurs.
    /// </remarks>
    public sealed class ScanViewModel : ObservableObject
    {
        private readonly Dictionary<string, ScanModuleItem> _byName =
            new Dictionary<string, ScanModuleItem>(StringComparer.OrdinalIgnoreCase);

        private bool _isActive;
        private bool _showPulses;
        private double _percent;
        private string _percentText = "0 %";
        private string _title = string.Empty;
        private string _machine = string.Empty;
        private string _currentModule = string.Empty;
        private string _caption = string.Empty;
        private int _doneCount;

        public bool IsActive
        {
            get => _isActive;
            private set { if (Set(ref _isActive, value)) ShowPulses = value && Motion.Enabled; }
        }

        /// <summary>
        /// Ondes concentriques derrière l'anneau. Ce sont les seules animations continues de
        /// l'application : elles s'arrêtent avec le reste en rendu logiciel, où trois cercles
        /// redessinés en boucle coûteraient plus que tout ce qu'ils apportent.
        /// </summary>
        public bool ShowPulses { get => _showPulses; private set => Set(ref _showPulses, value); }
        public double Percent { get => _percent; private set => Set(ref _percent, value); }
        public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
        public string Title { get => _title; private set => Set(ref _title, value); }
        public string Machine { get => _machine; set => Set(ref _machine, value); }
        public string CurrentModule { get => _currentModule; private set => Set(ref _currentModule, value); }
        public string Caption { get => _caption; private set => Set(ref _caption, value); }

        public ObservableCollection<ScanModuleItem> Modules { get; } = new ObservableCollection<ScanModuleItem>();

        public void Begin(RunMode mode, IReadOnlyList<(string Id, string Name, DiagnosticCategory Category)> plan)
        {
            Modules.Clear();
            _byName.Clear();
            _doneCount = 0;

            foreach (var module in plan)
            {
                var item = new ScanModuleItem(module.Id, module.Name, module.Category);
                Modules.Add(item);

                // La progression identifie les modules par leur libellé : c'est ce que le moteur
                // transmet. Deux libellés identiques seraient une erreur de catalogue, pas un cas
                // à gérer : le premier gagne, et le second resterait simplement en attente.
                if (!_byName.ContainsKey(module.Name)) _byName.Add(module.Name, item);
            }

            Percent = 0;
            PercentText = "0 %";
            Title = mode == RunMode.Quick ? "Analyse rapide" : "Diagnostic complet";
            CurrentModule = "Préparation des modules…";
            Caption = "0 module sur " + Modules.Count;
            IsActive = true;
        }

        public void Report(DiagnosticProgress report)
        {
            if (report.StartedProbe != null)
            {
                if (_byName.TryGetValue(report.StartedProbe, out var starting) &&
                    starting.State == ScanModuleState.Pending)
                    starting.State = ScanModuleState.Running;

                RefreshCurrent();
                return;
            }

            if (_byName.TryGetValue(report.CurrentProbe, out var finished) &&
                finished.State != ScanModuleState.Done)
            {
                finished.State = ScanModuleState.Done;
                _doneCount++;
            }

            RefreshCurrent();

            Percent = report.Fraction * 100d;
            PercentText = ((int)Math.Round(Percent)).ToString(CultureInfo.CurrentCulture) + " %";
            Caption = report.Completed + " module" + (report.Completed > 1 ? "s" : "") +
                      " sur " + report.Total;
        }

        /// <summary>
        /// Module annoncé sous l'anneau : le premier réellement en cours.
        /// </summary>
        /// <remarks>
        /// Afficher simplement le dernier module démarré donnait un texte faux dès qu'il
        /// s'achevait avant ses voisins : quatre modules tournent de front, et « en cours :
        /// Mémoire vive » restait affiché sous une tuile déjà verte. On relit donc l'état réel
        /// plutôt que de mémoriser un nom.
        /// </remarks>
        private void RefreshCurrent()
        {
            foreach (var module in Modules)
                if (module.State == ScanModuleState.Running)
                {
                    CurrentModule = module.Name;
                    return;
                }

            CurrentModule = _doneCount >= Modules.Count
                ? "Analyse des résultats…"
                : "Enchaînement des modules…";
        }

        public void End()
        {
            // Les modules restés en attente ne sont pas marqués terminés : une analyse annulée
            // laisse voir où elle s'est arrêtée.
            Percent = 100;
            IsActive = false;
        }
    }
}
