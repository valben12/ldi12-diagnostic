using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Ce qui peut faire rater une sauvegarde en intervention, et comment elle s'en garde.</summary>
    public class BackupRobustnessTests
    {
        private static string Desktop =>
            UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;

        [Fact]
        public async Task Une_destination_placee_dans_une_source_est_refusee()
        {
            var files = new FakeFileSystemGateway().WithFile(Desktop, Path.Combine(Desktop, "note.txt"));

            var preview = await Preview(files, Path.Combine(Desktop, "Sauvegarde"));

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains("à l'intérieur", preview.Blocker);
        }

        [Theory]
        [InlineData(@"C:\Users\Marie\Desktop\Sauvegarde", @"C:\Users\Marie\Desktop", true)]
        [InlineData(@"C:\Users\Marie\Desktop", @"C:\Users\Marie\Desktop\", true)]
        [InlineData(@"C:\Users\Marie\Desktop2", @"C:\Users\Marie\Desktop", false)]
        [InlineData(@"E:\", @"C:\Users\Marie\Desktop", false)]
        public void Une_destination_dans_une_source_se_reconnait(string path, string folder, bool inside)
            => Assert.Equal(inside, BackupUserDataAction.IsInside(path, folder));

        [Fact]
        public async Task Les_fichiers_de_quatre_gigaoctets_sont_signales_avant_une_copie_vers_du_fat32()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Desktop, Path.Combine(Desktop, "film.mkv"), 5_000_000_000)
                .WithFile(Desktop, Path.Combine(Desktop, "note.txt"), 2048);
            files.Format = "FAT32";

            var preview = await Preview(files, @"E:\");

            var line = Assert.Single(preview.Measurements, m => m.Label == "Fichiers trop gros pour ce support");
            Assert.Equal(PreviewLineKind.Caution, line.Kind);
            Assert.StartsWith("1 fichier", line.Value);
        }

        [Fact]
        public async Task Vers_de_l_exfat_rien_n_est_signale()
        {
            var files = new FakeFileSystemGateway().WithFile(Desktop, Path.Combine(Desktop, "film.mkv"), 5_000_000_000);
            files.Format = "exFAT";

            var preview = await Preview(files, @"E:\");

            Assert.DoesNotContain(preview.Measurements, m => m.Label == "Fichiers trop gros pour ce support");
        }

        [Theory]
        [InlineData(FileCopyOutcome.VerificationFailed)]
        [InlineData(FileCopyOutcome.DeviceError)]
        public void Une_erreur_passagere_a_droit_a_un_second_essai(FileCopyOutcome transient)
        {
            var files = new FakeFileSystemGateway().WithFile(@"C:\src", @"C:\src\a.txt", 100);
            files.FailOnce[@"C:\src\a.txt"] = transient;
            var results = new List<FileCopyResult>();

            var copier = new BatchCopier(files, new CopyProgress(null, 1, 1, "Copie de"), _ => false);
            copier.Run(new List<(FileEntry, string)> { (Entry(@"C:\src\a.txt"), @"E:\a.txt") },
                (_, result) => results.Add(result), CancellationToken.None);

            Assert.Equal(FileCopyOutcome.Copied, Assert.Single(results).Outcome);
            Assert.Equal(1, copier.Retries);
        }

        [Fact]
        public void Un_fichier_ouvert_n_est_pas_reessaye_pour_rien()
        {
            var files = new FakeFileSystemGateway().WithFile(@"C:\src", @"C:\src\a.pst", 100);
            files.LockedFiles.Add(@"C:\src\a.pst");

            var copier = new BatchCopier(files, new CopyProgress(null, 1, 1, "Copie de"), _ => false);
            copier.Run(new List<(FileEntry, string)> { (Entry(@"C:\src\a.pst"), @"E:\a.pst") }, (_, _) => { },
                CancellationToken.None);

            Assert.Equal(0, copier.Retries);
        }

        [Theory]
        [InlineData("LDI12-Sauvegarde-PC-2026-09-30-1400", "2026-09-30-1400")]
        [InlineData("LDI12-Sauvegarde-PC-2026-09-30-1400-2", "2026-09-30-1400-02")]
        [InlineData("LDI12-Sauvegarde-PC-2026-09-30-1400-12", "2026-09-30-1400-12")]
        [InlineData("LDI12-Sauvegarde-PC", "")]
        public void Deux_sauvegardes_de_la_meme_minute_se_distinguent_et_se_classent(string name, string stamp)
            => Assert.Equal(stamp, RestoreCatalog.Stamp(name));

        [Fact]
        public void La_seconde_sauvegarde_de_la_minute_se_classe_apres_la_premiere()
            => Assert.True(string.CompareOrdinal(
                RestoreCatalog.Stamp("LDI12-Sauvegarde-PC-2026-09-30-1400-2"),
                RestoreCatalog.Stamp("LDI12-Sauvegarde-PC-2026-09-30-1400")) > 0);

        private static FileEntry Entry(string path)
            => new FileEntry { Path = path, SizeBytes = 100, LastWriteUtc = new DateTime(2024, 1, 1) };

        private static Task<ActionPreview> Preview(FakeFileSystemGateway files, string destination)
            => new BackupUserDataAction().PreviewAsync(
                ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
                {
                    [BackupUserDataAction.DestinationParameter] = destination,
                    [BackupUserDataAction.ApplicationsParameter] = "0",
                }),
                CancellationToken.None);
    }
}
