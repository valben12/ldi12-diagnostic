using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Repairs
{
    /// <summary>Ce que les opérations natives ont en commun : la lecture du paramètre « device » et le compte rendu.</summary>
    internal static class NativeOutcome
    {
        internal const string DeviceParameter = "device";

        internal static ActionReadiness Elevated(ActionContext context, string why)
            => context.Platform.IsElevated ? ActionReadiness.Ready : ActionReadiness.Elevation(why);

        internal static ActionOutcome From(ControlResult result, TimeSpan duration, string? success = null)
            => new ActionOutcome
            {
                Status = result.Succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = result.Succeeded ? success ?? result.Message : result.Message,
                Details = result.RestartRequired ? new[] { "Windows demande un redémarrage pour terminer." } : Array.Empty<string>(),
                RestartRequired = result.RestartRequired,
                Duration = duration,
            };

        /// <summary>Le périphérique désigné, relu à l'instant : jamais celui qu'un plan aurait supposé.</summary>
        internal static (DeviceState? Device, ActionPreview? Blocked) Device(ActionContext context)
        {
            var id = context.Parameter(DeviceParameter);
            if (id == null) return (null, Block("Aucun périphérique désigné."));
            var device = context.Control.FindDevice(id);
            return device == null ? (null, Block("Ce périphérique n'existe pas sur ce PC : « " + id + " ».")) : (device, null);
        }

        internal static ActionPreview Block(string why)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = why, Blocker = why };

        internal static PreviewLine State(DeviceState device)
            => new PreviewLine("État", device.ProblemCode == 0 ? "fonctionne" : device.IsDisabled ? "désactivé" : "en erreur (code " + device.ProblemCode + ")");
    }

    /// <summary>« Rechercher les modifications sur le matériel », comme dans le Gestionnaire de périphériques.</summary>
    public sealed class RescanDevicesAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RescanDevices,
            DisplayName = "Rechercher le matériel",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Windows réexamine tous les périphériques et installe le pilote de ceux qui n'en ont pas.",
            PlainPurpose = "L'ordinateur refait le tour de son matériel, comme à un nouveau branchement.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(3),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve la recherche de matériel aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Windows va refaire le tour du matériel branché.",
                WillDo = new[] { "Détecter les périphériques nouveaux ou revenus, et leur installer un pilote s'il en existe un sur ce PC." },
                WillNotDo = new[] { "Ne désinstalle rien et ne change aucun pilote déjà en place." },
            });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Recherche du matériel…"));
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.RescanDevices(), clock.Elapsed));
        }
    }

    /// <summary>Redémarre le pilote d'un périphérique : l'équivalent d'un débranchement, sans toucher au câble.</summary>
    public sealed class RestartDeviceAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestartDevice,
            DisplayName = "Redémarrer un périphérique",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Arrête et relance le pilote d'un périphérique précis (carte Wi-Fi, son, caméra…).",
            PlainPurpose = "Le périphérique est éteint et rallumé, comme si on le débranchait.",
            TypicalDuration = TimeSpan.FromSeconds(5),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve le redémarrage d'un périphérique aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (device, blocked) = NativeOutcome.Device(context);
            if (blocked != null) return Task.FromResult(blocked);
            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "« " + device!.Name + " » va être redémarré.",
                WillDo = new[] { "Arrêter puis relancer son pilote : il se coupe une seconde." },
                WillNotDo = new[] { "Ne change pas de pilote et ne touche à aucun autre périphérique." },
                Measurements = new[] { new PreviewLine("Périphérique", device.Name), NativeOutcome.State(device) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Redémarrage du périphérique…"));
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.RestartDevice(context.Parameter(NativeOutcome.DeviceParameter)!), clock.Elapsed));
        }
    }

    /// <summary>Réactive un périphérique désactivé, souvent par mégarde ou par un logiciel.</summary>
    public sealed class EnableDeviceAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.EnableDevice,
            DisplayName = "Réactiver un périphérique",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Réactive un périphérique désactivé dans le Gestionnaire de périphériques (code 22).",
            PlainPurpose = "Un appareil éteint dans Windows est rallumé.",
            TypicalDuration = TimeSpan.FromSeconds(5),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve l'activation d'un périphérique aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (device, blocked) = NativeOutcome.Device(context);
            if (blocked != null) return Task.FromResult(blocked);
            if (!device!.IsDisabled)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "« " + device.Name + " » n'est pas désactivé : rien à faire.",
                    Measurements = new[] { NativeOutcome.State(device) },
                });

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "« " + device.Name + " » va être réactivé.",
                WillDo = new[] { "Réactiver le périphérique ; Windows recharge son pilote." },
                WillNotDo = new[] { "Ne change pas de pilote." },
                Measurements = new[] { new PreviewLine("Périphérique", device.Name), NativeOutcome.State(device) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress("Activation du périphérique…"));
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.EnableDevice(context.Parameter(NativeOutcome.DeviceParameter)!), clock.Elapsed));
        }
    }

    /// <summary>
    /// Désinstalle un périphérique puis le fait redétecter : Windows lui réinstalle un pilote propre.
    /// </summary>
    /// <remarks>
    /// Le remède classique d'un périphérique en erreur (codes 10, 28, 31, 43…). Refusé pour ce
    /// qui fait tenir la machine : disques, contrôleurs de stockage, processeur, carte mère,
    /// volumes. Un périphérique de ces classes qu'on désinstalle peut empêcher Windows de
    /// redémarrer, et aucun plan ne doit pouvoir le demander.
    /// </remarks>
    public sealed class ReinstallDeviceAction : IRepairAction
    {
        private static readonly string[] Protected =
        {
            "System", "Computer", "Processor", "Volume", "VolumeSnapshot", "DiskDrive", "HDC", "SCSIAdapter",
            "SoftwareDevice", "SecurityDevices", "Firmware", "LegacyDriver",
        };

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.ReinstallDevice,
            DisplayName = "Réinstaller un périphérique",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Moderate,
            Purpose = "Désinstalle un périphérique en erreur puis le fait redétecter : Windows lui réinstalle son pilote.",
            PlainPurpose = "L'appareil est retiré de Windows puis retrouvé, avec un pilote remis à neuf.",
            TypicalDuration = TimeSpan.FromSeconds(20),
            HardTimeout = TimeSpan.FromMinutes(4),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve la désinstallation d'un périphérique aux administrateurs");

        internal static bool IsProtected(string? deviceClass)
            => deviceClass != null && Protected.Any(p => string.Equals(p, deviceClass, StringComparison.OrdinalIgnoreCase));

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (device, blocked) = NativeOutcome.Device(context);
            if (blocked != null) return Task.FromResult(blocked);
            if (IsProtected(device!.Class))
                return Task.FromResult(NativeOutcome.Block("« " + device.Name + " » fait tenir le système (classe " + device.Class
                                                         + ") : il ne se désinstalle pas d'ici."));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "« " + device.Name + " » va être désinstallé puis redétecté.",
                WillDo = new[]
                {
                    "Désinstaller le périphérique, sans supprimer son pilote du PC.",
                    "Relancer aussitôt la recherche de matériel : Windows le retrouve et réinstalle le pilote.",
                },
                WillNotDo = new[] { "Ne supprime aucun pilote du magasin de pilotes : le même ou un plus récent revient." },
                Measurements = new[]
                {
                    new PreviewLine("Périphérique", device.Name),
                    new PreviewLine("Classe", device.Class ?? "inconnue"),
                    NativeOutcome.State(device),
                    new PreviewLine("Pendant l'opération", "le périphérique disparaît quelques secondes", PreviewLineKind.Caution),
                },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var id = context.Parameter(NativeOutcome.DeviceParameter)!;
            // Relu au moment d'agir : l'hôte élevé ne fait confiance à aucun relevé antérieur.
            var device = context.Control.FindDevice(id);
            if (device == null || IsProtected(device.Class))
                return Task.FromResult(new ActionOutcome { Status = ActionStatus.Failed, Summary = "Périphérique introuvable ou protégé : rien n'a été fait." });

            progress?.Report(new ActionProgress("Réinstallation de « " + device.Name + " »…"));
            var clock = Stopwatch.StartNew();
            var result = context.Control.ReinstallDevice(id);
            if (!result.Succeeded) return Task.FromResult(NativeOutcome.From(result, clock.Elapsed));

            var after = context.Control.FindDevice(id);
            var summary = after == null
                ? "Désinstallé ; il sera retrouvé au prochain redémarrage."
                : after.ProblemCode == 0 ? "« " + after.Name + " » réinstallé : il fonctionne." : "Réinstallé, mais toujours en erreur (code " + after.ProblemCode + ") : il faut le pilote du constructeur.";
            return Task.FromResult(new ActionOutcome
            {
                Status = after != null && after.ProblemCode != 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Succeeded,
                Summary = summary,
                RestartRequired = result.RestartRequired || after == null,
                Duration = clock.Elapsed,
            });
        }
    }

    /// <summary>
    /// Les pilotes que Windows Update propose pour ce PC : listés à la prévisualisation, installés à l'exécution.
    /// </summary>
    /// <remarks>
    /// Par l'agent Windows Update lui-même, sans module ni outil tiers. Les pilotes viennent du
    /// catalogue de Microsoft, signés et testés pour ce matériel : c'est la source la plus sûre
    /// après le site du constructeur, et la seule qui s'installe sans téléchargement manuel.
    /// Le paramètre facultatif « filter » restreint aux pilotes dont le nom ou la classe le contient.
    /// </remarks>
    public sealed class DriverUpdatesAction : IRepairAction
    {
        internal const string FilterParameter = "filter";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.DriverUpdates,
            DisplayName = "Installer les pilotes de Windows Update",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Moderate,
            Purpose = "Cherche les pilotes que Windows Update propose pour ce matériel, puis les installe.",
            PlainPurpose = "Windows va chercher chez Microsoft des pilotes plus récents pour ce PC et les installe.",
            TypicalDuration = TimeSpan.FromMinutes(5),
            HardTimeout = TimeSpan.FromMinutes(50),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows Update n'installe de pilote qu'avec les privilèges administrateur");

        public async Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var search = await context.Control.SearchDriverUpdatesAsync(cancellationToken).ConfigureAwait(false);
            if (!search.Succeeded) return NativeOutcome.Block(search.Error ?? "Windows Update n'a pas répondu.");

            var filter = context.Parameter(FilterParameter);
            var kept = search.Updates
                .Where(u => filter == null
                            || u.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || (u.DriverClass ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || (u.Model ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (kept.Count == 0)
                return new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = search.Updates.Count == 0
                        ? "Windows Update ne propose aucun pilote pour ce PC (ou une stratégie l'en empêche)."
                        : "Aucun des " + search.Updates.Count + " pilote(s) proposé(s) ne correspond à « " + filter + " ».",
                };

            var bytes = kept.Sum(u => u.DownloadBytes);
            return new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = kept.Count + " pilote(s) à installer depuis Windows Update" + (bytes > 0 ? ", " + ValueFormat.Bytes(bytes) + " à télécharger." : "."),
                WillDo = kept.Select(u => u.Title + (u.DriverDate.HasValue ? " (" + u.DriverDate.Value.ToString("d MMMM yyyy", Formats.French) + ")" : string.Empty)).ToList(),
                WillNotDo = new[] { "N'installe aucune autre mise à jour de Windows.", "Ne retire aucun pilote : l'ancien reste disponible dans le Gestionnaire de périphériques." },
                Measurements = new[] { new PreviewLine("Source", "Windows Update (catalogue de Microsoft)") },
                Plan = kept.Select(u => u.UpdateId).ToList(),
            };
        }

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var ids = preview.Plan as IReadOnlyCollection<string> ?? Array.Empty<string>();
            var clock = Stopwatch.StartNew();
            var text = new Progress<string>(line => progress?.Report(new ActionProgress(line)));
            var result = await context.Control.InstallDriverUpdatesAsync(ids, text, cancellationToken).ConfigureAwait(false);
            return NativeOutcome.From(result, clock.Elapsed);
        }
    }
}
