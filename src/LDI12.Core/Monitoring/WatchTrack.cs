using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Monitoring
{
    /// <summary>
    /// Une mesure suivie dans le temps : ses statistiques sur toute la session et ses points.
    /// </summary>
    /// <remarks>
    /// Tous les points sont conservés, et non les derniers seulement : une moyenne calculée sur
    /// une fenêtre glissante dirait autre chose que ce que le technicien croit lire, et le
    /// maximum (la valeur qui compte pour une température) disparaîtrait au bout de quelques
    /// minutes. Le coût est nul à cette échelle : une session de deux heures tient dans quelques
    /// dizaines de kilo-octets, et la surveillance s'arrête d'elle-même au-delà.
    /// <para>
    /// Un échantillon sans valeur est enregistré comme tel et n'entre dans aucune moyenne. Un
    /// capteur muet pendant la moitié de la session doit se voir, pas se diluer.
    /// </para>
    /// </remarks>
    public sealed class WatchTrack
    {
        private readonly List<double> _values = new List<double>();

        public WatchTrack(string label, string unit)
        {
            Label = label ?? string.Empty;
            Unit = unit ?? string.Empty;
            Min = double.MaxValue;
            Max = double.MinValue;
        }

        public string Label { get; }

        public string Unit { get; }

        /// <summary>Échantillons portant une valeur.</summary>
        public int Count { get; private set; }

        /// <summary>Échantillons où la mesure a manqué.</summary>
        public int Missing { get; private set; }

        /// <summary>Tours où la mesure n'a pas été demandée, une cadence, pas une lacune.</summary>
        public int Skipped { get; private set; }

        public double Sum { get; private set; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public double Last { get; private set; }

        /// <summary>Pourquoi la mesure manquait, la dernière fois qu'elle a manqué.</summary>
        public string? Reason { get; private set; }

        public bool HasData => Count > 0;

        public double Average => Count > 0 ? Sum / Count : 0d;

        /// <summary>Part des échantillons où la mesure a répondu.</summary>
        public double Coverage
        {
            get
            {
                var total = Count + Missing;
                return total > 0 ? (double)Count / total : 0d;
            }
        }

        /// <summary>
        /// La valeur n'a jamais bougé de toute la session.
        /// </summary>
        /// <remarks>
        /// Distinction utile pour la fréquence du processeur : une valeur rigoureusement
        /// constante sur des centaines d'échantillons ne décrit pas une machine stable, elle
        /// décrit une source qui ne mesure rien.
        /// </remarks>
        public bool Constant => Count > 1 && Max - Min < 0.0001d;

        /// <summary>Tous les points, dans l'ordre, <see cref="double.NaN"/> là où la mesure a manqué.</summary>
        public IReadOnlyList<double> Values => _values;

        /// <summary>
        /// Ajoute une mesure, ou constate son absence.
        /// </summary>
        /// <remarks>
        /// « Non collecté » n'est pas « manquant » : c'est la réponse d'une mesure qu'on n'a pas
        /// demandée à ce tour-ci, parce qu'elle coûte trop cher pour être relevée chaque seconde.
        /// La compter comme un trou ferait apparaître un capteur en panne là où il n'y a qu'une
        /// cadence différente. Elle est donc écartée des statistiques, sans l'être de la courbe.
        /// </remarks>
        public void Add(Measured<double> measured)
        {
            if (measured.HasValue) Add(measured.Value);
            else if (measured.Availability == Availability.Unknown) Skip();
            else AddMissing(measured.Reason);
        }

        /// <summary>
        /// Un tour où la mesure n'a pas été demandée.
        /// </summary>
        /// <remarks>
        /// Le point est tout de même inscrit, vide : les courbes de deux mesures relevées à des
        /// cadences différentes doivent rester alignées dans le temps, sans quoi la comparaison
        /// entre fréquence et température rapprocherait des instants qui n'ont rien à voir.
        /// </remarks>
        public void Skip()
        {
            Skipped++;
            _values.Add(double.NaN);
        }

        public void Add(double value)
        {
            Count++;
            Sum += value;
            Last = value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
            _values.Add(value);
        }

        public void AddMissing(string? reason)
        {
            Missing++;
            if (reason != null) Reason = reason;
            _values.Add(double.NaN);
        }

        /// <summary>Part des échantillons mesurés qui atteignent ou dépassent un seuil.</summary>
        public double ShareAtOrAbove(double threshold)
        {
            if (Count == 0) return 0d;

            var hits = 0;
            foreach (var value in _values)
                if (!double.IsNaN(value) && value >= threshold) hits++;

            return (double)hits / Count;
        }

        /// <summary>
        /// Moyenne de cette mesure sur les seuls échantillons où une autre a franchi un seuil.
        /// </summary>
        /// <remarks>
        /// C'est ce qui permet de comparer la fréquence du processeur quand il est chaud à
        /// celle qu'il tient quand il ne l'est pas : la seule façon d'établir un bridage
        /// thermique sans consulter la documentation du constructeur.
        /// </remarks>
        public double AverageWhere(WatchTrack condition, double threshold, bool atOrAbove, out int samples)
        {
            samples = 0;
            if (condition == null) return 0d;

            var sum = 0d;
            var shared = Math.Min(_values.Count, condition.Values.Count);

            for (var index = 0; index < shared; index++)
            {
                var pivot = condition.Values[index];
                var value = _values[index];
                if (double.IsNaN(pivot) || double.IsNaN(value)) continue;
                if (atOrAbove ? pivot < threshold : pivot >= threshold) continue;

                sum += value;
                samples++;
            }

            return samples > 0 ? sum / samples : 0d;
        }

        /// <summary>Les <paramref name="count"/> derniers points, pour le tracé.</summary>
        public IReadOnlyList<double> Tail(int count)
        {
            if (count <= 0 || _values.Count == 0) return Array.Empty<double>();
            if (_values.Count <= count) return _values;

            return _values.GetRange(_values.Count - count, count);
        }
    }
}
