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
    /// <summary>Les filtres des dossiers personnels, les plus gros dossiers, et le rapport remis au client.</summary>
    public class BackupFilterTests
    {
        private const string Folder = @"C:\Compta";

        [Fact]
        public void Les_extensions_se_saisissent_librement()
        {
            var filter = BackupFilter.Parse(" *.ISO, .vhdx;mkv ", "2,5");

            Assert.Equal(new[] { ".iso", ".mkv", ".vhdx" }, filter.Extensions.OrderBy(value => value));
            Assert.Equal((long)(2.5 * 1024 * 1024 * 1024), filter.MaxBytes);
            Assert.True(filter.Excludes(new FileEntry { Path = @"C:\a\win.iso", SizeBytes = 10 }));
            Assert.True(filter.Excludes(new FileEntry { Path = @"C:\a\film.mp4", SizeBytes = 3L * 1024 * 1024 * 1024 }));
            Assert.False(filter.Excludes(new FileEntry { Path = @"C:\a\bilan.xlsx", SizeBytes = 10 }));
            Assert.Equal(2, filter.ExcludedFiles);
        }

        [Fact]
        public void Sans_saisie_rien_n_est_ecarte()
            => Assert.True(BackupFilter.Parse(null, "  ").IsEmpty);

        [Fact]
        public async Task Les_filtres_ecartent_et_disent_ce_qu_ils_ecartent()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Folder, Folder + @"\Bilans\2025.xlsx", 50_000)
                .WithFile(Folder, Folder + @"\Images disque\windows.iso", 6_000_000_000)
                .WithFile(Folder, Folder + @"\Vidéos\formation.mp4", 900_000_000);
            files.Directories.Add(@"E:\");

            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\",
                [BackupUserDataAction.PersonalParameter] = "0",
                [BackupUserDataAction.ApplicationsParameter] = "0",
                [BackupUserDataAction.ExtraFoldersParameter] = ExtraFolders.Encode(new[] { Folder }),
                [BackupUserDataAction.ExcludeExtensionsParameter] = "iso",
            });

            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);

            Assert.Contains(preview.Measurements, line => line.Label == "Écartés par les filtres" && line.Value.StartsWith("1 fichier(s)", StringComparison.Ordinal));
            var largest = Assert.Single(preview.Measurements, line => line.Label == "Plus gros dossiers");
            Assert.StartsWith(@"Autres dossiers / Compta\Vidéos", largest.Value);

            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.DoesNotContain(files.Copied, copy => copy.Source.EndsWith(".iso", StringComparison.Ordinal));
            Assert.Contains(files.Copied, copy => copy.Source.EndsWith("formation.mp4", StringComparison.Ordinal));

            var report = Assert.Single(files.Written, pair => pair.Key.EndsWith(ReinstallSheet.ReportFileName, StringComparison.Ordinal)).Value;
            Assert.Contains("Rapport de sauvegarde", report);
            Assert.Contains("écartés à votre demande", report);
            Assert.Contains("Le client, bon pour accord", report);
            Assert.Contains("vérifié", report);
        }
    }
}
