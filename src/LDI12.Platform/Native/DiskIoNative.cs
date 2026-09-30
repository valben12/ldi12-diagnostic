using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Entrées-sorties sans mémoire tampon, pour mesurer un disque et non le cache de Windows.
    /// </summary>
    /// <remarks>
    /// <b>C'est la seule façon de mesurer un disque.</b> Une lecture ordinaire passe par le cache
    /// du système : sur une machine avec seize gigaoctets de mémoire, relire un fichier de deux
    /// cent cinquante mégaoctets qu'on vient d'écrire mesure la vitesse de la mémoire vive, et un
    /// disque mécanique de 2009 y afficherait des débits de mémoire flash. Le diagnostic serait
    /// exactement inversé.
    /// <para>
    /// <c>FILE_FLAG_NO_BUFFERING</c> impose ses conditions : chaque transfert doit être un
    /// multiple de la taille de secteur, et l'adresse du tampon doit y être alignée. Un
    /// <c>byte[]</c> managé ne le garantit pas, d'où l'allocation non managée alignée sur une
    /// page de quatre kilo-octets, qui couvre les secteurs de 512 comme ceux de 4096 octets.
    /// </para>
    /// </remarks>
    internal static class DiskIoNative
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;

        /// <summary>Partage complet, suppression comprise : exigée par la suppression à la fermeture.</summary>
        internal const uint ShareAll = 0x00000007;

        /// <summary>Partage en lecture seule : personne n'écrit dans un fichier qu'on est en train de relire.</summary>
        internal const uint ShareRead = 0x00000001;

        internal const uint CreateAlways = 2;
        internal const uint OpenExisting = 3;

        internal const uint FlagNoBuffering = 0x20000000;
        internal const uint FlagWriteThrough = 0x80000000;
        internal const uint FlagSequentialScan = 0x08000000;

        /// <summary>
        /// Le fichier de mesure disparaît à la fermeture du descripteur, plantage compris.
        /// </summary>
        /// <remarks>
        /// Un fichier de deux cent cinquante mégaoctets oublié sur le disque d'un client parce que
        /// le logiciel s'est arrêté au mauvais moment serait exactement le genre de trace qu'un
        /// outil de diagnostic ne doit pas laisser. Windows s'en charge, et il s'en charge même
        /// si personne ne le lui redemande.
        /// </remarks>
        internal const uint FlagDeleteOnClose = 0x04000000;

        internal const uint ErrorAccessDenied = 5;
        internal const uint ErrorDiskFull = 112;
        internal const uint ErrorNotSupported = 50;

        /// <summary>Rendu par une lecture sans mémoire tampon qui ne respecte pas la taille de secteur.</summary>
        internal const uint ErrorInvalidParameter = 87;

        /// <summary>Alignement d'une page : couvre les secteurs de 512 comme ceux de 4096 octets.</summary>
        internal const int Alignment = 4096;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr security,
            uint creationDisposition, uint flagsAndAttributes, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(
            SafeFileHandle file, IntPtr buffer, int bytesToWrite, out int bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            SafeFileHandle file, IntPtr buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFilePointerEx(
            SafeFileHandle file, long distance, IntPtr newPointer, uint origin);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushFileBuffers(SafeFileHandle file);

        /// <summary>Tampon non managé aligné sur une page, seul acceptable sans mémoire tampon.</summary>
        internal sealed class AlignedBuffer : IDisposable
        {
            private IntPtr _raw;

            internal AlignedBuffer(int size)
            {
                Size = size;
                _raw = Marshal.AllocHGlobal(size + Alignment);

                var address = _raw.ToInt64();
                Address = new IntPtr((address + Alignment - 1) / Alignment * Alignment);
            }

            internal IntPtr Address { get; }

            internal int Size { get; }

            /// <summary>
            /// Remplit le tampon de valeurs variées.
            /// </summary>
            /// <remarks>
            /// Écrire des zéros mesurerait autre chose que le disque : certains disques à mémoire
            /// flash et certains systèmes de fichiers reconnaissent un bloc vide et ne l'écrivent
            /// jamais vraiment.
            /// </remarks>
            internal void FillWithVariedData()
            {
                var random = new Random(20260904);
                var chunk = new byte[Alignment];

                for (var offset = 0; offset < Size; offset += chunk.Length)
                {
                    random.NextBytes(chunk);
                    Marshal.Copy(chunk, 0, IntPtr.Add(Address, offset), Math.Min(chunk.Length, Size - offset));
                }
            }

            public void Dispose()
            {
                if (_raw == IntPtr.Zero) return;

                Marshal.FreeHGlobal(_raw);
                _raw = IntPtr.Zero;
            }
        }
    }
}
