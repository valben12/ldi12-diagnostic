using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary>
    /// Le manifeste à empreintes, et la vérification d'une sauvegarde après coup.
    /// </summary>
    /// <remarks>
    /// La question de l'atelier, avant d'effacer le disque d'un client : « la sauvegarde est-elle
    /// vraiment bonne ? ». Une liste de noms ne le dit pas ; une relecture comparée aux empreintes
    /// du jour de la copie, si.
    /// </remarks>
    public class BackupVerifyTests
    {
        private const string Backup = @"E:\LDI12-Sauvegarde-PC-ACCUEIL-2026-10-17-0900";

        // ============================================================ le manifeste

        [Fact]
        public void Le_manifeste_se_relit_guillemets_et_points_virgules_compris()
        {
            var text = new StringBuilder(BackupManifest.Header + "\n");
            BackupManifest.Append(text, "Documents", "Compta; 2026\\\"bilan\".xlsx", 1234, "copié et vérifié", new string('b', 64));
            BackupManifest.Append(text, "Bureau", "verrouillé.pst", 99, "ouvert par un programme", null);

            var entries = BackupManifest.Parse("\uFEFF" + text);

            Assert.Equal(2, entries.Count);
            Assert.Equal("Compta; 2026\\\"bilan\".xlsx", entries[0].Relative);
            Assert.Equal(1234, entries[0].Size);
            Assert.True(entries[0].IsSaved);
            Assert.Equal(new string('b', 64), entries[0].Fingerprint);
            Assert.False(entries[1].IsSaved);
            Assert.Null(entries[1].Fingerprint);
        }

        [Fact]
        public void Un_manifeste_d_avant_les_empreintes_se_relit_encore()
        {
            var entries = BackupManifest.Parse("dossier;fichier;octets;résultat\r\nBureau;note.txt;2048;copié et vérifié\r\n");

            var entry = Assert.Single(entries);
            Assert.True(entry.IsSaved);
            Assert.Null(entry.Fingerprint);
            Assert.Equal(Path.Combine("Bureau", "note.txt"), entry.Key);
        }

        // ============================================================ la vérification

        [Fact]
        public async Task Une_sauvegarde_intacte_est_declaree_intacte()
        {
            var files = Saved(("Bureau", "note.txt"), ("Documents", "contrat.pdf"));

            var (preview, outcome) = await Verify(files);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.StartsWith("Sauvegarde intacte", outcome.Summary);
            Assert.Contains(outcome.Details, line => line.Contains("peut être effacé"));
        }

        [Fact]
        public async Task Un_fichier_abime_disparu_ou_illisible_est_nomme()
        {
            var files = Saved(("Bureau", "note.txt"), ("Documents", "contrat.pdf"), ("Documents", "devis.pdf"), ("Images", "photo.jpg"));
            files.Hashes[Path.Combine(Backup, "Documents", "contrat.pdf")] = new string('0', 64);
            files.Files.Remove(Path.Combine(Backup, "Documents", "devis.pdf"));
            files.Hashes.Remove(Path.Combine(Backup, "Documents", "devis.pdf"));
            files.Unreadable.Add(Path.Combine(Backup, "Images", "photo.jpg"));

            var (_, outcome) = await Verify(files);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("1 fichier(s) intacts, 3 en défaut", outcome.Summary);
            Assert.Contains(outcome.Details, line => line.Contains("plus identiques") && line.Contains("contrat.pdf"));
            Assert.Contains(outcome.Details, line => line.Contains("ont disparu") && line.Contains("devis.pdf"));
            Assert.Contains(outcome.Details, line => line.Contains("ne se relisent plus") && line.Contains("photo.jpg"));
            Assert.Contains(outcome.Details, line => line.Contains("refaire la sauvegarde"));
        }

        [Fact]
        public async Task Un_fichier_que_la_copie_n_a_pas_pu_prendre_n_est_pas_cherche()
        {
            var files = Saved(("Bureau", "note.txt"));
            var manifest = files.Texts[Path.Combine(Backup, BackupManifest.FileName)] +
                           "Bureau;ouvert.pst;10;ouvert par un programme;\r\n";
            files.Texts[Path.Combine(Backup, BackupManifest.FileName)] = manifest;

            var (preview, outcome) = await Verify(files);

            Assert.Equal(1, Assert.IsType<BackupVerificationPlan>(preview.Plan).Entries.Count);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
        }

        [Fact]
        public async Task Une_sauvegarde_sans_manifeste_ne_peut_pas_etre_verifiee()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Backup);
            files.Files.Add(Path.Combine(Backup, BackupState.FileName));

            var (preview, _) = await Verify(files, execute: false);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains("manifeste", preview.Blocker);
        }

        [Fact]
        public async Task La_verification_n_ecrit_rien()
        {
            var files = Saved(("Bureau", "note.txt"));

            await Verify(files);

            Assert.Empty(files.Written);
            Assert.Empty(files.Copied);
            Assert.Empty(files.Deleted);
        }

        // ============================================================ passerelle réelle

        [Fact]
        public void L_empreinte_relue_est_celle_que_la_copie_a_notee()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-verif-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var gateway = new FileSystemGateway(NullLogger.Instance);
                var source = Path.Combine(root, "source.bin");
                var bytes = new byte[3 * 1024 * 1024 + 17];
                new Random(7).NextBytes(bytes);
                File.WriteAllBytes(source, bytes);
                var info = new FileInfo(source);

                var copy = gateway.Copy(new FileCopyRequest(
                    new FileEntry { Path = source, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc },
                    Path.Combine(root, "copie.bin")), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, copy.Outcome);
                Assert.True(BackupManifest.IsFingerprint(copy.Fingerprint!));

                using var sha = System.Security.Cryptography.SHA256.Create();
                var expected = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                Assert.Equal(expected, copy.Fingerprint);

                var read = gateway.Fingerprint(Path.Combine(root, "copie.bin"), null, CancellationToken.None);
                Assert.True(read.HasValue);
                Assert.Equal(expected, read.Value);

                Assert.False(gateway.Fingerprint(Path.Combine(root, "absent.bin"), null, CancellationToken.None).HasValue);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // ============================================================ outils

        /// <summary>Une sauvegarde dont le manifeste note l'empreinte de chaque fichier, tous intacts.</summary>
        private static FakeFileSystemGateway Saved(params (string Folder, string Name)[] saved)
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Backup);
            files.Files.Add(Path.Combine(Backup, BackupManifest.FileName));

            var manifest = new StringBuilder(BackupManifest.Header + "\r\n");
            foreach (var (folder, name) in saved)
            {
                var path = Path.Combine(Backup, folder, name);
                files.Files.Add(path);
                files.Hashes[path] = FakeFileSystemGateway.HashOfName(name);
                BackupManifest.Append(manifest, folder, name, 100, "copié et vérifié", FakeFileSystemGateway.HashOfName(name));
            }

            files.Texts[Path.Combine(Backup, BackupManifest.FileName)] = manifest.ToString();
            return files;
        }

        private static async Task<(ActionPreview Preview, ActionOutcome Outcome)> Verify(FakeFileSystemGateway files, bool execute = true)
        {
            var context = ActionFakes.Context(files: files,
                parameters: new Dictionary<string, string> { [VerifyBackupAction.SourceParameter] = Backup });
            var action = new VerifyBackupAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            if (!execute) return (preview, ActionOutcome.Simple(ActionStatus.NothingToDo, string.Empty));
            return (preview, await action.ExecuteAsync(context, preview, null, CancellationToken.None));
        }
    }
}
