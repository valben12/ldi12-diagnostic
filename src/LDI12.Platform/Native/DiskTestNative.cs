using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using LDI12.Core.Disks;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les tests que GSmartControl et HDDScan lancent sur un disque : l'autotest intégré au disque
    /// (commande SMART « EXECUTE OFF-LINE IMMEDIATE ») et la lecture de toute sa surface, bloc par
    /// bloc, chronométrée. Tout est en lecture : rien n'est jamais écrit sur le disque testé.
    /// </summary>
    internal static class DiskTestNative
    {
        private const uint SmartSendDriveCommand = 0x0007C084;
        private const uint IoctlDiskGetLengthInfo = 0x0007405C;
        private const byte ExecuteOfflineImmediate = 0xD4;
        private const byte ReadLog = 0xD5;
        private const byte SelfTestLogAddress = 0x06;

        private static SafeFileHandle Open(int index, bool write, bool unbuffered = false)
            => StorageNative.CreateFileW(@"\\.\PhysicalDrive" + index,
                StorageNative.GENERIC_READ | (write ? 0x40000000u : 0),
                StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE,
                IntPtr.Zero, StorageNative.OPEN_EXISTING, unbuffered ? DiskIoNative.FlagNoBuffering : 0, IntPtr.Zero);

        private static byte[] Command(int index, byte feature, byte sectorNumber, int buffer)
            => StorageNative.StructureToBytes(new StorageNative.SENDCMDINPARAMS
            {
                cBufferSize = (uint)buffer,
                bDriveNumber = (byte)index,
                bReserved = new byte[3],
                dwReserved = new uint[4],
                irDriveRegs = new StorageNative.IDEREGS
                {
                    bFeaturesReg = feature,
                    bSectorCountReg = 1,
                    bSectorNumberReg = sectorNumber,
                    bCylLowReg = StorageNative.SMART_CYL_LOW,
                    bCylHighReg = StorageNative.SMART_CYL_HIGH,
                    bDriveHeadReg = 0xA0,
                    bCommandReg = StorageNative.SMART_CMD,
                },
            });

        /// <summary>Lance l'autotest court (environ deux minutes) ou long (une à plusieurs heures). 0 si accepté.</summary>
        public static int StartSelfTest(int index, bool extended)
        {
            using var handle = Open(index, write: true);
            if (handle.IsInvalid) return Marshal.GetLastWin32Error();
            var input = Command(index, ExecuteOfflineImmediate, extended ? (byte)2 : (byte)1, 0);
            var output = new byte[StorageNative.SendCmdOutHeaderSize + 1];
            return StorageNative.DeviceIoControl(handle, SmartSendDriveCommand, input, input.Length, output, output.Length, out _, IntPtr.Zero)
                ? 0
                : Marshal.GetLastWin32Error();
        }

        /// <summary>L'état de l'autotest et son journal, ou nul si le disque ne répond pas aux commandes SMART.</summary>
        public static SelfTestState? ReadSelfTest(int index)
        {
            using var handle = Open(index, write: true);
            if (handle.IsInvalid) return null;
            var data = Receive(handle, index, StorageNative.SMART_READ_ATTRIBUTES, 1);
            if (data == null) return null;
            var log = Receive(handle, index, ReadLog, SelfTestLogAddress);
            return SelfTestState.FromSmartData(data, log);
        }

        private static byte[]? Receive(SafeFileHandle handle, int index, byte feature, byte sectorNumber)
        {
            var input = Command(index, feature, sectorNumber, StorageNative.SmartDataSize);
            var output = new byte[StorageNative.SendCmdOutHeaderSize + StorageNative.SmartDataSize];
            if (!StorageNative.DeviceIoControl(handle, StorageNative.SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
                return null;
            if (returned < StorageNative.SendCmdOutHeaderSize + StorageNative.SmartDataSize) return null;
            var payload = new byte[StorageNative.SmartDataSize];
            Buffer.BlockCopy(output, StorageNative.SendCmdOutHeaderSize, payload, 0, payload.Length);
            return payload;
        }

        /// <summary>
        /// Lit la surface : en entier (bloc après bloc), ou par échantillons répartis sur tout le
        /// disque pour un premier avis en une minute. Chaque lecture est chronométrée.
        /// </summary>
        public static SurfaceScan ScanSurface(int index, bool full, Action<double, SurfaceScan> progress, CancellationToken cancellationToken)
        {
            const int Block = 1024 * 1024;
            const int Samples = 2400;
            var scan = new SurfaceScan(4000) { Full = full };
            using var handle = Open(index, write: false, unbuffered: true);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var length = new byte[8];
            if (!StorageNative.DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, length, length.Length, out _, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var size = BitConverter.ToInt64(length, 0);
            scan.DiskBytes = size;
            var blocks = size / Block;
            if (blocks <= 0) return scan;

            using var buffer = new DiskIoNative.AlignedBuffer(Block);
            var clock = Stopwatch.StartNew();
            var reported = Stopwatch.StartNew();
            var total = full ? blocks : Math.Min(blocks, Samples);
            for (long step = 0; step < total; step++)
            {
                if (cancellationToken.IsCancellationRequested) { scan.Cancelled = true; break; }
                var block = full ? step : step * blocks / total;
                var offset = block * Block;

                var timer = Stopwatch.StartNew();
                var ok = DiskIoNative.SetFilePointerEx(handle, offset, IntPtr.Zero, 0) &&
                         DiskIoNative.ReadFile(handle, buffer.Address, Block, out var read, IntPtr.Zero) && read > 0;
                timer.Stop();

                scan.Add((double)block / blocks, timer.Elapsed.TotalMilliseconds, !ok, offset);
                if (ok) scan.BytesRead += Block;

                if (reported.ElapsedMilliseconds >= 300)
                {
                    reported.Restart();
                    scan.Duration = clock.Elapsed;
                    progress((double)(step + 1) / total, scan);
                }
            }
            scan.Duration = clock.Elapsed;
            progress(1, scan);
            return scan;
        }
    }
}
