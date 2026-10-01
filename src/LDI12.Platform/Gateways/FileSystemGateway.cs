using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Native;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Système de fichiers : balayage borné et suppression vérifiée.
    /// </summary>
    /// <remarks>
    /// Le balayage est itératif et non récursif : une arborescence profonde ou circulaire par
    /// jonctions ferait déborder la pile d'un parcours récursif, exactement sur les machines
    /// abîmées où l'on a le plus besoin de l'outil. Les points de reparse ne sont jamais suivis.
    /// </remarks>
    public sealed class FileSystemGateway : IFileSystemGateway
    {
        private const string Category = "Platform.Files";

        private readonly IScopedLogger _log;

        public FileSystemGateway(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public bool DirectoryExists(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool FileExists(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        public IReadOnlyList<string> EnumerateDirectories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        public Measured<DirectoryScan> Scan(DirectoryScanRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!Directory.Exists(request.Root))
                return Measured.Missing<DirectoryScan>(
                    "Le dossier « " + request.Root + " » n'existe pas sur cette machine.", DataSource.FileSystem);

            var files = new List<FileEntry>();
            var pending = new Stack<string>();
            var stopwatch = Stopwatch.StartNew();

            long total = 0;
            var inaccessible = 0;
            var reparse = 0;
            var truncated = false;

            pending.Push(request.Root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (files.Count >= request.MaxFiles || stopwatch.Elapsed > request.Budget)
                {
                    truncated = true;
                    break;
                }

                var directory = pending.Pop();

                FileInfo[] entries;
                DirectoryInfo[] children;
                try
                {
                    // Sous sa forme étendue : sans elle, un dossier au-delà de 260 caractères lève
                    // une exception, compte comme « refusé », et tout ce qu'il contient disparaît
                    // du relevé. Mesuré sur une restauration d'essai : 106 fichiers d'un profil
                    // Edge sur 425, tous rangés dans des dossiers IndexedDB profonds.
                    var info = new DirectoryInfo(Extended(directory));
                    entries = info.GetFiles();
                    children = info.GetDirectories();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException ||
                                           ex is PathTooLongException || ex is System.Security.SecurityException)
                {
                    inaccessible++;
                    continue;
                }

                foreach (var child in children)
                {
                    // Relevé limité au premier niveau : les sous-dossiers sont relevés ailleurs,
                    // pour leur propre compte.
                    if (request.TopLevelOnly) break;

                    // Une jonction placée dans un dossier temporaire pointerait ailleurs :
                    // la suivre ferait sortir le balayage du périmètre annoncé.
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparse++;
                        continue;
                    }

                    var childPath = Plain(child.FullName);
                    if (IsExcludedRelative(request, childPath)) continue;

                    pending.Push(childPath);
                }

                foreach (var file in entries)
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparse++;
                        continue;
                    }

                    DateTime lastWrite;
                    long length;
                    try
                    {
                        lastWrite = file.LastWriteTimeUtc;
                        length = file.Length;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (request.OlderThanUtc.HasValue && lastWrite > request.OlderThanUtc.Value) continue;

                    files.Add(new FileEntry
                    {
                        Path = Plain(file.FullName),
                        SizeBytes = length,
                        LastWriteUtc = lastWrite,

                        // Relevé pendant le parcours, où l'attribut est déjà là : le relire
                        // fichier par fichier doublerait les appels système sur un profil qui
                        // en compte des centaines de milliers.
                        CloudOnly = IsCloudOnly(file.Attributes),
                    });
                    total += length;

                    if (files.Count >= request.MaxFiles)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            var scan = new DirectoryScan
            {
                Root = request.Root,
                Files = files,
                TotalBytes = total,
                InaccessibleDirectories = inaccessible,
                SkippedReparsePoints = reparse,
                Truncated = truncated,
            };

            _log.Debug("Balayage de " + request.Root + " : " + files.Count + " fichier(s), " +
                       inaccessible + " dossier(s) refusé(s), en " + stopwatch.ElapsedMilliseconds + " ms.");

            // Un balayage tronqué ou amputé de dossiers refusés reste exploitable, mais il ne
            // dit pas tout : c'est exactement ce que « partiel » signifie.
            if (truncated)
                return Measured.Partial(scan, DataSource.FileSystem,
                    "Relevé interrompu au plafond : ce dossier contient davantage de fichiers que listés.");

            if (inaccessible > 0)
                return Measured.Partial(scan, DataSource.FileSystem,
                    inaccessible + " dossier(s) n'ont pas pu être ouverts et ne sont pas comptés.");

            return Measured.Ok(scan, DataSource.FileSystem);
        }

        public FileDeletion Delete(FileEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            try
            {
                // Sous sa forme étendue, comme la copie : un chemin de plus de 260 caractères
                // existe bel et bien sur le disque, et refuser de le supprimer laisserait
                // derrière soi exactement ce qu'on venait retirer.
                var info = new FileInfo(Extended(entry.Path));
                if (!info.Exists) return FileDeletion.NotFound;

                // Le fichier montré au technicien doit être celui qui part. S'il a changé depuis
                // le relevé, ce n'est plus le même fichier et il n'a pas été montré.
                if (info.Length != entry.SizeBytes || info.LastWriteTimeUtc != entry.LastWriteUtc)
                    return FileDeletion.Changed;

                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    info.Attributes &= ~FileAttributes.ReadOnly;

                info.Delete();
                return FileDeletion.Deleted;
            }
            catch (UnauthorizedAccessException)
            {
                return FileDeletion.Denied;
            }
            catch (IOException)
            {
                // Le cas de loin le plus fréquent : un logiciel ouvert tient encore le fichier.
                return FileDeletion.Locked;
            }
            catch (Exception ex)
            {
                _log.Debug("Suppression impossible de " + entry.Path + " : " + ex.Message);
                return FileDeletion.Failed;
            }
        }

        /// <summary>
        /// Copie un fichier, puis le relit pour vérifier qu'il est arrivé entier.
        /// </summary>
        /// <remarks>
        /// L'empreinte de la source est calculée <b>pendant</b> la copie : les octets passent de
        /// toute façon par la mémoire, la calculer ne coûte rien de plus. Seule la relecture de
        /// la copie ajoute un passage sur le disque, et c'est celui qui apporte la preuve.
        /// <para>
        /// Une copie qui échoue en cours de route est effacée avant de rendre la main. Un
        /// fichier à moitié écrit porte le bon nom, la bonne date, et une taille plausible : il
        /// ressemble à une sauvegarde, et c'est exactement ce qui rend son existence pire que
        /// son absence.
        /// </para>
        /// <para>
        /// <b>Et quand l'effacer est impossible</b>, parce que le support vient d'être débranché,
        /// la copie ne porte pas encore son vrai nom : elle s'écrit sous
        /// <see cref="FileCopyRequest.PartialSuffix"/>, et n'est renommée qu'une fois relue
        /// identique. Un fichier qui porte son vrai nom a donc toujours été vérifié, et une reprise
        /// peut s'y fier.
        /// </para>
        /// </remarks>
        public FileCopyResult Copy(FileCopyRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var source = request.Source;
            if (source.CloudOnly)
                return FileCopyResult.Of(FileCopyOutcome.CloudOnly, 0,
                    "Fichier présent seulement en ligne : le copier le téléchargerait.");

            try
            {
                var info = new FileInfo(Extended(source.Path));
                if (!info.Exists) return FileCopyResult.Of(FileCopyOutcome.NotFound);

                // Le fichier annoncé au technicien doit être celui qui part. Changé depuis le
                // relevé, ce n'est plus celui dont il a vu la taille dans la prévisualisation.
                if (info.Length != source.SizeBytes || info.LastWriteTimeUtc != source.LastWriteUtc)
                    return FileCopyResult.Of(FileCopyOutcome.Changed);

                var destination = Extended(request.Destination);

                // FAT32 refuse tout fichier de 4 Go ou plus : le dire d'emblée, sous son vrai nom,
                // plutôt que d'écrire des gigaoctets pour échouer sur un motif trompeur.
                if (source.SizeBytes >= Fat32Limit && IsFat(destination))
                    return FileCopyResult.Of(FileCopyOutcome.FileTooLarge, 0,
                        "Fichier de 4 Go ou plus : le format FAT32 du support ne peut pas le recevoir.");

                var existing = new FileInfo(destination);
                if (existing.Exists)
                    return existing.Length == info.Length && SameTime(existing.LastWriteTimeUtc, info.LastWriteTimeUtc)
                        ? FileCopyResult.Of(FileCopyOutcome.AlreadyPresent)
                        : FileCopyResult.Of(FileCopyOutcome.Conflict, 0,
                            "Un fichier différent porte déjà ce nom à la destination.");

                // Le reste d'une copie interrompue du même fichier est écrasé : il est à nous, à son
                // nom, et le vérifier d'abord coûterait un appel au support par fichier.
                var partial = destination + FileCopyRequest.PartialSuffix;

                byte[] expected;
                long written;
                bool dated;

                try
                {
                    try
                    {
                        expected = Write(info, partial, out written, out dated, request.Progressed, cancellationToken);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Le dossier n'est créé qu'au premier fichier qui en a besoin : le demander
                        // pour chaque fichier coûtait un aller-retour au support, cent mille fois
                        // sur un profil de navigateur.
                        var parent = Path.GetDirectoryName(destination);
                        if (string.IsNullOrEmpty(parent)) throw;
                        Directory.CreateDirectory(parent);
                        expected = Write(info, partial, out written, out dated, request.Progressed, cancellationToken);
                    }
                }
                catch
                {
                    Discard(partial);
                    throw;
                }

                // La date de la source est reportée : sans elle, une reprise de sauvegarde
                // reverrait chaque fichier comme différent et recopierait tout. Posée d'ordinaire
                // pendant l'écriture ; sinon ici.
                if (!dated)
                {
                    try { File.SetLastWriteTimeUtc(partial, info.LastWriteTimeUtc); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }

                try
                {
                    if (request.Verify)
                    {
                        var actual = Fingerprint(partial, written, request.Progressed, cancellationToken);
                        if (!Same(expected, actual))
                        {
                            Discard(partial);
                            return FileCopyResult.Of(FileCopyOutcome.VerificationFailed, 0,
                                "La copie ne se relit pas identique à la source. Elle a été retirée.");
                        }
                    }

                    // Le renommage garde la date posée plus haut. Il échoue si un fichier du même
                    // nom est apparu entre-temps : rien n'est écrasé, même dans ce cas.
                    File.Move(partial, destination);
                }
                catch
                {
                    Discard(partial);
                    throw;
                }

                KeepAttributes(destination, info.Attributes);
                return FileCopyResult.Of(FileCopyOutcome.Copied, written);
            }
            catch (PathTooLongException)
            {
                return FileCopyResult.Of(FileCopyOutcome.PathTooLong, 0,
                    "Le chemin de destination dépasse ce que Windows accepte.");
            }
            catch (UnauthorizedAccessException)
            {
                return FileCopyResult.Of(FileCopyOutcome.Denied);
            }
            catch (IOException ex)
            {
                var code = ex.HResult & 0xFFFF;

                // 0x27 et 0x70 : disque plein. 0x20 : fichier tenu par un autre programme.
                if (code == 0x27 || code == 0x70)
                    return FileCopyResult.Of(FileCopyOutcome.NoSpace, 0, "La destination est pleine.");

                if (IsDeviceError(code))
                    return FileCopyResult.Of(FileCopyOutcome.DeviceError, 0,
                        "Le support ne répond plus : débranché, ou défaillant. " + ex.Message);

                // 223 : fichier trop gros pour le système de fichiers de la destination.
                if (code == 223)
                    return FileCopyResult.Of(FileCopyOutcome.FileTooLarge, 0,
                        "Fichier trop gros pour le format du support de destination.");

                return FileCopyResult.Of(FileCopyOutcome.Locked, 0, ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Arrêt demandé au milieu d'un fichier : sa copie partielle est déjà retirée. Ce
                // n'est pas un échec du fichier, il sera copié en entier à la reprise.
                throw;
            }
            catch (Exception ex)
            {
                _log.Debug("Copie impossible de " + source.Path + " : " + ex.Message);
                return FileCopyResult.Of(FileCopyOutcome.Failed, 0, ex.Message);
            }
        }

        /// <summary>Les attributs qu'une copie garde de sa source.</summary>
        private const FileAttributes KeptAttributes =
            FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

        /// <summary>
        /// Reporte les attributs de la source sur la copie, après son renommage.
        /// </summary>
        /// <remarks>
        /// Sans eux, les « desktop.ini » cachés du Bureau, des Documents et des Images revenaient
        /// visibles à la restauration, posés en icônes sur le Bureau du client. Posés après le
        /// renommage : un fichier en lecture seule ne se renommerait plus sur certains supports.
        /// Un refus ne fait pas échouer la copie, dont le contenu est déjà vérifié.
        /// </remarks>
        private static void KeepAttributes(string destination, FileAttributes source)
        {
            var kept = source & KeptAttributes;
            if (kept == 0 || kept == FileAttributes.Archive) return;

            try { File.SetAttributes(destination, kept); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Deux dates d'écriture tenues pour égales.
        /// </summary>
        /// <remarks>
        /// FAT32, format d'origine de la plupart des clés USB, ne retient les dates qu'à deux
        /// secondes près. Comparées à l'exacte, aucune copie n'y était jamais reconnue comme déjà
        /// faite : chaque reprise signalait tout en conflit.
        /// </remarks>
        internal static bool SameTime(DateTime left, DateTime right)
            => Math.Abs((left - right).TotalSeconds) <= 2;

        /// <summary>4 Gio : au-delà, FAT32 refuse le fichier.</summary>
        internal const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _fat =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Vrai si la destination est en FAT ou FAT32. Lu une fois par volume.</summary>
        private bool IsFat(string destination)
        {
            string root;
            try { root = Path.GetPathRoot(Plain(destination)) ?? string.Empty; }
            catch (ArgumentException) { return false; }
            if (root.Length == 0) return false;

            return _fat.GetOrAdd(root, key =>
            {
                try
                {
                    var format = new DriveInfo(key).DriveFormat;
                    return format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) &&
                           !format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Les erreurs Windows d'un support qui ne répond plus.
        /// </summary>
        /// <remarks>
        /// 21 : pas prêt. 23 : erreur de lecture (CRC). 31 : défaillance générale. 55 : ressource
        /// disparue. 483 : erreur matérielle. 1006 : volume modifié de l'extérieur, ce que rend un
        /// support arraché. 1117 : erreur d'entrée-sortie. 1167 : périphérique déconnecté.
        /// </remarks>
        internal static bool IsDeviceError(int code)
            => code == 21 || code == 23 || code == 31 || code == 55 || code == 483 ||
               code == 1006 || code == 1117 || code == 1167;

        /// <summary>Taille d'un bloc de copie.</summary>
        private const int Block = 1024 * 1024;

        /// <summary>
        /// Tampon interne des flux : aucun. Chaque lecture et chaque écriture porte un bloc
        /// entier, qu'un tampon de <see cref="FileStream"/> ne ferait que recopier ; et sous .NET
        /// Framework, un dernier bloc incomplet lui ferait allouer un mégaoctet par fichier.
        /// </summary>
        private const int NoStreamBuffer = 1;

        // Deux blocs par fil d'exécution, gardés d'un fichier à l'autre : les allouer à chaque
        // copie coûtait deux mégaoctets par fichier sur le tas des gros objets, jamais compacté,
        // soit des centaines de gigaoctets d'allocations pour un profil de navigateur.
        [ThreadStatic] private static byte[][]? _blocks;

        [ThreadStatic] private static HashAlgorithm? _sha256;

        /// <summary>Écrit la copie et rend l'empreinte de ce qui a été lu à la source.</summary>
        /// <remarks>
        /// L'écriture d'un bloc se fait pendant que le suivant se lit et que l'empreinte se
        /// calcule : source et destination sont presque toujours deux supports différents, et
        /// les faire attendre l'un l'autre revenait à additionner leurs temps au lieu de ne payer
        /// que celui du plus lent.
        /// </remarks>
        private static byte[] Write(
            FileInfo source, string destination, out long written, out bool dated, Action<long>? progressed,
            CancellationToken cancellationToken)
        {
            var hash = Sha256();
            using var input = OpenSource(source.FullName);
            using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, NoStreamBuffer);

            // Réserver la taille d'emblée, comme robocopy : le système de fichiers alloue le
            // fichier d'un seul tenant au lieu de l'étendre à chaque bloc, ce qui compte sur les
            // clés et disques externes en FAT32 ou exFAT. Un support plein le dit dès ici, et
            // non au dernier bloc.
            var expected = source.Length;
            if (expected > Block) output.SetLength(expected);

            written = Pump(
                block => input.Read(block, 0, block.Length),
                (block, count) => output.Write(block, 0, count),
                (block, count) => hash.TransformBlock(block, 0, count, null, 0),
                progressed, cancellationToken);

            // Une source raccourcie pendant la copie ne doit pas laisser de zéros en queue :
            // la relecture la rejettera, mais la copie sans vérification, elle, la garderait.
            if (written != expected && expected > Block) output.SetLength(written);

            dated = SetDate(output, source.LastWriteTimeUtc);

            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hash.Hash;
        }

        /// <summary>
        /// Pose la date de la source sur la copie encore ouverte.
        /// </summary>
        /// <remarks>
        /// La poser après coup rouvrait chaque fichier, soit une ouverture, une fermeture et un
        /// passage de l'antivirus de plus par fichier. Posée par le descripteur, après la dernière
        /// écriture, elle n'est plus modifiée à la fermeture.
        /// </remarks>
        private static bool SetDate(FileStream output, DateTime lastWriteUtc)
        {
            try
            {
                var time = lastWriteUtc.ToFileTimeUtc();
                return DiskIoNative.SetFileTime(output.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, ref time);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException ||
                                       ex is ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>
        /// Ouvre la source en lecture.
        /// </summary>
        /// <remarks>
        /// En mode sauvegarde, quand le processus en a le privilège (voir
        /// <see cref="EnableBackupSemantics"/>), l'ouverture passe outre les droits du fichier sans
        /// les modifier, comme robocopy /B : c'est ce qui permet de lire les comptes d'un autre
        /// Windows, protégés par des droits qui ne connaissent pas le technicien.
        /// </remarks>
        private static Stream OpenSource(string path)
        {
            if (!BackupSemantics)
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoStreamBuffer,
                    FileOptions.SequentialScan);

            var handle = DiskIoNative.CreateFile(
                path, DiskIoNative.GenericRead, DiskIoNative.ShareAll, IntPtr.Zero, DiskIoNative.OpenExisting,
                DiskIoNative.FlagBackupSemantics | DiskIoNative.FlagSequentialScan, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var code = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (code == 5) throw new UnauthorizedAccessException(new System.ComponentModel.Win32Exception(code).Message);
                if (code == 2 || code == 3) throw new FileNotFoundException(new System.ComponentModel.Win32Exception(code).Message, path);
                throw new IOException(new System.ComponentModel.Win32Exception(code).Message, unchecked((int)0x80070000) | code);
            }

            return new FileStream(handle, FileAccess.Read, NoStreamBuffer);
        }

        /// <summary>Lecture en mode sauvegarde, activée par <see cref="EnableBackupSemantics"/>.</summary>
        internal static bool BackupSemantics { get; private set; }

        /// <summary>
        /// Donne au processus le privilège de sauvegarde et fait lire toutes les sources avec lui.
        /// </summary>
        /// <remarks>
        /// Réservé à l'hôte élevé : un administrateur détient ce privilège, désactivé par défaut.
        /// Rend faux si Windows le refuse ; la copie se fait alors avec les droits ordinaires, et un
        /// fichier protégé est compté comme refusé.
        /// </remarks>
        public static bool EnableBackupSemantics()
        {
            try
            {
                BackupSemantics = PrivilegeNative.Enable("SeBackupPrivilege");
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                BackupSemantics = false;
            }

            return BackupSemantics;
        }

        /// <summary>
        /// Relit la copie sur le support lui-même, et rend son empreinte.
        /// </summary>
        /// <remarks>
        /// <b>Une relecture ordinaire ne relit rien.</b> Juste après l'écriture, les pages du
        /// fichier sont encore dans le cache de Windows : une lecture ordinaire les y reprend, et
        /// compare la mémoire vive à elle-même. Un disque externe qui accepte les écritures et
        /// rend autre chose à la relecture passait donc la vérification, c'est-à-dire exactement
        /// le cas pour lequel elle existe.
        /// <para>
        /// La relecture se fait donc sans mémoire tampon, comme la mesure de disque. Pour servir
        /// une telle lecture, le système de fichiers commence par écrire sur le support ce que
        /// son cache garde encore du fichier : les octets relus sont ceux du support. Seul le
        /// cache interne du disque peut encore s'intercaler, et aucun logiciel n'y a la main.
        /// </para>
        /// <para>
        /// <b>Pas de <c>FlushFileBuffers</c> à chaque fichier.</b> Il écrirait sur le support ce
        /// que la relecture sans tampon y fait déjà écrire, et ordonnerait en plus au disque de
        /// vider son propre cache sans pour autant empêcher la relecture d'y puiser : un aller-retour
        /// de plus par fichier, soit des minutes sur un profil de navigateur, pour aucune preuve
        /// supplémentaire.
        /// </para>
        /// <para>
        /// <b>Le prix.</b> La copie ne profite plus de l'écriture différée : chaque fichier est
        /// réellement sur le support avant de passer au suivant, et la relecture se fait à la
        /// vitesse du support au lieu de celle de la mémoire. Sur un support externe, c'est à peu
        /// près une lecture complète de la sauvegarde en plus, puisque c'est précisément ce que
        /// la vérification prétendait faire. Le coût réel sur un support donné se mesure avec
        /// <c>CopyReadBackMeasureTests</c>, qui copie les mêmes fichiers relus des deux façons.
        /// </para>
        /// <para>
        /// Un support qui refuse la lecture sans tampon (secteurs de plus de quatre kilo-octets,
        /// certains partages réseau) est relu par le cache, et le journal le dit une fois : mieux
        /// vaut une vérification plus faible, annoncée, qu'une sauvegarde impossible.
        /// </para>
        /// </remarks>
        private byte[] Fingerprint(string path, long length, Action<long>? progressed, CancellationToken cancellationToken)
        {
            if (!ReadBackThroughCache)
            {
                try
                {
                    using var reader = UnbufferedReader.Open(path, length);
                    if (reader != null)
                    {
                        var direct = Sha256();
                        Pump(reader.Read, (block, count) => direct.TransformBlock(block, 0, count, null, 0), null,
                            progressed, cancellationToken);

                        direct.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return direct.Hash;
                    }
                }
                catch (UnbufferedReadRefusedException)
                {
                    // Refusée dès le premier bloc : rien n'a encore été compté, on reprend par le cache.
                }

                if (!_cachedReadBackLogged)
                {
                    _cachedReadBackLogged = true;
                    _log.Warn("Relecture sans mémoire tampon refusée par la destination de " + path +
                              " : la vérification des copies passe par le cache de Windows.");
                }
            }

            var hash = Sha256();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, NoStreamBuffer, FileOptions.SequentialScan);

            Pump(block => stream.Read(block, 0, block.Length),
                (block, count) => hash.TransformBlock(block, 0, count, null, 0), null,
                progressed, cancellationToken);

            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hash.Hash;
        }

        /// <summary>
        /// Relire les copies par le cache de Windows, comme avant.
        /// </summary>
        /// <remarks>
        /// N'existe que pour mesurer ce que coûte la relecture sur le support : l'application ne
        /// le règle jamais.
        /// </remarks>
        internal bool ReadBackThroughCache { get; set; }

        private bool _cachedReadBackLogged;

        /// <summary>
        /// Lecture séquentielle sans mémoire tampon, bloc par bloc.
        /// </summary>
        /// <remarks>
        /// Les conditions sont celles de <see cref="DiskIoNative"/> : tampon aligné sur une page,
        /// transferts multiples de quatre kilo-octets. Le dernier secteur d'un fichier n'est
        /// généralement pas plein : la lecture demande un secteur entier et Windows ne rend que
        /// les octets du fichier. Après elle, la position n'est plus alignée, et une lecture de
        /// plus serait refusée au lieu de rendre zéro : la fin est donc retenue ici.
        /// </remarks>
        private sealed class UnbufferedReader : IDisposable
        {
            private readonly SafeFileHandle _file;
            private readonly DiskIoNative.AlignedBuffer _buffer;
            private bool _started;
            private bool _ended;

            private UnbufferedReader(SafeFileHandle file, DiskIoNative.AlignedBuffer buffer)
            {
                _file = file;
                _buffer = buffer;
            }

            /// <summary>Ouvre le fichier, ou rend nul si ce support ne sait pas lire sans tampon.</summary>
            internal static UnbufferedReader? Open(string path, long length)
            {
                SafeFileHandle file;
                try
                {
                    file = DiskIoNative.CreateFile(
                        path, DiskIoNative.GenericRead, DiskIoNative.ShareRead, IntPtr.Zero,
                        DiskIoNative.OpenExisting, DiskIoNative.FlagNoBuffering | DiskIoNative.FlagSequentialScan,
                        IntPtr.Zero);
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                    return null;
                }

                if (file.IsInvalid)
                {
                    var code = (uint)Marshal.GetLastWin32Error();
                    file.Dispose();
                    if (code == DiskIoNative.ErrorInvalidParameter || code == DiskIoNative.ErrorNotSupported)
                        return null;
                    throw Failure(code, path);
                }

                // Un petit fichier n'a pas besoin d'un mégaoctet : un tampon à sa taille, arrondie
                // à la page, suffit à le lire en une fois.
                var pages = Math.Max(1, Math.Min(Block, length + DiskIoNative.Alignment - 1) / DiskIoNative.Alignment);
                return new UnbufferedReader(file, new DiskIoNative.AlignedBuffer((int)pages * DiskIoNative.Alignment));
            }

            internal int Read(byte[] block)
            {
                if (_ended) return 0;

                var wanted = Math.Min(block.Length, _buffer.Size);
                if (!DiskIoNative.ReadFile(_file, _buffer.Address, wanted, out var read, IntPtr.Zero))
                {
                    var code = (uint)Marshal.GetLastWin32Error();
                    if (!_started && code == DiskIoNative.ErrorInvalidParameter)
                        throw new UnbufferedReadRefusedException();
                    throw Failure(code, null);
                }

                _started = true;
                if (read < wanted) _ended = true;

                Marshal.Copy(_buffer.Address, block, 0, read);
                return read;
            }

            public void Dispose()
            {
                _file.Dispose();
                _buffer.Dispose();
            }

            /// <summary>
            /// L'erreur Windows sous la forme que <see cref="Copy"/> sait classer : refus d'accès,
            /// ou entrée-sortie portant son code, qui distingue un disque plein d'un fichier tenu.
            /// </summary>
            private static Exception Failure(uint code, string? path)
            {
                var message = new Win32Exception((int)code).Message + (path == null ? string.Empty : " : " + path);
                return code == DiskIoNative.ErrorAccessDenied
                    ? new UnauthorizedAccessException(message)
                    : new IOException(message, unchecked((int)0x80070000) | (int)code);
            }
        }

        /// <summary>Le support refuse la lecture sans tampon, avant qu'un seul octet ait été lu.</summary>
        private sealed class UnbufferedReadRefusedException : Exception
        {
        }

        /// <summary>
        /// Lit bloc par bloc, et traite chaque bloc pendant que le suivant se lit.
        /// </summary>
        /// <remarks>
        /// <paramref name="read"/> remplit un bloc et rend le nombre d'octets lus, zéro à la fin.
        /// <paramref name="background"/> part sur un autre fil pendant que la lecture suivante
        /// avance ; <paramref name="alongside"/>, s'il est donné, s'exécute sur ce fil-ci en même
        /// temps que lui, sur le même bloc, que ni l'un ni l'autre ne modifie. Deux blocs
        /// alternent : celui qu'on lit n'est jamais celui qu'on traite. Un bloc incomplet, qui
        /// est le dernier ou le seul d'un petit fichier, est traité sur place : il n'y a plus
        /// rien à lire en parallèle, et changer de fil coûterait plus qu'il ne rapporte.
        /// <para>
        /// L'avancement n'est annoncé qu'une fois le bloc traité, et toujours depuis ce fil :
        /// celui qui le reçoit n'a pas à être prêt à être appelé de deux endroits à la fois.
        /// </para>
        /// </remarks>
        private static long Pump(
            Func<byte[], int> read, Action<byte[], int> background, Action<byte[], int>? alongside,
            Action<long>? progressed, CancellationToken cancellationToken)
        {
            var blocks = _blocks ??= new[] { new byte[Block], new byte[Block] };
            var current = 0;
            long total = 0;

            Task? pending = null;
            var pendingCount = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var block = blocks[current];
                    var count = read(block);

                    if (pending != null)
                    {
                        var finished = pending;
                        pending = null;

                        // GetResult et non Wait : l'exception d'origine remonte telle quelle,
                        // avec son code, et un disque plein reste reconnu comme tel.
                        finished.GetAwaiter().GetResult();
                        progressed?.Invoke(pendingCount);
                    }

                    if (count == 0) return total;
                    total += count;

                    if (count < block.Length)
                    {
                        background(block, count);
                        alongside?.Invoke(block, count);
                        progressed?.Invoke(count);
                        continue;
                    }

                    pendingCount = count;
                    pending = Task.Run(() => background(block, count));
                    alongside?.Invoke(block, count);
                    current ^= 1;
                }
            }
            finally
            {
                // Jamais de bloc encore en cours d'écriture quand le flux se ferme, ni quand
                // le tampon repart servir au fichier suivant.
                if (pending != null)
                    try { pending.Wait(); }
                    catch (AggregateException) { }
            }
        }

        /// <summary>
        /// L'empreinte SHA-256, par l'implémentation de Windows quand elle est disponible.
        /// </summary>
        /// <remarks>
        /// Sous .NET Framework, <c>SHA256.Create()</c> rend l'implémentation managée, plusieurs
        /// fois plus lente que celle de Windows et plus lente qu'un SSD externe en USB 3 : chaque
        /// octet copié passant deux fois par l'empreinte, c'était elle, et non le disque, qui
        /// fixait la vitesse de la sauvegarde. Elle est en outre refusée sur une machine
        /// configurée en mode FIPS. L'instance est gardée d'un fichier à l'autre et remise à zéro
        /// avant chaque usage.
        /// </remarks>
        private static HashAlgorithm Sha256()
        {
            var hash = _sha256 ??= CreateSha256();
            hash.Initialize();
            return hash;
        }

        private static HashAlgorithm CreateSha256()
        {
            try
            {
                return new SHA256Cng();
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is CryptographicException ||
                                       ex is NotImplementedException)
            {
                return SHA256.Create();
            }
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        /// <summary>Retire une copie incomplète. Son échec n'est pas une raison d'échouer deux fois.</summary>
        private static void Discard(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public string? VolumeFormat(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(Plain(path)));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).DriveFormat;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException)
            {
                return null;
            }
        }

        public bool CreateDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(Extended(path));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is PathTooLongException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            {
                _log.Debug("Création impossible du dossier " + path + " : " + ex.Message);
                return false;
            }
        }

        public bool MoveDirectory(string source, string destination)
        {
            try
            {
                var from = Extended(source);
                var to = Extended(destination);
                if (!Directory.Exists(from) || Directory.Exists(to) || File.Exists(to)) return false;

                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is PathTooLongException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            {
                _log.Debug("Déplacement impossible de " + source + " vers " + destination + " : " + ex.Message);
                return false;
            }
        }

        public Measured<long> FreeSpace(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                    return Measured.Missing<long>("Le volume de destination n'a pas pu être déterminé.");

                return Measured.Ok(new DriveInfo(root).AvailableFreeSpace, DataSource.FileSystem);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return Measured.Missing<long>("La place libre n'a pas pu être lue : " + ex.Message);
            }
        }

        public Measured<string> ReadText(string path)
        {
            try
            {
                var full = Extended(path);
                if (!File.Exists(full))
                    return Measured.Missing<string>("Le fichier est absent : " + path, DataSource.FileSystem);

                return Measured.Ok(File.ReadAllText(full), DataSource.FileSystem);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is PathTooLongException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            {
                return Measured.Missing<string>(
                    "Le fichier n'a pas pu être lu : " + ex.Message, DataSource.FileSystem);
            }
        }

        public bool ReplaceText(string path, string content)
        {
            try
            {
                File.WriteAllText(Extended(path), content ?? string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is PathTooLongException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            {
                _log.Debug("Écriture refusée sur " + path + " : " + ex.Message);
                return false;
            }
        }

        public bool WriteText(string path, string content)
        {
            try
            {
                var full = Extended(path);
                if (File.Exists(full)) return false;

                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                File.WriteAllText(full, content ?? string.Empty, new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is PathTooLongException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            {
                _log.Debug("Écriture impossible de " + path + " : " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Le chemin sous la forme que Windows accepte au-delà de 260 caractères.
        /// </summary>
        /// <remarks>
        /// Une sauvegarde allonge les chemins : le dossier de destination s'ajoute devant
        /// l'arborescence copiée, et des chemins qui tenaient de justesse à la source dépassent
        /// à l'arrivée. Le préfixe étendu lève la limite sans dépendre d'un réglage de la
        /// machine, contrairement au support des chemins longs de Windows 10, qui s'active dans
        /// le registre et que la plupart des machines de clients n'ont pas.
        /// </remarks>
        internal static string Extended(string path) => Extended(path, ExtendedPathsAccepted);

        /// <summary>
        /// La forme étendue quand le processus l'accepte, la forme ordinaire sinon.
        /// </summary>
        /// <remarks>
        /// <b>Ne jamais présumer que le préfixe passe.</b> Sous le traitement historique des
        /// chemins de .NET Framework, <c>\\?\C:\…</c> lève « caractères non conformes dans le
        /// chemin d'accès » dès la construction d'un <c>FileInfo</c>. La version 1.23.0 ne le
        /// vérifiait pas : la sauvegarde échouait sur la création de son dossier avant d'avoir
        /// copié un seul fichier, et le nettoyage rendait « échec » pour chaque fichier. Les tests
        /// passaient, parce que leur hôte accepte le préfixe ; l'application, non.
        /// <para>
        /// Sans préfixe, un chemin ordinaire fonctionne partout, et un chemin trop long échoue
        /// seul, signalé comme tel, au lieu de faire échouer toute l'opération.
        /// </para>
        /// </remarks>
        internal static string Extended(string path, bool accepted)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return accepted || IsDevicePath(path) ? path : Ordinary(path);

            var full = Path.GetFullPath(path);
            if (!accepted) return full;

            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC" + full.Substring(1)
                : @"\\?\" + full;
        }

        /// <summary>Le chemin tel que le reste du logiciel le manipule : jamais sous forme étendue.</summary>
        internal static string Plain(string path)
            => path.StartsWith(@"\\?\", StringComparison.Ordinal) && !IsDevicePath(path) ? Ordinary(path) : path;

        /// <summary>
        /// Un chemin de périphérique, comme celui d'un cliché instantané
        /// (<c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\…</c>).
        /// </summary>
        /// <remarks>
        /// Il n'a pas de forme ordinaire : sans son préfixe, il ne désigne plus rien. Il reste donc
        /// tel quel d'un bout à l'autre, relevé comme copie.
        /// </remarks>
        internal static bool IsDevicePath(string path)
            => path.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase);

        /// <summary>Retire le préfixe étendu : <c>\\?\UNC\srv\part</c> redevient <c>\\srv\part</c>.</summary>
        internal static string Ordinary(string path)
            => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                ? @"\" + path.Substring(7)
                : path.Substring(4);

        /// <summary>
        /// Vrai si ce processus accepte les chemins préfixés par <c>\\?\</c>.
        /// </summary>
        /// <remarks>
        /// Mesuré une fois, à la première utilisation de la passerelle, en construisant un
        /// <c>DirectoryInfo</c> sur le dossier temporaire : c'est ce constructeur qui normalise le
        /// chemin et qui refuse le préfixe sous le traitement historique. Rien n'est écrit.
        /// </remarks>
        internal static readonly bool ExtendedPathsAccepted = AcceptsExtendedPaths();

        private static bool AcceptsExtendedPaths()
        {
            try
            {
                _ = new DirectoryInfo(@"\\?\" + Path.GetFullPath(Path.GetTempPath()));
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Pèse un dossier sans retenir aucun nom de fichier.
        /// </summary>
        /// <remarks>
        /// Les tailles et les attributs viennent de l'énumération elle-même : sous .NET
        /// Framework, un <c>FileInfo</c> rendu par <c>GetFiles</c> porte déjà ce que Windows a
        /// renvoyé pendant le parcours. Les relire un par un doublerait le nombre d'appels
        /// système sur des dossiers qui comptent des centaines de milliers d'entrées.
        /// </remarks>
        public Measured<DirectoryMeasure> Measure(
            DirectoryMeasureRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!Directory.Exists(request.Root))
                return Measured.Missing<DirectoryMeasure>(
                    "Le dossier « " + request.Root + " » n'existe pas sur cette machine.",
                    DataSource.FileSystem);

            var pending = new Stack<string>();
            var stopwatch = Stopwatch.StartNew();

            long total = 0;
            long cloudOnly = 0;
            var files = 0;
            var cloudFiles = 0;
            var inaccessible = 0;
            var reparse = 0;
            var truncated = false;

            pending.Push(request.Root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.Elapsed > request.Budget)
                {
                    truncated = true;
                    break;
                }

                FileInfo[] entries;
                DirectoryInfo[] children;
                try
                {
                    var info = new DirectoryInfo(Extended(pending.Pop()));
                    entries = info.GetFiles();
                    children = info.GetDirectories();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException ||
                                           ex is PathTooLongException || ex is System.Security.SecurityException)
                {
                    inaccessible++;
                    continue;
                }

                foreach (var child in children)
                {
                    // Suivre une jonction compterait deux fois le même dossier, et ferait sortir
                    // le relevé du périmètre annoncé au technicien.
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reparse++;
                        continue;
                    }

                    var childPath = Plain(child.FullName);
                    if (IsExcluded(request.Exclude, childPath)) continue;

                    pending.Push(childPath);
                }

                foreach (var file in entries)
                {
                    long length;
                    FileAttributes attributes;
                    try
                    {
                        length = file.Length;
                        attributes = file.Attributes;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    files++;
                    total += length;

                    if (!IsCloudOnly(attributes)) continue;

                    cloudFiles++;
                    cloudOnly += length;
                }
            }

            return Measured.Ok(new DirectoryMeasure
            {
                Root = request.Root,
                TotalBytes = total,
                OnDiskBytes = total - cloudOnly,
                CloudOnlyBytes = cloudOnly,
                FileCount = files,
                CloudOnlyFileCount = cloudFiles,
                InaccessibleDirectories = inaccessible,
                SkippedReparsePoints = reparse,
                Truncated = truncated,
            }, DataSource.FileSystem);
        }

        internal static bool IsExcludedRelative(DirectoryScanRequest request, string path)
        {
            if (request.ExcludeRelative.Count == 0) return false;

            var root = request.Root.TrimEnd('\\', '/');
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length) return false;

            var relative = path.Substring(root.Length).Trim('\\', '/');
            foreach (var candidate in request.ExcludeRelative)
                if (MatchesRelative(candidate, relative)) return true;

            return false;
        }

        /// <summary>Compare segment par segment ; « * » vaut exactement un nom de dossier.</summary>
        internal static bool MatchesRelative(string pattern, string relative)
        {
            var wanted = pattern.Trim('\\', '/').Split('\\', '/');
            var actual = relative.Trim('\\', '/').Split('\\', '/');
            if (wanted.Length != actual.Length) return false;

            for (var i = 0; i < wanted.Length; i++)
                if (wanted[i] != "*" && !string.Equals(wanted[i], actual[i], StringComparison.OrdinalIgnoreCase))
                    return false;

            return true;
        }

        private static bool IsExcluded(IReadOnlyList<string> excluded, string path)
        {
            foreach (var candidate in excluded)
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Fichier annoncé par le disque mais stocké ailleurs.
        /// </summary>
        /// <remarks>
        /// OneDrive, et les autres synchronisations qui emploient la même mécanique, laissent sur
        /// le disque un fichier vide portant la taille du vrai. Le dossier annonce deux cents
        /// gigaoctets, le disque n'en contient que deux, et une copie du dossier ne rapatrierait
        /// rien. C'est la différence qui décide si une réinstallation est envisageable dans
        /// l'après-midi ou pas du tout.
        /// <para>
        /// Trois attributs la signalent. <c>Offline</c> existe depuis toujours ; les deux autres
        /// sont apparus avec les fichiers « à la demande » de Windows 10 et ne figurent pas dans
        /// l'énumération du framework, d'où leur valeur écrite en clair.
        /// </para>
        /// </remarks>
        internal static bool IsCloudOnly(FileAttributes attributes)
        {
            const int RecallOnOpen = 0x00040000;
            const int RecallOnDataAccess = 0x00400000;

            if ((attributes & FileAttributes.Offline) != 0) return true;
            return ((int)attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;
        }

        public int RemoveEmptyDirectories(string root, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(root)) return 0;

            var removed = 0;
            foreach (var directory in Descendants(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (Directory.GetFileSystemEntries(directory).Length > 0) continue;
                    Directory.Delete(directory);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Un dossier verrouillé n'est pas un incident : on le laisse.
                }
            }

            return removed;
        }

        /// <summary>Descendants du plus profond au moins profond, la racine n'y figure jamais.</summary>
        private IEnumerable<string> Descendants(string root)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    try
                    {
                        if ((new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    found.Add(child);
                    pending.Push(child);
                }
            }

            found.Sort((a, b) => b.Length.CompareTo(a.Length));
            return found;
        }

        public Measured<RecycleBinState> ReadRecycleBin()
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.SHQUERYRBINFO)),
            };

            try
            {
                var result = NativeMethods.SHQueryRecycleBin(null, ref info);
                if (result != 0)
                    return Measured.Missing<RecycleBinState>(
                        "Windows n'a pas répondu sur l'état de la corbeille.", DataSource.NativeApi);

                return Measured.Ok(
                    new RecycleBinState { SizeBytes = info.i64Size, ItemCount = info.i64NumItems },
                    DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return Measured.Missing<RecycleBinState>(
                    "L'interface de la corbeille n'est pas disponible sur cette machine.", DataSource.NativeApi);
            }
        }

        public bool EmptyRecycleBin()
        {
            try
            {
                // Ni confirmation, ni progression, ni son : la confirmation a déjà été demandée
                // par l'écran, avec le décompte et le volume sous les yeux.
                var flags = NativeMethods.SHERB_NOCONFIRMATION |
                            NativeMethods.SHERB_NOPROGRESSUI |
                            NativeMethods.SHERB_NOSOUND;

                return NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _log.Warn("Le vidage de la corbeille n'est pas disponible sur cette machine.", ex);
                return false;
            }
        }
    }
}
