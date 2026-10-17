using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Une piste de place à regagner, repérée dans l'arbre.</summary>
    public sealed class DiskUsageHint
    {
        public DiskUsageHint(DiskNode node, string title, string advice)
        {
            Node = node;
            Title = title;
            Advice = advice;
        }

        public DiskNode Node { get; }

        public string Title { get; }

        /// <summary>Ce qu'il convient d'en faire, en une phrase.</summary>
        public string Advice { get; }
    }

    /// <summary>
    /// Ce que l'analyse sait des emplacements de Windows : ce qui se regagne, et ce qu'il ne faut
    /// jamais mettre à la corbeille d'ici.
    /// </summary>
    public static class DiskUsageAdvisor
    {
        /// <summary>En dessous, une piste ne vaut pas la ligne qu'elle occupe.</summary>
        public const long HintThreshold = 500L * 1024 * 1024;

        private static readonly string[] ProtectedFolders =
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users",
            "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI",
        };

        private static readonly string[] ProtectedFiles =
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log.tmp",
        };

        /// <summary>
        /// La raison de refuser la corbeille pour cet élément, ou null s'il peut y aller.
        /// </summary>
        /// <remarks>
        /// Une carte du disque donne envie de supprimer ce qui est gros, et ce qui est gros sur
        /// un PC est souvent ce qui le fait démarrer. Refus net pour le système et les dossiers
        /// de premier niveau ; le reste passe par la corbeille, donc se rattrape.
        /// </remarks>
        public static string? Refusal(DiskNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (node.Parent == null) return "C'est le point de départ de l'analyse.";

            for (var current = node; current != null; current = current.Parent)
            {
                // Tout ce qui est sous Windows appartient au système, à une exception près, ses fichiers temporaires.
                if (current.IsDirectory && Is(current.Name, "Windows") && IsVolumeRoot(current.Parent) && !UnderWindowsTemp(node))
                    return "Fichier de Windows : la place se regagne par le Nettoyage, pas à la main.";
            }

            if (IsVolumeRoot(node.Parent))
            {
                foreach (var name in ProtectedFolders)
                    if (node.IsDirectory && Is(node.Name, name)) return "Dossier du système, à laisser en place.";
                foreach (var name in ProtectedFiles)
                    if (!node.IsDirectory && Is(node.Name, name))
                        return Is(name, "hiberfil.sys")
                            ? "Fichier de veille prolongée : il disparaît en désactivant la veille prolongée (powercfg /h off)."
                            : "Fichier du système, géré par Windows.";
            }

            if (node.IsDirectory && node.Parent != null && Is(node.Parent.Name, "Users") && IsVolumeRoot(node.Parent.Parent))
                return "Profil d'utilisateur entier : à supprimer depuis les paramètres de Windows.";

            if (node.IsDirectory && (Is(node.Name, "Program Files") || Is(node.Name, "Program Files (x86)")))
                return "Dossier des programmes : désinstaller plutôt que supprimer.";

            return null;
        }

        /// <summary>Les grosses pistes connues, de la plus lourde à la plus légère.</summary>
        public static IReadOnlyList<DiskUsageHint> Hints(DiskUsageTree tree)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            var hints = new List<DiskUsageHint>();
            var root = tree.Root;

            void Add(DiskNode? node, string title, string advice)
            {
                if (node != null && node.Size >= HintThreshold) hints.Add(new DiskUsageHint(node, title, advice));
            }

            Add(Child(root, "Windows.old"), "Ancienne installation de Windows",
                "Se supprime par le Nettoyage, une fois la mise à niveau jugée réussie.");
            Add(Child(root, "$Recycle.Bin"), "Corbeille", "Vider la corbeille rend cette place aussitôt.");
            Add(Child(root, "hiberfil.sys"), "Fichier de veille prolongée",
                "Inutile si la veille prolongée ne sert pas : powercfg /h off le supprime.");
            Add(Child(Child(root, "Windows"), "Temp"), "Fichiers temporaires de Windows", "Le Nettoyage s'en charge.");
            Add(Child(Child(Child(root, "Windows"), "SoftwareDistribution"), "Download"), "Téléchargements de Windows Update",
                "Déjà installés pour la plupart : le Nettoyage les retire.");

            var users = Child(root, "Users");
            if (users != null)
            {
                foreach (var profile in users.Children)
                {
                    if (!profile.IsDirectory) continue;
                    Add(Child(profile, "Downloads"), "Téléchargements de " + profile.Name,
                        "Souvent des installateurs déjà utilisés : à trier avec le client.");
                    Add(Child(Child(Child(profile, "AppData"), "Local"), "Temp"), "Fichiers temporaires de " + profile.Name,
                        "Le Nettoyage s'en charge.");
                }
            }

            hints.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
            return hints;
        }

        private static DiskNode? Child(DiskNode? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.Children)
                if (Is(child.Name, name)) return child;
            return null;
        }

        private static bool UnderWindowsTemp(DiskNode node)
        {
            for (var current = node; current?.Parent != null; current = current.Parent)
                if (Is(current.Name, "Temp") && Is(current.Parent.Name, "Windows") && current != node) return true;
            return false;
        }

        /// <summary>La racine d'un volume, « C:\ » : pas un dossier quelconque choisi pour l'analyse.</summary>
        private static bool IsVolumeRoot(DiskNode? node)
        {
            if (node == null || node.Parent != null) return false;
            var name = node.Name.TrimEnd('\\', '/');
            return name.Length == 2 && name[1] == ':' || Path.GetPathRoot(node.Name) == node.Name && node.Name.Length > 0;
        }

        private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
