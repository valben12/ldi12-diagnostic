using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Vérification du système de fichiers, <b>en lecture seule</b>.
    /// </summary>
    /// <remarks>
    /// Volontairement sans <c>/f</c> ni <c>/r</c>. Une réparation ne peut pas s'exécuter sur le
    /// volume qui porte Windows : elle se planifie au redémarrage suivant, immobilise la machine
    /// pendant un temps qu'on ne sait pas annoncer, et sur un disque en fin de vie elle peut
    /// achever ce qui était encore récupérable. L'outil dit ce qu'il constate ; décider de
    /// réparer reste une décision de technicien, prise en connaissance du reste du diagnostic.
    /// </remarks>
    public sealed class CheckDiskAction : IRepairAction
    {
        private const int Windows8Build = 9200;

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.CheckDisk,
            DisplayName = "Vérifier le système de fichiers",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Lance chkdsk en analyse seule sur le volume choisi : signale les erreurs de " +
                      "structure sans en corriger aucune.",
            PlainPurpose = "Windows contrôle l'organisation des fichiers sur le disque et signale ce qui cloche.",
            TypicalDuration = TimeSpan.FromMinutes(5),
            HardTimeout = TimeSpan.FromMinutes(40),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("chkdsk ne peut pas ouvrir le volume sans privilèges administrateur");

        /// <summary>Volume visé : celui que le technicien a choisi, sinon celui qui porte Windows.</summary>
        internal static string ResolveVolume(ActionContext context)
        {
            var chosen = context.Parameter("volume");
            if (chosen != null) return Normalize(chosen);

            var volumes = context.Snapshot?.Storage.Volumes;
            if (volumes != null)
                foreach (var volume in volumes)
                    if (volume.IsSystemVolume.Or(false) && volume.DriveLetter.HasValue)
                        return Normalize(volume.DriveLetter.Value);

            return Normalize(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:");
        }

        private static string Normalize(string value)
        {
            var trimmed = (value ?? string.Empty).Trim().TrimEnd('\\');
            if (trimmed.Length == 1) trimmed += ":";
            return trimmed.Length >= 2 ? trimmed.Substring(0, 2).ToUpperInvariant() : "C:";
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var volume = ResolveVolume(context);
            var online = context.Platform.Profile.Build >= Windows8Build;

            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Volume analysé", volume),
                new PreviewLine("Commande", "chkdsk " + volume + (online ? " /scan" : string.Empty)),
                new PreviewLine(
                    "Mode",
                    online
                        ? "analyse en ligne : la machine reste utilisable pendant le contrôle"
                        : "analyse en lecture seule (Windows 7 ne connaît pas l'analyse en ligne)"),
            };

            foreach (var candidate in context.Snapshot?.Storage.Volumes ?? Array.Empty<VolumeInfo>())
            {
                if (!candidate.DriveLetter.HasValue || Normalize(candidate.DriveLetter.Value) != volume) continue;
                if (candidate.FileSystem.HasValue)
                    measurements.Add(new PreviewLine("Système de fichiers", candidate.FileSystem.Value));
                if (candidate.TotalBytes.HasValue)
                    measurements.Add(new PreviewLine("Taille", ValueFormat.Bytes(candidate.TotalBytes.Value)));
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le volume " + volume + " va être analysé. Aucune correction ne sera appliquée : " +
                          "l'analyse rend un constat, pas une réparation.",
                WillDo = new[]
                {
                    "Parcourir la structure du système de fichiers du volume " + volume + ".",
                    "Signaler les entrées incohérentes, les secteurs illisibles et les fichiers orphelins.",
                },
                WillNotDo = new[]
                {
                    "Ne corrige rien : ni /f, ni /r, ni planification au redémarrage.",
                    "Ne supprime aucun fichier et ne déplace rien.",
                    "N'immobilise pas la machine et n'exige aucun redémarrage.",
                },
                Measurements = measurements,
                Plan = volume,
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var volume = preview.Plan as string ?? ResolveVolume(context);
            var online = context.Platform.Profile.Build >= Windows8Build;

            progress?.Report(new ActionProgress("Analyse du volume " + volume + " en cours…"));

            var stopwatch = Stopwatch.StartNew();
            var result = await context.Processes.RunAsync(
                new ProcessRequest("chkdsk.exe", volume + (online ? " /scan" : string.Empty))
                {
                    Timeout = Descriptor.HardTimeout,
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return Interpret(volume, result, stopwatch.Elapsed);
        }

        /// <summary>
        /// chkdsk code sa conclusion dans le code de sortie, et c'est heureux : sa sortie, elle,
        /// est traduite dans la langue de Windows.
        /// </summary>
        internal static ActionOutcome Interpret(string volume, ProcessResult result, TimeSpan duration)
        {
            if (result.TimedOut)
                return new ActionOutcome
                {
                    Status = ActionStatus.TimedOut,
                    Summary = "L'analyse du volume " + volume + " a dépassé le délai maximal. " +
                              "Sur un disque qui met autant de temps à se laisser lire, c'est déjà un constat.",
                    Duration = duration,
                    RawOutput = result.StandardOutput,
                };

            if (result.LaunchFailed)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "chkdsk.exe n'a pas pu être lancé sur cette machine.",
                    Details = new[] { result.Exception?.Message ?? "Cause inconnue." },
                    Duration = duration,
                };

            switch (result.ExitCode)
            {
                case 0:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.NothingToDo,
                        Summary = "Aucune erreur de système de fichiers sur le volume " + volume + ".",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };

                case 1:
                case 2:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Succeeded,
                        Summary = "Des erreurs de système de fichiers ont été relevées sur le volume " + volume +
                                  ". Elles n'ont pas été corrigées : une réparation se planifie et " +
                                  "s'exécute au redémarrage, après sauvegarde des données.",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };

                case 3:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.PartiallySucceeded,
                        Summary = "Le contrôle n'a pas pu aller à son terme sur le volume " + volume +
                                  ". C'est souvent le signe que le support lui-même répond mal.",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };

                default:
                    return new ActionOutcome
                    {
                        Status = ActionStatus.Failed,
                        Summary = "Le contrôle s'est terminé sur un code inattendu (" +
                                  result.ExitCode.ToString(CultureInfo.InvariantCulture) + ").",
                        Duration = duration,
                        RawOutput = result.StandardOutput,
                    };
            }
        }
    }

    /// <summary>
    /// Vide le cache de résolution de noms.
    /// </summary>
    /// <remarks>
    /// L'action la moins engageante du catalogue, et l'une des plus utiles : un cache DNS qui
    /// garde une adresse périmée donne exactement le tableau d'une « panne Internet » alors que
    /// la ligne fonctionne. Le cache se reconstruit seul à la première consultation.
    /// <para>
    /// Élévation non exigée d'avance : selon la version de Windows, la commande passe ou non en
    /// session standard. Plutôt que d'imposer une invite UAC qui ne sert souvent à rien, on
    /// tente, et on lit le refus s'il vient.
    /// </para>
    /// </remarks>
    public sealed class FlushDnsAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.FlushDns,
            DisplayName = "Vider le cache DNS",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Low,
            Purpose = "Lance ipconfig /flushdns : efface les correspondances nom / adresse mémorisées.",
            PlainPurpose = "L'ordinateur oublie les adresses de sites qu'il avait mémorisées et les redemande.",
            TypicalDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(30),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le cache de résolution de noms va être vidé. Il se reconstruit tout seul.",
                WillDo = new[]
                {
                    "Effacer les correspondances nom de site / adresse mémorisées par Windows.",
                },
                WillNotDo = new[]
                {
                    "Ne modifie aucun réglage réseau : ni adresse IP, ni serveurs DNS, ni Wi-Fi enregistré.",
                    "Ne coupe pas la connexion et n'exige aucun redémarrage.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Commande", "ipconfig /flushdns"),
                    new PreviewLine("Durée", "immédiat"),
                },
            });

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Vidage du cache DNS…"));

            var stopwatch = Stopwatch.StartNew();
            var result = await context.Processes.RunAsync(
                new ProcessRequest("ipconfig.exe", "/flushdns")
                {
                    Timeout = Descriptor.HardTimeout,
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return Interpret(result, stopwatch.Elapsed);
        }

        internal static ActionOutcome Interpret(ProcessResult result, TimeSpan duration)
        {
            if (result.LaunchFailed || result.TimedOut)
                return new ActionOutcome
                {
                    Status = result.TimedOut ? ActionStatus.TimedOut : ActionStatus.Failed,
                    Summary = "La commande n'a pas pu être menée à son terme.",
                    Duration = duration,
                };

            var text = (result.StandardOutput ?? string.Empty) + (result.StandardError ?? string.Empty);

            if (text.IndexOf("requires elevation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("nécessite une élévation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("privilèges", StringComparison.OrdinalIgnoreCase) >= 0)
                return new ActionOutcome
                {
                    Status = ActionStatus.ElevationRequired,
                    Summary = "Cette version de Windows demande les privilèges administrateur pour vider le cache DNS.",
                    Duration = duration,
                    RawOutput = text,
                };

            return result.ExitCode == 0
                ? new ActionOutcome
                {
                    Status = ActionStatus.Succeeded,
                    Summary = "Le cache de résolution de noms a été vidé.",
                    Duration = duration,
                    RawOutput = text,
                }
                : new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Windows a refusé de vider le cache (code " +
                              result.ExitCode.ToString(CultureInfo.InvariantCulture) + ").",
                    Duration = duration,
                    RawOutput = text,
                };
        }
    }

    /// <summary>
    /// Réinitialise la pile réseau : catalogue Winsock puis configuration TCP/IP.
    /// </summary>
    /// <remarks>
    /// L'action la plus engageante du catalogue. Elle remet à leur état d'origine des couches sur
    /// lesquelles s'appuient les VPN, les antivirus filtrants et les pare-feu tiers : après elle,
    /// certains de ces logiciels doivent être réinstallés pour refonctionner. Elle n'a de sens
    /// qu'après avoir écarté les causes simples, et l'écran l'annonce avant, pas après.
    /// </remarks>
    public sealed class NetworkStackResetAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.NetworkReset,
            DisplayName = "Réinitialiser la pile réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.High,
            Purpose = "Lance netsh winsock reset puis netsh int ip reset : remet le catalogue Winsock " +
                      "et la configuration TCP/IP dans leur état d'origine.",
            PlainPurpose = "Les réglages réseau de Windows sont remis à neuf, comme au premier démarrage.",
            TypicalDuration = TimeSpan.FromSeconds(15),
            HardTimeout = TimeSpan.FromMinutes(3),
            RequiresRestart = true,
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("netsh ne peut pas réécrire la configuration réseau sans privilèges administrateur");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "La configuration réseau de Windows va être remise à son état d'origine. " +
                          "L'effet n'est complet qu'après redémarrage.",
                WillDo = new[]
                {
                    "Réinitialiser le catalogue Winsock (netsh winsock reset).",
                    "Réinitialiser la configuration TCP/IP (netsh int ip reset).",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucun fichier personnel.",
                    "Ne supprime pas les réseaux Wi-Fi enregistrés ni leurs mots de passe.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Redémarrage", "obligatoire pour que la réinitialisation prenne effet",
                        PreviewLineKind.Caution),
                    new PreviewLine(
                        "Logiciels réseau tiers",
                        "un VPN, un antivirus filtrant ou un pare-feu tiers s'insère dans ces couches : " +
                        "après réinitialisation, il peut devoir être réinstallé pour refonctionner",
                        PreviewLineKind.Caution),
                    new PreviewLine(
                        "À faire avant",
                        "créer un point de restauration : la case est prévue en haut de l'écran",
                        PreviewLineKind.Caution),
                },
            });

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var output = new StringBuilder();
            var details = new List<string>();
            var failures = 0;

            var steps = new[]
            {
                ("Réinitialisation du catalogue Winsock…", "winsock reset", "Catalogue Winsock"),
                ("Réinitialisation de la configuration TCP/IP…", "int ip reset", "Configuration TCP/IP"),
            };

            foreach (var (message, arguments, label) in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress(message));

                var result = await context.Processes.RunAsync(
                    new ProcessRequest("netsh.exe", arguments)
                    {
                        Timeout = TimeSpan.FromMinutes(1),
                        OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    },
                    cancellationToken).ConfigureAwait(false);

                output.AppendLine("netsh " + arguments).AppendLine(result.StandardOutput).AppendLine();

                // netsh int ip reset rend 1 quand une partie de la configuration était déjà
                // à son état d'origine : ce n'est pas un échec.
                var ok = result.Completed && (result.ExitCode == 0 || result.ExitCode == 1);
                if (!ok) failures++;
                details.Add(label + " : " + (ok ? "réinitialisé." : "échec (code " +
                            result.ExitCode.ToString(CultureInfo.InvariantCulture) + ")."));
            }

            stopwatch.Stop();

            if (failures == steps.Length)
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Aucune des deux réinitialisations n'a abouti. La configuration réseau est inchangée.",
                    Details = details,
                    Duration = stopwatch.Elapsed,
                    RawOutput = output.ToString(),
                };

            return new ActionOutcome
            {
                Status = failures == 0 ? ActionStatus.Succeeded : ActionStatus.PartiallySucceeded,
                Summary = failures == 0
                    ? "La pile réseau a été réinitialisée. Redémarrer la machine pour que ce soit effectif."
                    : "Une seule des deux réinitialisations a abouti. Redémarrer, puis relancer l'opération.",
                Details = details,
                Duration = stopwatch.Elapsed,
                RestartRequired = true,
                RawOutput = output.ToString(),
            };
        }
    }
}
