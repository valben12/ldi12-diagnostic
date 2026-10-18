using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LDI12.Collectors.Internal;
using LDI12.Core.Execution;
using LDI12.Core.Programs;
using Microsoft.Win32;

namespace LDI12.Collectors.Programs
{
    /// <summary>
    /// Tout ce qui se lance tout seul, réparti comme Autoruns le fait : ouverture de session,
    /// tâches planifiées, services, pilotes, extensions de l'Explorateur, points sensibles.
    /// </summary>
    /// <remarks>
    /// Lecture seule. L'état « désactivé » est lu là où Windows et Autoruns le rangent : la clé
    /// StartupApproved du Gestionnaire des tâches (Windows 8 et plus), la sous-clé
    /// « AutorunsDisabled », le type de démarrage d'un service, la liste des extensions bloquées.
    /// </remarks>
    public sealed class AutorunsCollector
    {
        public const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        public const string RunKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        public const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        public const string PolicyRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
        public const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        public const string DisabledSubKey = "AutorunsDisabled";
        public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
        public const string BlockedExtensions = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
        public const string PreviousStartValue = "LDI12PreviousStart";
        private const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private const string ImageOptions = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string WindowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";

        private readonly IRegistryGateway _registry;
        private readonly string _windows;
        private readonly bool _modern;

        /// <param name="registry">La passerelle de registre de la plateforme.</param>
        /// <param name="build">Le build de Windows : StartupApproved n'existe qu'à partir de Windows 8 (9200).</param>
        public AutorunsCollector(IRegistryGateway registry, int build)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _modern = build >= 9200;
        }

        public IReadOnlyList<AutorunEntry> Collect(CancellationToken cancellationToken)
        {
            var entries = new List<AutorunEntry>();
            Guard(() => Logon(entries));
            cancellationToken.ThrowIfCancellationRequested();
            Guard(() => Tasks(entries, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            Guard(() => Services(entries));
            cancellationToken.ThrowIfCancellationRequested();
            Guard(() => Explorer(entries));
            Guard(() => Sensitive(entries));
            return entries;
        }

        // ============================================================ ouverture de session

        private void Logon(List<AutorunEntry> entries)
        {
            Run(entries, RegistryHive.CurrentUser, RunKey, "HKCU\\…\\Run", "Run");
            Run(entries, RegistryHive.LocalMachine, RunKey, "HKLM\\…\\Run", "Run");
            if (Environment.Is64BitOperatingSystem) Run(entries, RegistryHive.LocalMachine, RunKeyWow, "HKLM\\…\\Run (32 bits)", "Run32");
            Run(entries, RegistryHive.CurrentUser, RunOnceKey, "HKCU\\…\\RunOnce", null);
            Run(entries, RegistryHive.LocalMachine, RunOnceKey, "HKLM\\…\\RunOnce", null);
            Run(entries, RegistryHive.CurrentUser, PolicyRunKey, "Stratégie HKCU\\…\\Run", null);
            Run(entries, RegistryHive.LocalMachine, PolicyRunKey, "Stratégie HKLM\\…\\Run", null);

            Folder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Démarrage de l'utilisateur", RegistryHive.CurrentUser);
            Folder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Démarrage de tous les utilisateurs", RegistryHive.LocalMachine);
        }

        private void Run(List<AutorunEntry> entries, RegistryHive hive, string key, string location, string? approvedKey)
        {
            var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            foreach (var name in _registry.GetValueNames(hive, key))
            {
                var command = _registry.ReadString(hive, key, name);
                if (string.IsNullOrWhiteSpace(command)) continue;
                entries.Add(new AutorunEntry
                {
                    Category = AutorunCategory.Logon,
                    Kind = AutorunKind.RunKey,
                    Name = name.Length == 0 ? "(par défaut)" : name,
                    Location = location,
                    Command = command!,
                    ImagePath = Image(command),
                    Enabled = approvedKey == null || !_modern || IsApproved(hive, approvedKey, name),
                    Key = key + "|" + name,
                    Hive = hiveName,
                    CanToggle = approvedKey != null,
                });
            }

            // Ce qu'Autoruns, ou ce logiciel sous Windows 7, a désactivé en le rangeant à part.
            var disabledKey = key + "\\" + DisabledSubKey;
            foreach (var name in _registry.GetValueNames(hive, disabledKey))
            {
                var command = _registry.ReadString(hive, disabledKey, name);
                if (string.IsNullOrWhiteSpace(command)) continue;
                entries.Add(new AutorunEntry
                {
                    Category = AutorunCategory.Logon,
                    Kind = AutorunKind.RunKey,
                    Name = name,
                    Location = location,
                    Command = command!,
                    ImagePath = Image(command),
                    Enabled = false,
                    Key = key + "|" + name,
                    Hive = hiveName,
                    Detail = "désactivé par Autoruns",
                    CanToggle = false,
                });
            }
        }

        /// <summary>Le Gestionnaire des tâches note l'état dans le premier octet : impair, désactivé.</summary>
        private bool IsApproved(RegistryHive hive, string approvedKey, string name)
        {
            var value = _registry.ReadBinary(hive, Approved + approvedKey, name);
            return value == null || value.Length == 0 || (value[0] & 1) == 0;
        }

        private void Folder(List<AutorunEntry> entries, string folder, string location, RegistryHive hive)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : null;
                entries.Add(new AutorunEntry
                {
                    Category = AutorunCategory.Logon,
                    Kind = AutorunKind.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Location = location,
                    Command = target ?? file,
                    ImagePath = target ?? file,
                    Enabled = !_modern || IsApproved(hive, "StartupFolder", fileName),
                    Key = file,
                    Hive = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM",
                    CanToggle = _modern,
                });
            }
        }

        private static string? ShortcutTarget(string shortcut)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(type);
                dynamic link = ((dynamic)shell!).CreateShortcut(shortcut);
                string target = link.TargetPath;
                string arguments = link.Arguments;
                return string.IsNullOrWhiteSpace(target) ? null : target + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
            }
            catch (Exception ex) when (ex is COMException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException || ex is InvalidCastException)
            {
                return null;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        // ============================================================ tâches planifiées

        private void Tasks(List<AutorunEntry> entries, CancellationToken cancellationToken)
        {
            var tasks = TaskSchedulerReader.Read(cancellationToken);
            foreach (var task in tasks.Tasks)
            {
                entries.Add(new AutorunEntry
                {
                    Category = AutorunCategory.Task,
                    Kind = AutorunKind.ScheduledTask,
                    Name = task.Name,
                    Location = "Planificateur : " + task.Path,
                    Command = task.ImagePath ?? "(composant COM)",
                    ImagePath = task.ImagePath == null ? null : Image(task.ImagePath),
                    Enabled = task.Enabled,
                    Key = task.Path,
                    Detail = string.Join(", ", task.Triggers.Select(Describe).Distinct()),
                    CanToggle = true,
                });
            }
        }

        private static string Describe(Core.Model.TaskTriggerKind trigger) => trigger switch
        {
            Core.Model.TaskTriggerKind.Logon => "à l'ouverture de session",
            Core.Model.TaskTriggerKind.Boot => "au démarrage",
            _ => trigger.ToString().ToLowerInvariant(),
        };

        // ============================================================ services et pilotes

        private void Services(List<AutorunEntry> entries)
        {
            foreach (var name in _registry.GetSubKeyNames(RegistryHive.LocalMachine, ServicesKey))
            {
                var key = ServicesKey + "\\" + name;
                var type = _registry.ReadInt32(RegistryHive.LocalMachine, key, "Type");
                var start = _registry.ReadInt32(RegistryHive.LocalMachine, key, "Start");
                var image = _registry.ReadString(RegistryHive.LocalMachine, key, "ImagePath");
                if (!type.HasValue || !start.HasValue) continue;

                var previous = _registry.ReadInt32(RegistryHive.LocalMachine, key, PreviousStartValue);
                var isDriver = (type.Value & 0x3) != 0;
                var isService = (type.Value & 0x30) != 0;
                // Ce qui démarre avec Windows, plus ce que ce logiciel a désactivé et sait rétablir.
                var autoStart = isDriver ? start.Value <= 2 : start.Value == 2;
                if (!autoStart && !(start.Value == 4 && previous.HasValue)) continue;
                if (!isDriver && !isService) continue;

                var display = _registry.ReadString(RegistryHive.LocalMachine, key, "DisplayName");
                if (display != null && display.StartsWith("@", StringComparison.Ordinal)) display = null; // ressource non résolue
                var dll = isService ? _registry.ReadString(RegistryHive.LocalMachine, key + "\\Parameters", "ServiceDll") : null;
                var delayed = _registry.ReadInt32(RegistryHive.LocalMachine, key, "DelayedAutostart") == 1;

                entries.Add(new AutorunEntry
                {
                    Category = isDriver ? AutorunCategory.Driver : AutorunCategory.Service,
                    Kind = isDriver ? AutorunKind.Driver : AutorunKind.Service,
                    Name = display ?? name,
                    Location = "Service " + name,
                    Command = image ?? string.Empty,
                    ImagePath = dll != null ? ImagePaths.Normalize(Environment.ExpandEnvironmentVariables(dll), _windows, Path.Combine(_windows, "System32")) : Image(image),
                    Enabled = start.Value != 4,
                    Key = name,
                    Detail = start.Value switch
                    {
                        0 => "démarrage du noyau",
                        1 => "démarrage du système",
                        2 => delayed ? "automatique (différé)" : "automatique",
                        4 => "désactivé",
                        _ => "manuel",
                    },
                    CanToggle = true,
                });
            }
        }

        // ============================================================ explorateur

        private void Explorer(List<AutorunEntry> entries)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Handlers(string key, string where)
            {
                foreach (var name in _registry.GetSubKeyNames(RegistryHive.LocalMachine, key))
                {
                    var clsid = _registry.ReadString(RegistryHive.LocalMachine, key + "\\" + name, string.Empty);
                    if (string.IsNullOrWhiteSpace(clsid) || !clsid!.StartsWith("{", StringComparison.Ordinal)) clsid = name.StartsWith("{", StringComparison.Ordinal) ? name : null;
                    if (clsid == null || !seen.Add(clsid + where)) continue;

                    var dll = _registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Classes\CLSID\" + clsid + @"\InprocServer32", string.Empty);
                    if (string.IsNullOrWhiteSpace(dll)) continue;
                    var blocked = _registry.GetValueNames(RegistryHive.LocalMachine, BlockedExtensions)
                        .Any(v => string.Equals(v, clsid, StringComparison.OrdinalIgnoreCase));

                    entries.Add(new AutorunEntry
                    {
                        Category = AutorunCategory.Explorer,
                        Kind = AutorunKind.ShellExtension,
                        Name = name.Trim(),
                        Location = where,
                        Command = dll!,
                        ImagePath = Image(dll),
                        Enabled = !blocked,
                        Key = clsid,
                        Hive = "HKLM",
                        CanToggle = true,
                    });
                }
            }

            Handlers(@"SOFTWARE\Classes\*\shellex\ContextMenuHandlers", "Menu contextuel des fichiers");
            Handlers(@"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers", "Menu contextuel des dossiers");
            Handlers(@"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers", "Menu du fond des dossiers");
            Handlers(@"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers", "Menu contextuel des dossiers");
            Handlers(@"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers", "Menu contextuel des lecteurs");
            Handlers(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", "Icônes superposées");
        }

        // ============================================================ points sensibles

        private void Sensitive(List<AutorunEntry> entries)
        {
            var shell = _registry.ReadString(RegistryHive.LocalMachine, Winlogon, "Shell");
            if (!string.IsNullOrWhiteSpace(shell) && !string.Equals(shell!.Trim(), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                entries.Add(SensitiveEntry(AutorunKind.Winlogon, "Shell", "Winlogon : l'interface lancée à l'ouverture de session", shell, "attendu : explorer.exe"));

            var userinit = _registry.ReadString(RegistryHive.LocalMachine, Winlogon, "Userinit");
            if (!string.IsNullOrWhiteSpace(userinit))
            {
                var parts = userinit!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                foreach (var part in parts.Where(p => !p.EndsWith("userinit.exe", StringComparison.OrdinalIgnoreCase)))
                    entries.Add(SensitiveEntry(AutorunKind.Winlogon, "Userinit", "Winlogon : lancé avec userinit", part, "attendu : userinit.exe seul"));
            }

            foreach (var exe in _registry.GetSubKeyNames(RegistryHive.LocalMachine, ImageOptions))
            {
                var debugger = _registry.ReadString(RegistryHive.LocalMachine, ImageOptions + "\\" + exe, "Debugger");
                if (string.IsNullOrWhiteSpace(debugger)) continue;
                entries.Add(SensitiveEntry(AutorunKind.ImageHijack, exe, "Détournement : lancer " + exe + " lance autre chose", debugger!,
                    "Image File Execution Options"));
            }

            var appInit = _registry.ReadString(RegistryHive.LocalMachine, WindowsKey, "AppInit_DLLs");
            if (!string.IsNullOrWhiteSpace(appInit) && _registry.ReadInt32(RegistryHive.LocalMachine, WindowsKey, "LoadAppInit_DLLs") == 1)
                foreach (var dll in appInit!.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    entries.Add(SensitiveEntry(AutorunKind.AppInit, Path.GetFileName(dll), "AppInit : chargée dans chaque programme", dll, "AppInit_DLLs"));
        }

        private AutorunEntry SensitiveEntry(AutorunKind kind, string name, string location, string command, string detail) => new AutorunEntry
        {
            Category = AutorunCategory.Sensitive,
            Kind = kind,
            Name = name,
            Location = location,
            Command = command,
            ImagePath = Image(command),
            Enabled = true,
            Key = name,
            Hive = "HKLM",
            Detail = detail,
            CanToggle = false,
        };

        private string? Image(string? command) => ImagePaths.Extract(command, _windows, SafeExists);

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return false; }
        }

        private static void Guard(Action read)
        {
            try { read(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException || ex is OperationCanceledException))
            {
                // Une catégorie illisible n'empêche pas les autres.
            }
        }
    }
}
