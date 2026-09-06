using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Footprint
{
    /// <summary>
    /// Efface ce que ce logiciel a écrit sur la machine.
    /// </summary>
    /// <remarks>
    /// <b>La seule action du logiciel qui ne touche pas à la machine du client, mais à nous.</b>
    /// Un outil qu'on emmène chez les gens doit pouvoir repartir sans rien laisser, et surtout
    /// pouvoir le montrer plutôt que l'affirmer. La prévisualisation liste chaque emplacement,
    /// sa taille, et ce qu'on perd à l'effacer ; l'exécution ne fait rien d'autre.
    /// <para>
    /// La suppression se fait fichier par fichier, par la passerelle, qui vérifie que chacun est
    /// resté celui qui a été montré. Effacer le dossier entier serait plus court et emporterait
    /// aussi ce qui aurait été écrit entre le relevé et la validation.
    /// </para>
    /// <para>
    /// Elle ne demande aucun privilège : tout ce qu'elle retire a été écrit par l'utilisateur
    /// courant, dans son propre profil.
    /// </para>
    /// </remarks>
    public sealed class RemoveFootprintAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RemoveFootprint,
            DisplayName = "Effacer les traces de ce logiciel",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Moderate,
            Purpose = "Supprime ce que ce logiciel a écrit dans le profil de l'utilisateur : journaux, " +
                      "barème ajusté, diagnostics archivés et journal d'intervention.",
            PlainPurpose = "L'outil de diagnostic retire ce qu'il a lui-même écrit sur cet ordinateur. " +
                           "Rien de ce qui appartient au client n'est touché.",
            TypicalDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromMinutes(2),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var root = FootprintScanner.DefaultRoot();
            var scanned = FootprintScanner.Scan(context.Files, root, cancellationToken);

            var present = new List<FootprintEntry>();
            long total = 0;
            var files = 0;

            foreach (var entry in scanned)
            {
                if (!entry.Item.Exists) continue;
                present.Add(entry);
                total += entry.Item.SizeBytes;
                files += entry.Item.FileCount;
            }

            if (present.Count == 0)
            {
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Ce logiciel n'a rien écrit sur cette machine.",
                    Measurements = new[] { new PreviewLine("Dossier", root) },
                });
            }

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Dossier", root),
                new PreviewLine("Total", ValueFormat.Bytes(total) + " en " + files + " fichier(s)"),
            };

            var willDo = new List<string>();
            foreach (var entry in present)
            {
                var item = entry.Item;

                measurements.Add(new PreviewLine(
                    item.Label,
                    ValueFormat.Bytes(item.SizeBytes) + " · " + item.FileCount + " fichier(s)" +
                    (item.LastWrite.HasValue
                        ? " · dernier le " + ValueFormat.Date(item.LastWrite.Value)
                        : string.Empty),
                    Weighs(item.Kind) ? PreviewLineKind.Caution : PreviewLineKind.Fact));

                willDo.Add("Supprimer « " + item.Label + " » : " + item.Consequence);
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Tout ce que ce logiciel a écrit sur cette machine va être supprimé : " +
                          ValueFormat.Bytes(total) + " en " + files + " fichier(s).",
                WillDo = willDo,
                WillNotDo = new[]
                {
                    "Ne touche à aucun fichier du client : ce qui est effacé n'appartient qu'à ce logiciel.",
                    "Ne touche pas aux rapports exportés, où qu'ils aient été enregistrés : ils sont le " +
                    "travail du technicien, pas une trace de l'outil.",
                    "Ne modifie ni la base de registre, ni les services, ni les tâches planifiées, ce " +
                    "logiciel n'y écrit rien.",
                },
                Measurements = measurements,
                Plan = present,
            });
        }

        /// <summary>
        /// Emplacements dont la suppression perd quelque chose qui ne se refait pas.
        /// </summary>
        /// <remarks>
        /// Les journaux et l'hôte extrait se recréent tout seuls ; l'historique des diagnostics et
        /// le journal d'intervention, non. La distinction figure dans la prévisualisation parce
        /// qu'elle est la seule qui puisse faire changer d'avis.
        /// </remarks>
        private static bool Weighs(FootprintKind kind)
            => kind == FootprintKind.History || kind == FootprintKind.Interventions;

        public Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not IReadOnlyList<FootprintEntry> plan)
            {
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le relevé des traces n'a pas été établi : rien n'a été supprimé.",
                });
            }

            var stopwatch = Stopwatch.StartNew();
            var details = new List<string>();
            long freed = 0;
            var deleted = 0;
            var refused = 0;

            for (var index = 0; index < plan.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = plan[index];
                progress?.Report(new ActionProgress(entry.Item.Label, (double)index / plan.Count));

                var removedHere = 0;
                var refusedHere = 0;

                foreach (var file in entry.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (context.Files.Delete(file) == FileDeletion.Deleted)
                    {
                        removedHere++;
                        freed += file.SizeBytes;
                    }
                    else
                    {
                        refusedHere++;
                    }
                }

                deleted += removedHere;
                refused += refusedHere;

                details.Add(entry.Item.Label + " : " + removedHere + " fichier(s) supprimé(s)" +
                            (refusedHere > 0
                                ? ", " + refusedHere + " conservé(s) : ouverts ou modifiés depuis le relevé."
                                : "."));
            }

            // Les dossiers vidés partent avec ; la racine reste, le journal de la session en cours
            // y étant rouvert dès la ligne suivante.
            context.Files.RemoveEmptyDirectories(FootprintScanner.DefaultRoot(), cancellationToken);

            stopwatch.Stop();

            return Task.FromResult(new ActionOutcome
            {
                Status = refused == 0 ? ActionStatus.Succeeded
                    : deleted > 0 ? ActionStatus.PartiallySucceeded
                    : ActionStatus.Failed,
                Summary = refused == 0
                    ? deleted + " fichier(s) supprimé(s), " + ValueFormat.Bytes(freed) + " libéré(s)."
                    : deleted + " fichier(s) supprimé(s), " + refused + " conservé(s) : ils étaient " +
                      "ouverts ou avaient changé depuis le relevé.",
                Details = details,
                Duration = stopwatch.Elapsed,
                FreedBytes = freed,
            });
        }
    }
}
