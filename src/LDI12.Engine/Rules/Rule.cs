using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Engine.Profile;

namespace LDI12.Engine.Rules
{
    public sealed class RuleDescriptor
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public DiagnosticCategory Category { get; init; }

        /// <summary>Famille de plafonnement partagée avec les règles traitant de la même cause.</summary>
        public string? Family { get; init; }
    }

    /// <summary>
    /// Résultat d'une règle : des constats, aucun constat, ou l'impossibilité de conclure.
    /// </summary>
    /// <remarks>
    /// <see cref="NotEvaluated"/> n'est pas un échec. C'est ce qui permet au score de ne jamais
    /// pénaliser la machine pour une donnée que l'outil n'a pas su lire, et de dire au technicien
    /// sur combien de contrôles repose réellement le chiffre affiché.
    /// </remarks>
    public sealed class RuleResult
    {
        public static readonly RuleResult Clean = new RuleResult { Evaluated = true };

        public bool Evaluated { get; private init; }
        public string? NotEvaluatedReason { get; private init; }
        public IReadOnlyList<Finding> Findings { get; private init; } = Array.Empty<Finding>();

        public static RuleResult NotEvaluated(string reason)
            => new RuleResult { Evaluated = false, NotEvaluatedReason = reason };

        public static RuleResult Of(params Finding[] findings)
            => new RuleResult { Evaluated = true, Findings = findings };

        public static RuleResult Of(IReadOnlyList<Finding> findings)
            => new RuleResult { Evaluated = true, Findings = findings };
    }

    /// <summary>
    /// Une règle est une <b>fonction pure</b> du diagnostic vers des constats : ni entrée/sortie,
    /// ni accès système, ni horloge.
    /// </summary>
    /// <remarks>
    /// C'est cette pureté qui rend le moteur testable sur un fichier JSON enregistré, et qui
    /// garantit que deux machines identiques produisent le même diagnostic.
    /// </remarks>
    public interface IRule
    {
        RuleDescriptor Descriptor { get; }

        RuleResult Evaluate(RuleContext context);
    }

    /// <summary>
    /// Règle déclarée par expression.
    /// </summary>
    /// <remarks>
    /// Une classe par règle donnerait soixante fichiers de trente lignes pour un gain nul : le
    /// contrat (fonction pure, identifiant stable, catégorie, famille) est le même, et chaque
    /// règle reste isolable et testable par son identifiant.
    /// </remarks>
    public sealed class Rule : IRule
    {
        private readonly Func<RuleContext, RuleResult> _evaluate;

        public Rule(
            string id, string name, DiagnosticCategory category,
            Func<RuleContext, RuleResult> evaluate, string? family = null)
        {
            Descriptor = new RuleDescriptor { Id = id, Name = name, Category = category, Family = family };
            _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        }

        public RuleDescriptor Descriptor { get; }

        public RuleResult Evaluate(RuleContext context) => _evaluate(context);
    }

    /// <summary>
    /// Tout ce qu'une règle a le droit de consulter, plus les aides à la construction des constats.
    /// </summary>
    public sealed class RuleContext
    {
        public RuleContext(SystemSnapshot snapshot, DiagnosticProfile profile, RuleDescriptor rule)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        }

        public SystemSnapshot Snapshot { get; }
        public DiagnosticProfile Profile { get; }
        public RuleDescriptor Rule { get; }

        public Thresholds T => Profile.Limits;
        public WindowsProfile Windows => Snapshot.Platform.Windows;
        public HardwareSnapshot Hardware => Snapshot.Hardware;
        public StorageSnapshot Storage => Snapshot.Storage;
        public WindowsSnapshot System => Snapshot.Windows;
        public NetworkSnapshot Network => Snapshot.Network;

        /// <summary>Vrai sur Windows 10 ou 11, où les exigences matérielles minimales diffèrent.</summary>
        public bool IsModernWindows =>
            Windows.Family == WindowsFamily.Windows10 || Windows.Family == WindowsFamily.Windows11;

        /// <summary>
        /// Construit un constat en reprenant automatiquement l'identifiant, la catégorie et la
        /// famille de la règle courante : impossible de les désynchroniser.
        /// </summary>
        public Finding Finding(
            Severity severity,
            string title,
            string technical,
            string plain,
            string? subject = null,
            IReadOnlyList<Evidence>? evidence = null,
            IReadOnlyList<string>? recommendations = null,
            ConfidenceLevel confidence = ConfidenceLevel.High)
            => new Finding
            {
                RuleId = Rule.Id,
                Subject = subject,
                Severity = severity,
                Category = Rule.Category,
                Family = Rule.Family,
                Title = title,
                TechnicalDetail = technical,
                PlainExplanation = plain,
                Evidence = evidence ?? Array.Empty<Evidence>(),
                Recommendations = recommendations ?? Array.Empty<string>(),
                Confidence = confidence,
            };

        public static IReadOnlyList<Evidence> Ev(params Evidence[] evidence) => evidence;

        public static IReadOnlyList<string> Rec(params string[] recommendations) => recommendations;
    }
}
