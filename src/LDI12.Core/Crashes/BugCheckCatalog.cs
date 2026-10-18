using System.Collections.Generic;

namespace LDI12.Core.Crashes
{
    /// <summary>La famille de cause qu'un code d'arrêt désigne en premier.</summary>
    public enum CrashCause
    {
        Unknown,
        Driver,
        Memory,
        Storage,
        Hardware,
        Graphics,
        Power,
        System,
    }

    /// <summary>Un code d'arrêt, dit pour un technicien.</summary>
    public sealed class BugCheckInfo
    {
        public BugCheckInfo(uint code, string name, CrashCause cause, int addressParameter, string meaning)
        {
            Code = code;
            Name = name;
            Cause = cause;
            AddressParameter = addressParameter;
            Meaning = meaning;
        }

        public uint Code { get; }

        /// <summary>Le nom affiché sur l'écran bleu (« IRQL_NOT_LESS_OR_EQUAL »).</summary>
        public string Name { get; }

        public CrashCause Cause { get; }

        /// <summary>Le paramètre (0 à 3) qui porte l'adresse du code fautif ; -1 s'il n'y en a pas.</summary>
        public int AddressParameter { get; }

        /// <summary>Ce que cela veut dire, en une phrase.</summary>
        public string Meaning { get; }
    }

    /// <summary>
    /// Les codes d'arrêt qu'un dépanneur rencontre, avec ce qu'ils accusent d'abord. Les
    /// définitions suivent la documentation des codes d'arrêt de Microsoft ; le sens donné est
    /// celui du terrain, pas la traduction littérale.
    /// </summary>
    public static class BugCheckCatalog
    {
        private static readonly Dictionary<uint, BugCheckInfo> Known = new Dictionary<uint, BugCheckInfo>();

        static BugCheckCatalog()
        {
            void Add(uint code, string name, CrashCause cause, int address, string meaning)
                => Known[code] = new BugCheckInfo(code, name, cause, address, meaning);

            Add(0x0A, "IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver, 3, "Un pilote a touché une zone de mémoire interdite à ce moment-là.");
            Add(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver, 3, "Un pilote a lu une adresse mémoire invalide : presque toujours un pilote défectueux (réseau, Wi-Fi, antivirus, USB).");
            Add(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", CrashCause.Driver, 1, "Un pilote a provoqué une erreur qu'il n'a pas su traiter.");
            Add(0x3B, "SYSTEM_SERVICE_EXCEPTION", CrashCause.Driver, 1, "Une erreur s'est produite pendant un appel au noyau : pilote graphique, antivirus ou logiciel de protection en tête.");
            Add(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashCause.Driver, 1, "Un fil d'exécution système a planté : le pilote nommé est le premier suspect.");
            Add(0x1000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", CrashCause.Driver, 1, "Un fil d'exécution système a planté : le pilote nommé est le premier suspect.");
            Add(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", CrashCause.Driver, 1, "Une erreur non traitée dans le noyau : pilote, ou mémoire vive défaillante.");
            Add(0x1000008E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", CrashCause.Driver, 1, "Une erreur non traitée dans le noyau : pilote, ou mémoire vive défaillante.");
            Add(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", CrashCause.Memory, 2, "Accès à une page mémoire qui n'existe pas : pilote défectueux, mémoire vive ou disque.");
            Add(0x1A, "MEMORY_MANAGEMENT", CrashCause.Memory, -1, "La gestion de la mémoire a trouvé une incohérence : la mémoire vive est la première suspecte.");
            Add(0x4E, "PFN_LIST_CORRUPT", CrashCause.Memory, -1, "La table des pages mémoire est corrompue : mémoire vive, ou pilote qui écrit où il ne doit pas.");
            Add(0x19, "BAD_POOL_HEADER", CrashCause.Memory, -1, "Une zone mémoire du noyau est abîmée : pilote ou mémoire vive.");
            Add(0xC2, "BAD_POOL_CALLER", CrashCause.Driver, -1, "Un pilote a mal demandé ou rendu de la mémoire.");
            Add(0xC5, "DRIVER_CORRUPTED_EXPOOL", CrashCause.Driver, -1, "Un pilote a abîmé la mémoire du noyau.");
            Add(0x109, "CRITICAL_STRUCTURE_CORRUPTION", CrashCause.Memory, -1, "Une structure vitale du noyau a été modifiée : mémoire vive, pilote ou logiciel d'émulation.");
            Add(0x12B, "FAULTY_HARDWARE_CORRUPTED_PAGE", CrashCause.Memory, -1, "Une page mémoire s'est corrompue d'elle-même : barrette de mémoire défaillante.");
            Add(0xD5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", CrashCause.Driver, 2, "Un pilote a utilisé de la mémoire déjà rendue.");
            Add(0xD6, "DRIVER_PAGE_FAULT_BEYOND_END_OF_ALLOCATION", CrashCause.Driver, 2, "Un pilote a débordé de la mémoire qui lui était attribuée.");
            Add(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", CrashCause.Driver, -1, "Un pilote a tenté d'écrire dans une zone en lecture seule.");
            Add(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", CrashCause.Driver, -1, "Un pilote a tenté d'exécuter des données : pilote défectueux ou logiciel malveillant.");
            Add(0x133, "DPC_WATCHDOG_VIOLATION", CrashCause.Driver, -1, "Un pilote a monopolisé le processeur trop longtemps : souvent le contrôleur de stockage (SSD) ou son micrologiciel.");
            Add(0x139, "KERNEL_SECURITY_CHECK_FAILURE", CrashCause.Driver, -1, "Le noyau a détecté une structure corrompue : pilote incompatible, parfois mémoire vive.");
            Add(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", CrashCause.Driver, -1, "Le vérificateur de pilotes a pris un pilote en faute.");
            Add(0xC9, "DRIVER_VERIFIER_IOMANAGER_VIOLATION", CrashCause.Driver, -1, "Le vérificateur de pilotes a pris un pilote en faute.");
            Add(0xCE, "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", CrashCause.Driver, -1, "Un pilote s'est déchargé en laissant du travail en cours.");
            Add(0x4A, "IRQL_GT_ZERO_AT_SYSTEM_SERVICE", CrashCause.Driver, -1, "Un pilote a rendu la main au système dans un mauvais état.");
            Add(0x7C, "BUGCODE_NDIS_DRIVER", CrashCause.Driver, -1, "Le pilote d'une carte réseau a commis une erreur.");
            Add(0xFE, "BUGCODE_USB_DRIVER", CrashCause.Driver, -1, "Le pilote USB a commis une erreur : périphérique USB ou pilote du contrôleur.");
            Add(0x144, "BUGCODE_USB3_DRIVER", CrashCause.Driver, -1, "Le pilote USB 3 a commis une erreur : périphérique USB ou pilote du contrôleur.");
            Add(0x116, "VIDEO_TDR_FAILURE", CrashCause.Graphics, 1, "La carte graphique a cessé de répondre et n'a pas pu être relancée : pilote graphique, surchauffe ou carte.");
            Add(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", CrashCause.Graphics, 1, "La carte graphique a cessé de répondre.");
            Add(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", CrashCause.Graphics, -1, "Le planificateur graphique a trouvé une erreur : pilote graphique.");
            Add(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", CrashCause.Graphics, -1, "Erreur de mémoire graphique : pilote graphique ou carte.");
            Add(0x24, "NTFS_FILE_SYSTEM", CrashCause.Storage, -1, "Erreur du système de fichiers : disque abîmé ou système de fichiers à vérifier.");
            Add(0x7A, "KERNEL_DATA_INPAGE_ERROR", CrashCause.Storage, -1, "Windows n'a pas pu relire une page depuis le disque : disque, câble ou contrôleur.");
            Add(0x77, "KERNEL_STACK_INPAGE_ERROR", CrashCause.Storage, -1, "Windows n'a pas pu relire une page depuis le disque : disque, câble ou contrôleur.");
            Add(0x7B, "INACCESSIBLE_BOOT_DEVICE", CrashCause.Storage, -1, "Windows ne trouve plus son disque au démarrage : mode du contrôleur (AHCI/RAID) ou disque.");
            Add(0xED, "UNMOUNTABLE_BOOT_VOLUME", CrashCause.Storage, -1, "Le volume de Windows ne peut pas être monté : système de fichiers ou disque.");
            Add(0x154, "UNEXPECTED_STORE_EXCEPTION", CrashCause.Storage, -1, "Erreur du magasin de mémoire compressée : très souvent le disque ou son pilote.");
            Add(0xF4, "CRITICAL_OBJECT_TERMINATION", CrashCause.Storage, -1, "Un processus vital s'est arrêté : souvent un disque qui ne répond plus.");
            Add(0xEF, "CRITICAL_PROCESS_DIED", CrashCause.System, -1, "Un processus vital de Windows s'est arrêté : fichiers système, disque ou logiciel de sécurité.");
            Add(0xC000021A, "WINLOGON_FATAL_ERROR", CrashCause.System, -1, "Le sous-système de session a cessé de fonctionner : fichiers système ou mise à jour ratée.");
            Add(0x9F, "DRIVER_POWER_STATE_FAILURE", CrashCause.Power, -1, "Un pilote n'a pas suivi la mise en veille ou la sortie de veille.");
            Add(0xA0, "INTERNAL_POWER_ERROR", CrashCause.Power, -1, "Erreur de gestion de l'alimentation : veille prolongée ou pilote.");
            Add(0x124, "WHEA_UNCORRECTABLE_ERROR", CrashCause.Hardware, -1, "Le matériel a signalé une erreur fatale : processeur, chaleur, alimentation ou surcadençage.");
            Add(0x9C, "MACHINE_CHECK_EXCEPTION", CrashCause.Hardware, -1, "Le processeur a signalé une erreur matérielle.");
            Add(0x101, "CLOCK_WATCHDOG_TIMEOUT", CrashCause.Hardware, -1, "Un cœur du processeur a cessé de répondre : processeur, surcadençage, chaleur ou BIOS.");
            Add(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", CrashCause.Hardware, -1, "Le processeur a levé une erreur que le noyau ne peut pas traiter : matériel ou pilote.");
            Add(0xE2, "MANUALLY_INITIATED_CRASH", CrashCause.System, -1, "Écran bleu provoqué volontairement depuis le clavier.");
            Add(0xDEADDEAD, "MANUALLY_INITIATED_CRASH1", CrashCause.System, -1, "Écran bleu provoqué volontairement.");
        }

        public static BugCheckInfo Describe(uint code)
            => Known.TryGetValue(code, out var info)
                ? info
                : new BugCheckInfo(code, "0x" + code.ToString("X8"), CrashCause.Unknown, -1, "Code d'arrêt peu courant : le pilote mis en cause reste la meilleure piste.");

        public static string Explain(CrashCause cause) => cause switch
        {
            CrashCause.Driver => "un pilote",
            CrashCause.Memory => "la mémoire vive",
            CrashCause.Storage => "le disque",
            CrashCause.Hardware => "le matériel",
            CrashCause.Graphics => "la carte graphique",
            CrashCause.Power => "la gestion de l'alimentation",
            CrashCause.System => "Windows lui-même",
            _ => "une cause indéterminée",
        };
    }
}
