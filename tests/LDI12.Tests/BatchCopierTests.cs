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
            // Un SSD : chaque copie attend, mais les attentes se recouvrent.
            var files = Files(400, 4096);
            files.CopyDelay = TimeSpan.FromMilliseconds(4);
            var copier = Copier(files);

            copier.Run(Items(files), (_, _) => { }, CancellationToken.None);

            Assert.Equal(BatchCopier.Parallel, copier.Degree);
            Assert.True(files.MaxConcurrentCopies > 1);
            Assert.True(copier.ParallelBatches > 1);
        }

        [Fact]
        public void Quand_le_parallele_ne_gagne_rien_la_copie_reste_sequentielle()
        {
            // Un disque dur : une seule tête, les copies attendent leur tour.
            var files = Files(400, 4096);
            files.CopyDelay = TimeSpan.FromMilliseconds(4);
            files.SerializeDelay = true;
            var copier = Copier(files);

            copier.Run(Items(files), (_, _) => { }, CancellationToken.None);

            Assert.Equal(1, copier.Degree);
            Assert.Equal(1, copier.ParallelBatches);
        }

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
