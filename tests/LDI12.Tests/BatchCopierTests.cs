using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Plusieurs petits fichiers à la fois, quand et seulement quand le support s'y prête.
    /// </summary>
    public class BatchCopierTests
    {
        private const string Source = @"C:\Users\client\Documents";
        private const string Target = @"E:\Sauvegarde\Documents";

        [Fact]
        public void Chaque_fichier_est_copie_une_fois_et_rendu_dans_l_ordre()
        {
            var files = Files(200, 4096);
            files.WithFile(Source, Source + @"\film.mkv", 50_000_000);
            var items = Items(files);

            var seen = new List<int>();
            var copier = Copier(files);

            Assert.True(copier.Run(items, (index, result) => seen.Add(index), CancellationToken.None));

            Assert.Equal(Enumerable.Range(0, items.Count), seen);
            Assert.Equal(items.Count, files.Copied.Count);
            Assert.Equal(items.Count, files.Copied.Select(copy => copy.Destination).Distinct().Count());
        }

        [Fact]
        public void Quand_le_support_sert_plusieurs_fichiers_a_la_fois_le_parallele_est_retenu()
        {
            // Un SSD : chaque copie attend, mais les attentes se recouvrent. Seize millisecondes :
            // Windows ne sait pas dormir moins longtemps qu'un battement de son horloge.
            var files = Files(240, 4096);
            files.CopyDelay = TimeSpan.FromMilliseconds(16);
            var copier = Copier(files);

            copier.Run(Items(files), (_, _) => { }, CancellationToken.None);

            Assert.Equal(BatchCopier.Parallel, copier.Degree);
            Assert.True(files.MaxConcurrentCopies > 1);
            Assert.True(copier.ParallelBatches > 1);
        }

        // ============================================================ le choix, sans chronomètre

        [Theory]
        [InlineData(100, 100, 1)]   // disque dur : aucun gain
        [InlineData(100, 110, 1)]   // gain trop faible pour valoir le dérangement
        [InlineData(100, 150, 4)]   // SSD : le parallèle l'emporte
        [InlineData(100, 80, 1)]    // clé USB bas de gamme : le parallèle la ralentit
        public void Le_parallele_n_est_retenu_que_s_il_gagne_nettement(double sequential, double parallel, int expected)
        {
            var tuner = new ParallelTuner();

            // Échauffement, puis un par un, à plusieurs, un par un, à plusieurs.
            Record(tuner, 500);
            Record(tuner, sequential);
            Record(tuner, parallel);
            Record(tuner, sequential);
            Record(tuner, parallel);

            Assert.Equal(expected, tuner.Degree);
        }

        [Fact]
        public void Le_premier_lot_ne_compte_pas()
        {
            // Un premier lot lent, comme toujours à froid, ne doit pas faire choisir le parallèle.
            var tuner = new ParallelTuner();

            Record(tuner, 20);
            Record(tuner, 100);
            Record(tuner, 100);
            Record(tuner, 100);
            Record(tuner, 100);

            Assert.Equal(1, tuner.Degree);
        }

        [Fact]
        public void Le_choix_est_revu_regulierement()
        {
            var tuner = new ParallelTuner();
            Record(tuner, 100);
            Record(tuner, 100);
            Record(tuner, 200);
            Record(tuner, 100);
            Record(tuner, 200);
            Assert.Equal(BatchCopier.Parallel, tuner.Degree);

            for (var i = 0; i < 40; i++) Record(tuner, 200);

            // Nouvelle comparaison : on repart d'un lot un par un.
            Assert.Equal(1, tuner.Degree);
        }

        /// <summary>Un lot de 100 unités de travail, mené au rythme donné en unités par seconde.</summary>
        private static void Record(ParallelTuner tuner, double rate)
            => tuner.Record(tuner.Degree, 100, TimeSpan.FromSeconds(100 / rate));

        [Fact]
        public void Un_support_plein_arrete_tout_meme_en_plein_lot()
        {
            var files = Files(200, 4096);
            files.NoSpaceFrom = Source + @"\f0020.txt";
            var results = new List<FileCopyResult>();

            var completed = Copier(files).Run(Items(files), (_, result) => results.Add(result), CancellationToken.None);

            Assert.False(completed);
            Assert.Contains(results, result => result.Outcome == FileCopyOutcome.NoSpace);
            Assert.True(results.Count < 200, "La copie ne doit pas continuer sur un support plein.");
        }

        [Fact]
        public void Les_gros_fichiers_passent_un_par_un()
        {
            var files = new FakeFileSystemGateway();
            for (var i = 0; i < 20; i++) files.WithFile(Source, Source + @"\video" + i + ".mp4", 200_000_000);
            files.CopyDelay = TimeSpan.FromMilliseconds(2);

            Copier(files).Run(Items(files), (_, _) => { }, CancellationToken.None);

            Assert.Equal(1, files.MaxConcurrentCopies);
        }

        private static FakeFileSystemGateway Files(int count, long size)
        {
            var files = new FakeFileSystemGateway();
            for (var i = 0; i < count; i++) files.WithFile(Source, Source + @"\f" + i.ToString("D4") + ".txt", size);
            return files;
        }

        private static List<(FileEntry File, string Target)> Items(FakeFileSystemGateway files)
        {
            var scan = files.Scan(new DirectoryScanRequest(Source), CancellationToken.None);
            return scan.Value.Files
                .Select(file => (file, Target + file.Path.Substring(Source.Length)))
                .ToList();
        }

        private static BatchCopier Copier(IFileSystemGateway files)
            => new BatchCopier(files, new CopyProgress(null, 1, 1, "Copie de"),
                result => result.Outcome == FileCopyOutcome.NoSpace);
    }
}
