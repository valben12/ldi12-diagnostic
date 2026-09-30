using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les fichiers qu'un programme tenait ouverts, récupérés par un cliché instantané.</summary>
    public class OpenFilesTests
    {
        private const string Shadow = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7";

        [Fact]
        public void Seuls_les_fichiers_d_un_volume_local_passent()
        {
            var decoded = OpenFilesAction.Decode(
                @"C:\Users\Marie\Documents\Outlook\archive.pst" + "\t" + @"E:\Sauvegarde\Documents\Outlook\archive.pst" + "\n" +
                @"\\nas\partage\x.pst" + "\t" + @"E:\x.pst" + "\n" +
                "n'importe quoi\n");

            Assert.Equal(@"C:\Users\Marie\Documents\Outlook\archive.pst", Assert.Single(decoded).Source);
        }

        [Fact]
        public async Task Un_volume_qui_n_est_pas_en_ntfs_n_a_pas_de_cliche()
        {
            var files = new FakeFileSystemGateway { Format = "FAT32" };
            var context = Context(files, new ScriptedProcessRunner(_ => ActionFakes.Result(0)));

            var preview = await new OpenFilesAction().PreviewAsync(context, CancellationToken.None);

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
        }

        [Fact]
        public async Task Le_fichier_est_copie_depuis_le_cliche_puis_le_cliche_est_supprime()
        {
            var files = new FakeFileSystemGateway();
            files.WithFile(Shadow + @"\Users\Marie\Documents\Outlook", Shadow + @"\Users\Marie\Documents\Outlook\archive.pst", 5000);

            var scripts = new List<string>();
            var runner = new ScriptedProcessRunner(request =>
            {
                scripts.Add(Script(request));
                return scripts.Count == 1
                    ? ActionFakes.Result(0, "CLICHE {1234} " + Shadow + "\r\n")
                    : ActionFakes.Result(0);
            });

            var action = new OpenFilesAction();
            var context = Context(files, runner);
            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                null, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(files.Copied, copy => copy.Source.StartsWith(Shadow, StringComparison.Ordinal));
            Assert.Contains("Delete()", scripts.Last());
            Assert.Contains("{1234}", scripts.Last());
        }

        [Fact]
        public async Task Un_cliche_refuse_est_explique_et_rien_n_est_copie()
        {
            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result(1, "ERREUR 6\r\n"));
            var action = new OpenFilesAction();
            var context = Context(new FakeFileSystemGateway(), runner);

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains(outcome.Details, line => line.Contains("place insuffisante"));
        }

        [Fact]
        public async Task La_copie_releve_les_fichiers_tenus_ouverts()
        {
            var desktop = UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;
            var files = new FakeFileSystemGateway()
                .WithFile(desktop, Path.Combine(desktop, "note.txt"))
                .WithFile(desktop, Path.Combine(desktop, "archive.pst"));
            files.LockedFiles.Add(Path.Combine(desktop, "archive.pst"));

            var action = new BackupUserDataAction();
            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\",
                [BackupUserDataAction.ApplicationsParameter] = "0",
            });
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            var open = Assert.Single(((BackupPlan)preview.Plan!).OpenFiles);
            Assert.EndsWith("archive.pst", open.Source);
        }

        // ============================================================ cliché réel

        [AdministratorFact]
        public async Task Un_fichier_tenu_ouvert_se_copie_depuis_un_vrai_cliche()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-cliche-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "archive.pst");
            var target = Path.Combine(root, "copie", "archive.pst");
            File.WriteAllText(source, "le contenu de l'archive");

            try
            {
                var gateway = new FileSystemGateway(NullLogger.Instance);

                // Ouvert sans partage, comme Outlook tient son archive : la copie ordinaire est refusée.
                using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var info = new FileInfo(source);
                    var entry = new FileEntry { Path = source, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
                    Assert.Equal(FileCopyOutcome.Locked, gateway.Copy(new FileCopyRequest(entry, target), CancellationToken.None).Outcome);

                    var context = ActionFakes.Context(
                        platform: new FakePlatformInfo(elevated: true),
                        processes: new ProcessRunner(NullLogger.Instance),
                        files: gateway,
                        parameters: new Dictionary<string, string>
                        {
                            [OpenFilesAction.DestinationParameter] = root,
                            [OpenFilesAction.FilesParameter] =
                                OpenFilesAction.Encode(new[] { new OpenFile { Source = source, Target = target } }),
                        });

                    var action = new OpenFilesAction();
                    var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                        null, CancellationToken.None);

                    Assert.True(outcome.Status == ActionStatus.Succeeded, outcome.Summary + " " + string.Join(" ", outcome.Details));
                }

                Assert.Equal("le contenu de l'archive", File.ReadAllText(target));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        private static ActionContext Context(FakeFileSystemGateway files, ScriptedProcessRunner runner)
            => ActionFakes.Context(files: files, processes: runner, parameters: new Dictionary<string, string>
            {
                [OpenFilesAction.DestinationParameter] = @"E:\Sauvegarde",
                [OpenFilesAction.FilesParameter] = OpenFilesAction.Encode(new[]
                {
                    new OpenFile
                    {
                        Source = @"C:\Users\Marie\Documents\Outlook\archive.pst",
                        Target = @"E:\Sauvegarde\Documents\Outlook\archive.pst",
                    },
                }),
            });

        /// <summary>Le script PowerShell qu'une requête porte, décodé.</summary>
        private static string Script(ProcessRequest request)
        {
            var marker = "-EncodedCommand ";
            var index = request.Arguments.IndexOf(marker, StringComparison.Ordinal);
            return index < 0
                ? string.Empty
                : System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(request.Arguments.Substring(index + marker.Length)));
        }

        /// <summary>Ne tourne qu'en administrateur : Windows ne crée de cliché que pour lui.</summary>
        private sealed class AdministratorFactAttribute : FactAttribute
        {
            public AdministratorFactAttribute()
            {
                if (!IsAdministrator())
                    Skip = "Un cliché instantané demande les droits administrateur.";
            }

            private static bool IsAdministrator()
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
