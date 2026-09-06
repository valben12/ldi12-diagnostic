using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Reports.Facts;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Écran de domaine : constats, caractéristiques relevées et modules de collecte.
    /// </summary>
    /// <remarks>
    /// Les caractéristiques ne sont pas construites ici mais dans <see cref="FactSheetBuilder"/>,
    /// partagé avec les rapports. C'est délibéré : l'écran et le rapport HTML montrent exactement
    /// les mêmes faits, et un technicien qui lit une valeur à l'écran doit la retrouver au mot
    /// près dans le document qu'il remet au client.
    /// </remarks>
    public sealed class SectionViewModel : ObservableObject
    {
        private string _summary = string.Empty;
        private bool _hasFindings;
        private bool _hasModules;
        private bool _hasGroups;
        private string _emptyMessage = "Aucune analyse effectuée pour l'instant.";

        public SectionViewModel(string title, string iconKey, DiagnosticCategory category)
        {
            Title = title;
            IconKey = iconKey;
            Category = category;
        }

        public string Title { get; }
        public string IconKey { get; }
        public DiagnosticCategory Category { get; }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public bool HasFindings { get => _hasFindings; private set => Set(ref _hasFindings, value); }
        public bool HasModules { get => _hasModules; private set => Set(ref _hasModules, value); }
        public bool HasGroups { get => _hasGroups; private set => Set(ref _hasGroups, value); }
        public string EmptyMessage { get => _emptyMessage; private set => Set(ref _emptyMessage, value); }

        public ObservableCollection<FindingItem> Findings { get; } = new ObservableCollection<FindingItem>();
        public ObservableCollection<ModuleItem> Modules { get; } = new ObservableCollection<ModuleItem>();
        public ObservableCollection<FactGroup> Groups { get; } = new ObservableCollection<FactGroup>();

        /// <summary>
        /// Les fiches sont construites une fois pour toute l'application et distribuées ici : les
        /// bâtir par écran reviendrait à reconstruire quatre fois les quatre domaines à chaque
        /// analyse, pour un résultat identique.
        /// </summary>
        public void Update(SystemSnapshot snapshot, IReadOnlyList<FactSheet> sheets)
        {
            Findings.Clear();
            Modules.Clear();
            Groups.Clear();

            foreach (var finding in snapshot.Findings)
                if (finding.Category == Category) Findings.Add(new FindingItem(finding));

            foreach (var report in snapshot.ModuleReports)
                if (report.Category == Category) Modules.Add(new ModuleItem(report));

            foreach (var sheet in sheets)
            {
                if (sheet.Category != Category) continue;
                foreach (var group in sheet.Groups) Groups.Add(group);
            }

            HasFindings = Findings.Count > 0;
            HasModules = Modules.Count > 0;
            HasGroups = Groups.Count > 0;

            Summary = Findings.Count == 0
                ? "Aucun constat dans ce domaine : les contrôles évalués sont conformes."
                : Findings.Count + " constat" + (Findings.Count > 1 ? "s" : "") + " dans ce domaine.";

            EmptyMessage = HasModules
                ? "Aucun constat dans ce domaine."
                : "Aucun module de collecte n'a encore été exécuté pour ce domaine.";
        }
    }
}
