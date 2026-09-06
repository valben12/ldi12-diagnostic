using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La copie des données du client, la seule action de ce logiciel qui écrit ses fichiers.
    /// </summary>
    public class BackupTests
    {
        private const string Destination = @"E:\Sauvegarde";

        private static string Desktop =>
            UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;

        private static string Documents =>
            UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Documents").Path;

        // ============================================================ relevé

        [Fact]
        public async Task Sans_destination_choisie_rien_n_est_prepare()
        {
            var preview = await new BackupUserDataAction().PreviewAsync(
                ActionFakes.Context(files: Populated()), CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task Le_releve_annonce_ce_qui_part_et_ce_qui_ne_bouge_pas()
        {
            var preview = await Preview(Populated());

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Contains(preview.WillDo, line => line.Contains("Bureau"));
            Assert.Contains(preview.WillDo, line => line.Contains("Relire chaque fichier"));

            // Les trois promesses qui font qu'un technicien peut lancer cette action sans crainte.
            Assert.Contains(preview.WillNotDo, line => line.Contains("ne supprime rien à la source"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("N'écrase aucun fichier"));
        }

        [Fact]
        public async Task Une_destination_trop_petite_arrete_tout_avant_de_commencer()
        {
            // Découvrir que le support est trop petit après une heure de copie serait la pire
            // façon de l'apprendre.
            var files = Populated();
            files.Free = Measured.Ok(1024L, DataSource.FileSystem);

            var preview = await Preview(files);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains("manque de la place", preview.Blocker);
        }

        [Fact]
        public async Task Un_fichier_present_seulement_en_ligne_est_annonce_et_pas_copie()
        {
            // Le copier le téléchargerait : deux cents gigaoctets sur la ligne du client, et sur
            // le disque qu'on s'apprête à réinstaller.
            var files = Populated();
            files.WithFile(Documents, Path.Combine(Documents, "archive.zip"), 50_000_000, cloudOnly: true);

            var preview = await Preview(files);

            Assert.Contains(preview.WillNotDo, line => line.Contains("nuage"));

            var outcome = await Execute(files, preview);

            Assert.DoesNotContain(files.Copied, copy => copy.Source.EndsWith("archive.zip", StringComparison.Ordinal));

            // Écarté à la prévisualisation, donc jamais tenté : ce n'est pas un échec de copie,
            // c'est une exclusion annoncée. La sauvegarde reste complète pour ce qu'elle promet.
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
        }

        // ============================================================ copie

        [Fact]
        public async Task Sans_releve_prealable_rien_n_est_copie()
        {
            var files = Populated();

            var outcome = await new BackupUserDataAction().ExecuteAsync(
                ActionFakes.Context(files: files), new ActionPreview { Outcome = PreviewOutcome.Ready },
                null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Empty(files.Copied);
        }

        [Fact]
        public async Task Chaque_fichier_arrive_sous_le_nom_de_son_dossier()
        {
            var files = Populated();
            var outcome = await Execute(files, await Preview(files));

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(files.Copied, copy => copy.Destination.Contains(@"\Bureau\note.txt"));
            Assert.Contains(files.Copied, copy => copy.Destination.Contains(@"\Documents\dossier\contrat.pdf"));

            // La source ne bouge pas. C'est la promesse la plus importante de cette action.
            Assert.Empty(files.Deleted);
        }

        [Fact]
        public async Task Un_fichier_deja_present_sous_un_autre_contenu_n_est_jamais_ecrase()
        {
            var files = Populated();
            var preview = await Preview(files);
            var plan = Assert.IsType<BackupPlan>(preview.Plan);

            files.Occupied.Add(Path.Combine(plan.Destination, "Bureau", "note.txt"));

            var outcome = await Execute(files, preview);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.DoesNotContain(files.Copied, copy => copy.Destination.EndsWith("note.txt", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Une_copie_qui_ne_se_relit_pas_identique_compte_comme_un_echec()
        {
            var files = Populated();
            files.CorruptOnCopy.Add(Path.Combine(Desktop, "note.txt"));

            var outcome = await Execute(files, await Preview(files));

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("non copié", outcome.Summary);
        }

        [Fact]
        public async Task Une_destination_pleine_arrete_la_copie_sur_le_champ()
        {
            // Un support plein ne se libère pas en cours de route : continuer produirait des
            // milliers d'échecs identiques avant de le dire.
            var files = Populated();
            files.NoSpaceFrom = Path.Combine(Desktop, "note.txt");

            var outcome = await Execute(files, await Preview(files));

            Assert.Contains("pleine", outcome.Summary);
            Assert.Empty(files.Copied);
        }

        [Fact]
        public async Task Le_manifeste_liste_ce_qui_a_ete_copie_et_ce_qui_ne_l_a_pas_ete()
        {
            var files = Populated();
            files.CorruptOnCopy.Add(Path.Combine(Desktop, "note.txt"));

            await Execute(files, await Preview(files));

            var manifest = Assert.Single(files.Written.Where(entry => entry.Key.EndsWith("manifeste.csv", StringComparison.Ordinal)));

            Assert.Contains("note.txt", manifest.Value);
            Assert.Contains("copie retirée", manifest.Value);
            Assert.Contains("contrat.pdf;", manifest.Value);
        }

        // ============================================================ passerelle réelle

        [Fact]
        public void Une_copie_verifiee_arrive_identique()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "source.bin");
                File.WriteAllBytes(source, Bytes(3 * 1024 * 1024));

                var result = gateway.Copy(
                    new FileCopyRequest(Entry(source), Path.Combine(root, "copie", "source.bin")),
                    CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
                Assert.Equal(
                    File.ReadAllBytes(source),
                    File.ReadAllBytes(Path.Combine(root, "copie", "source.bin")));
            });
        }

        [Fact]
        public void Un_fichier_different_portant_le_meme_nom_n_est_pas_ecrase()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "note.txt");
                var target = Path.Combine(root, "copie", "note.txt");

                File.WriteAllText(source, "l'original");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "autre chose, déjà là");

                var result = gateway.Copy(new FileCopyRequest(Entry(source), target), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Conflict, result.Outcome);
                Assert.Equal("autre chose, déjà là", File.ReadAllText(target));
            });
        }

        [Fact]
        public void Un_fichier_deja_copie_a_l_identique_n_est_pas_recopie()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "note.txt");
                var target = Path.Combine(root, "copie", "note.txt");
                File.WriteAllText(source, "identique");

                var entry = Entry(source);
                Assert.Equal(FileCopyOutcome.Copied,
                    gateway.Copy(new FileCopyRequest(entry, target), CancellationToken.None).Outcome);

                // Deuxième passage : la date d'écriture a été reportée, donc rien à refaire.
                Assert.Equal(FileCopyOutcome.AlreadyPresent,
                    gateway.Copy(new FileCopyRequest(entry, target), CancellationToken.None).Outcome);
            });
        }

        [Fact]
        public void Un_fichier_modifie_depuis_le_releve_n_est_pas_copie()
        {
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "note.txt");
                File.WriteAllText(source, "avant");
                var entry = Entry(source);

                File.WriteAllText(source, "après, et plus long qu'avant");

                var result = gateway.Copy(
                    new FileCopyRequest(entry, Path.Combine(root, "copie", "note.txt")), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Changed, result.Outcome);
            });
        }

        [Fact]
        public void Un_chemin_de_destination_de_plus_de_deux_cent_soixante_caracteres_passe()
        {
            // Une sauvegarde allonge les chemins : le dossier de destination s'ajoute devant
            // l'arborescence copiée. Sans le préfixe étendu, les fichiers les plus profonds
            // d'un profil échoueraient silencieusement, et ce sont souvent ceux d'un logiciel
            // de messagerie.
            Sandbox((gateway, root) =>
            {
                var source = Path.Combine(root, "note.txt");
                File.WriteAllText(source, "un contenu quelconque");

                var deep = root;
                for (var i = 0; i < 12; i++) deep = Path.Combine(deep, new string('d', 24));

                var target = Path.Combine(deep, "note.txt");
                Assert.True(target.Length > 260, "Le chemin d'essai doit dépasser la limite historique.");

                var result = gateway.Copy(new FileCopyRequest(Entry(source), target), CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
            });
        }

        [Fact]
        public void Le_manifeste_n_ecrase_jamais_celui_d_une_sauvegarde_precedente()
        {
            // Deux sauvegardes dans le même dossier : la seconde ne doit pas effacer la trace de
            // la première, qui est la preuve de ce qui a été copié ce jour-là.
            Sandbox((gateway, root) =>
            {
                var path = Path.Combine(root, "manifeste.csv");

                Assert.True(gateway.WriteText(path, "premier"));
                Assert.False(gateway.WriteText(path, "second"));
                Assert.Equal("premier", File.ReadAllText(path));
            });
        }

        // ============================================================ outils

        private static FakeFileSystemGateway Populated()
            => new FakeFileSystemGateway()
                .WithFile(Desktop, Path.Combine(Desktop, "note.txt"), 2048)
                .WithFile(Documents, Path.Combine(Documents, "dossier", "contrat.pdf"), 4096);

        private static Task<ActionPreview> Preview(IFileSystemGateway files)
            => new BackupUserDataAction().PreviewAsync(Context(files), CancellationToken.None);

        private static Task<ActionOutcome> Execute(IFileSystemGateway files, ActionPreview preview)
            => new BackupUserDataAction().ExecuteAsync(Context(files), preview, null, CancellationToken.None);

        private static ActionContext Context(IFileSystemGateway files)
            => ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = Destination,
            });

        private static FileEntry Entry(string path)
        {
            var info = new FileInfo(path);
            return new FileEntry { Path = path, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }

        private static byte[] Bytes(int count)
        {
            var data = new byte[count];
            new Random(7).NextBytes(data);
            return data;
        }

        private static void Sandbox(Action<FileSystemGateway, string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-copie-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                test(new FileSystemGateway(NullLogger.Instance), root);
            }
            finally
            {
                // Sous sa forme étendue : l'essai crée volontairement un chemin de près de
                // quatre cents caractères, et un effacement ordinaire échoue dessus. La
                // première version laissait donc un dossier d'essai dans le dossier temporaire
                // de la machine à chaque exécution de la suite.
                try { Directory.Delete(@"\\?\" + root, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
