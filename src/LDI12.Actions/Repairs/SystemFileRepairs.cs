using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Platform;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// <c>sfc /scannow</c> : vérifie et remplace les fichiers protégés de Windows altérés.
    /// </summary>
    /// <remarks>
    /// La sonde <c>SystemFilesProbe</c> lit le verdict du dernier passage dans CBS.log sans rien
    /// exécuter ; c'est ici, et seulement ici, que l'outil est réellement lancé, après
    /// confirmation. La sortie de SFC est écrite en UTF-16LE sur une console redirigée : lue
    /// autrement, elle est illisible.
    /// </remarks>
    public sealed class SfcScanAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.Sfc,
            DisplayName = "Réparer les fichiers système de Windows",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Moderate,
            Purpose = "Lance sfc /scannow : Windows compare tous ses fichiers protégés à leur " +
                      "version de référence et remplace ceux qui sont altérés.",
            PlainPurpose = "Windows vérifie ses propres fichiers et remet en place ceux qui ont été abîmés.",
            TypicalDuration = TimeSpan.FromMinutes(10),
            HardTimeout = TimeSpan.FromMinutes(45),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("sfc /scannow refuse de s'exécuter sans privilèges administrateur");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Commande", "sfc /scannow"),
                new PreviewLine("Durée typique", "5 à 20 minutes selon le disque"),
                new PreviewLine(
                    "Pendant l'exécution",
                    "la machine reste utilisable, mais le disque est très sollicité",
                    PreviewLineKind.Caution),
            };

            if (context.Snapshot?.Windows?.SystemFiles != null)
            {
                var sfc = context.Snapshot.Windows.SystemFiles.Sfc;
                measurements.Add(new PreviewLine(
                    "Dernier contrôle connu",
                    sfc.HasValue ? Describe(sfc.Value) : "aucun relevé dans le journal CBS"));
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Windows va contrôler l'intégralité de ses fichiers protégés et " +
                          "réparer ceux qui ne correspondent plus à leur version d'origine.",
                WillDo = new[]
                {
                    "Comparer chaque fichier système protégé à sa version de référence.",
                    "Remplacer les fichiers altérés à partir du magasin de composants local.",
                    "Écrire le détail de l'opération dans le journal CBS de Windows.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucun document, photo, message ni mot de passe enregistré.",
                    "Ne désinstalle aucun logiciel et ne modifie aucun réglage.",
                    "Ne redémarre pas la machine.",
                },
                Measurements = measurements,
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Contrôle des fichiers système en cours…"));

            var stopwatch = Stopwatch.StartNew();
            var result = await context.Processes.RunAsync(
                new ProcessRequest("sfc.exe", "/scannow")
                {
                    Timeout = Descriptor.HardTimeout,
                    // Piège classique : sfc écrit en UTF-16LE sur une console redirigée.
                    OutputEncoding = ConsoleOutputEncoding.Utf16Le,
                },
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return Interpret(result, stopwatch.Elapsed);
        }

        /// <summary>
        /// Traduit la sortie de SFC en conclusion. Le code de sortie de l'outil ne distingue pas
        /// « rien à réparer » de « réparé » : seule la sortie le dit.
        /// </summary>
        internal static ActionOutcome Interpret(ProcessResult result, TimeSpan duration)
        {
            if (result.TimedOut)
                return new ActionOutcome
                {
                    Status = ActionStatus.TimedOut,
                    Summary = "Le contrôle a dépassé le délai maximal et a été interrompu. " +
                              "Sur un disque très lent ou en cours de défaillance, c'est en soi un constat.",
                    Duration = duration,
                    RawOutput = result.StandardOutput,
                };

            if (result.LaunchFailed)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "sfc.exe n'a pas pu être lancé sur cette machine.",
                    Details = new[] { result.Exception?.Message ?? "Cause inconnue." },
                    Duration = duration,
                };

            var text = result.StandardOutput ?? string.Empty;

            if (Contains(text, "did not find any integrity violations", "n'a trouvé aucune violation"))
                return new ActionOutcome
                {
                    Status = ActionStatus.NothingToDo,
                    Summary = "Aucun fichier système altéré. Il n'y avait rien à réparer.",
                    Duration = duration,
                    RawOutput = text,
                };

            if (Contains(text, "successfully repaired", "a réparé"))
                return new ActionOutcome
                {
                    Status = ActionStatus.Succeeded,
                    Summary = "Des fichiers système altérés ont été trouvés et remplacés. " +
                              "Un redémarrage est conseillé pour que Windows reparte sur les fichiers réparés.",
                    Duration = duration,
                    RestartRequired = true,
                    RawOutput = text,
                };

            if (Contains(text, "was unable to fix", "n'a pas pu réparer"))
                return new ActionOutcome
                {
                    Status = ActionStatus.PartiallySucceeded,
                    Summary = "Des fichiers altérés ont été trouvés, mais tous n'ont pas pu être réparés. " +
                              "Réparer d'abord le magasin de composants (DISM), puis relancer ce contrôle.",
                    Duration = duration,
                    RawOutput = text,
                };

            if (Contains(text, "could not perform", "n'a pas pu effectuer", "another servicing", "autre opération"))
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Windows a refusé de lancer le contrôle : une opération de maintenance " +
                              "est déjà en cours. Redémarrer la machine puis réessayer.",
                    Duration = duration,
                    RawOutput = text,
                };

            return new ActionOutcome
            {
                Status = result.ExitCode == 0 ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = result.ExitCode == 0
                    ? "Le contrôle s'est terminé. Sa sortie ne correspond à aucun cas connu : le détail est ci-dessous."
                    : "Le contrôle s'est terminé anormalement. Le détail de la sortie est ci-dessous.",
                Duration = duration,
                RawOutput = text,
            };
        }

        private static bool Contains(string text, params string[] needles)
        {
            foreach (var needle in needles)
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string Describe(Core.Model.SfcStatus status) => status switch
        {
            Core.Model.SfcStatus.Clean => "aucune corruption détectée",
            Core.Model.SfcStatus.CorruptionFound => "des fichiers altérés avaient été détectés",
            Core.Model.SfcStatus.RepairedSuccessfully => "des fichiers avaient été réparés",
            Core.Model.SfcStatus.RepairFailed => "des fichiers n'avaient pas pu être réparés",
            Core.Model.SfcStatus.NotRun => "jamais exécuté sur cette machine",
            _ => "état indéterminé",
        };
    }

    /// <summary>
    /// <c>DISM /Online /Cleanup-Image /RestoreHealth</c>, répare le magasin de composants.
    /// </summary>
    /// <remarks>
    /// L'ordre compte et l'écran le dit : SFC puise ses fichiers de référence dans le magasin de
    /// composants. Si celui-ci est lui-même endommagé, SFC échoue et c'est DISM qu'il faut passer
    /// d'abord. <c>/RestoreHealth</c> n'existe qu'à partir de Windows 8 : sur Windows 7, la
    /// réparation équivalente passe par un correctif distinct, que l'action nomme plutôt que de
    /// se contenter d'être grisée.
    /// </remarks>
    public sealed class DismRestoreHealthAction : IRepairAction
    {
        private const int Windows8Build = 9200;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.Dism,
            DisplayName = "Réparer le magasin de composants Windows",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Moderate,
            Purpose = "Lance DISM /Online /Cleanup-Image /RestoreHealth : Windows répare la réserve " +
                      "de fichiers de référence dans laquelle SFC puise pour réparer le reste.",
            PlainPurpose = "Windows répare sa réserve interne de fichiers d'origine, celle qui lui sert " +
                           "à se remettre en état lui-même.",
            TypicalDuration = TimeSpan.FromMinutes(15),
            HardTimeout = TimeSpan.FromMinutes(60),
            Requirements = new ActionRequirements
            {
                RequiresElevation = true,
                MinimumBuild = Windows8Build,
                Workaround = "Sur Windows 7, la réparation équivalente passe par l'outil de préparation " +
                             "aux mises à jour du système (KB947821), à télécharger chez Microsoft.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var profile = context.Platform.Profile;
            if (profile.Build < Windows8Build)
                return ActionReadiness.No(
                    "DISM /RestoreHealth n'existe pas sur " + profile.DisplayName + ".",
                    Descriptor.Requirements.Workaround);

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("DISM ne peut pas réparer l'image sans privilèges administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var readiness = CheckReadiness(context);
            if (readiness.Availability == ActionAvailability.Unavailable)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = readiness.Reason ?? "Action indisponible.",
                    Blocker = readiness.Reason,
                    WillDo = readiness.Workaround == null ? Array.Empty<string>() : new[] { readiness.Workaround },
                });

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Commande", "DISM /Online /Cleanup-Image /RestoreHealth"),
                new PreviewLine("Durée typique", "10 à 30 minutes"),
                new PreviewLine(
                    "Accès à Internet",
                    "DISM peut avoir besoin de télécharger des fichiers de référence par Windows Update",
                    PreviewLineKind.Caution),
            };

            if (context.Snapshot?.Windows?.SystemFiles != null)
            {
                var store = context.Snapshot.Windows.SystemFiles.ComponentStore;
                measurements.Add(new PreviewLine(
                    "État relevé au diagnostic",
                    store.HasValue ? Describe(store.Value) : "non mesuré"));
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "DISM va contrôler le magasin de composants et réparer ce qui peut l'être. " +
                          "À passer avant le contrôle des fichiers système, qui s'appuie dessus.",
                WillDo = new[]
                {
                    "Vérifier l'intégrité de l'image Windows installée.",
                    "Remplacer les composants endommagés, au besoin en les téléchargeant.",
                    "Consigner l'opération dans les journaux DISM et CBS.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucun fichier personnel ni à aucun logiciel installé.",
                    "Ne réinstalle pas Windows et ne modifie aucun réglage.",
                },
                Measurements = measurements,
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Réparation du magasin de composants en cours…"));

            var stopwatch = Stopwatch.StartNew();
            var result = await context.Processes.RunAsync(
                new ProcessRequest("dism.exe", "/Online /Cleanup-Image /RestoreHealth")
                {
                    Timeout = Descriptor.HardTimeout,
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return Interpret(result, stopwatch.Elapsed);
        }

        /// <summary>
        /// DISM a ses propres conventions de code de sortie : 0 réussite, 3010 réussite avec
        /// redémarrage requis. Les traiter comme « non nul donc échec » ferait passer une
        /// réparation réussie pour un échec.
        /// </summary>
        internal static ActionOutcome Interpret(ProcessResult result, TimeSpan duration)
        {
            if (result.TimedOut)
                return new ActionOutcome
                {
                    Status = ActionStatus.TimedOut,
                    Summary = "La réparation a dépassé le délai maximal et a été interrompue. " +
                              "DISM peut rester bloqué longtemps sans accès à Windows Update.",
                    Duration = duration,
                    RawOutput = result.StandardOutput,
                };

            if (result.LaunchFailed)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "dism.exe n'a pas pu être lancé sur cette machine.",
                    Details = new[] { result.Exception?.Message ?? "Cause inconnue." },
                    Duration = duration,
                };

            switch (result.ExitCode)
            {
                case 0:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Succeeded,
                        Summary = "Le magasin de composants a été contrôlé et réparé. " +
                                  "Enchaîner sur le contrôle des fichiers système pour finir la remise en état.",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };

                case 3010:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Succeeded,
                        Summary = "La réparation a abouti. Elle ne sera complète qu'après un redémarrage.",
                        Duration = duration,
                        RestartRequired = true,
                        RawOutput = result.StandardOutput,
                    };

                case unchecked((int)0x800F081F):
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Failed,
                        Summary = "DISM n'a pas trouvé de fichiers de référence utilisables. " +
                                  "Il faut soit rétablir l'accès à Windows Update, soit lui indiquer " +
                                  "une source (image ISO de la même version de Windows).",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };

                default:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Failed,
                        Summary = "La réparation s'est terminée sans aboutir (code " +
                                  result.ExitCode.ToString(CultureInfo.InvariantCulture) +
                                  "). Le journal DISM en dit la cause exacte.",
                        Details = new[] { @"Journal : C:\Windows\Logs\DISM\dism.log" },
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };
            }
        }

        private static string Describe(Core.Model.ComponentStoreState state) => state switch
        {
            Core.Model.ComponentStoreState.Healthy => "sain",
            Core.Model.ComponentStoreState.Repairable => "endommagé mais réparable",
            Core.Model.ComponentStoreState.NonRepairable => "endommagé, non réparable en ligne",
            _ => "état indéterminé",
        };
    }
}
