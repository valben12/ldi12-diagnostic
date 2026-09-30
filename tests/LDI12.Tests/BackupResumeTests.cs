using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Une sauvegarde qui s'arrête en route, support arraché, plein, ou arrêt demandé, et qui
    /// reprend où elle en était.
    /// </summary>
    public class BackupResumeTests
    {
        private const string Drive = @"E:\";
        private const string Machine = "ATELIER-PC";

        private static string Desktop =>
            UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;

        // ============================================================ le fichier d'état

        [Fact]
        public void L_etat_d_une_sauvegarde_se_relit()
        {
            var record = BackupState.Parse(BackupState.Render(BackupProgressState.Interrupted, Machine, "Marie"));

            Assert.NotNull(record);
            Assert.Equal(BackupProgressState.Interrupted, record!.State);
            Assert.Equal(Machine, record.Machine);
            Assert.Equal("Marie", record.Account);
        }

        [Fact]
        public void Seule_une_sauvegarde_inachevee_de_cette_machine_et_de_ce_compte_se_reprend()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Drive);
            files.Children[Drive] = new List<string>
            {
                Folder("ATELIER-PC", "2026-09-28-1000", BackupProgressState.Running, "Marie", files),
                Folder("ATELIER-PC", "2026-09-29-1000", BackupProgressState.Finished, "Marie", files),
                Folder("ATELIER-PC", "2026-09-30-0900", BackupProgressState.Interrupted, "Paul", files),
                Folder("AUTRE-PC", "2026-09-30-1000", BackupProgressState.Running, "Marie", files),
            };

            var resumable = BackupState.FindResumable(files, Drive, Machine, "Marie");

            Assert.NotNull(resumable);
            Assert.EndsWith("2026-09-28-1000", resumable!.Path);
            Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0), resumable.Started);
        }

        [Fact]
        public void Une_sauvegarde_terminee_ne_se_reprend_pas()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Drive);
            files.Children[Drive] = new List<string>
            {
                Folder("ATELIER-PC", "2026-09-29-1000", BackupProgressState.Finished, Environment.UserName, files),
            };

            Assert.Null(BackupState.FindResumable(files, Drive, Machine, Environment.UserName));
        }

        // ============================================================ la reprise

        [Fact]
        public async Task La_sauvegarde_reprend_dans_le_dossier_interrompu()
        {
            var files = Populated();
            var interrupted = Folder("ATELIER-PC", "2026-09-28-1000", BackupProgressState.Running, Environment.UserName, files);
            files.Children[Drive] = new List<string> { interrupted };

            var preview = await new BackupUserDataAction().PreviewAsync(Context(files), CancellationToken.None);

            var plan = Assert.IsType<BackupPlan>(preview.Plan);
            Assert.True(plan.Resumed);
            Assert.Equal(interrupted, plan.Destination);
            Assert.StartsWith("Reprise de la sauvegarde interrompue", preview.Summary);
        }

        [Fact]
        public async Task Sans_reprise_demandee_une_nouvelle_sauvegarde_commence()
        {
            var files = Populated();
            var interrupted = Folder("ATELIER-PC", "2026-09-28-1000", BackupProgressState.Running, Environment.UserName, files);
            files.Children[Drive] = new List<string> { interrupted };

            var preview = await new BackupUserDataAction().PreviewAsync(
                Context(files, resume: false), CancellationToken.None);

            var plan = Assert.IsType<BackupPlan>(preview.Plan);
            Assert.False(plan.Resumed);
            Assert.NotEqual(interrupted, plan.Destination);
        }

        [Fact]
        public async Task Une_sauvegarde_menee_au_bout_est_marquee_terminee()
        {
            var files = Populated();
            var action = new BackupUserDataAction();
            var context = Context(files);

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            var plan = (BackupPlan)preview.Plan!;
            var state = BackupState.Parse(files.Texts[Path.Combine(plan.Destination, BackupState.FileName)]);
            Assert.Equal(BackupProgressState.Finished, state!.State);
        }

        // ============================================================ le support arraché

        [Fact]
        public async Task Un_support_debranche_arrete_la_copie_sans_planter_et_la_laisse_reprenable()
        {
            var files = Populated();
            files.UnplugRoot = Drive;
            files.UnplugDuring = Path.Combine(Desktop, "photo.jpg");

            var action = new BackupUserDataAction();
            var context = Context(files);
            var preview = await action.PreviewAsync(context, CancellationToken.None);

            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            // Arrêtée au premier fichier refusé, et non après des milliers d'échecs identiques.
            Assert.Contains("ne répond plus", outcome.Summary);
            Assert.Contains("reprendra", outcome.Summary);
            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.DoesNotContain(files.Copied, copy => copy.Source.EndsWith("zzz.txt", StringComparison.Ordinal));

            // L'état écrit au départ reste « en cours » : c'est lui qui rend la reprise possible.
            var plan = (BackupPlan)preview.Plan!;
            var state = BackupState.Parse(files.Texts[Path.Combine(plan.Destination, BackupState.FileName)]);
            Assert.Equal(BackupProgressState.Running, state!.State);
        }

        [Fact]
        public async Task Un_fichier_illisible_a_la_source_n_arrete_pas_la_copie()
        {
            // Le cas de l'atelier : le disque du client a des secteurs morts. Tant que le support
            // de sauvegarde est là, on sauve tout ce qui se lit encore.
            var files = Populated();
            files.LockedFiles.Add(Path.Combine(Desktop, "photo.jpg"));

            var action = new BackupUserDataAction();
            var context = Context(files);
            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                null, CancellationToken.None);

            Assert.DoesNotContain("ne répond plus", outcome.Summary);
            Assert.Contains(files.Copied, copy => copy.Source.EndsWith("zzz.txt", StringComparison.Ordinal));
        }

        // ============================================================ la restauration

        [Fact]
        public async Task Une_copie_provisoire_laissee_par_une_sauvegarde_interrompue_n_est_jamais_restauree()
        {
            const string backup = @"E:\LDI12-Sauvegarde-ATELIER-PC-2026-09-28-1000";
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Drive);
            files.Directories.Add(backup);
            files.Children[Drive] = new List<string> { backup };
            files.Files.Add(Path.Combine(backup, BackupState.FileName));
            files.WithFile(Path.Combine(backup, "Bureau"), Path.Combine(backup, "Bureau", "note.txt"));
            files.WithFile(Path.Combine(backup, "Bureau"), Path.Combine(backup, "Bureau", "film.mkv" + FileCopyRequest.PartialSuffix));

            var root = Path.Combine(Path.GetTempPath(), "cible");
            var action = new RestoreUserDataAction(new RestoreTargets { Desktop = Path.Combine(root, "Bureau"), UserProfile = root });
            var context = ActionFakes.Context(files: files,
                parameters: new Dictionary<string, string> { [RestoreUserDataAction.SourceParameter] = Drive });

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var plan = Assert.IsType<RestorePlan>(preview.Plan);

            Assert.Equal(1, plan.Files);
        }

        // ============================================================ passerelle réelle

        [Fact]
        public void Les_dates_d_une_cle_fat32_ne_font_pas_passer_une_copie_pour_differente()
        {
            var date = new DateTime(2026, 9, 30, 14, 0, 1, DateTimeKind.Utc);

            Assert.True(FileSystemGateway.SameTime(date, date.AddSeconds(1)));
            Assert.False(FileSystemGateway.SameTime(date, date.AddSeconds(5)));
        }

        [Fact]
        public void Un_support_arrache_se_reconnait_a_son_code_d_erreur()
        {
            Assert.True(FileSystemGateway.IsDeviceError(21));
            Assert.True(FileSystemGateway.IsDeviceError(1167));
            Assert.False(FileSystemGateway.IsDeviceError(32));
        }

        [Fact]
        public void Le_reste_d_une_copie_interrompue_est_remplace_et_ne_reste_pas_sur_le_support()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "note.txt");
                var target = Path.Combine(root, "copie", "note.txt");
                File.WriteAllText(source, "le contenu complet");

                // Ce que laisse un support arraché au milieu du fichier.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target + FileCopyRequest.PartialSuffix, "le cont");

                var result = gateway.Copy(new FileCopyRequest(Entry(source), target), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
                Assert.Equal("le contenu complet", File.ReadAllText(target));
                Assert.False(File.Exists(target + FileCopyRequest.PartialSuffix));
            });
        }

        [Fact]
        public void Une_copie_reussie_ne_laisse_aucun_fichier_provisoire()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "gros.bin");
                var data = new byte[3 * 1024 * 1024 + 17];
                new Random(3).NextBytes(data);
                File.WriteAllBytes(source, data);
                var target = Path.Combine(root, "copie", "gros.bin");

                var result = gateway.Copy(new FileCopyRequest(Entry(source), target), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
                Assert.Equal(data, File.ReadAllBytes(target));
                Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*" + FileCopyRequest.PartialSuffix));
            });
        }

        // ============================================================ mise en place

        private static string Folder(string machine, string stamp, BackupProgressState state, string account,
            FakeFileSystemGateway files)
        {
            var path = Drive + "LDI12-Sauvegarde-" + machine + "-" + stamp;
            files.Directories.Add(path);
            files.Texts[Path.Combine(path, BackupState.FileName)] = BackupState.Render(state, machine, account);
            return path;
        }

        private static FakeFileSystemGateway Populated()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Desktop, Path.Combine(Desktop, "note.txt"), 2048)
                .WithFile(Desktop, Path.Combine(Desktop, "photo.jpg"), 4096)
                .WithFile(Desktop, Path.Combine(Desktop, "zzz.txt"), 1024);
            files.Directories.Add(Drive);
            return files;
        }

        private static ActionContext Context(IFileSystemGateway files, bool resume = true)
            => ActionFakes.Context(
                files: files,
                snapshot: new SystemSnapshot { Machine = new MachineIdentity { MachineName = Machine } },
                parameters: new Dictionary<string, string>
                {
                    [BackupUserDataAction.DestinationParameter] = Drive,
                    [BackupUserDataAction.ApplicationsParameter] = "0",
                    [BackupUserDataAction.ResumeParameter] = resume ? "1" : "0",
                });

        private static FileEntry Entry(string path)
        {
            var info = new FileInfo(path);
            return new FileEntry { Path = path, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }

        private static void Sandbox(Action<FileSystemGateway, string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-reprise-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                test(new FileSystemGateway(NullLogger.Instance), root);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
