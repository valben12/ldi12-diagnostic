using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Programs;
using Microsoft.Win32;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Désactive ou réactive une entrée de démarrage, toujours de façon réversible : rien n'est
    /// supprimé. Chaque emplacement a sa manière, celle que Windows ou Autoruns emploient déjà.
    /// </summary>
    /// <remarks>
    /// Deux identifiants pour une même opération : sans privilèges pour ce qui appartient à
    /// l'utilisateur (sa clé Run, son dossier Démarrage), par l'hôte élevé pour le reste. Une
    /// clé de l'utilisateur modifiée depuis l'hôte élevé toucherait le profil de l'administrateur
    /// qui a validé l'invite, pas celui de la session.
    /// </remarks>
    public sealed class AutorunToggleAction : IRepairAction
    {
        public const string KindParameter = "kind";
        public const string KeyParameter = "key";
        public const string HiveParameter = "hive";
        public const string NameParameter = "name";
        public const string EnableParameter = "enable";

        internal const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        internal const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        internal const string DisabledSubKey = "AutorunsDisabled";
        internal const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
        internal const string BlockedExtensions = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
        internal const string PreviousStart = "LDI12PreviousStart";

        private readonly bool _elevated;

        public AutorunToggleAction(bool elevated)
        {
            _elevated = elevated;
            Descriptor = new ActionDescriptor
            {
                Id = elevated ? ActionIds.AutorunToggleAdmin : ActionIds.AutorunToggle,
                DisplayName = "Activer ou désactiver au démarrage",
                Kind = ActionKind.Repair,
                Category = DiagnosticCategory.Performance,
                Risk = ActionRisk.Low,
                Purpose = "Empêche un programme, une tâche, un service ou une extension de se lancer tout seul, ou le rétablit. Réversible.",
                PlainPurpose = "Le PC démarre sans ce qui n'a rien à y faire.",
                TypicalDuration = TimeSpan.FromSeconds(1),
                HardTimeout = TimeSpan.FromSeconds(30),
                Requirements = new ActionRequirements { RequiresElevation = elevated },
            };
        }

        public ActionDescriptor Descriptor { get; }

        public ActionReadiness CheckReadiness(ActionContext context)
            => _elevated ? NativeOutcome.Elevated(context, "Windows réserve ce réglage aux administrateurs") : ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            if (!Enum.TryParse<AutorunKind>(context.Parameter(KindParameter), out var kind) || string.IsNullOrWhiteSpace(context.Parameter(KeyParameter)))
                return Task.FromResult(NativeOutcome.Block("Entrée de démarrage non désignée."));

            var enable = context.Parameter(EnableParameter) == "1";
            var name = context.Parameter(NameParameter) ?? context.Parameter(KeyParameter)!;

            if (!enable && (kind == AutorunKind.Driver || kind == AutorunKind.Service))
            {
                var start = context.Registry.ReadInt32(RegistryHive.LocalMachine, ServicesKey + "\\" + context.Parameter(KeyParameter), "Start");
                if (start == 0)
                    return Task.FromResult(NativeOutcome.Block("« " + name + " » se charge avec le noyau : le désactiver peut empêcher Windows de démarrer."));
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "« " + name + " » va être " + (enable ? "réactivé" : "désactivé") + " au démarrage.",
                WillDo = new[] { Describe(kind, enable) },
                WillNotDo = new[] { "Ne supprime ni le programme, ni l'entrée : tout se rétablit d'un clic." },
            });
        }

        private static string Describe(AutorunKind kind, bool enable) => kind switch
        {
            AutorunKind.RunKey or AutorunKind.StartupFolder => enable
                ? "Le rétablir dans la liste de démarrage, comme le Gestionnaire des tâches."
                : "Le désactiver comme le fait le Gestionnaire des tâches (onglet Démarrage).",
            AutorunKind.ScheduledTask => enable ? "Réactiver la tâche planifiée." : "Désactiver la tâche planifiée.",
            AutorunKind.Service => enable ? "Rendre au service son type de démarrage d'origine." : "Passer le service en « désactivé » ; son type d'origine est noté pour le rétablir.",
            AutorunKind.Driver => enable ? "Rendre au pilote son type de démarrage d'origine." : "Empêcher le pilote de se charger ; son type d'origine est noté pour le rétablir.",
            AutorunKind.ShellExtension => enable ? "Retirer l'extension de la liste des extensions bloquées." : "Bloquer l'extension dans l'Explorateur (liste officielle des extensions bloquées).",
            _ => "Basculer l'entrée.",
        };

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            Enum.TryParse<AutorunKind>(context.Parameter(KindParameter), out var kind);
            var key = context.Parameter(KeyParameter)!;
            var enable = context.Parameter(EnableParameter) == "1";
            var hive = context.Parameter(HiveParameter) == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var name = context.Parameter(NameParameter) ?? key;

            var result = kind switch
            {
                AutorunKind.RunKey => RunKeyToggle(context, hive, key, enable),
                AutorunKind.StartupFolder => context.Control.SetRegistryBinary(hive, Approved + "StartupFolder", Path.GetFileName(key), State(enable)),
                AutorunKind.ScheduledTask => context.Control.SetTaskEnabled(key, enable),
                AutorunKind.Service or AutorunKind.Driver => ServiceToggle(context, key, enable, kind == AutorunKind.Driver),
                AutorunKind.ShellExtension => enable
                    ? context.Control.DeleteRegistryValue(RegistryHive.LocalMachine, BlockedExtensions, key)
                    : context.Control.SetRegistryString(RegistryHive.LocalMachine, BlockedExtensions, key, name),
                _ => ControlResult.Fail("Cette entrée ne se bascule pas d'ici."),
            };

            return Task.FromResult(NativeOutcome.From(result, clock.Elapsed,
                "« " + name + " » " + (enable ? "réactivé" : "désactivé") + (kind == AutorunKind.Service || kind == AutorunKind.Driver ? " ; effet complet au prochain démarrage." : ".")));
        }

        /// <summary>
        /// Windows 8 et plus : la clé StartupApproved, que le Gestionnaire des tâches lit et écrit.
        /// Windows 7 : la valeur est rangée dans la sous-clé « AutorunsDisabled », comme Autoruns.
        /// </summary>
        private static ControlResult RunKeyToggle(ActionContext context, RegistryHive hive, string key, bool enable)
        {
            var bar = key.IndexOf('|');
            if (bar <= 0) return ControlResult.Fail("Entrée mal désignée.");
            var subKey = key.Substring(0, bar);
            var value = key.Substring(bar + 1);

            if (context.Platform.Profile.Build >= 9200)
            {
                var approved = string.Equals(subKey, RunKeyWow, StringComparison.OrdinalIgnoreCase) ? "Run32" : "Run";
                return context.Control.SetRegistryBinary(hive, Approved + approved, value, State(enable));
            }

            var from = enable ? subKey + "\\" + DisabledSubKey : subKey;
            var to = enable ? subKey : subKey + "\\" + DisabledSubKey;
            var command = context.Registry.ReadString(hive, from, value);
            if (command == null) return ControlResult.Fail("La valeur n'existe plus à cet endroit.");
            var written = context.Control.SetRegistryString(hive, to, value, command);
            return written.Succeeded ? context.Control.DeleteRegistryValue(hive, from, value) : written;
        }

        private static ControlResult ServiceToggle(ActionContext context, string name, bool enable, bool driver)
        {
            var key = ServicesKey + "\\" + name;
            if (!enable)
            {
                var current = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, "Start") ?? 2;
                var noted = context.Control.SetRegistryDword(RegistryHive.LocalMachine, key, PreviousStart, current);
                return noted.Succeeded ? context.Control.SetServiceStart(name, 4) : noted;
            }

            var previous = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, PreviousStart) ?? (driver ? 3 : 2);
            var restored = context.Control.SetServiceStart(name, previous);
            if (restored.Succeeded) context.Control.DeleteRegistryValue(RegistryHive.LocalMachine, key, PreviousStart);
            return restored;
        }

        /// <summary>Douze octets : 2 actif ; 3 désactivé, suivi de l'heure de la désactivation.</summary>
        internal static byte[] State(bool enable)
        {
            var data = new byte[12];
            data[0] = enable ? (byte)2 : (byte)3;
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            return data;
        }
    }

    /// <summary>Met fin à un ou plusieurs processus, comme « Terminer l'arborescence » de Process Explorer.</summary>
    public sealed class ProcessEndAction : IRepairAction
    {
        public const string IdsParameter = "ids";
        public const string NamesParameter = "names";

        /// <summary>Les processus sans lesquels Windows s'arrête net : jamais terminés d'ici.</summary>
        private static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe",
            "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "Processus inactif",
        };

        private readonly bool _elevated;

        public ProcessEndAction(bool elevated)
        {
            _elevated = elevated;
            Descriptor = new ActionDescriptor
            {
                Id = elevated ? ActionIds.ProcessEndAdmin : ActionIds.ProcessEnd,
                DisplayName = "Terminer un processus",
                Kind = ActionKind.Repair,
                Category = DiagnosticCategory.Performance,
                Risk = ActionRisk.Moderate,
                Purpose = "Met fin à un programme qui ne répond plus, et à ceux qu'il a lancés.",
                PlainPurpose = "Le programme bloqué est fermé de force.",
                TypicalDuration = TimeSpan.FromSeconds(1),
                HardTimeout = TimeSpan.FromSeconds(30),
                Requirements = new ActionRequirements { RequiresElevation = elevated },
            };
        }

        public ActionDescriptor Descriptor { get; }

        public ActionReadiness CheckReadiness(ActionContext context)
            => _elevated ? NativeOutcome.Elevated(context, "Ce processus appartient au système ou à un autre compte") : ActionReadiness.Ready;

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var (ids, names) = Targets(context);
            if (ids.Count == 0) return Task.FromResult(NativeOutcome.Block("Aucun processus désigné."));
            var critical = names.FirstOrDefault(n => Critical.Contains(n));
            if (critical != null) return Task.FromResult(NativeOutcome.Block("« " + critical + " » est vital pour Windows : le terminer arrêterait le PC net."));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = ids.Count == 1 ? "« " + names[0] + " » va être terminé." : names[0] + " et " + (ids.Count - 1) + " processus lancé(s) par lui vont être terminés.",
                WillDo = new[] { "Fermer de force, sans enregistrer : ce qui n'était pas enregistré est perdu." },
                WillNotDo = new[] { "Ne désinstalle rien ; le programme pourra être relancé." },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var (ids, names) = Targets(context);
            var failures = new List<string>();
            var denied = false;
            // Les enfants d'abord : un parent terminé avant eux pourrait les relancer.
            for (var index = ids.Count - 1; index >= 0; index--)
            {
                var result = context.Control.EndProcess(ids[index]);
                if (result.Succeeded) continue;
                failures.Add(names[index] + " : " + result.Message);
                denied |= result.ErrorCode == 5;
            }

            return Task.FromResult(new ActionOutcome
            {
                Status = failures.Count == 0 ? ActionStatus.Succeeded : failures.Count < ids.Count ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = failures.Count == 0
                    ? (ids.Count == 1 ? "« " + names[0] + " » terminé." : ids.Count + " processus terminés.")
                    : denied ? "Refusé : privilèges administrateur nécessaires." : string.Join(" ; ", failures),
                Details = failures,
                Duration = clock.Elapsed,
            });
        }

        private static (List<int> Ids, List<string> Names) Targets(ActionContext context)
        {
            var ids = (context.Parameter(IdsParameter) ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, out var id) ? id : -1).Where(id => id > 4).ToList();
            var names = (context.Parameter(NamesParameter) ?? string.Empty).Split('|').ToList();
            while (names.Count < ids.Count) names.Add("processus " + ids[names.Count]);
            return (ids, names);
        }
    }
}
