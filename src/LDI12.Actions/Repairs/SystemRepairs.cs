using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using Microsoft.Win32;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Arrête un service, vide son dossier de travail, le relance avec ses dépendants : le geste
    /// commun à la file d'impression bloquée et au cache de Windows Update.
    /// </summary>
    internal static class ServiceReset
    {
        internal static ActionOutcome Run(
            ActionContext context, IReadOnlyList<string> services, string folder, string label, IProgress<ActionProgress>? progress)
        {
            var clock = Stopwatch.StartNew();
            var details = new List<string>();
            var restart = new List<string>();
            var timeout = TimeSpan.FromSeconds(45);

            foreach (var service in services)
            {
                if (context.Control.ServiceStatus(service) == null) continue;
                progress?.Report(new ActionProgress("Arrêt du service " + service + "…"));
                var stopped = context.Control.StopService(service, timeout, out var dependents);
                details.Add(stopped.Message);
                if (!stopped.Succeeded)
                {
                    // On ne vide pas un dossier qu'un service encore vivant tient ouvert ; on remet tout en marche.
                    foreach (var name in restart.AsEnumerable().Reverse()) context.Control.StartService(name, timeout);
                    return new ActionOutcome { Status = ActionStatus.Failed, Summary = stopped.Message, Details = details, Duration = clock.Elapsed };
                }
                restart.Add(service);
                restart.AddRange(dependents);
            }

            progress?.Report(new ActionProgress("Vidage de " + label + "…"));
            var cleared = context.Control.ClearFolder(folder);
            details.Add(cleared.Message);

            var failures = 0;
            foreach (var name in restart.AsEnumerable().Reverse().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                progress?.Report(new ActionProgress("Relance du service " + name + "…"));
                var started = context.Control.StartService(name, timeout);
                details.Add(started.Message);
                if (!started.Succeeded) failures++;
            }

            return new ActionOutcome
            {
                Status = failures == 0 ? ActionStatus.Succeeded : ActionStatus.PartiallySucceeded,
                Summary = failures == 0
                    ? label.Substring(0, 1).ToUpperInvariant() + label.Substring(1) + " remis à zéro : " + cleared.Message
                    : "Vidé, mais " + failures + " service(s) n'ont pas redémarré : un redémarrage du PC les relancera.",
                Details = details,
                RestartRequired = failures > 0,
                Duration = clock.Elapsed,
            };
        }

        internal static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    /// <summary>La file d'impression bloquée : un document coincé qui empêche tous les autres de sortir.</summary>
    public sealed class PrintQueueResetAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.PrintQueueReset,
            DisplayName = "Débloquer la file d'impression",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Hardware,
            Risk = ActionRisk.Low,
            Purpose = "Arrête le spouleur, supprime les travaux d'impression coincés, le relance.",
            PlainPurpose = "Les impressions bloquées sont effacées et l'imprimante repart.",
            TypicalDuration = TimeSpan.FromSeconds(15),
            HardTimeout = TimeSpan.FromMinutes(3),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        private static string Folder => Path.Combine(ServiceReset.Windows, @"System32\spool\PRINTERS");

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve l'arrêt du spouleur aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "La file d'impression va être vidée.",
                WillDo = new[] { "Arrêter le spouleur d'impression.", "Supprimer les travaux en attente.", "Relancer le spouleur." },
                WillNotDo = new[] { "Ne supprime aucune imprimante ni aucun pilote d'imprimante." },
                Measurements = new[]
                {
                    new PreviewLine("Dossier", Folder),
                    new PreviewLine("Impressions en attente", "seront perdues : à relancer", PreviewLineKind.Caution),
                },
            });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(ServiceReset.Run(context, new[] { "Spooler" }, Folder, "la file d'impression", progress));
    }

    /// <summary>Le cache de téléchargement de Windows Update, quand une mise à jour échoue en boucle.</summary>
    public sealed class WindowsUpdateResetAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.WindowsUpdateReset,
            DisplayName = "Réinitialiser Windows Update",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Low,
            Purpose = "Arrête Windows Update et BITS, vide le cache de téléchargement, relance les services.",
            PlainPurpose = "Windows Update oublie ses téléchargements abîmés et les refait proprement.",
            TypicalDuration = TimeSpan.FromSeconds(30),
            HardTimeout = TimeSpan.FromMinutes(5),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        private static string Folder => Path.Combine(ServiceReset.Windows, @"SoftwareDistribution\Download");

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve l'arrêt de Windows Update aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le cache de Windows Update va être vidé.",
                WillDo = new[] { "Arrêter Windows Update et BITS.", "Vider le dossier des téléchargements de mises à jour.", "Relancer les services." },
                WillNotDo = new[] { "Ne désinstalle aucune mise à jour et ne touche pas à l'historique." },
                Measurements = new[]
                {
                    new PreviewLine("Dossier", Folder),
                    new PreviewLine("Ensuite", "les mises à jour en attente se retéléchargent", PreviewLineKind.Caution),
                },
            });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(ServiceReset.Run(context, new[] { "wuauserv", "bits" }, Folder, "le cache de Windows Update", progress));
    }

    /// <summary>
    /// Désactive un programme au démarrage, exactement comme l'onglet Démarrage du Gestionnaire des tâches.
    /// </summary>
    /// <remarks>
    /// Rien n'est supprimé : Windows tient la liste des entrées désactivées dans la clé
    /// StartupApproved, et le technicien comme le client les réactivent d'un clic depuis le
    /// Gestionnaire des tâches. Cette clé n'existe qu'à partir de Windows 8 ; sous Windows 7,
    /// msconfig fait la même chose à la main.
    /// </remarks>
    public sealed class StartupDisableAction : IRepairAction
    {
        internal const string EntryParameter = "entry";

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.StartupDisable,
            DisplayName = "Désactiver un programme au démarrage",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Performance,
            Risk = ActionRisk.Low,
            Purpose = "Empêche un programme de se lancer à l'ouverture de session, comme le Gestionnaire des tâches. Réversible.",
            PlainPurpose = "Le programme ne se lance plus tout seul au démarrage ; il reste installé.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
            Requirements = new ActionRequirements
            {
                MinimumBuild = 9200,
                Workaround = "Sous Windows 7 : msconfig, onglet Démarrage, décocher le programme.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        /// <summary>Où l'entrée est déclarée : la clé StartupApproved à écrire, et la ruche.</summary>
        internal (RegistryHive Hive, string ApprovedKey, string Name)? Locate(ActionContext context, string entry)
        {
            bool In(RegistryHive hive, string key) => context.Registry.GetValueNames(hive, key)
                .Any(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase));

            if (In(RegistryHive.CurrentUser, RunKey)) return (RegistryHive.CurrentUser, Approved + "Run", entry);
            if (In(RegistryHive.LocalMachine, RunKey)) return (RegistryHive.LocalMachine, Approved + "Run", entry);
            if (In(RegistryHive.LocalMachine, RunKeyWow)) return (RegistryHive.LocalMachine, Approved + "Run32", entry);

            foreach (var (folder, hive) in new[]
                     {
                         (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser),
                         (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine),
                     })
            {
                foreach (var file in new[] { entry, entry + ".lnk" })
                    if (!string.IsNullOrEmpty(folder) && context.Files.FileExists(Path.Combine(folder, file)))
                        return (hive, Approved + "StartupFolder", file);
            }
            return null;
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var entry = context.Parameter(EntryParameter);
            if (entry == null) return Task.FromResult(NativeOutcome.Block("Aucun programme désigné."));

            var place = Locate(context, entry);
            if (place == null) return Task.FromResult(NativeOutcome.Block("« " + entry + " » ne figure pas parmi les programmes au démarrage de ce PC."));
            if (place.Value.Hive == RegistryHive.LocalMachine && !context.Platform.IsElevated)
                return Task.FromResult(NativeOutcome.Block("« " + entry + " » est déclaré pour tous les utilisateurs : relancez le logiciel en administrateur."));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "« " + entry + " » ne se lancera plus au démarrage.",
                WillDo = new[] { "Le marquer désactivé, comme dans l'onglet Démarrage du Gestionnaire des tâches." },
                WillNotDo = new[] { "Ne désinstalle pas le programme et ne supprime rien : il se réactive d'un clic." },
                Measurements = new[] { new PreviewLine("Portée", place.Value.Hive == RegistryHive.CurrentUser ? "cet utilisateur" : "tous les utilisateurs") },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var entry = context.Parameter(EntryParameter)!;
            var place = Locate(context, entry);
            if (place == null) return Task.FromResult(new ActionOutcome { Status = ActionStatus.Failed, Summary = "Entrée introuvable : rien n'a été fait." });

            // Le format du Gestionnaire des tâches : 03 (désactivé), puis la date de désactivation.
            var data = new byte[12];
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

            var clock = Stopwatch.StartNew();
            var result = context.Control.SetRegistryBinary(place.Value.Hive, place.Value.ApprovedKey, place.Value.Name, data);
            return Task.FromResult(NativeOutcome.From(result, clock.Elapsed, "« " + entry + " » ne se lancera plus au démarrage."));
        }
    }

    /// <summary>
    /// Désactive le démarrage rapide : un « arrêt » qui n'en est pas un, cause classique de
    /// pilotes qui ne se rechargent jamais, de mises à jour qui ne s'achèvent pas, de PC lents
    /// après des semaines sans vrai redémarrage.
    /// </summary>
    public sealed class FastStartupOffAction : IRepairAction
    {
        private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        private const string Value = "HiberbootEnabled";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.FastStartupOff,
            DisplayName = "Désactiver le démarrage rapide",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Low,
            Purpose = "Fait d'un arrêt un vrai arrêt : les pilotes et Windows repartent à neuf à chaque allumage.",
            PlainPurpose = "Éteindre l'ordinateur l'éteint vraiment, et les petits blocages ne survivent plus d'un jour à l'autre.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
            Requirements = new ActionRequirements
            {
                RequiresElevation = true,
                MinimumBuild = 9200,
                Workaround = "Windows 7 n'a pas de démarrage rapide : rien à faire.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Ce réglage de Windows est réservé aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var current = context.Registry.ReadInt32(RegistryHive.LocalMachine, Key, Value);
            if (current == 0)
                return Task.FromResult(new ActionPreview { Outcome = PreviewOutcome.NothingToDo, Summary = "Le démarrage rapide est déjà désactivé." });

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le démarrage rapide va être désactivé.",
                WillDo = new[] { "Désactiver le démarrage rapide (réglage « Activer le démarrage rapide » des options d'alimentation)." },
                WillNotDo = new[] { "Ne touche pas à la veille prolongée ni à la mise en veille." },
                Measurements = new[] { new PreviewLine("Effet", "l'allumage prend quelques secondes de plus", PreviewLineKind.Caution) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var result = context.Control.SetRegistryDword(RegistryHive.LocalMachine, Key, Value, 0);
            return Task.FromResult(NativeOutcome.From(result, clock.Elapsed, "Démarrage rapide désactivé : effectif au prochain arrêt."));
        }
    }

    /// <summary>Le mode d'alimentation : « Économie d'énergie » bridé par erreur est une cause fréquente de lenteur.</summary>
    public sealed class PowerPlanAction : IRepairAction
    {
        internal const string PlanParameter = "plan";

        internal static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        internal static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        internal static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.PowerPlan,
            DisplayName = "Choisir le mode d'alimentation",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Performance,
            Risk = ActionRisk.Low,
            Purpose = "Active le mode « Utilisation normale » (equilibre) ou « Performances élevées » (performances).",
            PlainPurpose = "L'ordinateur arrête de se brider pour économiser l'énergie.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

        private static (Guid Scheme, string Label) Target(ActionContext context)
            => string.Equals(context.Parameter(PlanParameter), "performances", StringComparison.OrdinalIgnoreCase)
                ? (HighPerformance, "Performances élevées")
                : (Balanced, "Utilisation normale");

        private static string Describe(Guid? scheme)
            => scheme == Balanced ? "Utilisation normale" : scheme == HighPerformance ? "Performances élevées"
                : scheme == PowerSaver ? "Économie d'énergie" : scheme.HasValue ? "mode personnalisé" : "inconnu";

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (scheme, label) = Target(context);
            var active = context.Control.ActivePowerScheme();
            if (active == scheme)
                return Task.FromResult(new ActionPreview { Outcome = PreviewOutcome.NothingToDo, Summary = "Le mode « " + label + " » est déjà actif." });
            if (!context.Control.PowerSchemeExists(scheme))
                return Task.FromResult(NativeOutcome.Block("Ce PC ne propose pas le mode « " + label + " » (fréquent sur les portables récents)."));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Passage au mode « " + label + " ».",
                WillDo = new[] { "Activer le mode d'alimentation « " + label + " »." },
                WillNotDo = new[] { "Ne modifie aucun réglage à l'intérieur des modes." },
                Measurements = new[] { new PreviewLine("Mode actuel", Describe(active)) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var (scheme, label) = Target(context);
            var clock = Stopwatch.StartNew();
            return Task.FromResult(NativeOutcome.From(context.Control.ActivatePowerScheme(scheme), clock.Elapsed, "Mode « " + label + " » actif."));
        }
    }

    /// <summary>
    /// Remet l'horloge à l'heure : une horloge décalée fait échouer les sites sécurisés, Windows
    /// Update et l'activation, avec des messages qui ne parlent jamais d'heure.
    /// </summary>
    public sealed class TimeResyncAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.TimeResync,
            DisplayName = "Remettre l'horloge à l'heure",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Low,
            Purpose = "Relance le service de temps Windows et resynchronise l'horloge sur Internet (w32tm /resync).",
            PlainPurpose = "L'ordinateur reprend l'heure exacte, et les sites sécurisés refonctionnent.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(1),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "La resynchronisation de l'horloge est réservée aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "L'horloge va être resynchronisée.",
                WillDo = new[] { "Démarrer le service de temps Windows s'il est arrêté.", "Demander l'heure exacte au serveur de temps." },
                WillNotDo = new[] { "Ne change pas le fuseau horaire." },
                Measurements = new[] { new PreviewLine("Commande", "w32tm /resync") },
            });

        public async Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new ActionProgress("Service de temps…"));
            var started = context.Control.StartService("W32Time", TimeSpan.FromSeconds(30));
            if (!started.Succeeded) return NativeOutcome.From(started, clock.Elapsed);

            progress?.Report(new ActionProgress("Synchronisation…"));
            var result = await context.Processes.RunAsync(
                new ProcessRequest("w32tm.exe", "/resync /rediscover") { Timeout = TimeSpan.FromSeconds(40), OutputEncoding = ConsoleOutputEncoding.OemCodePage },
                cancellationToken).ConfigureAwait(false);

            // w32tm répond 0 en cas de réussite ; un serveur injoignable donne un code non nul.
            return new ActionOutcome
            {
                Status = !result.LaunchFailed && !result.TimedOut && result.ExitCode == 0 ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = !result.LaunchFailed && !result.TimedOut && result.ExitCode == 0
                    ? "Horloge à l'heure."
                    : "Le serveur de temps n'a pas répondu : vérifiez la connexion à Internet, puis réessayez.",
                RawOutput = result.StandardOutput,
                Duration = clock.Elapsed,
            };
        }
    }
}
