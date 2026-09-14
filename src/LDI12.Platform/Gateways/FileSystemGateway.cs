using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Native;

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

                var existing = new FileInfo(destination);
                if (existing.Exists)
                    return existing.Length == info.Length && existing.LastWriteTimeUtc == info.LastWriteTimeUtc
                        ? FileCopyResult.Of(FileCopyOutcome.AlreadyPresent)
                        : FileCopyResult.Of(FileCopyOutcome.Conflict, 0,
                            "Un fichier différent porte déjà ce nom à la destination.");

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                byte[] expected;
                long written;

                try
                {
                    expected = Write(info, destination, out written, cancellationToken);
                }
                catch
                {
                    Discard(destination);
                    throw;
                }

                // La date de la source est reportée : sans elle, une reprise de sauvegarde
                // reverrait chaque fichier comme différent et recopierait tout.
                try { File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

                if (!request.Verify) return FileCopyResult.Of(FileCopyOutcome.Copied, written);

                byte[] actual;
                try
                {
                    actual = Fingerprint(destination, cancellationToken);
                }
                catch
                {
                    Discard(destination);
                    throw;
                }

                if (!Same(expected, actual))
                {
                    Discard(destination);
                    return FileCopyResult.Of(FileCopyOutcome.VerificationFailed, 0,
                        "La copie ne se relit pas identique à la source. Elle a été retirée.");
                }

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

                return FileCopyResult.Of(FileCopyOutcome.Locked, 0, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Debug("Copie impossible de " + source.Path + " : " + ex.Message);
                return FileCopyResult.Of(FileCopyOutcome.Failed, 0, ex.Message);
            }
        }

        /// <summary>Écrit la copie et rend l'empreinte de ce qui a été lu à la source.</summary>
        private static byte[] Write(
            FileInfo source, string destination, out long written, CancellationToken cancellationToken)
        {
            const int Buffer = 1024 * 1024;

            written = 0;
            using var hash = SHA256.Create();
            using var input = new FileStream(
                source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Buffer);
            using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, Buffer);

            var buffer = new byte[Buffer];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.TransformBlock(buffer, 0, read, null, 0);
                written += read;
            }

            hash.TransformFinalBlock(buffer, 0, 0);
            return hash.Hash;
        }

        private static byte[] Fingerprint(string path, CancellationToken cancellationToken)
        {
            const int Buffer = 1024 * 1024;

            using var hash = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);

            var buffer = new byte[Buffer];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.TransformBlock(buffer, 0, read, null, 0);
            }

            hash.TransformFinalBlock(buffer, 0, 0);
            return hash.Hash;
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
                return accepted ? path : Ordinary(path);

            var full = Path.GetFullPath(path);
            if (!accepted) return full;

            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC" + full.Substring(1)
                : @"\\?\" + full;
        }

        /// <summary>Le chemin tel que le reste du logiciel le manipule : jamais sous forme étendue.</summary>
        internal static string Plain(string path)
            => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? Ordinary(path) : path;

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
