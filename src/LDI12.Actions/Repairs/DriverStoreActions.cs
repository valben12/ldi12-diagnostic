using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Retire du magasin de pilotes les paquets choisis sur l'écran Magasin de pilotes : les
    /// anciennes versions qu'aucun périphérique n'utilise plus.
    /// </summary>
    /// <remarks>
    /// Jamais forcé. Un paquet qu'un périphérique présent utilise est refusé par Windows lui-même,
    /// et l'écran ne le propose de toute façon pas. Le point de restauration est créé avant, par
    /// l'écran, une fois par intervention.
    /// </remarks>
    public sealed class DriverPackagesDeleteAction : IRepairAction
    {
        public const string PackagesParameter = "packages";
        public const string LabelsParameter = "labels";

        private static readonly Regex Published = new Regex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.DriverPackagesDelete,
            DisplayName = "Retirer d'anciens pilotes du magasin",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Moderate,
            Purpose = "Supprime du magasin de pilotes les versions remplacées et inutilisées, comme le nettoyage de Driver Store Explorer.",
            PlainPurpose = "De la place regagnée, et des pilotes qui ne reviennent plus par erreur.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(5),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve le magasin de pilotes aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (packages, labels) = Targets(context);
            if (packages.Count == 0) return Task.FromResult(NativeOutcome.Block("Aucun paquet désigné."));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = packages.Count + " paquet(s) vont être retirés du magasin de pilotes.",
                WillDo = labels.Take(12).Select(l => "Retirer " + l).Concat(packages.Count > 12 ? new[] { "… et " + (packages.Count - 12) + " autre(s)." } : Array.Empty<string>()).ToArray(),
                WillNotDo = new[] { "Ne touche à aucun pilote en service : Windows refuse de lui-même un paquet utilisé." },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var (packages, labels) = Targets(context);
            var refused = new List<string>();
            for (var index = 0; index < packages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress("Retrait de " + labels[index] + "…", (double)index / packages.Count));
                var result = context.Control.DeleteDriverPackage(packages[index]);
                if (!result.Succeeded) refused.Add(labels[index] + " : " + result.Message);
            }

            var removed = packages.Count - refused.Count;
            return Task.FromResult(new ActionOutcome
            {
                Status = refused.Count == 0 ? ActionStatus.Succeeded : removed > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = refused.Count == 0
                    ? removed + " paquet(s) retiré(s) du magasin."
                    : removed + " retiré(s), " + refused.Count + " refusé(s) : " + string.Join(" ; ", refused.Take(3)),
                Details = refused,
                Duration = clock.Elapsed,
            });
        }

        private static (List<string> Packages, List<string> Labels) Targets(ActionContext context)
        {
            // Rien d'autre qu'un nom publié « oemNN.inf » n'est admis : jamais un pilote de Windows.
            // Noms et libellés vont par paires : un nom écarté emporte son libellé.
            var given = (context.Parameter(PackagesParameter) ?? string.Empty).Split('|');
            var texts = (context.Parameter(LabelsParameter) ?? string.Empty).Split('|');
            var packages = new List<string>();
            var labels = new List<string>();
            for (var index = 0; index < given.Length; index++)
            {
                var package = given[index].Trim();
                if (!Published.IsMatch(package) || packages.Contains(package, StringComparer.OrdinalIgnoreCase)) continue;
                packages.Add(package);
                labels.Add(index < texts.Length && !string.IsNullOrWhiteSpace(texts[index]) ? texts[index] : package);
            }
            return (packages, labels);
        }
    }
}
