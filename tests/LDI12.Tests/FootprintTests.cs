using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Footprint;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que ce logiciel laisse sur la machine d'un client, et ce qu'il faut pour le retirer.
    /// </summary>
    public class FootprintTests
    {
        private static string Root => FootprintScanner.DefaultRoot();

        // ============================================================ relevé

        [Fact]
        public async Task Le_releve_nomme_chaque_emplacement_et_ce_qu_on_perd_en_l_effacant()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Path.Combine(Root, "logs"), Path.Combine(Root, "logs", "ldi12.log"), 4096)
                .WithFile(Path.Combine(Root, "historique"), Path.Combine(Root, "historique", "d1.json"), 2048);

            var preview = await Preview(files);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);

            // Chaque ligne dit ce qui part et ce que cela coûte : c'est la seule chose qui puisse
            // faire changer d'avis avant de valider.
            Assert.Contains(preview.WillDo, line => line.Contains("Journaux") && line.Contains("repart de zéro"));
            Assert.Contains(preview.WillDo, line => line.Contains("Diagnostics archivés") &&
                                                    line.Contains("comparaison avant / après"));
        }

        [Fact]
        public async Task Un_emplacement_absent_n_est_pas_propose_a_la_suppression()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Path.Combine(Root, "logs"), Path.Combine(Root, "logs", "ldi12.log"));

            var preview = await Preview(files);

            Assert.DoesNotContain(preview.WillDo, line => line.Contains("Journal d'intervention"));
        }

        [Fact]
        public async Task Une_machine_vierge_se_dit_plutot_que_de_se_taire()
        {
            var preview = await Preview(new FakeFileSystemGateway());

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
            Assert.Contains("n'a rien écrit", preview.Summary);
        }

        [Fact]
        public async Task Le_releve_annonce_ce_qu_il_ne_touchera_pas()
        {
            // Aussi important que le reste : c'est ce qui permet de répondre « non, cela
            // n'efface pas vos rapports » sans avoir à le supposer.
            var files = new FakeFileSystemGateway()
                .WithFile(Path.Combine(Root, "logs"), Path.Combine(Root, "logs", "ldi12.log"));

            var preview = await Preview(files);

            Assert.Contains(preview.WillNotDo, line => line.Contains("rapports exportés"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("base de registre"));
        }

        // ============================================================ effacement

        [Fact]
        public async Task Sans_releve_prealable_rien_n_est_supprime()
        {
            // La prévisualisation obligatoire est portée par le typage : sans le relevé, il n'y a
            // rien à exécuter. Le test vérifie qu'on le dit plutôt que de deviner une liste.
            var files = new FakeFileSystemGateway()
                .WithFile(Path.Combine(Root, "logs"), Path.Combine(Root, "logs", "ldi12.log"));

            var outcome = await new RemoveFootprintAction().ExecuteAsync(
                ActionFakes.Context(files: files),
                new ActionPreview { Outcome = PreviewOutcome.Ready },
                null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Empty(files.Deleted);
        }

        [Fact]
        public async Task L_effacement_ne_touche_que_les_fichiers_releves()
        {
            // Un fichier écrit entre le relevé et la validation n'a pas été montré au technicien :
            // il ne part pas. C'est la raison pour laquelle la suppression se fait fichier par
            // fichier plutôt que par dossier entier.
            var logs = Path.Combine(Root, "logs");
            var files = new FakeFileSystemGateway()
                .WithFile(logs, Path.Combine(logs, "ldi12.log"));

            var action = new RemoveFootprintAction();
            var context = ActionFakes.Context(files: files);
            var preview = await action.PreviewAsync(context, CancellationToken.None);

            files.WithFile(logs, Path.Combine(logs, "écrit-après-le-relevé.log"));

            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(new[] { Path.Combine(logs, "ldi12.log") }, files.Deleted);
        }

        [Fact]
        public async Task Un_fichier_ouvert_est_conserve_et_compte()
        {
            var logs = Path.Combine(Root, "logs");
            var files = new FakeFileSystemGateway()
                .WithFile(logs, Path.Combine(logs, "ldi12.log"))
                .WithFile(logs, Path.Combine(logs, "session.log"));
            files.LockedFiles.Add(Path.Combine(logs, "session.log"));

            var action = new RemoveFootprintAction();
            var context = ActionFakes.Context(files: files);
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("conservé", outcome.Summary);
        }

        // ============================================================ relevé de premier niveau

        [Fact]
        public void Un_releve_de_premier_niveau_ne_compte_pas_les_sous_dossiers()
        {
            // La racine porte les fichiers de réglages et rien d'autre ; ses sous-dossiers sont
            // relevés séparément. Sans cette limite, tout y serait compté deux fois, et proposé
            // deux fois à la suppression.
            var root = Path.Combine(Path.GetTempPath(), "LDI12-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));

            try
            {
                File.WriteAllText(Path.Combine(root, "profil.json"), "{}");
                File.WriteAllText(Path.Combine(root, "logs", "ldi12.log"), "trace");

                var gateway = new FileSystemGateway(NullLogger.Instance);

                var shallow = gateway.Scan(
                    new DirectoryScanRequest(root) { TopLevelOnly = true }, CancellationToken.None);
                var deep = gateway.Scan(new DirectoryScanRequest(root), CancellationToken.None);

                Assert.Equal(1, shallow.Value.Files.Count);
                Assert.Equal(2, deep.Value.Files.Count);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        private static Task<ActionPreview> Preview(IFileSystemGateway files)
            => new RemoveFootprintAction().PreviewAsync(
                ActionFakes.Context(files: files), CancellationToken.None);
    }
}
