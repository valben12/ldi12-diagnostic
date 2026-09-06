using System;

namespace LDI12.Core.Diagnostics
{
    /// <summary>
    /// Vue non générique d'un <see cref="Measured{T}"/>. Permet à la sérialisation et à
    /// l'interface de traiter n'importe quelle mesure sans connaître son type.
    /// </summary>
    public interface IMeasured
    {
        object? RawValue { get; }
        Availability Availability { get; }
        DataSource Source { get; }
        string? Reason { get; }
        DateTimeOffset CollectedAt { get; }
        bool HasValue { get; }
    }

    /// <summary>
    /// Une valeur mesurée, accompagnée de son état de disponibilité, de sa provenance
    /// et (quand elle manque) de la raison de son absence.
    /// </summary>
    /// <remarks>
    /// C'est la pierre angulaire du modèle de données. Un outil de diagnostic qui affiche
    /// « 0 °C » ou « Inconnu » quand il n'a pas su mesurer est un outil qui ment. Ici,
    /// l'absence est une donnée à part entière : l'interface l'affiche avec sa raison,
    /// et le moteur de règles refuse de conclure dessus.
    /// </remarks>
    public readonly struct Measured<T> : IMeasured
    {
        private readonly T _value;
        private readonly Availability _availability;

        private Measured(T value, Availability availability, DataSource source, string? reason, DateTimeOffset collectedAt)
        {
            _value = value;
            _availability = availability;
            Source = source;
            Reason = reason;
            CollectedAt = collectedAt;
        }

        /// <summary>État de disponibilité de la mesure.</summary>
        public Availability Availability => _availability;

        /// <summary>D'où vient la valeur.</summary>
        public DataSource Source { get; }

        /// <summary>
        /// Raison de l'indisponibilité, rédigée pour être affichée telle quelle
        /// (« SMART NVMe requiert Windows 10 1607, build actuel : 7601 »).
        /// Renseignée aussi sur une mesure partielle pour en expliquer la limite.
        /// </summary>
        public string? Reason { get; }

        /// <summary>Horodatage de la collecte.</summary>
        public DateTimeOffset CollectedAt { get; }

        /// <summary>Vrai si une valeur exploitable est présente (disponible ou partielle).</summary>
        public bool HasValue => _availability == Availability.Available || _availability == Availability.Partial;

        /// <summary>Vrai uniquement si la valeur est pleinement fiable. C'est le test qu'utilisent les règles.</summary>
        public bool IsReliable => _availability == Availability.Available;

        /// <summary>
        /// La valeur mesurée. Lève si aucune valeur n'a été obtenue : l'appelant doit tester
        /// <see cref="HasValue"/>, ou utiliser <see cref="Or"/> / <see cref="ValueOrDefault"/>.
        /// </summary>
        public T Value => HasValue
            ? _value
            : throw new InvalidOperationException(
                "Aucune valeur mesurée (" + _availability + ")" + (Reason == null ? "." : " : " + Reason));

        /// <summary>La valeur, ou la valeur par défaut du type si la mesure a échoué.</summary>
        public T? ValueOrDefault => HasValue ? _value : default;

        /// <summary>La valeur, ou <paramref name="fallback"/> si la mesure a échoué.</summary>
        public T Or(T fallback) => HasValue ? _value : fallback;

        object? IMeasured.RawValue => HasValue ? (object?)_value : null;

        /// <summary>Projette la valeur si elle existe, en conservant l'état et la provenance.</summary>
        public Measured<TResult> Map<TResult>(Func<T, TResult> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return HasValue
                ? new Measured<TResult>(selector(_value), _availability, Source, Reason, CollectedAt)
                : new Measured<TResult>(default!, _availability, Source, Reason, CollectedAt);
        }

        public override string ToString()
        {
            if (HasValue) return _value?.ToString() ?? string.Empty;
            return "[" + _availability + (Reason == null ? "" : " : " + Reason) + "]";
        }

        // Fabriques internes : l'API publique passe par la classe statique Measured.
        internal static Measured<T> Create(T value, Availability availability, DataSource source, string? reason)
            => new Measured<T>(value, availability, source, reason, DateTimeOffset.Now);

        internal static Measured<T> CreateAt(
            T value, Availability availability, DataSource source, string? reason, DateTimeOffset collectedAt)
            => new Measured<T>(value, availability, source, reason, collectedAt);
    }

    /// <summary>Fabriques de <see cref="Measured{T}"/> avec inférence de type.</summary>
    public static class Measured
    {
        /// <summary>✅ Mesure réussie et fiable.</summary>
        public static Measured<T> Ok<T>(T value, DataSource source)
            => Measured<T>.Create(value, Availability.Available, source, null);

        /// <summary>⚠️ Valeur obtenue mais incomplète ou approximative : la raison est obligatoire.</summary>
        public static Measured<T> Partial<T>(T value, DataSource source, string reason)
            => Measured<T>.Create(value, Availability.Partial, source, Require(reason));

        /// <summary>❌ Valeur impossible à obtenir sur cette machine : la raison est obligatoire.</summary>
        public static Measured<T> Missing<T>(string reason)
            => Measured<T>.Create(default!, Availability.Unavailable, DataSource.Unknown, Require(reason));

        /// <summary>❌ Valeur impossible à obtenir, avec la source qui a été tentée.</summary>
        public static Measured<T> Missing<T>(string reason, DataSource attemptedSource)
            => Measured<T>.Create(default!, Availability.Unavailable, attemptedSource, Require(reason));

        /// <summary>🔒 Valeur accessible uniquement avec des privilèges administrateur.</summary>
        public static Measured<T> NeedsElevation<T>(string what)
            => Measured<T>.Create(default!, Availability.RequiresElevation, DataSource.Unknown,
                "Privilèges administrateur requis : " + Require(what));

        /// <summary>État indéterminé, collecte non effectuée.</summary>
        public static Measured<T> NotCollected<T>()
            => Measured<T>.Create(default!, Availability.Unknown, DataSource.Unknown, "Non collecté.");

        /// <summary>
        /// Reconstruit une mesure telle qu'elle a été enregistrée. Réservé à la relecture d'un
        /// rapport JSON : c'est le seul chemin qui permet de recréer un état sans le mesurer,
        /// et il ne doit jamais être utilisé par une sonde.
        /// </summary>
        public static Measured<T> Rehydrate<T>(
            T value, Availability availability, DataSource source, string? reason, DateTimeOffset collectedAt)
            => Measured<T>.CreateAt(value, availability, source, reason, collectedAt);

        private static string Require(string reason)
            => string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("Une mesure absente ou partielle doit toujours porter sa raison.", nameof(reason))
                : reason;
    }
}
