using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Maintenance
{
    /// <summary>
    /// Nettoyage : la seule action du logiciel qui supprime des fichiers.
    /// </summary>
    /// <remarks>
    /// Deux garanties, toutes deux vérifiées par des tests :
    /// <list type="number">
    /// <item>rien n'est supprimé qui n'ait été relevé et montré : l'exécution reprend la liste de
    /// la prévisualisation, elle ne re-balaye pas les dossiers ;</item>
    /// <item>aucune source contenant des données personnelles n'est retenue d'elle-même : la
    /// corbeille doit être cochée explicitement par le technicien.</item>
    /// </list>
    /// </remarks>
    public sealed class CleanupAction : IRepairAction
    {
        /// <summary>Nombre de chemins transmis pour affichage par source. Au-delà, on annonce le total.</summary>
        public const int DisplayedFilesPerGroup = 400;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.Cleanup,
            DisplayName = "Libérer de l'espace disque",
            Kind = ActionKind.Maintenance,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Low,
            Purpose = "Relève puis supprime les fichiers temporaires et les caches sélectionnés, " +
                      "après affichage de la liste exacte.",
            PlainPurpose = "Les fichiers de travail devenus inutiles sont supprimés pour rendre de la place.",
            TypicalDuration = TimeSpan.FromMinutes(1),
            HardTimeout = TimeSpan.FromMinutes(15),
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var selected = Selected(context);
            if (selected.Count == 0)
                return ActionReadiness.No("Aucune source de nettoyage sélectionnée.");

            foreach (var provider in selected)
                if (provider.RequiresElevation && !context.Platform.IsElevated)
                    return ActionReadiness.Elevation(
                        "les dossiers temporaires de Windows ne sont lisibles qu'avec des privilèges administrateur");

            return ActionReadiness.Ready;
        }

        /// <summary>
        /// Sources retenues : celles que le technicien a cochées, sinon celles qui ne contiennent
        /// aucune donnée personnelle. La corbeille n'entre jamais dans le choix par défaut.
        /// </summary>
        internal static IReadOnlyList<CleanupProvider> Selected(ActionContext context)
            => Selected(CleanupCatalog.Create(), context.Parameter("providers"));

        internal static IReadOnlyList<CleanupProvider> Selected(
            IReadOnlyList<CleanupProvider> catalog, string? requested)
        {
            if (requested == null)
            {
                var defaults = new List<CleanupProvider>();
                foreach (var provider in catalog)
                    if (provider.SelectedByDefault) defaults.Add(provider);
                return defaults;
            }

            var wanted = new HashSet<string>(
                requested.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var kept = new List<CleanupProvider>();
            foreach (var provider in catalog)
                if (wanted.Contains(provider.Id.Trim())) kept.Add(provider);
            return kept;
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var selected = Selected(context);
            if (selected.Count == 0)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucune source de nettoyage sélectionnée.",
                    Blocker = "Cocher au moins une source avant d'analyser.",
                });

            var scanner = new CleanupScanner(context.Files, context.Logger);
            var plan = scanner.Scan(selected, context.Platform.IsElevated, cancellationToken);

            return Task.FromResult(Describe(plan));
        }

        /// <summary>
        /// Met le plan en mots. Séparé du balayage pour que l'hôte élevé et l'application
        /// produisent exactement le même texte à partir du même relevé.
        /// </summary>
        public static ActionPreview Describe(CleanupPlan plan, string? remoteToken = null)
        {
            var measurements = new List<PreviewLine>();
            var willDo = new List<string>();
            var unavailable = new List<string>();

            foreach (var group in plan.Groups)
            {
                if (group.Unavailable != null)
                {
                    unavailable.Add(group.Title + " : " + group.Unavailable);
                    continue;
                }

                measurements.Add(new PreviewLine(
                    group.Title,
                    group.ItemCount == 0
                        ? "rien à supprimer"
                        : ValueFormat.Number(group.ItemCount) + " élément(s) : " + ValueFormat.Bytes(group.Bytes)));

                if (group.ItemCount == 0) continue;

                willDo.Add(group.Title + " : " + ValueFormat.Number(group.ItemCount) + " élément(s), " +
                           ValueFormat.Bytes(group.Bytes) + ".");

                if (group.Consequence != null)
                    measurements.Add(new PreviewLine(group.Title + " : conséquence", group.Consequence,
                        PreviewLineKind.Caution));

                if (group.InaccessibleDirectories > 0)
                    measurements.Add(new PreviewLine(
                        group.Title + " : dossiers non lus",
                        ValueFormat.Number(group.InaccessibleDirectories) +
                        " dossier(s) n'ont pas pu être ouverts : leur contenu n'est ni compté ni supprimé"));

                if (group.Truncated)
                    measurements.Add(new PreviewLine(
                        group.Title + " : relevé partiel",
                        "le balayage a atteint son plafond ; seuls les fichiers relevés seront supprimés",
                        PreviewLineKind.Caution));
            }

            foreach (var line in unavailable)
                measurements.Add(new PreviewLine("Non relevé", line));

            if (plan.TotalItems == 0)
                return new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Rien à supprimer : les sources sélectionnées sont déjà vides.",
                    Measurements = measurements,
                    Plan = plan,
                    RemoteToken = remoteToken,
                };

            var willNotDo = new List<string>
            {
                "Ne touche à aucun document, photo, vidéo ni message.",
                "Ne supprime aucun favori, aucun mot de passe enregistré, aucun historique.",
                "Ne désinstalle aucun logiciel.",
            };

            if (!plan.ContainsUserData)
                willNotDo.Add("Ne vide pas la corbeille : elle n'est nettoyée que si elle est cochée.");

            return new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = ValueFormat.Number(plan.TotalItems) + " élément(s) seront supprimés, soit " +
                          ValueFormat.Bytes(plan.TotalBytes) + " libérés." +
                          (plan.ContainsUserData
                              ? " La corbeille est comprise : son contenu ne sera plus récupérable."
                              : string.Empty),
                WillDo = willDo,
                WillNotDo = willNotDo,
                Measurements = measurements,
                Plan = plan,
                RemoteToken = remoteToken,
            };
        }

        public Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!(preview.Plan is CleanupPlan plan))
                return Task.FromResult(ActionOutcome.Simple(
                    ActionStatus.Failed,
                    "Aucun relevé à exécuter. Le nettoyage ne supprime que ce qu'une prévisualisation a établi."));

            return Task.FromResult(Execute(context.Files, plan, progress, cancellationToken));
        }

        /// <summary>
        /// Supprime exactement ce que le plan contient. Chaque échec est classé, aucun n'est tu :
        /// un fichier verrouillé par un logiciel ouvert est le cas le plus courant, et ce n'est
        /// pas une erreur.
        /// </summary>
        public static ActionOutcome Execute(
            IFileSystemGateway files, CleanupPlan plan, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var details = new List<string>();

            long freed = 0;
            var deleted = 0;
            var locked = 0;
            var changed = 0;
            var denied = 0;
            var failed = 0;
            var vanished = 0;
            var processed = 0;
            var total = Math.Max(1, plan.TotalItems);

            foreach (var group in plan.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!group.HasContent) continue;

                if (group.Kind == CleanupGroupKind.RecycleBin)
                {
                    progress?.Report(new ActionProgress("Vidage de la corbeille…", (double)processed / total));

                    if (files.EmptyRecycleBin())
                    {
                        freed += group.Bytes;
                        deleted += group.ItemCount;
                        details.Add(group.Title + " : vidée (" + ValueFormat.Bytes(group.Bytes) + ").");
                    }
                    else
                    {
                        failed += group.ItemCount;
                        details.Add(group.Title + " : Windows a refusé de la vider.");
                    }

                    processed += group.ItemCount;
                    continue;
                }

                var groupDeleted = 0;
                long groupFreed = 0;

                foreach (var file in group.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (files.Delete(file))
                    {
                        case FileDeletion.Deleted:
                            groupDeleted++;
                            groupFreed += file.SizeBytes;
                            break;
                        case FileDeletion.Locked: locked++; break;
                        case FileDeletion.Changed: changed++; break;
                        case FileDeletion.Denied: denied++; break;
                        case FileDeletion.NotFound: vanished++; break;
                        default: failed++; break;
                    }

                    processed++;
                    if (processed % 250 == 0)
                        progress?.Report(new ActionProgress(
                            "Suppression : " + ValueFormat.Number(processed) + " / " + ValueFormat.Number(total),
                            (double)processed / total));
                }

                deleted += groupDeleted;
                freed += groupFreed;
                details.Add(group.Title + " : " + ValueFormat.Number(groupDeleted) + " supprimé(s), " +
                            ValueFormat.Bytes(groupFreed) + ".");

                foreach (var root in group.Roots)
                    files.RemoveEmptyDirectories(root, cancellationToken);
            }

            stopwatch.Stop();

            if (locked > 0)
                details.Add(ValueFormat.Number(locked) + " fichier(s) étaient ouverts par un logiciel en cours " +
                            "d'exécution et ont été laissés en place.");
            if (changed > 0)
                details.Add(ValueFormat.Number(changed) + " fichier(s) avaient changé depuis le relevé : " +
                            "ils n'étaient plus ceux qui vous ont été montrés, ils ont été laissés.");
            if (denied > 0)
                details.Add(ValueFormat.Number(denied) + " fichier(s) ont été refusés faute de droits.");
            if (vanished > 0)
                details.Add(ValueFormat.Number(vanished) + " fichier(s) avaient déjà disparu.");

            var skipped = locked + changed + denied + failed;

            if (deleted == 0)
                return new ActionOutcome
                {
                    Status = skipped > 0 ? ActionStatus.Failed : ActionStatus.NothingToDo,
                    Summary = skipped > 0
                        ? "Aucun fichier n'a pu être supprimé. Fermer les logiciels ouverts puis réessayer."
                        : "Il n'y avait rien à supprimer.",
                    Details = details,
                    Duration = stopwatch.Elapsed,
                };

            return new ActionOutcome
            {
                Status = skipped > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Succeeded,
                Summary = ValueFormat.Number(deleted) + " élément(s) supprimés, " + ValueFormat.Bytes(freed) +
                          " libérés" + (skipped > 0
                              ? " : " + ValueFormat.Number(skipped) + " élément(s) laissés en place."
                              : "."),
                Details = details,
                Duration = stopwatch.Elapsed,
                FreedBytes = freed,
            };
        }
    }
}
