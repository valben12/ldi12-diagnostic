using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Platform;

namespace LDI12.Tests
{
    /// <summary>
    /// Système de fichiers en mémoire.
    /// </summary>
    /// <remarks>
    /// Les tests de nettoyage portent sur une garantie qu'on ne peut pas vérifier sur un vrai
    /// disque sans risquer d'y supprimer quelque chose : que <b>seuls</b> les fichiers relevés
    /// partent. Ici, la liste des suppressions est observable ligne à ligne.
    /// </remarks>
    internal sealed class FakeFileSystemGateway : IFileSystemGateway
    {
        private readonly Dictionary<string, List<FileEntry>> _roots =
            new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> LockedFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Deleted { get; } = new List<string>();

        public HashSet<string> Directories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Measured<RecycleBinState> RecycleBin { get; set; } =
            Measured.Ok(new RecycleBinState { ItemCount = 0, SizeBytes = 0 }, DataSource.NativeApi);

        public bool RecycleBinEmptied { get; private set; }

        public bool RecycleBinFails { get; set; }

        public FakeFileSystemGateway WithFile(
            string root, string path, long size = 1024, bool cloudOnly = false)
        {
            if (!_roots.TryGetValue(root, out var files)) _roots[root] = files = new List<FileEntry>();

            files.Add(new FileEntry
            {
                Path = path,
                SizeBytes = size,
                LastWriteUtc = new DateTime(2024, 1, 1),
                CloudOnly = cloudOnly,
            });
            _existing.Add(path);
            Directories.Add(root);
            return this;
        }

        /// <summary>Contenus préparés, par chemin.</summary>
        public Dictionary<string, string> Texts { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Measured<string> ReadText(string path)
            => Texts.TryGetValue(path, out var text)
                ? Measured.Ok(text, DataSource.FileSystem)
                : Measured.Missing<string>("Le fichier est absent : " + path, DataSource.FileSystem);

        public bool ReplaceText(string path, string content)
        {
            if (LockedFiles.Contains(path) || Gone(path)) return false;
            Written[path] = content;
            Texts[path] = content;
            return true;
        }

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public bool FileExists(string path) => Files.Contains(path);

        /// <summary>Sous-dossiers rendus par racine, sert aux profils voisins du relevé de données.</summary>
        public Dictionary<string, List<string>> Children { get; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pesées préparées, par dossier.</summary>
        public Dictionary<string, DirectoryMeasure> Measures { get; } =
            new Dictionary<string, DirectoryMeasure>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Dossiers dont la pesée a été demandée, dans l'ordre.</summary>
        public List<string> MeasuredRoots { get; } = new List<string>();

        /// <summary>Exclusions reçues pour un dossier donné.</summary>
        public Dictionary<string, IReadOnlyList<string>> Exclusions { get; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        public FakeFileSystemGateway WithMeasure(string root, DirectoryMeasure measure)
        {
            Directories.Add(root);
            Measures[root] = measure;
            return this;
        }

        public IReadOnlyList<string> EnumerateDirectories(string path)
            => Children.TryGetValue(path, out var children) ? children : (IReadOnlyList<string>)Array.Empty<string>();

        public Measured<DirectoryMeasure> Measure(
            DirectoryMeasureRequest request, CancellationToken cancellationToken)
        {
            MeasuredRoots.Add(request.Root);
            Exclusions[request.Root] = request.Exclude;

            return Measures.TryGetValue(request.Root, out var measure)
                ? Measured.Ok(measure, DataSource.FileSystem)
                : Measured.Missing<DirectoryMeasure>(
                    "Dossier absent du système de fichiers de test.", DataSource.FileSystem);
        }

        public Measured<DirectoryScan> Scan(DirectoryScanRequest request, CancellationToken cancellationToken)
        {
            if (!_roots.TryGetValue(request.Root, out var files))
                return Measured.Missing<DirectoryScan>("Dossier absent du système de fichiers de test.");

            // Une copie, comme le fait la vraie passerelle : un relevé est une photographie, et
            // ce qui s'écrit après lui ne doit pas s'y ajouter rétroactivement. Les exclusions et
            // le premier niveau sont appliqués comme elle le fait, sur le chemin relatif.
            var kept = new List<FileEntry>();
            long total = 0;
            foreach (var file in files)
            {
                var relative = file.Path.Length > request.Root.Length
                    ? file.Path.Substring(request.Root.Length).TrimStart('\\')
                    : string.Empty;

                if (request.TopLevelOnly && relative.IndexOf('\\') >= 0) continue;
                var directories = relative.Split('\\');
                var excluded = false;
                for (var depth = 1; depth < directories.Length && !excluded; depth++)
                {
                    var prefix = string.Join("\\", directories, 0, depth);
                    excluded = request.ExcludeRelative.Any(pattern =>
                        LDI12.Platform.Gateways.FileSystemGateway.MatchesRelative(pattern, prefix));
                }

                if (excluded) continue;

                kept.Add(file);
                total += file.SizeBytes;
            }

            return Measured.Ok(
                new DirectoryScan { Root = request.Root, Files = kept, TotalBytes = total },
                DataSource.FileSystem);
        }

        public FileDeletion Delete(FileEntry entry)
        {
            if (LockedFiles.Contains(entry.Path)) return FileDeletion.Locked;
            if (!_existing.Contains(entry.Path)) return FileDeletion.NotFound;

            _existing.Remove(entry.Path);
            Deleted.Add(entry.Path);
            return FileDeletion.Deleted;
        }

        // ---------- copie

        /// <summary>Ce que la copie a écrit : destination puis source.</summary>
        public List<(string Destination, string Source)> Copied { get; } = new List<(string, string)>();

        /// <summary>Destinations déjà occupées par un fichier différent.</summary>
        public HashSet<string> Occupied { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fichiers dont la relecture ne correspondra pas à la source.</summary>
        public HashSet<string> CorruptOnCopy { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fichier à partir duquel la destination se déclare pleine.</summary>
        public string? NoSpaceFrom { get; set; }

        /// <summary>Dossiers créés par la copie.</summary>
        public List<string> Created { get; } = new List<string>();

        public Dictionary<string, string> Written { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Measured<long> Free { get; set; } = Measured.Ok(1_000_000_000_000L, DataSource.FileSystem);

        /// <summary>Le support qui sera arraché : tout chemin qui commence ainsi disparaît.</summary>
        public string? UnplugRoot { get; set; }

        /// <summary>Le fichier pendant la copie duquel le support est arraché.</summary>
        public string? UnplugDuring { get; set; }

        public bool Unplugged { get; private set; }

        private bool Gone(string path)
            => Unplugged && UnplugRoot != null && path.StartsWith(UnplugRoot, StringComparison.OrdinalIgnoreCase);

        /// <summary>Durée simulée d'une copie : de quoi voir ce que gagne le parallèle.</summary>
        public TimeSpan CopyDelay { get; set; }

        /// <summary>
        /// Un seul accès au support à la fois, comme la tête d'un disque dur : copier à plusieurs
        /// n'y gagne rien.
        /// </summary>
        public bool SerializeDelay { get; set; }

        /// <summary>Le plus grand nombre de copies menées en même temps.</summary>
        public int MaxConcurrentCopies { get; private set; }

        private readonly object _copyGate = new object();
        private readonly object _deviceGate = new object();
        private int _concurrent;

        public FileCopyResult Copy(FileCopyRequest request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _concurrent);
            try
            {
                lock (_copyGate) MaxConcurrentCopies = Math.Max(MaxConcurrentCopies, now);

                if (CopyDelay > TimeSpan.Zero)
                {
                    if (SerializeDelay) lock (_deviceGate) Thread.Sleep(CopyDelay);
                    else Thread.Sleep(CopyDelay);
                }

                lock (_copyGate) return CopyCore(request);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private FileCopyResult CopyCore(FileCopyRequest request)
        {
            var source = request.Source;

            if (UnplugDuring != null && string.Equals(UnplugDuring, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                Unplugged = true;
                Directories.RemoveWhere(Gone);
            }

            if (Gone(request.Destination) || Gone(source.Path)) return FileCopyResult.Of(FileCopyOutcome.DeviceError);

            if (source.CloudOnly) return FileCopyResult.Of(FileCopyOutcome.CloudOnly);
            if (LockedFiles.Contains(source.Path)) return FileCopyResult.Of(FileCopyOutcome.Locked);
            if (!_existing.Contains(source.Path)) return FileCopyResult.Of(FileCopyOutcome.NotFound);
            if (Occupied.Contains(request.Destination)) return FileCopyResult.Of(FileCopyOutcome.Conflict);

            if (NoSpaceFrom != null &&
                string.Equals(NoSpaceFrom, source.Path, StringComparison.OrdinalIgnoreCase))
                return FileCopyResult.Of(FileCopyOutcome.NoSpace);

            if (request.Verify && CorruptOnCopy.Contains(source.Path))
                return FileCopyResult.Of(FileCopyOutcome.VerificationFailed);

            // Comme la vraie passerelle : l'écriture puis la relecture remontent chacune la taille.
            request.Progressed?.Invoke(source.SizeBytes);
            request.Progressed?.Invoke(source.SizeBytes);

            Copied.Add((request.Destination, source.Path));
            return FileCopyResult.Of(FileCopyOutcome.Copied, source.SizeBytes);
        }

        public bool CreateDirectory(string path)
        {
            if (Gone(path)) return false;
            Created.Add(path);

            // Comme la vraie passerelle : un dossier créé existe ensuite. La sauvegarde s'en sert
            // pour distinguer un fichier refusé d'un support débranché.
            Directories.Add(path);
            return true;
        }

        /// <summary>Dossiers déplacés : source puis destination.</summary>
        public List<(string Source, string Destination)> Moved { get; } = new List<(string, string)>();

        /// <summary>Dossiers qui refusent d'être déplacés.</summary>
        public HashSet<string> Unmovable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool MoveDirectory(string source, string destination)
        {
            if (Unmovable.Contains(source) || !Directories.Contains(source) || Directories.Contains(destination)) return false;

            Moved.Add((source, destination));
            Directories.Remove(source);
            Directories.Add(destination);
            return true;
        }

        public Measured<long> FreeSpace(string path) => Free;

        public bool WriteText(string path, string content)
        {
            if (Gone(path)) return false;
            Written[path] = content;
            return true;
        }

        public int RemoveEmptyDirectories(string root, CancellationToken cancellationToken) => 0;

        public Measured<RecycleBinState> ReadRecycleBin() => RecycleBin;

        public bool EmptyRecycleBin()
        {
            if (RecycleBinFails) return false;
            RecycleBinEmptied = true;
            return true;
        }

        public bool StillExists(string path) => _existing.Contains(path);
    }

    internal sealed class FakeProcessLauncher : IProcessLauncher
    {
        public List<LaunchRequest> Launched { get; } = new List<LaunchRequest>();

        public bool Refuse { get; set; }

        public LaunchResult Launch(LaunchRequest request)
        {
            Launched.Add(request);
            return Refuse ? LaunchResult.Denied() : LaunchResult.Ok(4242);
        }
    }

    internal sealed class FakeSystemRestoreGateway : ISystemRestoreGateway
    {
        public RestorePointResult Result { get; set; } = new RestorePointResult
        {
            State = RestorePointState.Created,
            Message = "Point de restauration créé.",
        };

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    /// <summary>Exécution d'outil scriptée : le test décide de la sortie et du code de retour.</summary>
    internal sealed class ScriptedProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRequest, ProcessResult> _body;

        public ScriptedProcessRunner(Func<ProcessRequest, ProcessResult> body) => _body = body;

        public List<ProcessRequest> Requests { get; } = new List<ProcessRequest>();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_body(request));
        }
    }

    internal static class ActionFakes
    {
        public static ActionContext Context(
            IPlatformInfo? platform = null,
            IProcessRunner? processes = null,
            IFileSystemGateway? files = null,
            IProcessLauncher? launcher = null,
            ISystemRestoreGateway? restore = null,
            SystemSnapshot? snapshot = null,
            IReadOnlyDictionary<string, string>? parameters = null)
            => new ActionContext(
                platform ?? new FakePlatformInfo(elevated: true),
                processes ?? new FakeProcessRunner(),
                launcher ?? new FakeProcessLauncher(),
                files ?? new FakeFileSystemGateway(),
                new FakeRegistryGateway(),
                restore ?? new FakeSystemRestoreGateway(),
                NullLogger.Instance,
                snapshot,
                parameters);

        public static ProcessResult Result(int exitCode = 0, string output = "", bool timedOut = false, bool launchFailed = false)
            => new ProcessResult
            {
                ExitCode = exitCode,
                StandardOutput = output,
                TimedOut = timedOut,
                LaunchFailed = launchFailed,
            };
    }
}
