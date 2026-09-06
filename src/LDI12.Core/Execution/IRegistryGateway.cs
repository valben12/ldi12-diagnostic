using System.Collections.Generic;
using Microsoft.Win32;

namespace LDI12.Core.Execution
{
    /// <summary>
    /// Lecture du registre, en lecture seule et sans exception.
    /// </summary>
    /// <remarks>
    /// Le processus étant compilé en AnyCPU, il s'exécute en 64 bits sur un Windows x64 et voit
    /// donc la vue native. La vue est néanmoins explicite à chaque appel, car certaines
    /// informations (logiciels installés, pilotes 32 bits) n'existent que sous WOW6432Node.
    /// </remarks>
    public interface IRegistryGateway
    {
        string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default);

        int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default);

        long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default);

        string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default);

        /// <summary>
        /// Valeur binaire brute.
        /// </summary>
        /// <remarks>
        /// Ajoutée pour un seul cas, et qui le vaut : la configuration de proxy de la machine 
        /// (celle que suit Windows Update) n'existe que sous forme d'un bloc binaire. Sans cette
        /// lecture, le logiciel ne peut pas dire pourquoi les mises à jour échouent alors que le
        /// navigateur fonctionne.
        /// </remarks>
        byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default);

        bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default);

        IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default);

        IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default);
    }

    /// <summary>Chemins de registre utilisés à plusieurs endroits de l'application.</summary>
    public static class RegistryPaths
    {
        public const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        public const string Bios = @"HARDWARE\DESCRIPTION\System\BIOS";
        public const string SystemProcessor = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        public const string PowerShellEngine = @"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine";
        public const string PowerShellEngineV1 = @"SOFTWARE\Microsoft\PowerShell\1\PowerShellEngine";
        public const string SecureBootState = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
        public const string SystemRestoreConfig = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        public const string UninstallX64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        public const string UninstallX86 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        public const string RunMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        public const string Personalize = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    }
}
