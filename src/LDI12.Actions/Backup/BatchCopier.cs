using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// Copie une liste de fichiers, plusieurs petits fichiers à la fois quand le support s'y prête.
    /// </summary>
    /// <remarks>
    /// <b>Ce que coûte un petit fichier.</b> Ses octets ne sont presque rien : ce qui compte, ce
    /// sont les allers-retours qu'il impose au support (création, fermeture, renommage,
    /// relecture) et le passage de l'antivirus. Pendant ces attentes, un SSD ou une clé récente
    /// pourraient servir d'autres fichiers ; un disque dur mécanique, lui, perdrait en sauts de tête
    /// ce qu'on croirait gagner.
    /// <para>
    /// <b>Plutôt que de deviner le support, la copie le mesure.</b> Les petits fichiers partent par
    /// lots : un lot un par un, le suivant quatre à la fois, et le plus rapide l'emporte. Le choix
    /// est revu régulièrement, parce qu'un profil de navigateur ne se comporte pas comme un dossier
    /// de photos. Le parallèle doit gagner nettement pour être retenu : à égalité, la copie reste
    /// séquentielle, qui ne dérange rien.
    /// </para>
    /// <para>
    /// Les gros fichiers, eux, passent toujours un par un : leur copie en flux continu sature déjà
    /// le support, et en mener plusieurs de front ne ferait que les fragmenter.
    /// </para>
    /// <para>
    /// Les résultats sont rendus à l'appelant dans l'ordre de la liste et sur son propre fil : le
    /// manifeste, les compteurs et le compte rendu ne voient aucune différence.
    /// </para>
    /// </remarks>
    internal sealed class BatchCopier
    {
        /// <summary>En dessous, un fichier est « petit » : son prix fixe dépasse celui de ses octets.</summary>
        internal const long SmallFileBytes = 1024 * 1024;

        internal const int BatchSize = 48;

        internal const int Parallel = 4;

        private readonly IFileSystemGateway _files;
        private readonly CopyProgress _tracker;
        private readonly Func<FileCopyResult, bool> _isFatal;
        private readonly object _gate = new object();

        private readonly ParallelTuner _tuner = new ParallelTuner();

        /// <param name="isFatal">
        /// Vrai si ce résultat doit tout arrêter : support plein, support disparu. Appelé depuis
        /// les fils de copie, il doit le supporter.
        /// </param>
        public BatchCopier(IFileSystemGateway files, CopyProgress tracker, Func<FileCopyResult, bool> isFatal)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _isFatal = isFatal ?? throw new ArgumentNullException(nameof(isFatal));
        }

        /// <summary>Le degré retenu pour le prochain lot : 1, ou <see cref="Parallel"/>.</summary>
        internal int Degree => _tuner.Degree;

        /// <summary>Combien de lots sont partis en parallèle : de quoi le dire dans le journal.</summary>
        internal int ParallelBatches { get; private set; }

        /// <summary>
        /// Copie les fichiers, et rend chaque résultat dans l'ordre de la liste.
        /// </summary>
        /// <returns>Faux si la copie s'est arrêtée sur un résultat fatal.</returns>
        public bool Run(
            IReadOnlyList<(FileEntry File, string Target)> items, Action<int, FileCopyResult> onResult,
            CancellationToken cancellationToken)
        {
            var index = 0;
            while (index < items.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (items[index].File.SizeBytes >= SmallFileBytes)
                {
                    var (file, target) = items[index];
                    var result = CopyWithRetry(new FileCopyRequest(file, target) { Progressed = _tracker.Bytes }, cancellationToken);
                    _tracker.FileDone(file.SizeBytes);
                    onResult(index, result);
                    index++;
                    if (_isFatal(result)) return false;
                    continue;
                }

                // Un lot : les petits fichiers qui se suivent, jusqu'à la taille d'un lot.
                var end = index;
                while (end < items.Count && end - index < BatchSize && items[end].File.SizeBytes < SmallFileBytes) end++;

                if (!Batch(items, index, end, onResult, cancellationToken)) return false;
                index = end;
            }

            return true;
        }

        /// <summary>
        /// Copie, et recommence une fois sur une erreur qui peut être passagère.
        /// </summary>
        /// <remarks>
        /// Une relecture différente de la source, ou un support qui a manqué une réponse : sur une
        /// clé USB fatiguée ou un câble qui bouge, le second essai passe le plus souvent. La copie
        /// ratée a déjà été retirée, rien n'est écrasé. Un support parti ou plein n'est pas
        /// réessayé : c'est à l'appelant de s'arrêter.
        /// </remarks>
        private FileCopyResult CopyWithRetry(FileCopyRequest request, CancellationToken cancellationToken)
        {
            var result = _files.Copy(request, cancellationToken);
            if (result.Outcome != FileCopyOutcome.VerificationFailed && result.Outcome != FileCopyOutcome.DeviceError)
                return result;
            if (_isFatal(result)) return result;

            cancellationToken.WaitHandle.WaitOne(RetryPause);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _retries);
            return _files.Copy(request, cancellationToken);
        }

        private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(500);

        /// <summary>Combien de fichiers ont eu droit à un second essai.</summary>
        internal int Retries => Volatile.Read(ref _retries);

        private int _retries;

        private bool Batch(
            IReadOnlyList<(FileEntry File, string Target)> items, int start, int end,
            Action<int, FileCopyResult> onResult, CancellationToken cancellationToken)
        {
            var degree = end - start < 8 ? 1 : Degree;
            var results = new FileCopyResult?[end - start];
            var stop = 0;
            long bytes = 0;

            var stopwatch = Stopwatch.StartNew();

            void CopyOne(int offset)
            {
                if (Volatile.Read(ref stop) != 0) return;
                cancellationToken.ThrowIfCancellationRequested();

                var (file, target) = items[start + offset];

                // Pas d'avancement octet par octet : l'avancement compte un fichier à la fois, et un
                // fichier de moins d'un mégaoctet se compte très bien d'un coup, à la fin.
                var result = CopyWithRetry(new FileCopyRequest(file, target), cancellationToken);
                results[offset] = result;

                lock (_gate) _tracker.FileDone(file.SizeBytes);
                Interlocked.Add(ref bytes, file.SizeBytes);

                if (_isFatal(result)) Interlocked.Exchange(ref stop, 1);
            }

            if (degree == 1)
            {
                for (var offset = 0; offset < results.Length; offset++) CopyOne(offset);
            }
            else
            {
                ParallelBatches++;
                try
                {
                    System.Threading.Tasks.Parallel.For(0, results.Length,
                        new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
                        CopyOne);
                }
                catch (AggregateException ex) when (ex.InnerException is OperationCanceledException canceled)
                {
                    throw canceled;
                }
            }

            stopwatch.Stop();
            Learn(degree, results, Interlocked.Read(ref bytes), stopwatch.Elapsed);

            // Dans l'ordre de la liste, et seulement ce qui a été tenté : après un arrêt, les
            // fichiers qui n'ont pas été touchés ne sont pas comptés comme vus.
            for (var offset = 0; offset < results.Length; offset++)
                if (results[offset] is FileCopyResult result) onResult(start + offset, result);

            return Volatile.Read(ref stop) == 0;
        }

        /// <summary>
        /// Retient la vitesse du lot qui vient de passer, pour choisir la façon du suivant.
        /// </summary>
        /// <remarks>
        /// Un lot se mesure en unités de travail : un fichier, plus ses octets comptés par quart
        /// de mégaoctet. Deux lots de fichiers de tailles différentes se comparent ainsi à peu près
        /// honnêtement. Un lot fait de fichiers déjà présents (une reprise) ne dit rien du support :
        /// il n'est pas retenu.
        /// </remarks>
        private void Learn(int degree, FileCopyResult?[] results, long bytes, TimeSpan elapsed)
        {
            var copied = 0;
            foreach (var result in results)
                if (result?.Outcome == FileCopyOutcome.Copied) copied++;

            if (copied < results.Length / 2 || results.Length < 8 || elapsed <= TimeSpan.Zero) return;

            _tuner.Record(degree, results.Length + bytes / (256.0 * 1024), elapsed);
        }
    }

    /// <summary>
    /// Choisit, lot après lot, entre copier un par un et copier à plusieurs.
    /// </summary>
    /// <remarks>
    /// <b>Deux mesures de chaque, en alternance.</b> Une seule mesure par façon se laissait
    /// tromper par un passage de l'antivirus ou de l'indexation au mauvais moment : un disque dur
    /// pouvait alors passer pour un SSD. Alterner un par un, à plusieurs, un par un, à plusieurs,
    /// et comparer les totaux, étale ces aléas sur les deux côtés.
    /// <para>
    /// Le premier lot n'est jamais retenu : il paie la mise en route, dossiers créés, code chargé,
    /// antivirus à froid, et ferait paraître la copie un par un plus lente qu'elle n'est. Le
    /// parallèle doit gagner d'au moins 15 % : à égalité, la copie reste séquentielle, qui ne
    /// dérange rien. Le choix est revu tous les quarante lots.
    /// </para>
    /// </remarks>
    internal sealed class ParallelTuner
    {
        private const double RequiredGain = 1.15;
        private const int ReviewEvery = 40;

        private static readonly int[] Exploration = { 1, BatchCopier.Parallel, 1, BatchCopier.Parallel };

        private bool _warmedUp;
        private int _step;
        private int? _decided;
        private int _sinceDecision;
        private double _sequentialUnits, _sequentialSeconds, _parallelUnits, _parallelSeconds;

        public int Degree => _decided ?? (_warmedUp ? Exploration[_step] : 1);

        /// <summary>Un lot mené à <paramref name="degree"/>, et le travail qu'il représentait.</summary>
        public void Record(int degree, double units, TimeSpan elapsed)
        {
            if (!_warmedUp)
            {
                _warmedUp = true;
                return;
            }

            if (_decided != null)
            {
                if (++_sinceDecision >= ReviewEvery) Review();
                return;
            }

            if (degree != Exploration[_step] || elapsed <= TimeSpan.Zero) return;

            if (degree == 1)
            {
                _sequentialUnits += units;
                _sequentialSeconds += elapsed.TotalSeconds;
            }
            else
            {
                _parallelUnits += units;
                _parallelSeconds += elapsed.TotalSeconds;
            }

            if (++_step < Exploration.Length) return;

            var sequential = _sequentialUnits / _sequentialSeconds;
            var parallel = _parallelUnits / _parallelSeconds;
            _decided = parallel >= sequential * RequiredGain ? BatchCopier.Parallel : 1;
            _sinceDecision = 0;
        }

        private void Review()
        {
            _decided = null;
            _step = 0;
            _sequentialUnits = _sequentialSeconds = _parallelUnits = _parallelSeconds = 0;
        }
    }
}
