using System;
using System.Collections.Generic;

namespace LDI12.Core.Programs
{
    /// <summary>Ce que la signature numérique d'un fichier dit de lui.</summary>
    public enum SignatureState
    {
        /// <summary>Pas encore vérifiée.</summary>
        Unknown,

        /// <summary>Signature présente et valide.</summary>
        Signed,

        /// <summary>Aucune signature : rien ne dit qui a fait le fichier.</summary>
        Unsigned,

        /// <summary>Signature présente mais refusée (fichier modifié, certificat révoqué).</summary>
        Invalid,

        /// <summary>Le fichier n'existe pas (ou plus).</summary>
        Missing,
    }

    /// <summary>L'identité d'un fichier exécutable : qui l'a fait, et si sa signature le prouve.</summary>
    public sealed class FileIdentity
    {
        public string Path { get; init; } = string.Empty;

        public bool Exists { get; init; }

        public string? Company { get; init; }

        public string? Description { get; init; }

        public string? Version { get; init; }

        public SignatureState Signature { get; init; }

        /// <summary>Le titulaire du certificat (« Microsoft Windows », « Google LLC »).</summary>
        public string? Signer { get; init; }

        public bool IsMicrosoft
            => Signature == SignatureState.Signed &&
               ((Signer?.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (Signer == null && (Company?.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0));

        /// <summary>« Google LLC (signé) », « non signé », « introuvable ».</summary>
        public string Describe()
        {
            if (!Exists) return "fichier introuvable";
            var who = Signer ?? Company;
            return Signature switch
            {
                SignatureState.Signed => (who ?? "éditeur inconnu") + " · signé",
                SignatureState.Invalid => (who ?? "éditeur inconnu") + " · signature invalide",
                SignatureState.Unsigned => (Company ?? "éditeur inconnu") + " · non signé",
                _ => Company ?? "éditeur inconnu",
            };
        }
    }

    /// <summary>Lit l'identité d'un fichier : la plateforme vérifie la signature, les tests la simulent.</summary>
    public interface IFileIdentitySource
    {
        FileIdentity Identify(string path);
    }

    /// <summary>Un processus en cours, à l'instant du relevé.</summary>
    public sealed class ProcessEntry
    {
        public int Id { get; init; }

        public int ParentId { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>Chemin complet de l'exécutable ; nul quand Windows le refuse (processus protégé).</summary>
        public string? Path { get; init; }

        /// <summary>Temps processeur cumulé (noyau + utilisateur), en unités de 100 ns.</summary>
        public long CpuTime { get; init; }

        /// <summary>Mémoire propre au processus (octets engagés privés).</summary>
        public long PrivateBytes { get; init; }

        /// <summary>Mémoire physique occupée.</summary>
        public long WorkingSet { get; init; }

        public int Threads { get; init; }

        public int Handles { get; init; }

        public DateTime? StartedUtc { get; init; }

        /// <summary>Session Windows : 0 pour les services, 1 et plus pour les utilisateurs.</summary>
        public int SessionId { get; init; }

        public bool IsAccessible => Path != null;
    }

    /// <summary>Les catégories d'Autoruns, réunies sous des noms qu'un technicien comprend.</summary>
    public enum AutorunCategory
    {
        /// <summary>Clés Run, dossiers Démarrage : lancé à l'ouverture de session.</summary>
        Logon,

        /// <summary>Tâches planifiées ajoutées par des logiciels.</summary>
        Task,

        /// <summary>Services qui démarrent avec Windows.</summary>
        Service,

        /// <summary>Pilotes chargés au démarrage.</summary>
        Driver,

        /// <summary>Extensions de l'Explorateur (menus contextuels, icônes superposées).</summary>
        Explorer,

        /// <summary>Points sensibles : Winlogon, détournements d'exécutables, AppInit.</summary>
        Sensitive,
    }

    /// <summary>Où l'entrée est déclarée, pour savoir comment la désactiver.</summary>
    public enum AutorunKind
    {
        RunKey,
        StartupFolder,
        ScheduledTask,
        Service,
        Driver,
        ShellExtension,
        Winlogon,
        ImageHijack,
        AppInit,
    }

    /// <summary>Une entrée qui se lance toute seule.</summary>
    public sealed class AutorunEntry
    {
        public AutorunCategory Category { get; init; }

        public AutorunKind Kind { get; init; }

        /// <summary>Le nom affiché : valeur du registre, nom du raccourci, de la tâche ou du service.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Où elle est déclarée (« HKLM\…\Run », « Démarrage de l'utilisateur »).</summary>
        public string Location { get; init; } = string.Empty;

        /// <summary>La commande telle qu'écrite.</summary>
        public string Command { get; init; } = string.Empty;

        /// <summary>L'exécutable ou la bibliothèque lancé, extrait de la commande.</summary>
        public string? ImagePath { get; init; }

        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Ce qu'il faut pour la retrouver et la basculer : la clé et la valeur, le chemin du
        /// raccourci, le chemin de la tâche, le nom du service, le CLSID de l'extension.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Ruche de la clé : « HKLM » ou « HKCU », pour une entrée du registre.</summary>
        public string? Hive { get; init; }

        /// <summary>Précision utile : type de démarrage d'un service, déclencheur d'une tâche.</summary>
        public string? Detail { get; init; }

        public FileIdentity? Identity { get; set; }

        /// <summary>Le logiciel sait la désactiver et la réactiver.</summary>
        public bool CanToggle { get; init; }
    }
}
