using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LDI12.Core.Programs;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les processus en cours, lus d'un seul appel au noyau, comme le font le Gestionnaire des
    /// tâches et Process Explorer : mémoire, temps processeur, parent, poignées et session
    /// arrivent pour tous les processus, même ceux qu'on n'a pas le droit d'ouvrir.
    /// </summary>
    /// <remarks>
    /// Seul le chemin de l'exécutable demande d'ouvrir le processus, avec le droit le plus
    /// restreint qui existe (lecture d'informations limitées) : il est accordé pour presque tout,
    /// sauf les processus protégés, qui restent listés sans chemin.
    /// </remarks>
    public static class ProcessNative
    {
        private const int SystemProcessInformation = 5;
        private const uint StatusInfoLengthMismatch = 0xC0000004;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessTerminate = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        // Disposition publiée par Windows depuis Vista ; le marshaling place les champs selon la
        // taille des pointeurs, ce qui couvre 32 et 64 bits avec une seule définition.
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemProcess
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivateSize;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public UnicodeString ImageName;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public UIntPtr UniqueProcessKey;
            public UIntPtr PeakVirtualSize;
            public UIntPtr VirtualSize;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivatePageCount;
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        /// <summary>Tous les processus, à l'instant.</summary>
        public static IReadOnlyList<ProcessEntry> Snapshot()
        {
            var length = 512 * 1024;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    var status = NtQuerySystemInformation(SystemProcessInformation, buffer, length, out var returned);
                    if (status == StatusInfoLengthMismatch)
                    {
                        length = Math.Max(length * 2, returned + 64 * 1024);
                        continue;
                    }
                    if (status != 0) throw new Win32Exception("Relevé des processus refusé (0x" + status.ToString("X8") + ").");
                    return Parse(buffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            throw new Win32Exception("La liste des processus change trop vite pour être relevée.");
        }

        private static List<ProcessEntry> Parse(IntPtr buffer)
        {
            var entries = new List<ProcessEntry>(256);
            var offset = 0L;
            while (true)
            {
                var at = new IntPtr(buffer.ToInt64() + offset);
                var info = (SystemProcess)Marshal.PtrToStructure(at, typeof(SystemProcess))!;
                var id = info.UniqueProcessId.ToInt32();
                var name = id == 0
                    ? "Processus inactif"
                    : info.ImageName.Buffer == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);

                entries.Add(new ProcessEntry
                {
                    Id = id,
                    ParentId = info.InheritedFromUniqueProcessId.ToInt32(),
                    Name = name ?? string.Empty,
                    Path = id <= 4 ? null : CachedPath(id, info.CreateTime),
                    CpuTime = info.UserTime + info.KernelTime,
                    PrivateBytes = (long)info.PagefileUsage.ToUInt64(),
                    WorkingSet = (long)info.WorkingSetSize.ToUInt64(),
                    Threads = (int)info.NumberOfThreads,
                    Handles = (int)info.HandleCount,
                    StartedUtc = info.CreateTime > 0 ? DateTime.FromFileTimeUtc(info.CreateTime) : (DateTime?)null,
                    SessionId = (int)info.SessionId,
                });

                if (info.NextEntryOffset == 0) break;
                offset += info.NextEntryOffset;
            }
            return entries;
        }

        // Un processus garde son chemin toute sa vie : il n'est demandé qu'une fois par processus,
        // reconnu à son numéro et à sa date de création (un numéro est réattribué après la fin).
        private static readonly Dictionary<(int, long), string?> Paths = new Dictionary<(int, long), string?>();

        private static string? CachedPath(int id, long created)
        {
            lock (Paths)
            {
                if (Paths.TryGetValue((id, created), out var known)) return known;
                if (Paths.Count > 4096) Paths.Clear();
                return Paths[(id, created)] = ImagePath(id);
            }
        }

        /// <summary>Le chemin de l'exécutable, ou nul pour un processus protégé.</summary>
        public static string? ImagePath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var size = 1024;
                var text = new StringBuilder(size);
                return QueryFullProcessImageNameW(handle, 0, text, ref size) ? text.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);

        /// <summary>
        /// La ligne de commande d'un processus (Windows 8.1 et plus), ou nul. Lue par le noyau,
        /// sans aller lire la mémoire du processus.
        /// </summary>
        public static string? CommandLine(int processId)
        {
            const int ProcessCommandLineInformation = 60;
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            var buffer = Marshal.AllocHGlobal(32 * 1024);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, 32 * 1024, out _);
                if (status != 0) return null;
                var text = (UnicodeString)Marshal.PtrToStructure(buffer, typeof(UnicodeString))!;
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        /// <summary>Temps processeur total de la machine (noyau + utilisateur, inactivité comprise).</summary>
        public static long SystemTime()
            => GetSystemTimes(out _, out var kernel, out var user) ? kernel + user : 0;

        /// <summary>Met fin à un processus. Faux avec la raison si Windows le refuse.</summary>
        public static bool Terminate(int processId, out string? reason)
        {
            reason = null;
            var handle = OpenProcess(ProcessTerminate, false, processId);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                reason = error == 5 ? "accès refusé : réservé aux administrateurs" : error == 87 ? "le processus est déjà terminé" : new Win32Exception(error).Message;
                return error == 87;
            }
            try
            {
                if (TerminateProcess(handle, 1)) return true;
                var error = Marshal.GetLastWin32Error();
                reason = error == 5 ? "accès refusé : processus protégé par Windows" : new Win32Exception(error).Message;
                return false;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
