using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Où en est l'exploration : lu sans verrou, à la cadence de l'affichage.</summary>
    public sealed class DiskScanProgress
    {
        internal long _files;
        internal long _directories;
        internal long _bytes;
        internal volatile string _current = string.Empty;

        public long Files => Interlocked.Read(ref _files);

        public long Directories => Interlocked.Read(ref _directories);

        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>Le dernier dossier ouvert, pour montrer que ça avance.</summary>
        public string Current => _current;
    }

    /// <summary>
    /// Pèse une arborescence entière, fichier par fichier.
    /// </summary>
    /// <remarks>
    /// Plusieurs lecteurs travaillent en parallèle sur une pile de dossiers à ouvrir : chacun
    /// lit un dossier, crée ses fichiers, empile ses sous-dossiers. La pile plutôt qu'une file :
    /// l'exploration va en profondeur d'abord et garde peu de dossiers en attente. Les totaux et
    /// les tris viennent après, en une seule passe, quand plus rien ne bouge.
    /// <para>
    /// Les jonctions et liens symboliques ne sont jamais suivis : « Documents and Settings »
    /// renvoie vers « Users », et les compter tous les deux doublerait le profil. Les fichiers
    /// restés dans le nuage ne pèsent rien, puisqu'ils n'occupent rien sur ce disque.
    /// </para>
    /// </remarks>
    public sealed class DiskUsageScanner
    {
        private readonly IDirectoryLister _lister;
        private readonly int _workers;

        public DiskUsageScanner(IDirectoryLister lister, int? workers = null)
        {
            _lister = lister ?? throw new ArgumentNullException(nameof(lister));
            _workers = Math.Max(1, workers ?? Math.Min(16, Math.Max(4, Environment.ProcessorCount * 2)));
        }

        public DiskScanProgress Progress { get; private set; } = new DiskScanProgress();

        public DiskUsageTree Scan(string rootPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Chemin vide.", nameof(rootPath));

            var progress = Progress = new DiskScanProgress();
            var clock = Stopwatch.StartNew();
            var root = new DiskNode(rootPath, DiskNodeKind.Directory, null);

            using (var pending = new BlockingCollection<Job>(new ConcurrentStack<Job>()))
            {
                var outstanding = 1;
                var unreadable = 0L;
                pending.Add(new Job(root, rootPath));

                void Work()
                {
                    var entries = new List<DiskEntry>(256);
                    var children = new List<DiskNode>(256);
                    try
                    {
                        foreach (var job in pending.GetConsumingEnumerable(cancellationToken))
                        {
                            entries.Clear();
                            children.Clear();
                            progress._current = job.Path;

                            if (!_lister.TryList(job.Path, entries))
                            {
                                job.Node.Unreadable = true;
                                Interlocked.Increment(ref unreadable);
                            }

                            long files = 0, bytes = 0;
                            foreach (var entry in entries)
                            {
                                if (entry.IsDirectory)
                                {
                                    if (entry.IsReparsePoint) continue;
                                    var directory = new DiskNode(entry.Name, DiskNodeKind.Directory, job.Node) { LastWriteUtcTicks = entry.LastWriteUtcTicks };
                                    children.Add(directory);
                                    Interlocked.Increment(ref outstanding);
                                    pending.Add(new Job(directory, System.IO.Path.Combine(job.Path, entry.Name)));
                                }
                                else
                                {
                                    var size = entry.IsCloudOnly || entry.IsReparsePoint ? 0 : entry.Size;
                                    children.Add(new DiskNode(entry.Name, DiskNodeKind.File, job.Node)
                                    {
                                        Size = size,
                                        Files = 1,
                                        LastWriteUtcTicks = entry.LastWriteUtcTicks,
                                    });
                                    files++;
                                    bytes += size;
                                }
                            }

                            job.Node.SetChildren(children);
                            Interlocked.Add(ref progress._files, files);
                            Interlocked.Add(ref progress._bytes, bytes);
                            Interlocked.Increment(ref progress._directories);

                            if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                var threads = new Thread[_workers];
                for (var index = 0; index < threads.Length; index++)
                {
                    threads[index] = new Thread(Work) { IsBackground = true, Name = "LDI12 analyse du disque " + index };
                    threads[index].Start();
                }
                foreach (var thread in threads) thread.Join();
                cancellationToken.ThrowIfCancellationRequested();

                var tree = new DiskUsageTree(root, unreadable, clock.Elapsed);
                return tree;
            }
        }

        private readonly struct Job
        {
            public Job(DiskNode node, string path)
            {
                Node = node;
                Path = path;
            }

            public DiskNode Node { get; }

            public string Path { get; }
        }
    }
}
