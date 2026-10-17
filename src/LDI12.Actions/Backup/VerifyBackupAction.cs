using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    public sealed class BackupVerificationPlan
    {
        public string Backup { get; init; } = string.Empty;

        /// <summary>Les fichiers que la sauvegarde contient, d'après son manifeste.</summary>
        public IReadOnlyList<ManifestEntry> Entries { get; init; } = Array.Empty<ManifestEntry>();

        public long Bytes { get; init; }
    }

    /// <summary>
    /// Relit une sauvegarde LDI12 sur son support et la compare à son manifeste, fichier par fichier.
    /// </summary>
    /// <remarks>
    /// <b>La question qu'on se pose avant d'effacer un disque.</b> Chaque fichier a été relu au
    /// moment de la copie ; mais entre la copie et la réinstallation, le support a été débranché,
    /// transporté, parfois branché sur un autre poste. Une clé bon marché perd des données en
    /// dormant, un disque tombé ne relit plus tout. Cette vérification relit chaque fichier sur le
    /// support lui-même et compare son empreinte SHA-256 à celle notée le jour de la sauvegarde :
    /// « intacte » veut alors dire intacte, octet pour octet, et non « les noms sont là ».
    /// <para>
    /// Lecture seule : rien n'est écrit, ni sur le support, ni sur la machine. Une sauvegarde faite
    /// avant les empreintes se vérifie quand même, fichier présent par fichier présent, et le compte
    /// rendu dit que son contenu n'a pas pu être comparé.
    /// </para>
    /// </remarks>
    public sealed class VerifyBackupAction : IRepairAction
    {
        public const string SourceParameter = "source";

        /// <summary>Au-delà, le compte rendu ne nomme plus chaque fichier : il en donne le nombre.</summary>
        private const int NamedProblems = 20;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.VerifyBackup,
            DisplayName = "Vérifier une sauvegarde",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Relit chaque fichier d'une sauvegarde LDI12 sur son support et compare son empreinte SHA-256 à celle " +
                      "notée dans le manifeste le jour de la sauvegarde. N'écrit rien.",
            PlainPurpose = "Nous avons relu votre sauvegarde en entier pour nous assurer qu'aucun fichier n'a été abîmé ou perdu.",
            TypicalDuration = TimeSpan.FromMinutes(10),
            HardTimeout = TimeSpan.FromHours(12),
            Requirements = ActionRequirements.None,
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null) return ActionReadiness.No("Aucune sauvegarde n'a été désignée.");
            return context.Files.DirectoryExists(source)
                ? ActionReadiness.Ready
                : ActionReadiness.No("Le dossier « " + source + " » n'existe pas ou n'est pas accessible.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var source = context.Parameter(SourceParameter);
            var backup = source == null ? null : RestoreCatalog.Find(context.Files, source, out _);
            if (backup == null)
                return Task.FromResult(Blocked("Aucune sauvegarde LDI12 n'a été trouvée dans « " + source + " »."));

            var entries = new List<ManifestEntry>();
            long bytes = 0;
            var withFingerprint = 0;
            foreach (var entry in BackupManifest.Read(context.Files, backup))
            {
                if (!entry.IsSaved) continue;
                entries.Add(entry);
                bytes += entry.Size;
                if (entry.Fingerprint != null) withFingerprint++;
            }

            if (entries.Count == 0)
                return Task.FromResult(Blocked(
                    "Cette sauvegarde n'a pas de manifeste lisible : elle date d'avant la vérification, ou sa copie a été interrompue " +
                    "avant la fin du premier dossier."));

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Sauvegarde", backup),
                new PreviewLine("À relire", entries.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) +
                                            " fichier(s), " + ValueFormat.Bytes(bytes)),
            };
            if (withFingerprint < entries.Count)
                measurements.Add(new PreviewLine("Sans empreinte",
                    (entries.Count - withFingerprint) + " fichier(s) sauvegardés avant les empreintes : leur présence se vérifie, " +
                    "pas leur contenu", PreviewLineKind.Caution));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = entries.Count + " fichier(s), " + ValueFormat.Bytes(bytes) + ", seront relus sur le support.",
                WillDo = new[] { "Relire chaque fichier de la sauvegarde et comparer son empreinte à celle du manifeste" },
                WillNotDo = new[] { "N'écrit rien, ni sur le support, ni sur ce PC." },
                Measurements = measurements,
                Plan = new BackupVerificationPlan { Backup = backup, Entries = entries, Bytes = bytes },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not BackupVerificationPlan plan)
                return Task.FromResult(ActionOutcome.Simple(ActionStatus.Failed, "La sauvegarde à vérifier n'a pas été relevée."));

            return Task.Run(() => Verify(context, plan, progress, cancellationToken), cancellationToken);
        }

        private static ActionOutcome Verify(
            ActionContext context, BackupVerificationPlan plan, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var tracker = new CopyProgress(progress, plan.Bytes, plan.Entries.Count, "Vérification de");
            var missing = new List<string>();
            var unreadable = new List<string>();
            var altered = new List<string>();
            int intact = 0, unchecked_ = 0;
            string? folder = null;

            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(folder, entry.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    folder = entry.Folder;
                    tracker.Folder(entry.Folder);
                }

                var path = Path.Combine(plan.Backup, entry.Key);

                // Une relecture compte double dans l'avancement d'une copie, écriture puis relecture :
                // ici il n'y a que la relecture, qui compte donc pour les deux.
                var read = context.Files.Fingerprint(path, count => tracker.Bytes(count * 2), cancellationToken);
                tracker.FileDone(entry.Size);

                if (!read.HasValue)
                {
                    if (!context.Files.FileExists(path)) missing.Add(entry.Key);
                    else unreadable.Add(entry.Key);

                    // Le support entier a disparu : inutile d'échouer sur chacun des fichiers restants.
                    if (!context.Files.DirectoryExists(plan.Backup))
                        return Outcome(plan, intact, unchecked_, missing, unreadable, altered, stopwatch,
                            " La vérification s'est arrêtée : le support ne répond plus.");
                    continue;
                }

                if (entry.Fingerprint == null) unchecked_++;
                else if (string.Equals(entry.Fingerprint, read.Value, StringComparison.OrdinalIgnoreCase)) intact++;
                else altered.Add(entry.Key);
            }

            return Outcome(plan, intact, unchecked_, missing, unreadable, altered, stopwatch, null);
        }

        private static ActionOutcome Outcome(
            BackupVerificationPlan plan, int intact, int unchecked_, List<string> missing, List<string> unreadable,
            List<string> altered, Stopwatch stopwatch, string? interruption)
        {
            stopwatch.Stop();
            var problems = missing.Count + unreadable.Count + altered.Count;
            var details = new List<string>();

            if (altered.Count > 0)
                details.Add(altered.Count + " fichier(s) ne sont plus identiques à ce qui avait été sauvegardé : le support les a abîmés. " +
                            Names(altered));
            if (unreadable.Count > 0)
                details.Add(unreadable.Count + " fichier(s) ne se relisent plus : secteurs défaillants, ou support qui fatigue. " +
                            Names(unreadable));
            if (missing.Count > 0)
                details.Add(missing.Count + " fichier(s) ont disparu de la sauvegarde depuis qu'elle a été faite. " + Names(missing));
            if (unchecked_ > 0)
                details.Add(unchecked_ + " fichier(s) sauvegardés avant les empreintes : présents et lisibles, contenu non comparé.");

            if (problems > 0)
                details.Add("Tant que la machine d'origine n'est pas effacée, refaire la sauvegarde sur un autre support. " +
                            "Si elle l'est déjà, restaurer sans attendre ce qui se lit encore.");
            else if (interruption == null)
                details.Add("Le support peut servir à la restauration, et le disque d'origine peut être effacé.");

            var status = interruption != null
                ? ActionStatus.Failed
                : problems == 0 ? ActionStatus.Succeeded : intact > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed;

            var summary = problems == 0 && interruption == null
                ? "Sauvegarde intacte : " + (intact + unchecked_) + " fichier(s) relus, " + ValueFormat.Bytes(plan.Bytes) +
                  (unchecked_ > 0 ? ", dont " + unchecked_ + " sans empreinte." : ", tous identiques au jour de la sauvegarde.")
                : intact + " fichier(s) intacts, " + problems + " en défaut." + (interruption ?? string.Empty);

            return new ActionOutcome { Status = status, Summary = summary, Details = details, Duration = stopwatch.Elapsed };
        }

        private static string Names(List<string> keys)
            => (keys.Count <= NamedProblems
                   ? string.Join(", ", keys)
                   : string.Join(", ", keys.GetRange(0, NamedProblems)) + ", et " + (keys.Count - NamedProblems) + " autre(s)") + ".";

        private static ActionPreview Blocked(string reason)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = reason, Blocker = reason };
    }
}
