using System;
using System.Collections.Generic;
using System.Linq;

namespace LDI12.Core.Crashes
{
    /// <summary>La conclusion de l'analyse, dite comme un technicien la dirait au client.</summary>
    public sealed class CrashVerdict
    {
        public string Title { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        public IReadOnlyList<string> Advice { get; init; } = Array.Empty<string>();

        public CrashCause Cause { get; init; }

        /// <summary>« Good », « Warning », « Bad » : la couleur de la carte.</summary>
        public string Tone { get; init; } = "Good";

        /// <summary>Le pilote en cause, quand il y en a un.</summary>
        public string? Suspect { get; init; }

        public ModuleFile? SuspectFile { get; init; }

        /// <summary>L'opération du logiciel qui répond à la conclusion, quand il en porte une.</summary>
        public string? ActionId { get; init; }

        public string? ActionLabel { get; init; }
    }

    /// <summary>
    /// Désigne le pilote en cause dans chaque écran bleu, puis tire une conclusion de l'ensemble :
    /// ce que BlueScreenView montre fichier par fichier, et ce que WhoCrashed en conclut.
    /// </summary>
    /// <remarks>
    /// Le noyau (ntoskrnl, hal) est presque toujours présent dans la pile : il est là où l'erreur
    /// est constatée, rarement là où elle naît. Il n'est donc retenu qu'en dernier recours. Entre
    /// deux pilotes présents, celui d'un tiers passe avant celui de Microsoft : un pilote de
    /// Windows qui plante est le plus souvent appelé par un pilote tiers qui lui a passé de
    /// mauvaises données.
    /// </remarks>
    public static class CrashAnalyzer
    {
        public const string MemoryTest = "TOOL-MEMORY-DIAGNOSTIC";

        private static readonly HashSet<string> Kernel = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll", "halmacpi.dll", "halacpi.dll",
        };

        public static bool IsKernel(string? module) => module != null && Kernel.Contains(module);

        /// <summary>Le pilote mis en cause dans un vidage, et la raison, du signe le plus fort au plus faible.</summary>
        public static void AssignSuspect(CrashDump dump, IModuleFileSource? files)
        {
            if (!dump.IsReadable || dump.Modules.Count == 0) return;
            var info = BugCheckCatalog.Describe(dump.BugCheckCode);

            if (info.AddressParameter >= 0 && info.AddressParameter < dump.Parameters.Count)
            {
                var module = CrashDumpReader.Find(dump.Modules, dump.Parameters[info.AddressParameter]);
                if (module != null && !IsKernel(module.Name))
                {
                    Set(dump, module.Name, "l'adresse de l'erreur, donnée par l'écran bleu, est dans ce pilote");
                    return;
                }
            }

            if (dump.CrashModule != null && !IsKernel(dump.CrashModule))
            {
                Set(dump, dump.CrashModule, "l'arrêt s'est produit dans ce pilote");
                return;
            }

            var inStack = dump.StackModules.Where(m => !IsKernel(m)).ToList();
            var thirdParty = inStack.FirstOrDefault(m => files?.Find(m)?.IsMicrosoft != true);
            var candidate = thirdParty ?? inStack.FirstOrDefault();
            if (candidate != null)
            {
                Set(dump, candidate, "présent dans la pile au moment de l'arrêt");
                return;
            }

            if (dump.CrashModule != null)
                Set(dump, dump.CrashModule, "l'arrêt s'est produit dans le noyau de Windows, sans autre pilote dans la pile");
        }

        private static void Set(CrashDump dump, string module, string reason)
        {
            dump.Suspect = module;
            dump.SuspectReason = reason;
        }

        /// <summary>Le nombre d'écrans bleus : les vidages, plus ceux que le journal a notés sans vidage.</summary>
        public static int CountBlueScreens(CrashHistory history)
        {
            var readable = history.Dumps.Where(d => d.IsReadable).ToList();
            var withoutDump = history.BugChecks.Count(e => !readable.Any(d => Math.Abs((d.TimeUtc - e.TimeUtc).TotalMinutes) < 10));
            return readable.Count + withoutDump;
        }

        public static CrashVerdict Conclude(CrashHistory history, IModuleFileSource? files)
        {
            foreach (var dump in history.Dumps) AssignSuspect(dump, files);

            var dumps = history.Dumps.Where(d => d.IsReadable).OrderByDescending(d => d.TimeUtc).ToList();
            var total = CountBlueScreens(history);
            var settingsAdvice = history.Settings.Disabled
                ? "Windows est réglé pour ne rien enregistrer d'un écran bleu : activez les minividages (opération « Enregistrer les écrans bleus ») pour pouvoir en trouver la cause la prochaine fois."
                : null;

            if (total == 0)
            {
                if (history.UnexpectedShutdowns > 0)
                    return new CrashVerdict
                    {
                        Title = history.UnexpectedShutdowns + (history.UnexpectedShutdowns > 1 ? " arrêts brutaux" : " arrêt brutal") + ", sans écran bleu",
                        Detail = "Le PC s'est éteint d'un coup sans écran bleu : coupure de courant, alimentation, surchauffe, ou bouton maintenu enfoncé.",
                        Advice = Compact(
                            "Vérifiez les températures (écran Surveillance) et la propreté des ventilateurs.",
                            "Sur un portable, faites le test sur batterie puis sur secteur ; sur un fixe, l'alimentation est la première suspecte.",
                            settingsAdvice),
                        Cause = CrashCause.Hardware,
                        Tone = "Warning",
                    };

                return new CrashVerdict
                {
                    Title = "Aucun écran bleu sur " + history.WindowDays + " jours",
                    Detail = history.AppCrashes.Count == 0
                        ? "Ni écran bleu, ni programme qui plante dans les journaux."
                        : "Windows est stable ; seuls des programmes ont planté (voir plus bas).",
                    Advice = Compact(settingsAdvice),
                    Tone = history.Settings.Disabled ? "Warning" : "Good",
                };
            }

            var countText = total + " écran" + Plural(total) + " bleu" + Plural(total) + " sur " + history.WindowDays + " jours";

            // Un même pilote dans la moitié au moins des vidages : c'est lui.
            var suspects = dumps.Where(d => d.Suspect != null && !IsKernel(d.Suspect))
                .GroupBy(d => d.Suspect!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(d => d.TimeUtc))
                .ToList();
            if (suspects.Count > 0 && suspects[0].Count() * 2 >= Math.Max(1, dumps.Count))
            {
                var name = suspects[0].Key;
                var file = files?.Find(name);
                var who = Describe(name, file);
                var times = suspects[0].Count();
                return new CrashVerdict
                {
                    Title = "En cause : " + who,
                    Detail = countText + " ; " + name + " est mis en cause dans " + (times == dumps.Count ? (times == 1 ? "le seul vidage lu" : "tous les vidages lus") : times + " sur " + dumps.Count) + ".",
                    Advice = DriverAdvice(name, file, settingsAdvice),
                    Cause = CrashCause.Driver,
                    Tone = "Bad",
                    Suspect = name,
                    SuspectFile = file,
                    ActionId = file?.IsMicrosoft == true ? null : "DRIVERS-WINDOWS-UPDATE",
                    ActionLabel = file?.IsMicrosoft == true ? null : "Chercher un pilote plus récent",
                };
            }

            // Sinon, la famille de cause qui revient le plus, d'après les codes d'arrêt.
            var codes = dumps.Select(d => d.BugCheckCode)
                .Concat(history.BugChecks.Where(e => e.BugCheckCode.HasValue && !dumps.Any(d => Math.Abs((d.TimeUtc - e.TimeUtc).TotalMinutes) < 10)).Select(e => e.BugCheckCode!.Value))
                .ToList();
            var distinct = codes.Distinct().Count();
            var cause = codes.Count == 0
                ? CrashCause.Unknown
                : codes.GroupBy(c => BugCheckCatalog.Describe(c).Cause).OrderByDescending(g => g.Count()).First().Key;

            // Des codes qui changent à chaque fois, sans pilote qui revient : la mémoire vive.
            if (codes.Count >= 3 && distinct >= 3 && suspects.Count != 1) cause = CrashCause.Memory;

            return cause switch
            {
                CrashCause.Memory => new CrashVerdict
                {
                    Title = "La mémoire vive est la première suspecte",
                    Detail = countText + (distinct > 1 ? ", avec des codes différents et sans pilote qui revient : le signe d'une mémoire qui se corrompt." : " ; le code d'arrêt désigne la mémoire."),
                    Advice = Compact(
                        "Lancez le diagnostic de mémoire Windows (redémarrage, une vingtaine de minutes).",
                        "Si le PC a deux barrettes, testez-le avec une seule, puis avec l'autre.",
                        "Désactivez tout surcadençage de la mémoire (profil XMP/EXPO) dans le BIOS.",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                    ActionId = MemoryTest,
                    ActionLabel = "Diagnostic de mémoire Windows",
                },
                CrashCause.Storage => new CrashVerdict
                {
                    Title = "Le disque est le premier suspect",
                    Detail = countText + " ; Windows n'arrivait plus à relire ses données sur le disque.",
                    Advice = Compact(
                        "Regardez l'état SMART du disque (écran Stockage) ; sauvegardez les données sans attendre s'il est douteux.",
                        "Reprenez le câble SATA et la connexion du disque sur un fixe.",
                        "Vérifiez le système de fichiers (opération « Vérifier le disque »).",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                    ActionId = "CHKDSK",
                    ActionLabel = "Vérifier le disque",
                },
                CrashCause.Hardware => new CrashVerdict
                {
                    Title = "Le matériel a signalé une erreur",
                    Detail = countText + " ; le processeur lui-même a levé l'erreur : ce n'est pas un pilote.",
                    Advice = Compact(
                        "Vérifiez les températures du processeur en charge (écran Surveillance) et le refroidissement.",
                        "Retirez tout surcadençage ou sous-voltage, puis remettez le BIOS par défaut.",
                        "Mettez le BIOS à jour ; si l'erreur persiste, l'alimentation et le processeur sont à tester.",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                },
                CrashCause.Graphics => new CrashVerdict
                {
                    Title = "La carte graphique a cessé de répondre",
                    Detail = countText + " ; le pilote graphique ou la carte elle-même.",
                    Advice = Compact(
                        "Installez le dernier pilote du fabricant de la carte (NVIDIA, AMD, Intel), après avoir désinstallé l'ancien.",
                        "Vérifiez la température de la carte en jeu et la poussière du ventilateur.",
                        "Retirez tout surcadençage de la carte graphique.",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                    ActionId = "DRIVERS-WINDOWS-UPDATE",
                    ActionLabel = "Chercher un pilote plus récent",
                },
                CrashCause.Power => new CrashVerdict
                {
                    Title = "Un pilote ne suit pas la mise en veille",
                    Detail = countText + " ; l'arrêt survient en entrant en veille ou en en sortant.",
                    Advice = Compact(
                        "Mettez à jour les pilotes du chipset, du Wi-Fi et de la carte graphique depuis le site du fabricant du PC.",
                        "En attendant, désactivez le démarrage rapide et la mise en veille des périphériques USB.",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                    ActionId = "FAST-STARTUP-OFF",
                    ActionLabel = "Désactiver le démarrage rapide",
                },
                CrashCause.System => new CrashVerdict
                {
                    Title = "Windows lui-même est en cause",
                    Detail = countText + " ; un processus vital de Windows s'est arrêté.",
                    Advice = Compact(
                        "Réparez les fichiers système (DISM puis SFC).",
                        "Vérifiez le disque : un processus vital qui tombe est souvent un disque qui ne répond plus.",
                        settingsAdvice),
                    Cause = cause,
                    Tone = "Bad",
                    ActionId = "REPAIR-SFC",
                    ActionLabel = "Réparer les fichiers système",
                },
                _ => new CrashVerdict
                {
                    Title = "Un pilote, sans coupable unique",
                    Detail = countText + " ; les pilotes mis en cause changent d'un écran bleu à l'autre.",
                    Advice = Compact(
                        "Mettez à jour les pilotes du chipset, du stockage, du réseau et de la carte graphique.",
                        "Désinstallez les logiciels qui posent des pilotes : antivirus tiers, VPN, outils de surcadençage, émulateurs.",
                        "Si cela continue, testez la mémoire vive.",
                        dumps.Count == 0 ? "Aucun vidage lisible : activez les minividages pour désigner le pilote la prochaine fois." : null,
                        settingsAdvice),
                    Cause = CrashCause.Driver,
                    Tone = "Bad",
                    ActionId = "DRIVERS-WINDOWS-UPDATE",
                    ActionLabel = "Chercher un pilote plus récent",
                },
            };
        }

        /// <summary>« nvlddmkm.sys (NVIDIA Windows Kernel Mode Driver, NVIDIA Corporation) ».</summary>
        public static string Describe(string module, ModuleFile? file)
        {
            if (file == null) return module;
            var parts = new[] { file.Description, file.Company }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            return parts.Count == 0 ? module : module + " (" + string.Join(", ", parts) + ")";
        }

        private static IReadOnlyList<string> DriverAdvice(string module, ModuleFile? file, string? settingsAdvice)
        {
            if (file?.IsMicrosoft == true)
                return Compact(
                    module + " est un pilote de Windows : il est rarement fautif seul. Le vrai coupable est souvent un pilote tiers qui l'appelle (réseau, antivirus, stockage).",
                    "Mettez Windows et les pilotes du matériel concerné à jour.",
                    "Si les écrans bleus continuent, testez la mémoire vive.",
                    settingsAdvice);

            var product = file?.Product ?? file?.Description;
            var vendor = file?.Company;
            var age = file?.FileDateUtc is DateTime date && date < DateTime.UtcNow.AddYears(-2)
                ? " La version installée date de " + date.Year + "."
                : string.Empty;
            return Compact(
                "Mettez à jour " + (product != null ? "« " + product + " »" : "ce pilote") + (vendor != null ? " depuis le site de " + vendor : " depuis le site du fabricant") + "." + age,
                "Si aucune version plus récente n'existe, désinstallez le logiciel qui l'a posé, ou revenez à une version antérieure du pilote.",
                file?.Version != null ? "Version en place : " + file.Version + "." : null,
                settingsAdvice);
        }

        private static IReadOnlyList<string> Compact(params string?[] lines) => lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();

        private static string Plural(int count) => count > 1 ? "s" : string.Empty;
    }
}
