using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using LDI12.App.Mvvm;

namespace LDI12.App.ViewModels
{
    /// <summary>Un résultat de la recherche : un écran, une réparation, un outil, un geste.</summary>
    public sealed class SearchEntry
    {
        public SearchEntry(string title, string kind, string detail, Action open, string? keywords = null)
        {
            Title = title;
            Kind = kind;
            Detail = detail;
            Open = open ?? throw new ArgumentNullException(nameof(open));
            Haystack = SearchViewModel.Normalize(title + " " + detail + " " + kind + " " + (keywords ?? string.Empty));
            NormalizedTitle = SearchViewModel.Normalize(title);
        }

        public string Title { get; }

        /// <summary>« Écran », « Réparation », « Outil », « Données ».</summary>
        public string Kind { get; }

        public string Detail { get; }

        internal Action Open { get; }

        internal string Haystack { get; }

        internal string NormalizedTitle { get; }
    }

    /// <summary>
    /// La recherche universelle : Ctrl+K, quelques lettres, Entrée.
    /// </summary>
    /// <remarks>
    /// Le logiciel compte dix-sept écrans, une quinzaine de réparations et vingt-trois consoles de
    /// Windows. Tout est rangé, mais il faut savoir où : « hosts », « dns », « pilote », « sfc »
    /// trouvent maintenant l'opération sans connaître l'écran qui la porte. Les accents et la casse
    /// ne comptent pas, chaque mot tapé doit se trouver dans le titre ou la description, et un
    /// titre qui commence par ce qu'on tape passe devant.
    /// </remarks>
    public sealed class SearchViewModel : ObservableObject
    {
        private const int MaxResults = 8;

        private readonly Func<IReadOnlyList<SearchEntry>> _source;
        private IReadOnlyList<SearchEntry> _entries = Array.Empty<SearchEntry>();
        private bool _isOpen;
        private string _query = string.Empty;
        private SearchEntry? _selected;

        public SearchViewModel(Func<IReadOnlyList<SearchEntry>> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            OpenCommand = new RelayCommand(Open);
            CloseCommand = new RelayCommand(Close);
            ActivateCommand = new RelayCommand(Activate);
        }

        public ObservableCollection<SearchEntry> Results { get; } = new ObservableCollection<SearchEntry>();

        public ICommand OpenCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ActivateCommand { get; }

        public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

        public string Query
        {
            get => _query;
            set { if (Set(ref _query, value ?? string.Empty)) Filter(); }
        }

        public SearchEntry? Selected { get => _selected; set => Set(ref _selected, value); }

        public bool HasNoResult => Results.Count == 0 && _query.Trim().Length > 0;

        /// <summary>Ouvre la recherche, vide, avec les entrées du moment : un diagnostic a pu en ajouter.</summary>
        public void Open()
        {
            _entries = _source();
            _query = string.Empty;
            Raise(nameof(Query));
            Filter();
            IsOpen = true;
        }

        public void Close() => IsOpen = false;

        /// <summary>Ouvre le résultat choisi, ou le premier.</summary>
        public void Activate()
        {
            var entry = Selected ?? (Results.Count > 0 ? Results[0] : null);
            if (entry == null) return;

            Close();
            entry.Open();
        }

        /// <summary>Descend ou monte dans les résultats, sans sortir de la liste.</summary>
        public void Move(int step)
        {
            if (Results.Count == 0) return;
            var index = Selected == null ? -1 : Results.IndexOf(Selected);
            index = Math.Max(0, Math.Min(Results.Count - 1, index + step));
            Selected = Results[index];
        }

        private void Filter()
        {
            Results.Clear();
            foreach (var entry in Search(_entries, _query, MaxResults)) Results.Add(entry);
            Selected = Results.Count > 0 ? Results[0] : null;
            Raise(nameof(HasNoResult));
        }

        /// <summary>Les entrées qui contiennent chaque mot tapé, les mieux placées d'abord.</summary>
        internal static IReadOnlyList<SearchEntry> Search(IReadOnlyList<SearchEntry> entries, string query, int max)
        {
            var words = Normalize(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var scored = new List<(SearchEntry Entry, int Score, int Order)>();

            for (var order = 0; order < entries.Count; order++)
            {
                var entry = entries[order];
                if (words.Length == 0)
                {
                    // Sans saisie : les écrans, dans l'ordre de la barre, pour qu'on voie ce qu'on peut chercher.
                    if (entry.Kind == "Écran") scored.Add((entry, 0, order));
                    continue;
                }

                var all = true;
                foreach (var word in words) all &= entry.Haystack.Contains(word);
                if (!all) continue;

                var first = words[0];
                var score = entry.NormalizedTitle.StartsWith(first, StringComparison.Ordinal) ? 0
                    : entry.NormalizedTitle.Contains(" " + first) ? 1
                    : entry.NormalizedTitle.Contains(first) ? 2
                    : 3;
                scored.Add((entry, score, order));
            }

            scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Order.CompareTo(b.Order));

            var result = new List<SearchEntry>();
            foreach (var item in scored)
            {
                if (result.Count == max) break;
                result.Add(item.Entry);
            }

            return result;
        }

        /// <summary>Sans accents, en minuscules, la ponctuation remplacée par des espaces.</summary>
        internal static string Normalize(string text)
        {
            var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return builder.ToString();
        }
    }
}
