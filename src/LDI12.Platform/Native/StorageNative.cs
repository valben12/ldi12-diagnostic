using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Structures et codes de contrôle du sous-système de stockage Windows.
    /// </summary>
    /// <remarks>
    /// Deux chemins totalement différents mènent à la santé d'un disque :
    /// <list type="bullet">
    /// <item><b>ATA / SATA</b> : <c>SMART_RCV_DRIVE_DATA</c>, disponible depuis Windows 2000, mais
    /// qui exige un handle ouvert en lecture, donc des privilèges administrateur.</item>
    /// <item><b>NVMe</b> : <c>IOCTL_STORAGE_QUERY_PROPERTY</c> avec la propriété protocolaire,
    /// introduite avec Windows 10 1607 et inaccessible avant.</item>
    /// </list>
    /// </remarks>
    internal static class StorageNative
    {
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;

        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        /// <summary>
        /// IOCTL_DISK_GET_LENGTH_INFO exige un handle ouvert en lecture, donc l'élévation.
        /// GET_DRIVE_GEOMETRY_EX est déclaré FILE_ANY_ACCESS et répond avec un accès nul :
        /// c'est lui qui permet de connaître la capacité sans privilèges.
        /// </summary>
        internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

        /// <summary>DISK_GEOMETRY fait 24 octets ; DiskSize suit immédiatement.</summary>
        internal const int DiskGeometryExSizeOffset = 24;
        /// <summary>
        /// Rattache un volume à son ou ses disques physiques. FILE_ANY_ACCESS : fonctionne avec
        /// un handle ouvert sans droit d'accès, donc sans privilèges administrateur.
        /// </summary>
        internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        internal const uint SMART_GET_VERSION = 0x00074080;
        internal const uint SMART_RCV_DRIVE_DATA = 0x0007C088;

        /// <summary>
        /// Envoi d'une commande ATA brute au disque. Exige des privilèges administrateur.
        /// </summary>
        /// <remarks>
        /// Déclaré avec <c>FILE_READ_ACCESS | FILE_WRITE_ACCESS</c> : contrairement à la
        /// prédiction de panne, il n'existe aucune porte dérobée. C'est le seul chemin vers
        /// <c>IDENTIFY DEVICE</c>, et donc vers les deux caractéristiques que le reste de Windows
        /// ne publie nulle part : la vitesse de rotation des plateaux et la génération SATA
        /// réellement négociée entre le disque et son port.
        /// </remarks>
        internal const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;

        internal const ushort ATA_FLAGS_DRDY_REQUIRED = 0x0001;
        internal const ushort ATA_FLAGS_DATA_IN = 0x0002;

        /// <summary>Commande ATA « IDENTIFY DEVICE » : 512 octets de description du disque.</summary>
        internal const byte ATA_COMMAND_IDENTIFY = 0xEC;

        /// <summary>Longueur de la réponse d'IDENTIFY DEVICE : 256 mots de 16 bits.</summary>
        internal const int IdentifySize = 512;

        /// <summary>
        /// En-tête d'une commande ATA transmise telle quelle au disque.
        /// </summary>
        /// <remarks>
        /// <c>DataBufferOffset</c> est un <c>ULONG_PTR</c> : la structure ne fait pas la même
        /// taille en 32 et en 64 bits, et l'exécutable étant AnyCPU, les deux cas se produisent.
        /// D'où <see cref="IntPtr"/> plutôt qu'un entier, et une taille calculée par le
        /// marshaleur plutôt qu'écrite en dur.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ATA_PASS_THROUGH_EX
        {
            internal ushort Length;
            internal ushort AtaFlags;
            internal byte PathId;
            internal byte TargetId;
            internal byte Lun;
            internal byte ReservedAsUchar;
            internal uint DataTransferLength;
            internal uint TimeOutValue;
            internal uint ReservedAsUlong;
            internal IntPtr DataBufferOffset;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            internal byte[] PreviousTaskFile;

            /// <summary>
            /// Registres ATA : fonctionnalité, compte de secteurs, LBA bas, moyen, haut,
            /// périphérique, commande, réservé.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            internal byte[] CurrentTaskFile;
        }

        /// <summary>Position du registre de commande dans le bloc de registres ATA.</summary>
        internal const int TaskFileCommandIndex = 6;

        /// <summary>Position du compte de secteurs dans le bloc de registres ATA.</summary>
        internal const int TaskFileSectorCountIndex = 1;

        /// <summary>
        /// Prédiction de panne du disque : le seul chemin SMART qui n'exige aucun privilège.
        /// </summary>
        /// <remarks>
        /// <c>SMART_RCV_DRIVE_DATA</c> est défini avec <c>FILE_READ_ACCESS</c> : il faut ouvrir
        /// le disque en lecture, donc être administrateur. Celui-ci est défini avec
        /// <c>FILE_ANY_ACCESS</c>, et se contente d'un descripteur ouvert sans aucun droit, ce
        /// qu'un compte standard obtient sur <c>\.\PhysicalDriveN</c>.
        /// <para>
        /// Il rend un entier de prédiction suivi de 512 octets « spécifiques au constructeur »
        /// qui, sur un disque ATA, sont exactement la table d'attributs SMART. La même que celle
        /// que rend la commande privilégiée, au mot de révision près.
        /// </para>
        /// </remarks>
        internal const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;

        /// <summary>Taille de <c>STORAGE_PREDICT_FAILURE</c> : un entier puis 512 octets.</summary>
        internal const int PredictFailureSize = 516;

        /// <summary>Position de la table d'attributs dans la réponse de prédiction.</summary>
        internal const int PredictFailureDataOffset = 4;

        internal const int StorageDeviceProperty = 0;
        internal const int StorageDeviceSeekPenaltyProperty = 7;

        /// <summary>Introduite avec Windows 10 1607 : c'est elle qui ouvre le SMART NVMe.</summary>
        internal const int StorageDeviceProtocolSpecificProperty = 50;

        internal const int PropertyStandardQuery = 0;

        internal const int ProtocolTypeNvme = 3;
        internal const int NVMeDataTypeLogPage = 2;
        internal const int NvmeLogPageHealth = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
            [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public int BusType;
            public uint RawPropertiesLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROTOCOL_SPECIFIC_DATA
        {
            public int ProtocolType;
            public uint DataType;
            public uint ProtocolDataRequestValue;
            public uint ProtocolDataRequestSubValue;
            public uint ProtocolDataOffset;
            public uint ProtocolDataLength;
            public uint FixedProtocolReturnData;
            public uint ProtocolDataRequestSubValue2;
            public uint ProtocolDataRequestSubValue3;
            public uint ProtocolDataRequestSubValue4;
        }

        // ---- SMART ATA ----

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct IDEREGS
        {
            public byte bFeaturesReg;
            public byte bSectorCountReg;
            public byte bSectorNumberReg;
            public byte bCylLowReg;
            public byte bCylHighReg;
            public byte bDriveHeadReg;
            public byte bCommandReg;
            public byte bReserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct SENDCMDINPARAMS
        {
            public uint cBufferSize;
            public IDEREGS irDriveRegs;
            public byte bDriveNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] bReserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] dwReserved;
            public byte bBuffer;
        }

        internal const byte SMART_READ_ATTRIBUTES = 0xD0;
        internal const byte SMART_READ_THRESHOLDS = 0xD1;
        internal const byte SMART_CMD = 0xB0;
        internal const byte SMART_CYL_LOW = 0x4F;
        internal const byte SMART_CYL_HIGH = 0xC2;

        /// <summary>Taille du secteur de données SMART renvoyé par le disque.</summary>
        internal const int SmartDataSize = 512;

        /// <summary>
        /// En-tête de SENDCMDOUTPARAMS devant les 512 octets utiles :
        /// cBufferSize (4) + DRIVERSTATUS (bDriverError, bIDEError, bReserved[2], dwReserved[2]) = 16.
        /// </summary>
        internal const int SendCmdOutHeaderSize = 16;

        /// <summary>Les deux premiers octets du secteur SMART portent la révision de structure.</summary>
        internal const int SmartAttributeTableOffset = 2;

        internal const int SmartAttributeCount = 30;
        internal const int SmartAttributeSize = 12;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device, uint controlCode,
            byte[]? inBuffer, int inBufferSize,
            byte[]? outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        internal static byte[] StructureToBytes<T>(T value) where T : struct
        {
            var size = Marshal.SizeOf(typeof(T));
            var buffer = new byte[size];
            var handle = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, handle, false);
                Marshal.Copy(handle, buffer, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }
            return buffer;
        }

        internal static T BytesToStructure<T>(byte[] buffer, int offset = 0) where T : struct
        {
            var size = Marshal.SizeOf(typeof(T));
            var handle = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(buffer, offset, handle, size);
                return (T)Marshal.PtrToStructure(handle, typeof(T))!;
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        /// <summary>Lit une chaîne ASCII terminée par zéro à un décalage donné du tampon.</summary>
        internal static string? ReadAnsiAt(byte[] buffer, uint offset)
        {
            if (offset == 0 || offset >= buffer.Length) return null;
            var end = (int)offset;
            while (end < buffer.Length && buffer[end] != 0) end++;
            var length = end - (int)offset;
            if (length <= 0) return null;
            var text = System.Text.Encoding.ASCII.GetString(buffer, (int)offset, length).Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
