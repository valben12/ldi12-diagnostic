using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LDI12.Core.DiskUsage;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Lecture de dossier par FindFirstFileExW : sans le nom court 8.3 (FindExInfoBasic), par
    /// gros paquets (FIND_FIRST_EX_LARGE_FETCH), et sans créer d'objet par entrée comme le fait
    /// le framework. C'est la lecture la plus rapide disponible sans privilèges.
    /// </summary>
    /// <remarks>
    /// Chemins préfixés par \\?\ : les profils contiennent souvent des chemins de plus de 260
    /// caractères (caches d'applications, node_modules), que l'analyse doit aussi peser.
    /// </remarks>
    public sealed class FastDirectoryLister : IDirectoryLister
    {
        private const int FindExInfoBasic = 1;
        private const int FindExSearchNameMatch = 0;
        private const int FindFirstExLargeFetch = 2;

        private const uint AttributeDirectory = 0x10;
        private const uint AttributeReparsePoint = 0x400;
        private const uint AttributeRecallOnDataAccess = 0x400000;
        private const uint AttributeOffline = 0x1000;

        public static readonly FastDirectoryLister Instance = new FastDirectoryLister();

        public bool TryList(string path, List<DiskEntry> into)
        {
            var pattern = Extended(path);
            pattern += pattern.EndsWith("\\", StringComparison.Ordinal) ? "*" : "\\*";

            using (var handle = FindFirstFileExW(pattern, FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch))
            {
                if (handle.IsInvalid)
                {
                    // Un dossier vide n'a même pas « . » à la racine d'un volume : ce n'est pas une erreur.
                    return Marshal.GetLastWin32Error() == 2;
                }

                do
                {
                    var name = data.FileName;
                    if (name == "." || name == "..") continue;

                    var attributes = data.FileAttributes;
                    var isDirectory = (attributes & AttributeDirectory) != 0;
                    into.Add(new DiskEntry
                    {
                        Name = name,
                        IsDirectory = isDirectory,
                        IsReparsePoint = (attributes & AttributeReparsePoint) != 0,
                        IsCloudOnly = (attributes & (AttributeRecallOnDataAccess | AttributeOffline)) != 0,
                        Size = isDirectory ? 0 : ((long)data.FileSizeHigh << 32) | data.FileSizeLow,
                        LastWriteUtcTicks = FileTimeTicks(data.LastWriteTimeHigh, data.LastWriteTimeLow),
                    });
                }
                while (FindNextFileW(handle, out data));
            }

            return true;
        }

        private static string Extended(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        private static long FileTimeTicks(uint high, uint low)
        {
            var fileTime = ((long)high << 32) | low;
            if (fileTime <= 0) return 0;
            try { return DateTime.FromFileTimeUtc(fileTime).Ticks; }
            catch (ArgumentOutOfRangeException) { return 0; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeFindHandle() : base(true)
            {
            }

            protected override bool ReleaseHandle() => FindClose(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFindHandle FindFirstFileExW(
            string fileName, int infoLevel, out FindData findData, int searchOp, IntPtr searchFilter, int additionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFileW(SafeFindHandle findFile, out FindData findData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr findFile);
    }
}
