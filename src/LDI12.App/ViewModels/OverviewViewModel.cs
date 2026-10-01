using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using LDI12.App.Mvvm;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Écran d'accueil : l'état de la machine compris en quelques secondes.
    /// </summary>
    /// <remarks>
    /// L'ordre de lecture est délibéré, score, ce qu'il faut faire, puis pourquoi. Le technicien
    /// arrive chez un client avec une question simple ; la liste exhaustive des composants ne
    /// l'intéresse qu'ensuite.
    /// </remarks>
    public sealed class OverviewViewModel : ObservableObject
    {
        private bool _hasResult;
        private int _score;
        private string _scoreText = "-";
        private ScoreBand _band = ScoreBand.Attention;
        private string _bandLabel = string.Empty;
        private string _confidenceText = string.Empty;
        private string? _capMessage;
        private bool _hasCapMessage;
        private string _findingSummary = string.Empty;
        private string? _compatibilityMessage;
        private bool _hasCompatibilityMessage;
        private string? _elevationMessage;
        private bool _hasElevationMessage;
        private string? _scopeMessage;
        private bool _hasScopeMessage;

        private bool _isScanning;
        private bool _showScore;
        private bool _showIntro = true;

        public bool HasResult
        {
            get => _hasResult;
            private set { if (Set(ref _hasResult, value)) RefreshPanels(); }
        }

        // ------------------------------------------------------------------ état d'analyse

        /// <summary>
        /// Vrai pendant une analyse. L'écran d'analyse plein cadre prend alors la main ; ce
        /// drapeau ne sert plus ici qu'à masquer les cartes de résultat et à déclencher la brève
        /// dilatation de l'anneau au retour du score.
        /// </summary>
        public bool IsScanning
        {
            get => _isScanning;
            private set { if (Set(ref _isScanning, value)) RefreshPanels(); }
        }

        /// <summary>Carte de score : résultat disponible et aucune analyse en cours.</summary>
        public bool ShowScore { get => _showScore; private set => Set(ref _showScore, value); }

        /// <summary>Carte d'accueil : ni résultat, ni analyse en cours.</summary>
        public bool ShowIntro { get => _showIntro; private set => Set(ref _showIntro, value); }

        private void RefreshPanels()
        {
            ShowScore = HasResult && !IsScanning;
            ShowIntro = !HasResult && !IsScanning;
        }

        public void BeginScan() => IsScanning = true;

        public void EndScan() => IsScanning = false;
        public int Score { get => _score; private set => Set(ref _score, value); }
        public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }
        public ScoreBand Band { get => _band; private set => Set(ref _band, value); }
        public string BandLabel { get => _bandLabel; private set => Set(ref _bandLabel, value); }
        public string ConfidenceText { get => _confidenceText; private set => Set(ref _confidenceText, value); }
        public string? CapMessage { get => _capMessage; private set => Set(ref _capMessage, value); }
        public bool HasCapMessage { get => _hasCapMessage; private set => Set(ref _hasCapMessage, value); }
        public string FindingSummary { get => _findingSummary; private set => Set(ref _findingSummary, value); }

        /// <summary>Les constats par gravité, en compteurs : ils se lisent d'un regard, la phrase se lit.</summary>
        public IReadOnlyList<SeverityCount> SeverityCounts { get => _severityCounts; private set => Set(ref _severityCounts, value); }

        private IReadOnlyList<SeverityCount> _severityCounts = Array.Empty<SeverityCount>();

        /// <summary>Aucun constat : la phrase le dit, les compteurs à zéro ne suffiraient pas.</summary>
        public bool HasNoFinding { get => _hasNoFinding; private set => Set(ref _hasNoFinding, value); }

        private bool _hasNoFinding;

        public string? CompatibilityMessage { get => _compatibilityMessage; private set => Set(ref _compatibilityMessage, value); }
        public bool HasCompatibilityMessage { get => _hasCompatibilityMessage; private set => Set(ref _hasCompatibilityMessage, value); }
        public string? ElevationMessage { get => _elevationMessage; private set => Set(ref _elevationMessage, value); }
        public bool HasElevationMessage { get => _hasElevationMessage; private set => Set(ref _hasElevationMessage, value); }

        /// <summary>Renseigné après une analyse personnalisée, et seulement dans ce cas.</summary>
        public string? ScopeMessage { get => _scopeMessage; private set => Set(ref _scopeMessage, value); }

        public bool HasScopeMessage { get => _hasScopeMessage; private set => Set(ref _hasScopeMessage, value); }

        public ObservableCollection<DimensionItem> Dimensions { get; } = new ObservableCollection<DimensionItem>();
        public ObservableCollection<CorrelationItem> Correlations { get; } = new ObservableCollection<CorrelationItem>();
        public ObservableCollection<RecommendationItem> Priorities { get; } = new ObservableCollection<RecommendationItem>();

        public void Update(SystemSnapshot snapshot)
        {
            Dimensions.Clear();
            Correlations.Clear();
            Priorities.Clear();

            var score = snapshot.Score;
            HasResult = score != null;
            if (score == null) return;

            Score = score.Global;
            ScoreText = score.Global.ToString(CultureInfo.InvariantCulture);
            Band = score.Band;
            BandLabel = DiagnosticScore.BandLabel(score.Band);
            ConfidenceText = "Confiance " + Labels.Describe(score.Confidence) + " · barème v" + score.ProfileVersion;
            CapMessage = score.CapApplied;
            HasCapMessage = score.CapApplied != null;

            foreach (var dimension in score.Dimensions) Dimensions.Add(new DimensionItem(dimension));
            foreach (var correlation in snapshot.Correlations) Correlations.Add(new CorrelationItem(correlation));

            var rank = 1;
            foreach (var recommendation in snapshot.Recommendations)
            {
                if (rank > 5) break;
                Priorities.Add(new RecommendationItem(recommendation, rank++));
            }

            FindingSummary = BuildFindingSummary(snapshot.Findings);
            SeverityCounts = CountBySeverity(snapshot.Findings);
            HasNoFinding = snapshot.Findings.Count == 0;
            UpdateCompatibility(snapshot);
            UpdateElevation(snapshot);
            UpdateScope(snapshot);
        }

        private static IReadOnlyList<SeverityCount> CountBySeverity(IReadOnlyList<Finding> findings)
        {
            int critical = 0, problem = 0, warning = 0, info = 0;
            foreach (var finding in findings)
            {
                switch (finding.Severity)
                {
                    case Severity.Critical: critical++; break;
                    case Severity.Problem: problem++; break;
                    case Severity.Warning: warning++; break;
                    default: info++; break;
                }
            }

            return new[]
            {
                new SeverityCount(Severity.Critical, critical, critical > 1 ? "critiques" : "critique"),
                new SeverityCount(Severity.Problem, problem, problem > 1 ? "problèmes" : "problème"),
                new SeverityCount(Severity.Warning, warning, "à surveiller"),
                new SeverityCount(Severity.Info, info, info > 1 ? "informations" : "information"),
            };
        }

        private static string BuildFindingSummary(IReadOnlyList<Finding> findings)
        {
            if (findings.Count == 0) return "Aucun constat : tous les contrôles évalués sont conformes.";

            int critical = 0, problem = 0, warning = 0, info = 0;
            foreach (var finding in findings)
            {
                switch (finding.Severity)
                {
                    case Severity.Critical: critical++; break;
                    case Severity.Problem: problem++; break;
                    case Severity.Warning: warning++; break;
                    default: info++; break;
                }
            }

            var parts = new List<string>();
            if (critical > 0) parts.Add(critical + " critique" + (critical > 1 ? "s" : ""));
            if (problem > 0) parts.Add(problem + " problème" + (problem > 1 ? "s" : ""));
            if (warning > 0) parts.Add(warning + " à surveiller");
            if (info > 0) parts.Add(info + " information" + (info > 1 ? "s" : ""));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>
        /// Bandeau de compatibilité : le technicien doit savoir tout de suite ce que cette
        /// version de Windows l'empêchera de mesurer, avant de conclure quoi que ce soit.
        /// </summary>
        private void UpdateCompatibility(SystemSnapshot snapshot)
        {
            var profile = snapshot.Platform.Windows;
            if (profile.Level == CompatibilityLevel.Full)
            {
                HasCompatibilityMessage = false;
                CompatibilityMessage = null;
                return;
            }

            var unavailable = 0;
            foreach (var feature in snapshot.Platform.Features)
                if (feature.Availability == Availability.Unavailable) unavailable++;

            CompatibilityMessage = WindowsProfile.FamilyLabel(profile.Family) + " : " + profile.LevelReason +
                (unavailable > 0
                    ? " " + unavailable + " contrôle(s) sont indisponibles sur cette version."
                    : string.Empty);
            HasCompatibilityMessage = true;
        }

        /// <summary>
        /// Ce que l'analyse n'a pas regardé, quand c'est le technicien qui l'a décidé.
        /// </summary>
        /// <remarks>
        /// Une note calculée sur une sélection est mécaniquement plus haute qu'une note complète
        /// dès qu'un domaine problématique n'a pas été regardé. La confiance du score le signale
        /// déjà en petits caractères ; ce bandeau le dit en toutes lettres, parce qu'une note de
        /// quatre-vingt-dix-huit obtenue en n'ayant rien regardé est le résultat le plus
        /// trompeur que ce logiciel puisse produire.
        /// </remarks>
        private void UpdateScope(SystemSnapshot snapshot)
        {
            if (snapshot.Metadata.RunMode != RunMode.Custom)
            {
                HasScopeMessage = false;
                ScopeMessage = null;
                return;
            }

            var skipped = 0;
            var executed = 0;
            foreach (var report in snapshot.ModuleReports)
            {
                if (report.Status == Core.Probes.ProbeStatus.Skipped) skipped++;
                else executed++;
            }

            ScopeMessage = "Analyse personnalisée : " + executed + " module(s) exécuté(s), " +
                skipped + " écarté(s) à votre demande. La note ne porte que sur ce qui a été " +
                "regardé, et ne se compare pas à celle d'un diagnostic complet.";
            HasScopeMessage = true;
        }

        /// <summary>
        /// Ce que le manque de privilèges a réellement empêché.
        /// </summary>
        /// <remarks>
        /// Les modules bloqués sont <b>nommés</b>, et non illustrés par un exemple écrit en dur.
        /// Ce message a longtemps cité la lecture SMART des disques ATA : jusqu'à ce qu'elle
        /// devienne accessible sans élévation, ce qui l'a rendu faux sans que rien ne le signale.
        /// Un bandeau qui nomme ce qu'il a sous les yeux ne peut pas se périmer.
        /// </remarks>
        private void UpdateElevation(SystemSnapshot snapshot)
        {
            var blocked = new List<string>();
            foreach (var report in snapshot.ModuleReports)
                if (report.Status == Core.Probes.ProbeStatus.ElevationRequired)
                    blocked.Add(report.DisplayName);

            if (blocked.Count == 0)
            {
                HasElevationMessage = false;
                ElevationMessage = null;
                return;
            }

            ElevationMessage = blocked.Count + " module" + (blocked.Count > 1 ? "s n'ont" : " n'a") +
                " pas pu s'exécuter faute de privilèges administrateur : " +
                string.Join(", ", blocked.ToArray()) +
                ". Relancer l'analyse en tant qu'administrateur donnerait une image complète.";
            HasElevationMessage = true;
        }
    }

    /// <summary>Un compteur de constats d'une gravité.</summary>
    public sealed class SeverityCount
    {
        public SeverityCount(Severity severity, int count, string label)
        {
            Severity = severity;
            Count = count;
            Label = label;
        }

        public Severity Severity { get; }
        public int Count { get; }
        public string Label { get; }

        /// <summary>Un compteur à zéro reste à sa place, éteint : la grille ne bouge pas d'une analyse à l'autre.</summary>
        public bool IsZero => Count == 0;
    }
}
