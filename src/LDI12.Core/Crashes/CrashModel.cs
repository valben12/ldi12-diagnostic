using System;
using System.Collections.Generic;

namespace LDI12.Core.Crashes
{
    /// <summary>Un pilote ou un module chargé au moment de l'écran bleu.</summary>
    public sealed class CrashModule
    {
        public string Name { get; init; } = string.Empty;

        public ulong Base { get; init; }

        public uint Size { get; init; }

        /// <summary>Horodatage de compilation inscrit dans le module : sa date de fabrication.</summary>
        public uint TimeStamp { get; init; }

        /// <summary>Une adresse de ce module figure dans la pile au moment de l'arrêt.</summary>
        public bool InStack { get; set; }

        public bool Contains(ulong address) => address >= Base && Size > 0 && address < Base + Size;

        public DateTime? BuiltUtc => TimeStamp == 0 ? (DateTime?)null : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(TimeStamp);
    }

    /// <summary>Ce qu'un fichier de vidage dit d'un écran bleu.</summary>
    public sealed class CrashDump
    {
        public string File { get; init; } = string.Empty;

        public long FileSize { get; init; }

        public DateTime TimeUtc { get; init; }

        public uint BugCheckCode { get; init; }

        public IReadOnlyList<ulong> Parameters { get; init; } = Array.Empty<ulong>();

        public bool Is64Bit { get; init; }

        /// <summary>« mini », « noyau » ou « complet ».</summary>
        public string Kind { get; init; } = "mini";

        /// <summary>L'instruction en cours au moment de l'arrêt, quand le vidage la porte.</summary>
        public ulong? CrashAddress { get; init; }

        /// <summary>Le module qui contient l'adresse de l'arrêt.</summary>
        public string? CrashModule { get; init; }

        public IReadOnlyList<CrashModule> Modules { get; init; } = Array.Empty<CrashModule>();

        /// <summary>Les modules trouvés dans la pile, dans l'ordre où ils y apparaissent.</summary>
        public IReadOnlyList<string> StackModules { get; init; } = Array.Empty<string>();

        /// <summary>Le pilote mis en cause, et pourquoi.</summary>
        public string? Suspect { get; set; }

        public string? SuspectReason { get; set; }

        /// <summary>Pourquoi le fichier n'a pas pu être lu ; nul s'il l'a été.</summary>
        public string? Error { get; init; }

        public bool IsReadable => Error == null;

        public string CodeText => "0x" + BugCheckCode.ToString("X8");
    }

    /// <summary>Un programme qui plante ou se fige, regroupé par programme et module fautif.</summary>
    public sealed class AppCrash
    {
        public string Application { get; init; } = string.Empty;

        public string? Version { get; init; }

        /// <summary>Le module où l'erreur s'est produite (une DLL du programme, un pilote d'impression…).</summary>
        public string? Module { get; init; }

        public string? ExceptionCode { get; init; }

        public bool IsHang { get; init; }

        public int Count { get; set; }

        public DateTime LastUtc { get; set; }

        public string? Path { get; init; }
    }

    /// <summary>Un écran bleu tel que le journal système l'a noté, avec ou sans fichier de vidage.</summary>
    public sealed class BugCheckEvent
    {
        public DateTime TimeUtc { get; init; }

        public uint? BugCheckCode { get; init; }

        public string? DumpPath { get; init; }
    }

    /// <summary>Le réglage de Windows qui décide si un écran bleu laisse une trace.</summary>
    public sealed class DumpSettings
    {
        /// <summary>0 aucun, 1 complet, 2 noyau, 3 mini, 7 automatique ; nul si illisible.</summary>
        public int? Mode { get; init; }

        public string MinidumpFolder { get; init; } = @"C:\Windows\Minidump";

        public string MemoryDump { get; init; } = @"C:\Windows\MEMORY.DMP";

        public bool Disabled => Mode == 0;

        public string Describe() => Mode switch
        {
            0 => "aucun : un écran bleu ne laisse aucune trace",
            1 => "vidage complet de la mémoire",
            2 => "vidage du noyau",
            3 => "minividage (256 Ko)",
            7 => "automatique",
            null => "illisible",
            _ => "réglage " + Mode,
        };
    }

    /// <summary>Ce que l'on sait d'un module sur le disque : qui l'a fait, quelle version.</summary>
    public sealed class ModuleFile
    {
        public string? Path { get; init; }

        public string? Company { get; init; }

        public string? Description { get; init; }

        public string? Product { get; init; }

        public string? Version { get; init; }

        public DateTime? FileDateUtc { get; init; }

        public bool IsMicrosoft => Company != null && Company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Trouve un module sur le disque : la plateforme lit son fichier, les tests le simulent.</summary>
    public interface IModuleFileSource
    {
        ModuleFile? Find(string moduleName);
    }

    /// <summary>Tout ce qui a été relevé sur les plantages de la machine.</summary>
    public sealed class CrashHistory
    {
        public IReadOnlyList<CrashDump> Dumps { get; init; } = Array.Empty<CrashDump>();

        public IReadOnlyList<BugCheckEvent> BugChecks { get; init; } = Array.Empty<BugCheckEvent>();

        public IReadOnlyList<AppCrash> AppCrashes { get; init; } = Array.Empty<AppCrash>();

        /// <summary>Arrêts brutaux (Kernel-Power 41) sur la période.</summary>
        public int UnexpectedShutdowns { get; init; }

        public int WindowDays { get; init; } = 60;

        public DumpSettings Settings { get; init; } = new DumpSettings();

        /// <summary>Les vidages n'ont pas pu être lus faute de privilèges.</summary>
        public bool DumpsNeedElevation { get; init; }
    }
}
