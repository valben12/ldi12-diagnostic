using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LDI12.Core.Benchmarks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Platform.Native;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Mesure le débit réel d'un volume.
    /// </summary>
    /// <remarks>
    /// <b>La seule opération du logiciel qui écrive sur le disque d'un client sans réparer quoi
    /// que ce soit.</b> Elle est donc traitée comme une action : le plan est établi et montré
    /// avant, l'écriture est bornée, et le fichier est confié à Windows avec la consigne de
    /// disparaître à la fermeture du descripteur, y compris si le logiciel s'arrête au mauvais
    /// moment.
    /// <para>
    /// Le test aléatoire est borné dans le temps autant que dans le nombre d'accès : sur un
    /// disque mécanique fatigué, cinq cents accès isolés peuvent demander plusieurs minutes, et
    /// il n'y a rien à apprendre de plus après dix secondes qu'après une. Le nombre d'accès
    /// réellement effectués est rendu avec la mesure.
    /// </para>
    /// </remarks>
    public sealed class StorageBenchmarkGateway : IStorageBenchmarkGateway
    {
        private const string Category = "Mesures";

        /// <summary>Blocs d'un mégaoctet : la taille où un disque donne son débit continu.</summary>
        private const int BlockBytes = 1024 * 1024;

        /// <summary>Taille d'un accès isolé : celle d'une page, comme presque tout ce que lit Windows.</summary>
        private const int RandomBytes = 4096;

        private const int RandomReadCount = 512;

        /// <summary>Au-delà, le test aléatoire s'arrête : le résultat est acquis depuis longtemps.</summary>
        private static readonly TimeSpan RandomBudget = TimeSpan.FromSeconds(12);

        /// <summary>Marge exigée en plus du fichier de mesure. Un disque plein ne se mesure pas.</summary>
        private const long RequiredHeadroomBytes = 1024L * 1024 * 1024;

        private readonly ILdiLogger _logger;

        public StorageBenchmarkGateway(ILdiLogger logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public StorageBenchmarkPlan Plan(string volumeRoot, long bytes)
        {
            var volume = (volumeRoot ?? string.Empty).TrimEnd('\\');
            var rounded = Math.Max(BlockBytes, bytes / BlockBytes * BlockBytes);

            try
            {
                var drive = new DriveInfo(volumeRoot!);

                if (!drive.IsReady)
                    return Refused(volume, rounded, Measured.Missing<long>("Volume non prêt."),
                        "Ce volume n'est pas prêt : il n'y a rien à mesurer tant qu'il n'est pas accessible.");

                if (drive.DriveType == DriveType.Network)
                    return Refused(volume, rounded, Measured.Ok(drive.AvailableFreeSpace, DataSource.FileSystem),
                        "Ce volume est un lecteur réseau : la mesure décrirait le réseau, pas un disque.");

                var free = drive.AvailableFreeSpace;
                if (free < rounded + RequiredHeadroomBytes)
                {
                    return Refused(volume, rounded, Measured.Ok(free, DataSource.FileSystem),
                        "Il reste " + ValueFormat.Bytes(free) + " sur ce volume. La mesure en écrit " +
                        ValueFormat.Bytes(rounded) + " et exige un gigaoctet de marge : remplir un " +
                        "disque déjà plein pour mesurer sa lenteur serait aggraver ce qu'on vient " +
                        "de constater.");
                }

                return new StorageBenchmarkPlan
                {
                    FilePath = Path.Combine(Directory(volumeRoot!), FileName()),
                    Volume = volume,
                    Bytes = rounded,
                    BlockBytes = BlockBytes,
                    RandomReads = RandomReadCount,
                    FreeBytes = Measured.Ok(free, DataSource.FileSystem),
                };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                _logger.For(Category).Warn("Le volume " + volume + " n'a pas pu être décrit : " + ex.Message);

                return Refused(volume, rounded, Measured.Missing<long>(ex.Message),
                    "Ce volume n'a pas pu être examiné : " + ex.Message);
            }
        }

        public StorageThroughput Measure(
            StorageBenchmarkPlan plan, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.CanRun) return Failed(plan.Refusal!);

            var log = _logger.For(Category);

            try
            {
                using (var buffer = new DiskIoNative.AlignedBuffer(plan.BlockBytes))
                using (var file = Open(plan.FilePath))
                {
                    if (file.IsInvalid)
                    {
                        var code = (uint)Marshal.GetLastWin32Error();
                        return Failed(DescribeOpenFailure(code, plan.Volume));
                    }

                    buffer.FillWithVariedData();

                    progress?.Report("Écriture de " + ValueFormat.Bytes(plan.Bytes) + " sur " + plan.Volume + "…");
                    var write = Sequential(file, buffer, plan, cancellationToken, writing: true);

                    progress?.Report("Relecture de " + ValueFormat.Bytes(plan.Bytes) + "…");
                    var read = Sequential(file, buffer, plan, cancellationToken, writing: false);

                    progress?.Report("Accès isolés…");
                    var random = Random(file, buffer, plan, cancellationToken);

                    log.Info(
                        "Mesure de " + plan.Volume + " : écriture " + Rate(write) + ", lecture " +
                        Rate(read) + ", accès isolé " +
                        random.Milliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms sur " +
                        random.Count + " accès.");

                    return new StorageThroughput
                    {
                        WriteBytesPerSecond = Measured.Ok(write, DataSource.FileSystem),
                        ReadBytesPerSecond = Measured.Ok(read, DataSource.FileSystem),
                        RandomReadMilliseconds = random.Count > 0
                            ? Measured.Ok(random.Milliseconds, DataSource.FileSystem)
                            : Measured.Missing<double>("Aucun accès isolé n'a pu être mesuré."),
                        RandomReadsPerSecond = random.Count > 0 && random.Milliseconds > 0
                            ? Measured.Ok(1000d / random.Milliseconds, DataSource.FileSystem)
                            : Measured.Missing<double>("Aucun accès isolé n'a pu être mesuré."),
                        Limitation = random.Count < plan.RandomReads
                            ? "Le test d'accès isolés s'est arrêté après " + random.Count + " accès sur " +
                              plan.RandomReads + " : ce disque met trop de temps à répondre pour que " +
                              "les suivants apprennent quelque chose de plus."
                            : null,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                // Le fichier disparaît avec le descripteur : l'annulation ne laisse rien.
                return Failed("Mesure annulée. Le fichier de test a été supprimé.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is OutOfMemoryException)
            {
                log.Warn("La mesure de " + plan.Volume + " a échoué : " + ex.Message);
                return Failed("La mesure n'a pas pu être menée : " + ex.Message);
            }
        }

        private static SafeFileHandle Open(string path)
            => DiskIoNative.CreateFile(
                path,
                DiskIoNative.GenericRead | DiskIoNative.GenericWrite,
                DiskIoNative.ShareAll,
                IntPtr.Zero,
                DiskIoNative.CreateAlways,
                DiskIoNative.FlagNoBuffering | DiskIoNative.FlagWriteThrough | DiskIoNative.FlagDeleteOnClose,
                IntPtr.Zero);

        private static double Sequential(
            SafeFileHandle file, DiskIoNative.AlignedBuffer buffer, StorageBenchmarkPlan plan,
            CancellationToken cancellationToken, bool writing)
        {
            if (!DiskIoNative.SetFilePointerEx(file, 0, IntPtr.Zero, 0))
                throw new IOException("Le début du fichier de mesure n'a pas pu être atteint.");

            var blocks = (int)(plan.Bytes / plan.BlockBytes);
            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < blocks; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ok = writing
                    ? DiskIoNative.WriteFile(file, buffer.Address, plan.BlockBytes, out var moved, IntPtr.Zero)
                    : DiskIoNative.ReadFile(file, buffer.Address, plan.BlockBytes, out moved, IntPtr.Zero);

                if (!ok || moved != plan.BlockBytes)
                {
                    var code = (uint)Marshal.GetLastWin32Error();
                    throw new IOException(code == DiskIoNative.ErrorDiskFull
                        ? "Le volume s'est rempli pendant la mesure."
                        : (writing ? "L'écriture" : "La lecture") + " s'est interrompue.");
                }
            }

            // La consigne d'écriture traversante est déjà donnée à l'ouverture ; ce vidage garantit
            // que rien ne reste en attente au moment où le chronomètre s'arrête.
            if (writing) DiskIoNative.FlushFileBuffers(file);

            stopwatch.Stop();

            var seconds = stopwatch.Elapsed.TotalSeconds;
            return seconds > 0 ? plan.Bytes / seconds : 0d;
        }

        /// <summary>
        /// Accès isolés à des positions tirées au hasard.
        /// </summary>
        /// <remarks>
        /// Les positions sont alignées sur une page, condition d'une lecture sans mémoire tampon,
        /// et tirées d'une graine fixe : deux mesures du même disque doivent parcourir le même
        /// chemin, sans quoi l'écart entre un avant et un après mesurerait le tirage.
        /// </remarks>
        private static (int Count, double Milliseconds) Random(
            SafeFileHandle file, DiskIoNative.AlignedBuffer buffer, StorageBenchmarkPlan plan,
            CancellationToken cancellationToken)
        {
            var random = new Random(12);
            var positions = plan.Bytes / RandomBytes;
            if (positions <= 0) return (0, 0d);

            var stopwatch = Stopwatch.StartNew();
            var done = 0;

            for (var index = 0; index < plan.RandomReads; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > RandomBudget) break;

                var offset = (long)(random.NextDouble() * (positions - 1)) * RandomBytes;

                if (!DiskIoNative.SetFilePointerEx(file, offset, IntPtr.Zero, 0)) break;
                if (!DiskIoNative.ReadFile(file, buffer.Address, RandomBytes, out var moved, IntPtr.Zero)) break;
                if (moved != RandomBytes) break;

                done++;
            }

            stopwatch.Stop();

            return done > 0 ? (done, stopwatch.Elapsed.TotalMilliseconds / done) : (0, 0d);
        }

        /// <summary>
        /// Dossier où écrire.
        /// </summary>
        /// <remarks>
        /// La racine du volume système est protégée en écriture pour un compte standard depuis
        /// Windows Vista : y écrire échouerait sur toutes les machines où le logiciel est censé
        /// fonctionner sans privilèges. Le dossier temporaire est sur le même volume et pose la
        /// question au même disque.
        /// </remarks>
        private static string Directory(string volumeRoot)
        {
            var temp = Path.GetTempPath();

            return string.Equals(Path.GetPathRoot(temp), Path.GetPathRoot(volumeRoot),
                       StringComparison.OrdinalIgnoreCase)
                ? temp
                : volumeRoot;
        }

        private static string FileName()
            => "LDI12-mesure-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".tmp";

        internal static string DescribeOpenFailure(uint code, string volume) => code switch
        {
            DiskIoNative.ErrorAccessDenied =>
                "Windows a refusé l'écriture sur " + volume + ". Un volume protégé en écriture ne " +
                "peut pas être mesuré de cette façon.",
            DiskIoNative.ErrorDiskFull =>
                "Il ne reste pas assez de place sur " + volume + " pour le fichier de mesure.",
            _ => "Le fichier de mesure n'a pas pu être créé sur " + volume + ".",
        };

        private static StorageBenchmarkPlan Refused(
            string volume, long bytes, Measured<long> free, string refusal)
            => new StorageBenchmarkPlan
            {
                Volume = volume,
                Bytes = bytes,
                BlockBytes = BlockBytes,
                RandomReads = RandomReadCount,
                FreeBytes = free,
                Refusal = refusal,
            };

        private static StorageThroughput Failed(string reason)
            => new StorageThroughput
            {
                WriteBytesPerSecond = Measured.Missing<double>(reason, DataSource.FileSystem),
                ReadBytesPerSecond = Measured.Missing<double>(reason, DataSource.FileSystem),
                RandomReadMilliseconds = Measured.Missing<double>(reason, DataSource.FileSystem),
                RandomReadsPerSecond = Measured.Missing<double>(reason, DataSource.FileSystem),
                Limitation = reason,
            };

        private static string Rate(double bytesPerSecond)
            => ValueFormat.Bytes((long)bytesPerSecond) + "/s";
    }
}
