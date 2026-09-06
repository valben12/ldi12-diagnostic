using System;
using System.Collections.Generic;
using System.IO;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Platform;

namespace LDI12.Actions.Tools
{
    /// <summary>
    /// Une console Windows que le technicien peut ouvrir depuis le diagnostic.
    /// </summary>
    /// <remarks>
    /// Ce ne sont pas des actions : elles ne modifient rien par elles-mêmes et n'ont donc ni
    /// prévisualisation ni compte rendu. Elles vivent à part de <c>IRepairAction</c> pour cette
    /// raison : confondre « ouvrir un outil » et « intervenir sur la machine » brouillerait la
    /// seule distinction que le journal d'intervention doit conserver.
    /// </remarks>
    public sealed class WindowsTool
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;

        /// <summary>Ce qu'on y trouve, en une ligne.</summary>
        public string Purpose { get; init; } = string.Empty;

        public DiagnosticCategory Category { get; init; }

        /// <summary>Nom du fichier, tel qu'il apparaît dans le dossier système.</summary>
        public string FileName { get; init; } = string.Empty;

        public string Arguments { get; init; } = string.Empty;

        /// <summary>Vrai si le fichier vit dans %SystemRoot% et non dans System32.</summary>
        public bool InWindowsDirectory { get; init; }

        /// <summary>
        /// Console absente des éditions Famille.
        /// </summary>
        /// <remarks>
        /// Le griser sans rien dire laisserait croire à une machine abîmée. C'est une décision
        /// de Microsoft sur l'édition, pas un défaut de ce PC, et le technicien doit pouvoir le
        /// dire au client sans hésiter.
        /// </remarks>
        public bool ProfessionalEditionOnly { get; init; }

        /// <summary>Windows demandera lui-même l'élévation à l'ouverture.</summary>
        public bool PromptsForElevation { get; init; }

        public int? MinimumBuild { get; init; }
    }

    public sealed class WindowsToolState
    {
        public WindowsTool Tool { get; init; } = new WindowsTool();

        public bool Available { get; init; }

        /// <summary>Renseignée si et seulement si l'outil n'est pas disponible.</summary>
        public string? Reason { get; init; }

        /// <summary>Chemin complet retenu, quand l'outil existe.</summary>
        public string? Path { get; init; }
    }

    public static class WindowsToolCatalog
    {
        public static IReadOnlyList<WindowsTool> All { get; } = Build();

        public static WindowsTool? Find(string id)
        {
            foreach (var tool in All)
                if (string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)) return tool;
            return null;
        }

        /// <summary>
        /// État réel de chaque console sur cette machine.
        /// </summary>
        /// <remarks>
        /// L'édition passe avant la présence du fichier, et c'est contre-intuitif, mais
        /// <c>lusrmgr.msc</c> est bel et bien livré avec les éditions Famille de Windows 10 et 11,
        /// où la console refuse ensuite de s'ouvrir : « ce composant logiciel enfichable ne peut
        /// pas être utilisé avec cette édition ». Se fier au fichier proposerait donc un bouton
        /// qui ne peut qu'échouer. Pour les trois consoles réservées aux éditions
        /// professionnelles, c'est l'édition qui fait foi ; pour toutes les autres, la présence.
        /// </remarks>
        public static IReadOnlyList<WindowsToolState> Inspect(IPlatformInfo platform, IFileSystemGateway files)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            if (files == null) throw new ArgumentNullException(nameof(files));

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var states = new List<WindowsToolState>(All.Count);

            foreach (var tool in All)
            {
                var directory = tool.InWindowsDirectory ? windows : system32;
                var path = string.IsNullOrEmpty(directory) ? tool.FileName : Path.Combine(directory, tool.FileName);

                if (tool.MinimumBuild.HasValue && platform.Profile.Build < tool.MinimumBuild.Value)
                {
                    states.Add(new WindowsToolState
                    {
                        Tool = tool,
                        Reason = "Cette console n'existe pas sur " + platform.Profile.DisplayName + ".",
                    });
                    continue;
                }

                if (tool.ProfessionalEditionOnly && IsHomeEdition(platform.Profile))
                {
                    states.Add(new WindowsToolState
                    {
                        Tool = tool,
                        Reason = "L'édition " + (platform.Profile.EditionLabel ?? "Famille") +
                                 " de Windows ne donne pas accès à cette console. Ce n'est pas un défaut " +
                                 "de cette machine.",
                    });
                    continue;
                }

                if (files.FileExists(path))
                {
                    states.Add(new WindowsToolState { Tool = tool, Available = true, Path = path });
                    continue;
                }

                states.Add(new WindowsToolState
                {
                    Tool = tool,
                    Reason = "Console absente de cette installation de Windows (" + tool.FileName + " introuvable).",
                });
            }

            return states;
        }

        internal static bool IsHomeEdition(WindowsProfile profile)
        {
            var edition = profile.EditionId ?? string.Empty;
            return edition.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   edition.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   edition.IndexOf("Starter", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyList<WindowsTool> Build() => new[]
        {
            new WindowsTool
            {
                Id = "TOOL-DEVICE-MANAGER", Title = "Gestionnaire de périphériques",
                Purpose = "Matériel reconnu, pilotes manquants et périphériques en erreur.",
                Category = DiagnosticCategory.Hardware, FileName = "devmgmt.msc",
            },
            new WindowsTool
            {
                Id = "TOOL-DISK-MANAGEMENT", Title = "Gestion des disques",
                Purpose = "Partitions, lettres de lecteur, volumes non montés.",
                Category = DiagnosticCategory.Storage, FileName = "diskmgmt.msc",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-COMPUTER-MANAGEMENT", Title = "Gestion de l'ordinateur",
                Purpose = "Console qui regroupe périphériques, disques, services et journaux.",
                Category = DiagnosticCategory.Windows, FileName = "compmgmt.msc",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-SERVICES", Title = "Services",
                Purpose = "État et type de démarrage de chaque service Windows.",
                Category = DiagnosticCategory.Windows, FileName = "services.msc",
            },
            new WindowsTool
            {
                Id = "TOOL-EVENT-VIEWER", Title = "Observateur d'événements",
                Purpose = "Journaux système et application : erreurs, avertissements, arrêts inattendus.",
                Category = DiagnosticCategory.Windows, FileName = "eventvwr.msc",
            },
            new WindowsTool
            {
                Id = "TOOL-TASK-SCHEDULER", Title = "Planificateur de tâches",
                Purpose = "Tâches programmées, y compris celles ajoutées par des logiciels tiers.",
                Category = DiagnosticCategory.Windows, FileName = "taskschd.msc",
            },
            new WindowsTool
            {
                Id = "TOOL-TASK-MANAGER", Title = "Gestionnaire des tâches",
                Purpose = "Processus en cours, charge processeur et mémoire, démarrage automatique.",
                Category = DiagnosticCategory.Performance, FileName = "taskmgr.exe",
            },
            new WindowsTool
            {
                Id = "TOOL-RESOURCE-MONITOR", Title = "Moniteur de ressources",
                Purpose = "Détail de l'activité disque, réseau et mémoire, processus par processus.",
                Category = DiagnosticCategory.Performance, FileName = "resmon.exe",
            },
            new WindowsTool
            {
                Id = "TOOL-RELIABILITY", Title = "Moniteur de fiabilité",
                Purpose = "Historique des pannes de la machine sur une frise, très lisible en clientèle.",
                Category = DiagnosticCategory.Windows, FileName = "perfmon.exe", Arguments = "/rel",
            },
            new WindowsTool
            {
                Id = "TOOL-PERFORMANCE", Title = "Analyseur de performances",
                Purpose = "Compteurs de performance détaillés et collecteurs de données.",
                Category = DiagnosticCategory.Performance, FileName = "perfmon.msc",
            },
            new WindowsTool
            {
                Id = "TOOL-SYSTEM-INFO", Title = "Informations système",
                Purpose = "Inventaire complet du matériel et des composants logiciels.",
                Category = DiagnosticCategory.Hardware, FileName = "msinfo32.exe",
            },
            new WindowsTool
            {
                Id = "TOOL-POWER-OPTIONS", Title = "Options d'alimentation",
                Purpose = "Plan d'alimentation actif et bridage du processeur.",
                Category = DiagnosticCategory.Performance, FileName = "powercfg.cpl",
            },
            new WindowsTool
            {
                Id = "TOOL-SYSTEM-PROPERTIES", Title = "Propriétés système",
                Purpose = "Options de démarrage et de récupération, dont l'enregistrement des plantages.",
                Category = DiagnosticCategory.Windows, FileName = "sysdm.cpl",
            },
            new WindowsTool
            {
                Id = "TOOL-SYSTEM-CONFIG", Title = "Configuration du système",
                Purpose = "Options de démarrage, services et mode sans échec.",
                Category = DiagnosticCategory.Windows, FileName = "msconfig.exe",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-REGISTRY", Title = "Éditeur du registre",
                Purpose = "Base de configuration de Windows. À n'ouvrir qu'en sachant ce qu'on cherche.",
                Category = DiagnosticCategory.Windows, FileName = "regedit.exe",
                InWindowsDirectory = true, PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-FIREWALL", Title = "Pare-feu avec fonctions avancées",
                Purpose = "Règles entrantes et sortantes, profils réseau.",
                Category = DiagnosticCategory.Security, FileName = "wf.msc",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-PROGRAMS", Title = "Programmes et fonctionnalités",
                Purpose = "Logiciels installés et fonctionnalités facultatives de Windows.",
                Category = DiagnosticCategory.Windows, FileName = "appwiz.cpl",
            },
            new WindowsTool
            {
                Id = "TOOL-DISK-CLEANUP", Title = "Nettoyage de disque",
                Purpose = "Nettoyage intégré de Windows, y compris les anciennes installations.",
                Category = DiagnosticCategory.Storage, FileName = "cleanmgr.exe",
            },
            new WindowsTool
            {
                Id = "TOOL-MEMORY-DIAGNOSTIC", Title = "Diagnostic de mémoire Windows",
                Purpose = "Test de la mémoire vive au redémarrage suivant.",
                Category = DiagnosticCategory.Hardware, FileName = "mdsched.exe",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-SYSTEM-RESTORE", Title = "Restauration du système",
                Purpose = "Retour à un point de restauration antérieur.",
                Category = DiagnosticCategory.Windows, FileName = "rstrui.exe",
                PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-LOCAL-USERS", Title = "Utilisateurs et groupes locaux",
                Purpose = "Comptes locaux, appartenance au groupe Administrateurs.",
                Category = DiagnosticCategory.Security, FileName = "lusrmgr.msc",
                ProfessionalEditionOnly = true, PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-GROUP-POLICY", Title = "Stratégie de groupe locale",
                Purpose = "Stratégies appliquées à la machine et aux utilisateurs.",
                Category = DiagnosticCategory.Security, FileName = "gpedit.msc",
                ProfessionalEditionOnly = true, PromptsForElevation = true,
            },
            new WindowsTool
            {
                Id = "TOOL-SECURITY-POLICY", Title = "Stratégie de sécurité locale",
                Purpose = "Stratégies de mot de passe, de verrouillage et d'audit.",
                Category = DiagnosticCategory.Security, FileName = "secpol.msc",
                ProfessionalEditionOnly = true, PromptsForElevation = true,
            },
        };
    }
}
