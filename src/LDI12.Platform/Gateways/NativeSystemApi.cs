using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    public sealed class NativeSystemApi : INativeSystemApi
    {
        private const string Category = "Platform.Native";

        private readonly IScopedLogger _log;

        public NativeSystemApi(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public Measured<MemoryStatus> ReadMemoryStatus()
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX)),
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                var error = Marshal.GetLastWin32Error();
                _log.Warn("GlobalMemoryStatusEx a échoué (code " + error + ").");
                return Measured.Missing<MemoryStatus>(
                    "L'état de la mémoire n'a pas pu être lu (erreur système " + error + ").", DataSource.NativeApi);
            }

            var total = (long)status.ullTotalPhys;
            var available = (long)status.ullAvailPhys;
            var commitLimit = (long)status.ullTotalPageFile;
            var committed = commitLimit - (long)status.ullAvailPageFile;

            return Measured.Ok(new MemoryStatus
            {
                TotalBytes = total,
                AvailableBytes = available,
                CommittedBytes = committed,
                CommitLimitBytes = commitLimit,
                UsagePercent = total > 0 ? Math.Round(100d * (total - available) / total, 1) : 0d,
            }, DataSource.NativeApi);
        }

        public async Task<Measured<double>> ReadCpuUsagePercentAsync(TimeSpan sampleWindow, CancellationToken cancellationToken)
        {
            if (!TryReadSystemTimes(out var idle1, out var kernel1, out var user1))
                return Measured.Missing<double>("La charge processeur n'a pas pu être mesurée.", DataSource.NativeApi);

            try
            {
                await Task.Delay(sampleWindow, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Measured.Missing<double>("Mesure de charge interrompue.", DataSource.NativeApi);
            }

            if (!TryReadSystemTimes(out var idle2, out var kernel2, out var user2))
                return Measured.Missing<double>("La charge processeur n'a pas pu être mesurée.", DataSource.NativeApi);

            // kernel inclut déjà idle : le temps réellement occupé est (kernel + user) - idle.
            var idle = idle2 - idle1;
            var busy = (kernel2 - kernel1) + (user2 - user1);
            if (busy <= 0)
                return Measured.Missing<double>("Fenêtre de mesure trop courte pour être exploitable.", DataSource.NativeApi);

            var usage = 100d * (busy - idle) / busy;
            return Measured.Ok(Math.Round(Math.Max(0d, Math.Min(100d, usage)), 1), DataSource.NativeApi);
        }

        public Measured<bool> ReadRunningOnBattery()
        {
            try
            {
                if (!NativeMethods.GetSystemPowerStatus(out var status))
                    return Measured.Missing<bool>(
                        "L'état de l'alimentation n'a pas pu être lu.", DataSource.NativeApi);

                // 255 signifie « indéterminé » : certaines machines virtuelles et quelques
                // portables anciens ne renseignent rien. Le dire vaut mieux que de choisir.
                if (status.ACLineStatus == NativeMethods.AC_LINE_OFFLINE)
                    return Measured.Ok(true, DataSource.NativeApi);

                if (status.ACLineStatus == NativeMethods.AC_LINE_ONLINE)
                    return Measured.Ok(false, DataSource.NativeApi);

                return Measured.Missing<bool>(
                    "Cette machine ne renseigne pas si elle est branchée sur le secteur.",
                    DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return Measured.Missing<bool>(
                    "L'état de l'alimentation n'a pas pu être lu.", DataSource.NativeApi);
            }
        }

        public Measured<TimeSpan> ReadUptime()
        {
            try
            {
                // GetTickCount64 existe depuis Vista et ne déborde pas au bout de 49 jours,
                // contrairement à Environment.TickCount.
                var milliseconds = NativeMethods.GetTickCount64();
                return Measured.Ok(TimeSpan.FromMilliseconds(milliseconds), DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return Measured.Missing<TimeSpan>("Le temps de fonctionnement n'a pas pu être lu.", DataSource.NativeApi);
            }
        }

        /// <summary>
        /// Deux relevés du temps processeur de chaque processus, séparés par la fenêtre demandée.
        /// </summary>
        /// <remarks>
        /// Les processus qui apparaissent ou disparaissent entre les deux relevés sont écartés :
        /// on ne peut rien dire de la charge d'un processus qu'on n'a vu qu'une fois. Ceux dont
        /// le temps processeur est refusé (les processus système protégés) sont conservés avec
        /// leur mémoire et une charge non mesurée, plutôt que d'être présentés à 0 %.
        /// </remarks>
        public async Task<Measured<IReadOnlyList<ProcessUsage>>> ReadProcessUsageAsync(
            TimeSpan sampleWindow, CancellationToken cancellationToken)
        {
            try
            {
                var first = Sample();
                if (first.Count == 0)
                    return Measured.Missing<IReadOnlyList<ProcessUsage>>(
                        "Aucun processus n'a pu être énuméré.", DataSource.NativeApi);

                await Task.Delay(sampleWindow, cancellationToken).ConfigureAwait(false);
                var second = Sample();

                var elapsed = sampleWindow.TotalMilliseconds * Environment.ProcessorCount;
                var usage = new List<ProcessUsage>(second.Count);
                var refused = 0;

                foreach (var pair in second)
                {
                    if (!first.TryGetValue(pair.Key, out var before)) continue;

                    var after = pair.Value;
                    Measured<double> cpu;

                    if (before.CpuMs < 0 || after.CpuMs < 0)
                    {
                        refused++;
                        cpu = Measured.Missing<double>(
                            "Le temps processeur de ce processus n'est pas lisible sans privilèges.",
                            DataSource.NativeApi);
                    }
                    else
                    {
                        var percent = elapsed <= 0 ? 0d : (after.CpuMs - before.CpuMs) / elapsed * 100d;
                        cpu = Measured.Ok(Math.Max(0d, Math.Min(100d, percent)), DataSource.NativeApi);
                    }

                    usage.Add(new ProcessUsage
                    {
                        Name = after.Name,
                        ProcessId = pair.Key,
                        WorkingSetBytes = Measured.Ok(after.WorkingSet, DataSource.NativeApi),
                        CpuPercent = cpu,
                        IsSystem = IsSystemProcess(after.Name),
                    });
                }

                if (usage.Count == 0)
                    return Measured.Missing<IReadOnlyList<ProcessUsage>>(
                        "Aucun processus n'est resté visible entre les deux relevés.", DataSource.NativeApi);

                _log.Debug("Processus relevés : " + usage.Count +
                           (refused > 0 ? ", dont " + refused + " sans temps processeur lisible." : "."));

                return refused > 0
                    ? Measured.Partial<IReadOnlyList<ProcessUsage>>(usage, DataSource.NativeApi,
                        refused + " processus protégés n'exposent pas leur temps processeur en session utilisateur.")
                    : Measured.Ok<IReadOnlyList<ProcessUsage>>(usage, DataSource.NativeApi);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                _log.Warn("L'énumération des processus a échoué.", ex);
                return Measured.Missing<IReadOnlyList<ProcessUsage>>(
                    "L'énumération des processus a échoué : " + ex.Message, DataSource.NativeApi);
            }
        }

        private static Dictionary<int, (string Name, long WorkingSet, double CpuMs)> Sample()
        {
            var samples = new Dictionary<int, (string, long, double)>();

            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    double cpu;
                    try
                    {
                        cpu = process.TotalProcessorTime.TotalMilliseconds;
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException ||
                                               ex is NotSupportedException)
                    {
                        // Processus protégé : la mémoire reste lisible, pas le temps processeur.
                        cpu = -1d;
                    }

                    samples[process.Id] = (process.ProcessName, process.WorkingSet64, cpu);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
                {
                    // Processus terminé entre l'énumération et la lecture : sans conséquence.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return samples;
        }

        /// <summary>
        /// Processus livrés avec Windows.
        /// </summary>
        /// <remarks>
        /// Sert uniquement à ne pas présenter au client un composant du système comme un intrus.
        /// La liste est volontairement courte et ne prétend pas être exhaustive : ce qui n'y
        /// figure pas est simplement affiché sans étiquette, jamais qualifié de suspect.
        /// </remarks>
        private static bool IsSystemProcess(string name)
        {
            foreach (var known in SystemProcessNames)
                if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] SystemProcessNames =
        {
            "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "svchost", "fontdrvhost", "dwm", "explorer", "taskhostw", "sihost",
            "RuntimeBroker", "SearchIndexer", "SearchHost", "ctfmon", "spoolsv", "conhost", "dllhost",
            "WmiPrvSE", "MsMpEng", "NisSrv", "SecurityHealthService", "TrustedInstaller", "TiWorker",
        };

        public Measured<IReadOnlyList<LocalAccountInfo>> ReadLocalAccounts()
        {
            var administrators = ReadAdministrators();

            var buffer = IntPtr.Zero;
            var resume = 0;

            try
            {
                var status = AccountNative.NetUserEnum(
                    null, AccountNative.UserInfoLevel1, AccountNative.FILTER_NORMAL_ACCOUNT,
                    out buffer, -1, out var read, out _, ref resume);

                if (status != AccountNative.NERR_Success && status != AccountNative.ERROR_MORE_DATA)
                    return Measured.Missing<IReadOnlyList<LocalAccountInfo>>(
                        "L'énumération des comptes locaux a été refusée par Windows (code " + status + ").",
                        DataSource.NativeApi);

                var size = Marshal.SizeOf(typeof(AccountNative.USER_INFO_1));
                var accounts = new List<LocalAccountInfo>(read);

                for (var i = 0; i < read; i++)
                {
                    var entry = (AccountNative.USER_INFO_1)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (i * size)), typeof(AccountNative.USER_INFO_1))!;

                    var flags = (int)entry.usri1_flags;
                    accounts.Add(new LocalAccountInfo
                    {
                        Name = entry.usri1_name,
                        Enabled = Measured.Ok((flags & AccountNative.UF_ACCOUNTDISABLE) == 0, DataSource.NativeApi),
                        NoPasswordRequired = Measured.Ok(
                            (flags & AccountNative.UF_PASSWD_NOTREQD) != 0, DataSource.NativeApi),
                        PasswordNeverExpires = Measured.Ok(
                            (flags & AccountNative.UF_DONT_EXPIRE_PASSWD) != 0, DataSource.NativeApi),
                        IsAdministrator = administrators == null
                            ? Measured.Missing<bool>(
                                "Le contenu du groupe Administrateurs n'a pas pu être lu.", DataSource.NativeApi)
                            : Measured.Ok(administrators.Contains(entry.usri1_name), DataSource.NativeApi),
                    });
                }

                if (accounts.Count == 0)
                    return Measured.Missing<IReadOnlyList<LocalAccountInfo>>(
                        "Aucun compte local n'a été énuméré sur cette machine.", DataSource.NativeApi);

                return administrators == null
                    ? Measured.Partial<IReadOnlyList<LocalAccountInfo>>(accounts, DataSource.NativeApi,
                        "Comptes énumérés, mais l'appartenance au groupe Administrateurs n'a pas pu être établie.")
                    : Measured.Ok<IReadOnlyList<LocalAccountInfo>>(accounts, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException ||
                                       ex is AccessViolationException)
            {
                _log.Warn("L'énumération des comptes locaux a échoué.", ex);
                return Measured.Missing<IReadOnlyList<LocalAccountInfo>>(
                    "L'API d'énumération des comptes n'est pas disponible sur cette machine.", DataSource.NativeApi);
            }
            finally
            {
                if (buffer != IntPtr.Zero) AccountNative.NetApiBufferFree(buffer);
            }
        }

        /// <summary>
        /// Membres du groupe Administrateurs intégré.
        /// </summary>
        /// <remarks>
        /// Le nom du groupe est traduit : « Administrateurs », « Administrators », « Administratoren ».
        /// Il est donc résolu depuis son identifiant de sécurité bien connu plutôt qu'écrit en dur :
        /// un audit de sécurité qui ne fonctionne que sur un Windows anglais n'auditerait rien.
        /// </remarks>
        private HashSet<string>? ReadAdministrators()
        {
            string groupName;
            try
            {
                var sid = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var account = (System.Security.Principal.NTAccount)sid.Translate(
                    typeof(System.Security.Principal.NTAccount));

                var separator = account.Value.LastIndexOf('\\');
                groupName = separator < 0 ? account.Value : account.Value.Substring(separator + 1);
            }
            catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException ||
                                       ex is SystemException)
            {
                _log.Debug("Le nom local du groupe Administrateurs n'a pas pu être résolu : " + ex.Message);
                return null;
            }

            var buffer = IntPtr.Zero;
            var resume = IntPtr.Zero;

            try
            {
                var status = AccountNative.NetLocalGroupGetMembers(
                    null, groupName, AccountNative.LocalGroupMembersLevel3,
                    out buffer, -1, out var read, out _, ref resume);

                if (status != AccountNative.NERR_Success && status != AccountNative.ERROR_MORE_DATA) return null;

                var size = Marshal.SizeOf(typeof(AccountNative.LOCALGROUP_MEMBERS_INFO_3));
                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < read; i++)
                {
                    var entry = (AccountNative.LOCALGROUP_MEMBERS_INFO_3)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (i * size)),
                        typeof(AccountNative.LOCALGROUP_MEMBERS_INFO_3))!;

                    var name = entry.lgrmi3_domainandname ?? string.Empty;
                    var separator = name.LastIndexOf('\\');
                    members.Add(separator < 0 ? name : name.Substring(separator + 1));
                }

                return members;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) AccountNative.NetApiBufferFree(buffer);
            }
        }

        public Measured<FirmwareMode> ReadFirmwareMode()
        {
            // GetFirmwareEnvironmentVariable échoue avec ERROR_INVALID_FUNCTION (1) sur une
            // machine démarrée en BIOS hérité, et avec ERROR_ACCESS_DENIED / NOENT en UEFI.
            // C'est le code d'erreur qui porte l'information, pas la valeur lue.
            const int ErrorInvalidFunction = 1;
            const int ErrorNoAccess = 5;

            try
            {
                var buffer = new byte[8];
                NativeMethods.GetFirmwareEnvironmentVariableW(
                    "", "{00000000-0000-0000-0000-000000000000}", buffer, (uint)buffer.Length);
                var error = Marshal.GetLastWin32Error();

                if (error == ErrorInvalidFunction)
                    return Measured.Ok(FirmwareMode.LegacyBios, DataSource.NativeApi);

                // Tout autre code signifie que l'appel a été compris : la machine est en UEFI.
                if (error == ErrorNoAccess || error != 0)
                    return Measured.Ok(FirmwareMode.Uefi, DataSource.NativeApi);

                return Measured.Ok(FirmwareMode.Uefi, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is Win32Exception)
            {
                return Measured.Missing<FirmwareMode>(
                    "Le mode de démarrage (UEFI ou BIOS hérité) n'a pas pu être déterminé.", DataSource.NativeApi);
            }
        }

        private static bool TryReadSystemTimes(out long idle, out long kernel, out long user)
        {
            idle = kernel = user = 0;
            if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return false;

            idle = ToLong(idleTime);
            kernel = ToLong(kernelTime);
            user = ToLong(userTime);
            return true;
        }

        private static long ToLong(NativeMethods.FILETIME time)
            => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    }
}
