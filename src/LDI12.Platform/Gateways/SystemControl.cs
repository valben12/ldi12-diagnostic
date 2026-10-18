using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Platform.Native;
using Microsoft.Win32;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Les gestes natifs des réparations : périphériques, services, registre, alimentation,
    /// pilotes de Windows Update. Voir <see cref="ISystemControl"/>.
    /// </summary>
    public sealed class SystemControl : ISystemControl
    {
        private const string Category = "SystemControl";

        private readonly IPlatformInfo _platform;
        private readonly ILdiLogger _logger;

        public SystemControl(IPlatformInfo platform, ILdiLogger logger)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ControlResult RescanDevices() => Log("Recherche de matériel", DeviceNative.Rescan());

        public DeviceState? FindDevice(string instanceId)
        {
            try { return DeviceNative.Find(instanceId); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _logger.Warn(Category, "Lecture du périphérique impossible.", ex);
                return null;
            }
        }

        public ControlResult RestartDevice(string instanceId) => Log("Redémarrage de " + instanceId, Guard(() => DeviceNative.Restart(instanceId)));

        public ControlResult EnableDevice(string instanceId) => Log("Activation de " + instanceId, Guard(() => DeviceNative.Enable(instanceId)));

        public ControlResult ReinstallDevice(string instanceId) => Log("Réinstallation de " + instanceId, Guard(() => DeviceNative.Reinstall(instanceId)));

        public string? ServiceStatus(string name)
        {
            try
            {
                using var service = new ServiceController(name);
                return service.Status.ToString();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is ArgumentException)
            {
                return null;
            }
        }

        public ControlResult StopService(string name, TimeSpan timeout, out IReadOnlyList<string> stoppedDependents)
        {
            var dependents = new List<string>();
            stoppedDependents = dependents;
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Stopped) return ControlResult.Ok("Service « " + name + " » déjà arrêté.");

                // Les dépendants qui tournent sont notés : on les relancera après.
                foreach (var dependent in service.DependentServices)
                    using (dependent)
                        if (dependent.Status == ServiceControllerStatus.Running) dependents.Add(dependent.ServiceName);

                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                return Log("Arrêt de " + name, ControlResult.Ok("Service « " + name + " » arrêté."));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return Log("Arrêt de " + name, ControlResult.Fail("Le service « " + name + " » ne s'est pas arrêté à temps."));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return Log("Arrêt de " + name, ControlResult.Fail("Arrêt du service « " + name + " » refusé : " + Reason(ex) + "."));
            }
        }

        public ControlResult StartService(string name, TimeSpan timeout)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Running) return ControlResult.Ok("Service « " + name + " » en marche.");
                if (service.Status != ServiceControllerStatus.StartPending) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                return Log("Démarrage de " + name, ControlResult.Ok("Service « " + name + " » relancé."));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return Log("Démarrage de " + name, ControlResult.Fail("Le service « " + name + " » n'a pas redémarré à temps."));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return Log("Démarrage de " + name, ControlResult.Fail("Démarrage du service « " + name + " » refusé : " + Reason(ex) + "."));
            }
        }

        public ControlResult ClearFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return ControlResult.Ok("Dossier absent : rien à vider.");

            int removed = 0, locked = 0;
            long bytes = 0;
            foreach (var file in SafeFiles(path))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    removed++;
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    locked++;
                }
            }

            foreach (var directory in SafeDirectories(path))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { locked++; }
            }

            var message = removed + " fichier(s) retiré(s), " + Core.Diagnostics.ValueFormat.Bytes(bytes)
                          + (locked > 0 ? " ; " + locked + " élément(s) encore utilisé(s), laissé(s) en place" : string.Empty) + ".";
            return Log("Vidage de " + path, ControlResult.Ok(message));
        }

        public ControlResult SetRegistryDword(RegistryHive hive, string subKey, string name, int value)
            => WriteRegistry(hive, subKey, name, value, RegistryValueKind.DWord);

        public ControlResult SetRegistryBinary(RegistryHive hive, string subKey, string name, byte[] value)
            => WriteRegistry(hive, subKey, name, value, RegistryValueKind.Binary);

        public ControlResult SetRegistryString(RegistryHive hive, string subKey, string name, string value)
            => WriteRegistry(hive, subKey, name, value ?? string.Empty,
                (value ?? string.Empty).IndexOf('%') >= 0 ? RegistryValueKind.ExpandString : RegistryValueKind.String);

        public ControlResult DeleteRegistryValue(RegistryHive hive, string subKey, string name)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, NativeView());
                using var key = root.OpenSubKey(subKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
                return Log("Registre " + subKey + "\\" + name + " supprimé", ControlResult.Ok("Valeur supprimée."));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                return Log("Registre " + subKey + "\\" + name, ControlResult.Fail("Suppression refusée : privilèges administrateur nécessaires."));
            }
        }

        public ControlResult SetServiceStart(string name, int start)
            => Guard(() =>
            {
                var error = ServiceNative.ChangeStart(name, start);
                return Log("Démarrage du service " + name + " → " + start, error == 0
                    ? ControlResult.Ok("Type de démarrage changé.")
                    : ControlResult.Fail(error == 5 ? "Refusé : privilèges administrateur nécessaires." : "Windows a refusé : " + new Win32Exception(error).Message + ".", error));
            });

        public ControlResult SetTaskEnabled(string path, bool enabled)
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return ControlResult.Fail("Le planificateur de tâches n'est pas disponible.");
            object? service = null;
            try
            {
                service = Activator.CreateInstance(type)!;
                type.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, service, Array.Empty<object>());
                var root = type.InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, service, new object[] { "\\" })!;
                var task = root.GetType().InvokeMember("GetTask", System.Reflection.BindingFlags.InvokeMethod, null, root, new object[] { path })!;
                task.GetType().InvokeMember("Enabled", System.Reflection.BindingFlags.SetProperty, null, task, new object[] { enabled });
                return Log("Tâche " + path + (enabled ? " activée" : " désactivée"), ControlResult.Ok(enabled ? "Tâche réactivée." : "Tâche désactivée."));
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException || ex.InnerException is System.Runtime.InteropServices.COMException)
            {
                return Log("Tâche " + path, ControlResult.Fail(ex.InnerException is UnauthorizedAccessException
                    ? "Refusé : cette tâche appartient au système ou à un autre compte."
                    : "Le planificateur a refusé : " + ex.InnerException!.Message));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is UnauthorizedAccessException)
            {
                return Log("Tâche " + path, ControlResult.Fail("Le planificateur a refusé : " + ex.Message));
            }
            finally
            {
                if (service != null && System.Runtime.InteropServices.Marshal.IsComObject(service)) System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
            }
        }

        public ControlResult EndProcess(int processId)
            => Guard(() => Log("Fin du processus " + processId, ProcessNative.Terminate(processId, out var reason)
                ? ControlResult.Ok("Processus terminé.")
                : ControlResult.Fail("Windows a refusé : " + reason + ".", reason != null && reason.StartsWith("accès refusé", StringComparison.Ordinal) ? 5 : 0)));

        [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool SetupUninstallOEMInfW(string infFileName, uint flags, IntPtr reserved);

        public ControlResult DeleteDriverPackage(string publishedName)
            => Guard(() =>
            {
                // Sans l'option de forçage : un paquet en service est refusé par Windows lui-même.
                if (SetupUninstallOEMInfW(publishedName, 0, IntPtr.Zero))
                    return Log("Paquet " + publishedName + " retiré", ControlResult.Ok("Paquet retiré du magasin."));
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                var reason = unchecked((uint)error) == 0xE000023D
                    ? "un périphérique présent l'utilise"
                    : error == 5 ? "privilèges administrateur nécessaires" : new Win32Exception(error).Message;
                return Log("Paquet " + publishedName, ControlResult.Fail(reason, error));
            });

        private RegistryView NativeView()
            => _platform.Profile.NativeArchitecture == ProcessorArchitecture.X64 || Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Registry32;

        public Guid? ActivePowerScheme()
        {
            try { return PowerSchemeNative.Active(); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return null; }
        }

        public bool PowerSchemeExists(Guid scheme)
        {
            try { return PowerSchemeNative.Exists(scheme); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return false; }
        }

        public ControlResult ActivatePowerScheme(Guid scheme)
        {
            try
            {
                var error = PowerSchemeNative.Activate(scheme);
                return Log("Mode d'alimentation " + scheme, error == 0
                    ? ControlResult.Ok("Mode d'alimentation appliqué.")
                    : ControlResult.Fail("Windows a refusé ce mode d'alimentation : " + new Win32Exception(error).Message + ".", error));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return ControlResult.Fail("Gestion de l'alimentation indisponible sur ce Windows.");
            }
        }

        public IReadOnlyList<AudioOutputState> AudioEndpoints()
        {
            try { return AudioNative.Endpoints(); }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is InvalidComObjectException)
            {
                _logger.Warn(Category, "Lecture des sorties audio impossible.", ex);
                return Array.Empty<AudioOutputState>();
            }
        }

        public ControlResult SetDefaultAudioEndpoint(string endpointId)
            => Log("Sortie audio par défaut", Audio(() => AudioNative.SetDefault(endpointId)));

        public ControlResult RestoreAudioVolume(string? endpointId)
            => Log("Volume audio", Audio(() => AudioNative.RestoreVolume(endpointId)));

        public ControlResult PlayTestSound()
            => Log("Son de test", Audio(AudioNative.PlayTone));

        private static ControlResult Audio(Func<ControlResult> action)
        {
            try { return action(); }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is InvalidComObjectException
                                       || ex is InvalidOperationException || ex is System.TimeoutException)
            {
                return ControlResult.Fail("Le son de Windows n'a pas répondu : " + ex.Message);
            }
        }

        public Task<DriverUpdateSearch> SearchDriverUpdatesAsync(CancellationToken cancellationToken)
            => WindowsUpdateAgent.SearchAsync(cancellationToken);

        public async Task<ControlResult> InstallDriverUpdatesAsync(
            IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken)
            => Log("Pilotes Windows Update", await WindowsUpdateAgent.InstallAsync(updateIds, progress, cancellationToken).ConfigureAwait(false));

        private ControlResult WriteRegistry(RegistryHive hive, string subKey, string name, object value, RegistryValueKind kind)
        {
            try
            {
                // La vue 64 bits sur un Windows 64 bits : c'est elle que Windows lit pour ces réglages.
                var view = _platform.Profile.NativeArchitecture == ProcessorArchitecture.X64 || Environment.Is64BitOperatingSystem
                    ? RegistryView.Registry64
                    : RegistryView.Registry32;
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.CreateSubKey(subKey);
                if (key == null) return ControlResult.Fail("Clé de registre inaccessible.");
                key.SetValue(name, value, kind);
                return Log("Registre " + subKey + "\\" + name, ControlResult.Ok("Réglage enregistré."));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                return Log("Registre " + subKey + "\\" + name, ControlResult.Fail("Écriture refusée : privilèges administrateur nécessaires."));
            }
        }

        private ControlResult Guard(Func<ControlResult> action)
        {
            try { return action(); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return ControlResult.Fail("Fonction absente de ce Windows : " + ex.Message);
            }
        }

        private ControlResult Log(string what, ControlResult result)
        {
            if (result.Succeeded) _logger.Info(Category, what + " : " + result.Message);
            else _logger.Warn(Category, what + " : " + result.Message);
            return result;
        }

        private static string Reason(Exception ex)
            => ex.InnerException is Win32Exception { NativeErrorCode: 5 } || ex is Win32Exception { NativeErrorCode: 5 }
                ? "privilèges administrateur nécessaires"
                : (ex.InnerException ?? ex).Message;

        private static IEnumerable<string> SafeFiles(string path)
        {
            try { return Directory.GetFiles(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }
}
