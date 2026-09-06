using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Actions
{
    public enum ActionKind
    {
        /// <summary>Répare ou remet en état une fonction de Windows.</summary>
        Repair = 0,

        /// <summary>Libère de l'espace. Toujours précédée d'une prévisualisation de ce qui part.</summary>
        Maintenance = 1,
    }

    /// <summary>
    /// Ce que l'action engage. Sert à trier l'écran et à décider ce qu'on demande au technicien.
    /// </summary>
    public enum ActionRisk
    {
        /// <summary>Ne modifie rien : analyse, vérification, lecture.</summary>
        ReadOnly = 0,

        /// <summary>Modification sans conséquence durable, un cache qui se reconstruit seul.</summary>
        Low = 1,

        /// <summary>Modifie l'état du système, de façon réversible ou reconstructible.</summary>
        Moderate = 2,

        /// <summary>Exige un redémarrage, ou peut désorganiser une configuration tierce.</summary>
        High = 3,
    }

    public sealed class ActionRequirements
    {
        public static readonly ActionRequirements None = new ActionRequirements();

        public bool RequiresElevation { get; init; }

        /// <summary>Build minimal de Windows. Null si l'action fonctionne dès Windows 7 SP1.</summary>
        public int? MinimumBuild { get; init; }

        /// <summary>
        /// Ce qu'il faut faire à la place en deçà de <see cref="MinimumBuild"/>.
        /// </summary>
        /// <remarks>
        /// « Indisponible » sans suite laisse le technicien sans solution. Une action absente sur
        /// Windows 7 a presque toujours un équivalent manuel, et c'est le moment de le dire.
        /// </remarks>
        public string? Workaround { get; init; }
    }

    public sealed class ActionDescriptor
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public ActionKind Kind { get; init; }

        public DiagnosticCategory Category { get; init; }

        public ActionRisk Risk { get; init; }

        /// <summary>Ce que fait l'action, pour le technicien.</summary>
        public string Purpose { get; init; } = string.Empty;

        /// <summary>La même chose, dans les mots qu'on emploie devant le client.</summary>
        public string PlainPurpose { get; init; } = string.Empty;

        /// <summary>Durée typique observée. Sert à prévenir, pas à borner.</summary>
        public TimeSpan TypicalDuration { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>Délai au-delà duquel le processus est tué.</summary>
        public TimeSpan HardTimeout { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>L'effet n'est complet qu'après redémarrage. Annoncé avant, pas après.</summary>
        public bool RequiresRestart { get; init; }

        public ActionRequirements Requirements { get; init; } = ActionRequirements.None;

        public override string ToString() => Id + " (" + DisplayName + ")";
    }

    public enum ActionAvailability
    {
        /// <summary>Exécutable ici et maintenant.</summary>
        Available = 0,

        /// <summary>Exécutable, mais une invite UAC sera nécessaire.</summary>
        NeedsElevation = 1,

        /// <summary>Impossible sur cette machine. La raison est toujours renseignée.</summary>
        Unavailable = 2,
    }

    public sealed class ActionReadiness
    {
        public static readonly ActionReadiness Ready = new ActionReadiness();

        public static ActionReadiness Elevation(string what)
            => new ActionReadiness { Availability = ActionAvailability.NeedsElevation, Reason = what };

        public static ActionReadiness No(string reason, string? workaround = null)
            => new ActionReadiness { Availability = ActionAvailability.Unavailable, Reason = reason, Workaround = workaround };

        public ActionAvailability Availability { get; init; }
        public string? Reason { get; init; }
        public string? Workaround { get; init; }
    }

    public enum PreviewLineKind
    {
        /// <summary>Un relevé chiffré ou factuel.</summary>
        Fact = 0,

        /// <summary>Une conséquence qu'il faut avoir lue avant de valider.</summary>
        Caution = 1,
    }

    public sealed class PreviewLine
    {
        public PreviewLine(string label, string value, PreviewLineKind kind = PreviewLineKind.Fact)
        {
            Label = label;
            Value = value;
            Kind = kind;
        }

        public string Label { get; }
        public string Value { get; }
        public PreviewLineKind Kind { get; }
    }

    public enum PreviewOutcome
    {
        /// <summary>Prévisualisation faite, l'action peut être exécutée.</summary>
        Ready = 0,

        /// <summary>Rien à faire : la prévisualisation n'a rien trouvé à traiter.</summary>
        NothingToDo = 1,

        /// <summary>L'action ne peut pas être exécutée. <c>Blocker</c> dit pourquoi.</summary>
        Blocked = 2,
    }

    /// <summary>
    /// Ce qui sera fait, établi avant de le faire.
    /// </summary>
    /// <remarks>
    /// Une prévisualisation n'est pas une description générique de l'action : c'est le relevé de
    /// ce que <b>cette</b> machine va subir, mesuré à l'instant. C'est ce qui permet d'exiger que
    /// rien ne soit exécuté sans avoir été montré : <see cref="IRepairAction.ExecuteAsync"/>
    /// réclame la prévisualisation en paramètre, si bien que la règle est portée par le
    /// compilateur et non par la discipline de celui qui écrit l'écran.
    /// </remarks>
    public sealed class ActionPreview
    {
        public PreviewOutcome Outcome { get; init; }

        /// <summary>Une phrase : ce qui va se passer si le technicien valide.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>Ce que l'action va faire, point par point.</summary>
        public IReadOnlyList<string> WillDo { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Ce qu'elle ne touchera pas.
        /// </summary>
        /// <remarks>
        /// Aussi important que le reste : c'est la seule chose qui permet à un technicien de
        /// répondre « non, cela ne supprimera pas vos photos » sans avoir à le supposer.
        /// </remarks>
        public IReadOnlyList<string> WillNotDo { get; init; } = Array.Empty<string>();

        /// <summary>Relevés chiffrés de la machine, mesurés pendant la prévisualisation.</summary>
        public IReadOnlyList<PreviewLine> Measurements { get; init; } = Array.Empty<PreviewLine>();

        /// <summary>Renseigné si et seulement si <see cref="Outcome"/> vaut <c>Blocked</c>.</summary>
        public string? Blocker { get; init; }

        /// <summary>
        /// Ce que l'exécution devra traiter, pour un nettoyage, la liste exacte des fichiers.
        /// </summary>
        public object? Plan { get; init; }

        /// <summary>
        /// Jeton d'une prévisualisation faite par l'hôte élevé, qui en garde le plan.
        /// </summary>
        /// <remarks>
        /// La liste des fichiers d'un dossier temporaire dépasse couramment cinquante mille
        /// entrées : elle reste du côté qui l'a établie, et seul son résumé traverse le canal.
        /// </remarks>
        public string? RemoteToken { get; init; }

        public bool CanExecute => Outcome == PreviewOutcome.Ready;
    }

    public enum ActionStatus
    {
        Succeeded = 0,

        /// <summary>Une partie du travail a abouti. Le compte rendu dit laquelle.</summary>
        PartiallySucceeded = 1,

        /// <summary>Il n'y avait rien à faire. Ce n'est pas un échec.</summary>
        NothingToDo = 2,

        Failed = 3,

        Cancelled = 4,

        TimedOut = 5,

        /// <summary>Privilèges refusés ou absents.</summary>
        ElevationRequired = 6,

        /// <summary>Action impossible sur cette machine.</summary>
        Unavailable = 7,
    }

    public sealed class ActionOutcome
    {
        public ActionStatus Status { get; init; }

        /// <summary>Conclusion en français. Jamais un code de sortie brut.</summary>
        public string Summary { get; init; } = string.Empty;

        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

        /// <summary>Sortie intégrale de l'outil, repliée dans l'écran. Utile quand rien d'autre ne l'est.</summary>
        public string? RawOutput { get; init; }

        public TimeSpan Duration { get; init; }

        public bool RestartRequired { get; init; }

        /// <summary>Octets réellement libérés, pour les actions de maintenance.</summary>
        public long FreedBytes { get; init; }

        public bool Succeeded =>
            Status == ActionStatus.Succeeded || Status == ActionStatus.PartiallySucceeded ||
            Status == ActionStatus.NothingToDo;

        public static ActionOutcome Simple(ActionStatus status, string summary)
            => new ActionOutcome { Status = status, Summary = summary };
    }

    public sealed class ActionProgress
    {
        public ActionProgress(string text, double? fraction = null)
        {
            Text = text;
            Fraction = fraction;
        }

        public string Text { get; }

        /// <summary>Avancement de 0 à 1 quand il est connu. La plupart des outils ne le disent pas.</summary>
        public double? Fraction { get; }
    }
}
