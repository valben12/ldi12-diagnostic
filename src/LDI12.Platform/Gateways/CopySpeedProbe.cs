using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Native;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Mesure, en une quinzaine de secondes, ce qu'une sauvegarde coûtera sur un support.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi pas la mesure de disque de l'écran Mesures.</b> Elle donne le débit continu, et
    /// c'est la moitié de la réponse : sur un profil de navigateur, cent mille fichiers de quelques
    /// kilo-octets coûtent chacun une création, une fermeture, un renommage, une relecture et un
    /// passage de l'antivirus, et ce prix fixe dépasse de loin celui de leurs octets. Cette mesure
    /// fait donc les deux : un fichier continu, écrit puis relu sans mémoire tampon comme la
    /// vérification relit, et une série de petits fichiers traités exactement comme la copie les
    /// traite.
    /// <para>
    /// Rien n'est écrit sur le disque du client : tout va sur le support mesuré, dans un dossier
    /// qui est effacé à la fin, et le gros fichier est confié à Windows avec la consigne de
    /// disparaître à sa fermeture, y compris si le logiciel s'arrête en route.
    /// </para>
    /// </remarks>
    public sealed class CopySpeedProbe
    {
        private const int Block = 1024 * 1024;

        /// <summary>Au plus 64 Mo en continu : de quoi dépasser le cache interne d'une clé USB.</summary>
        private const int MaxBlocks = 64;

        private const int SmallFileBytes = 16 * 1024;
        private const int MaxSmallFiles = 150;

        /// <summary>Place laissée libre sur le support, en plus de ce que la mesure écrit.</summary>
        private const long Headroom = 256L * 1024 * 1024;

        private static readonly TimeSpan PhaseBudget = TimeSpan.FromSeconds(5);

        private readonly IScopedLogger _log;

        public CopySpeedProbe(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For("Platform.Files");

        public CopySpeed Measure(string destination, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(destination)) return Failed("Aucun support n'est choisi.");

            string folder;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(destination));
                if (string.IsNullOrEmpty(root)) return Failed("Le volume de destination n'a pas pu être déterminé.");

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return Failed("Le support n'est pas prêt.");
                if (drive.AvailableFreeSpace < (long)MaxBlocks * Block + Headroom)
                    return Failed("Il reste trop peu de place sur le support pour le mesurer.");

                folder = Path.Combine(Directory.Exists(destination) ? destination : root,
                    ".ldi12-mesure-" + Guid.NewGuid().ToString("N"));
                var created = Directory.CreateDirectory(folder);
                created.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return Failed("Le support refuse l'écriture : " + ex.Message);
            }

            try
            {
                progress?.Report("Écriture et relecture en continu…");
                var (write, read) = Sequential(Path.Combine(folder, "continu.tmp"), cancellationToken);

                progress?.Report("Petits fichiers…");
                var perFile = SmallFiles(folder, write, read, cancellationToken);

                _log.Info("Vitesse de copie vers " + destination + " : écriture " + (int)(write / 1e6) + " Mo/s, relecture " +
                          (int)(read / 1e6) + " Mo/s, " + perFile.TotalMilliseconds.ToString("0.0") + " ms par fichier.");

                return new CopySpeed { WriteBytesPerSecond = write, ReadBytesPerSecond = read, PerFile = perFile };
            }
            catch (OperationCanceledException)
            {
                return Failed("Mesure annulée.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Failed("La mesure a échoué : " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _log.Warn("Le dossier de mesure " + folder + " n'a pas pu être effacé : " + ex.Message);
                }
            }
        }

        /// <summary>Écrit puis relit sans mémoire tampon, comme la copie écrit et comme la vérification relit.</summary>
        private static (double Write, double Read) Sequential(string path, CancellationToken cancellationToken)
        {
            using var buffer = new DiskIoNative.AlignedBuffer(Block);
            using var file = DiskIoNative.CreateFile(
                path, DiskIoNative.GenericRead | DiskIoNative.GenericWrite, DiskIoNative.ShareAll, IntPtr.Zero,
                DiskIoNative.CreateAlways,
                DiskIoNative.FlagNoBuffering | DiskIoNative.FlagWriteThrough | DiskIoNative.FlagDeleteOnClose,
                IntPtr.Zero);

            if (file.IsInvalid) throw Error("Le fichier de mesure n'a pas pu être créé");

            buffer.FillWithVariedData();

            var stopwatch = Stopwatch.StartNew();
            var blocks = 0;
            while (blocks < MaxBlocks && stopwatch.Elapsed < PhaseBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DiskIoNative.WriteFile(file, buffer.Address, Block, out var moved, IntPtr.Zero) || moved != Block)
                    throw Error("L'écriture s'est interrompue");
                blocks++;
            }

            DiskIoNative.FlushFileBuffers(file);
            var write = (double)blocks * Block / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-3);

            if (!DiskIoNative.SetFilePointerEx(file, 0, IntPtr.Zero, 0)) throw Error("Le début du fichier n'a pas pu être atteint");

            stopwatch.Restart();
            var readBlocks = 0;
            while (readBlocks < blocks && stopwatch.Elapsed < PhaseBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DiskIoNative.ReadFile(file, buffer.Address, Block, out var moved, IntPtr.Zero) || moved != Block)
                    throw Error("La relecture s'est interrompue");
                readBlocks++;
            }

            var read = (double)readBlocks * Block / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-3);
            return (write, read);
        }

        /// <summary>
        /// Le prix fixe d'un fichier : ce que la copie fait à chacun, octets mis à part.
        /// </summary>
        /// <remarks>
        /// Chaque petit fichier passe par les mêmes étapes que dans la copie : vérification
        /// d'existence, écriture sous le nom provisoire, date reportée, relecture sans mémoire
        /// tampon, renommage. Le temps de transfert de ses seize kilo-octets, déduit des débits
        /// mesurés juste avant, est retiré : il reste ce que coûte le fichier en tant que tel.
        /// </remarks>
        private static TimeSpan SmallFiles(string folder, double write, double read, CancellationToken cancellationToken)
        {
            var data = new byte[SmallFileBytes];
            new Random(20260930).NextBytes(data);
            var stamp = DateTime.UtcNow.AddDays(-1);

            using var buffer = new DiskIoNative.AlignedBuffer(SmallFileBytes);

            var stopwatch = Stopwatch.StartNew();
            var count = 0;
            while (count < MaxSmallFiles && stopwatch.Elapsed < PhaseBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var final = Path.Combine(folder, "f" + count + ".bin");
                var partial = final + FileCopyRequest.PartialSuffix;
                _ = new FileInfo(final).Exists;

                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                    output.Write(data, 0, data.Length);

                File.SetLastWriteTimeUtc(partial, stamp);

                using (var input = DiskIoNative.CreateFile(
                           partial, DiskIoNative.GenericRead, DiskIoNative.ShareRead, IntPtr.Zero,
                           DiskIoNative.OpenExisting, DiskIoNative.FlagNoBuffering | DiskIoNative.FlagSequentialScan,
                           IntPtr.Zero))
                {
                    if (input.IsInvalid) throw Error("Un petit fichier n'a pas pu être relu");
                    if (!DiskIoNative.ReadFile(input, buffer.Address, SmallFileBytes, out _, IntPtr.Zero))
                        throw Error("Un petit fichier n'a pas pu être relu");
                }

                File.Move(partial, final);
                count++;
            }

            var each = stopwatch.Elapsed.TotalSeconds / Math.Max(1, count);
            var transfer = SmallFileBytes / write + SmallFileBytes / read;
            return TimeSpan.FromSeconds(Math.Max(0, each - transfer));
        }

        private static IOException Error(string what)
        {
            var code = Marshal.GetLastWin32Error();
            return new IOException(what + " (code " + code + ").", unchecked((int)0x80070000) | code);
        }

        private static CopySpeed Failed(string reason) => new CopySpeed { Failure = reason };
    }
}
