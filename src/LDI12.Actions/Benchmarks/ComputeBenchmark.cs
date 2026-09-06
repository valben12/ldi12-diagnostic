using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LDI12.Actions.Benchmarks
{
    /// <summary>
    /// Mesures de calcul : processeur seul, processeur en parallèle, bande passante mémoire.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi un débit et non un score.</b> Un score de processeur ne veut rien dire sans la
    /// base de références qui le classe, qu'un logiciel hors ligne n'a pas, et qui n'apprendrait
    /// rien à un client de toute façon. Ce qui est mesuré ici est donc un travail fixe rapporté à
    /// son temps : des mégaoctets traités par seconde, une unité qui garde un sens quand on
    /// compare la machine à elle-même : avant et après une réparation, à froid et à chaud, sur
    /// secteur et sur batterie.
    /// <para>
    /// Le rapport entre la version parallèle et la version à un seul fil est la seule vraie
    /// conclusion que ces deux mesures autorisent : une machine qui ne tire presque rien de ses
    /// cœurs supplémentaires est bridée, et cela se voit sans connaître son modèle.
    /// </para>
    /// </remarks>
    internal static class ComputeBenchmark
    {
        /// <summary>Taille du bloc de travail : assez petit pour tenir dans le cache du processeur.</summary>
        private const int BlockElements = 256 * 1024;

        /// <summary>Passes sur le bloc. Fixe : c'est ce qui rend deux mesures comparables.</summary>
        private const int Passes = 96;

        /// <summary>Deux tampons de mémoire, assez grands pour sortir du cache le plus large.</summary>
        private const int MemoryBytes = 64 * 1024 * 1024;

        private const int MemoryRounds = 8;

        /// <summary>Travail à un seul fil, en mégaoctets traités par seconde.</summary>
        internal static double SingleThread(CancellationToken cancellationToken)
        {
            var block = Prepare();
            var stopwatch = Stopwatch.StartNew();

            Work(block, cancellationToken);

            stopwatch.Stop();
            return Rate(1, stopwatch);
        }

        /// <summary>
        /// Le même travail réparti sur tous les cœurs logiques.
        /// </summary>
        /// <remarks>
        /// Chaque fil travaille sur sa propre tranche du bloc : ils ne se disputent ni la même
        /// ligne de cache ni le même verrou, faute de quoi la mesure décrirait la contention
        /// qu'on aurait créée soi-même plutôt que la machine.
        /// </remarks>
        internal static double MultiThread(CancellationToken cancellationToken)
        {
            var workers = Math.Max(1, Environment.ProcessorCount);
            var blocks = new int[workers][];
            for (var index = 0; index < workers; index++) blocks[index] = Prepare();

            var stopwatch = Stopwatch.StartNew();

            Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellationToken },
                index => Work(blocks[index], cancellationToken));

            stopwatch.Stop();
            return Rate(workers, stopwatch);
        }

        /// <summary>Bande passante mémoire, en octets par seconde, lecture et écriture confondues.</summary>
        internal static double MemoryBandwidth(CancellationToken cancellationToken)
        {
            var source = new byte[MemoryBytes];
            var destination = new byte[MemoryBytes];

            // Les deux tampons sont touchés avant la mesure : sans cela, le premier tour
            // mesurerait l'allocation des pages par Windows et non la mémoire elle-même.
            for (var index = 0; index < MemoryBytes; index += 4096)
            {
                source[index] = (byte)index;
                destination[index] = 1;
            }

            var stopwatch = Stopwatch.StartNew();

            for (var round = 0; round < MemoryRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Buffer.BlockCopy(source, 0, destination, 0, MemoryBytes);
            }

            stopwatch.Stop();

            var seconds = stopwatch.Elapsed.TotalSeconds;

            // Une copie lit autant qu'elle écrit : le trafic est le double du volume déplacé.
            return seconds > 0 ? 2d * MemoryBytes * MemoryRounds / seconds : 0d;
        }

        private static int[] Prepare()
        {
            var block = new int[BlockElements];
            for (var index = 0; index < block.Length; index++) block[index] = unchecked(index * 1103515245 + 12345);
            return block;
        }

        /// <summary>
        /// Le travail lui-même : entiers et flottants mêlés, dépendances en chaîne.
        /// </summary>
        /// <remarks>
        /// Les opérations dépendent les unes des autres à dessein : une boucle dont chaque tour
        /// serait indépendant mesurerait surtout la capacité du processeur à les exécuter dans le
        /// désordre, ce qui varie énormément d'une génération à l'autre et n'a aucun rapport avec
        /// ce que ressent l'utilisateur.
        /// </remarks>
        private static void Work(int[] block, CancellationToken cancellationToken)
        {
            var accumulator = 0d;

            for (var pass = 0; pass < Passes; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = 1u;
                for (var index = 0; index < block.Length; index++)
                {
                    var value = block[index];
                    state = unchecked(state * 1664525u + 1013904223u);
                    accumulator += (value ^ (int)state) * 0.5d;
                    block[index] = (int)(state ^ (uint)(accumulator % int.MaxValue));
                }
            }

            // Le résultat est consommé : sans cela, rien n'interdit au compilateur de constater
            // que personne ne le lit et de supprimer la boucle entière.
            if (double.IsNaN(accumulator)) throw new InvalidOperationException("Mesure incohérente.");
        }

        private static double Rate(int workers, Stopwatch stopwatch)
        {
            var bytes = (double)BlockElements * sizeof(int) * Passes * workers;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            return seconds > 0 ? bytes / seconds : 0d;
        }
    }
}
