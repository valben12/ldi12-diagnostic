using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les dossiers ajoutés à la main, où qu'ils soient.</summary>
    public class ExtraFoldersTests
    {
        private const string Backup = @"E:\LDI12-Sauvegarde-PC-2026-09-30-1400";

        [Fact]
        public void Chaque_dossier_a_un_nom_unique_dans_la_sauvegarde()
        {
            var plan = ExtraFolders.Plan(new[] { @"C:\Compta", @"D:\Archives\Compta\", @"C:\Compta", "F:" });

            Assert.Equal(new[] { "Compta", "Compta (2)", "Disque F" }, plan.Select(folder => folder.Name));
            Assert.True(plan[2].IsVolumeRoot);
            Assert.Equal(@"F:\", plan[2].Path);
            Assert.Equal(@"Autres dossiers\Compta (2)", plan[1].Target);
        }

        [Fact]
        public void Les_fichiers_systeme_d_une_racine_ne_sont_pas_sauvegardes()
        {
            Assert.False(ExtraFolders.KeepAtRoot("pagefile.sys"));
            Assert.False(ExtraFolders.KeepAtRoot("hiberfil.sys"));
            Assert.True(ExtraFolders.KeepAtRoot("factures.xlsx"));
            Assert.Contains("System Volume Information", ExtraFolders.SystemDirectories);
        }

        [Fact]
        public void L_emplacement_d_origine_se_relit()
        {
            var map = ExtraFolders.ParseMap("\uFEFF" + ExtraFolders.Map(ExtraFolders.Plan(new[] { @"C:\Compta", "D:" })));

            Assert.Equal(@"C:\Compta", map["Compta"]);
            Assert.Equal(@"D:\", map["Disque D"]);
        }

        [Fact]
        public async Task Un_dossier_ajoute_part_dans_autres_dossiers_avec_son_emplacement_d_origine()
        {
            var files = new FakeFileSystemGateway().WithFile(@"C:\Compta", @"C:\Compta\bilan.xlsx", 5000);
            files.Directories.Add(@"E:\");
            var action = new BackupUserDataAction();
            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\",
                [BackupUserDataAction.PersonalParameter] = "0",
                [BackupUserDataAction.ApplicationsParameter] = "0",
                [BackupUserDataAction.ExtraFoldersParameter] = ExtraFolders.Encode(new[] { @"C:\Compta", @"C:\Absent" }),
            });

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Autres dossiers\Compta\bilan.xlsx", StringComparison.Ordinal));
            Assert.Contains(preview.Measurements, line => line.Label == "Dossier introuvable" && line.Value.Contains(@"C:\Absent"));
            Assert.Contains(files.Written, pair => pair.Key.EndsWith(ExtraFolders.MapFileName, StringComparison.Ordinal) &&
                                                   pair.Value.Contains("Compta\t" + @"C:\Compta"));
        }

        [Fact]
        public void A_la_restauration_un_dossier_revient_a_son_emplacement_d_origine()
        {
            var files = Restorable(@"Compta" + "\t" + @"C:\Compta");
            files.Directories.Add(@"C:\");

            var item = Assert.Single(RestoreCatalog.Build(files, Backup, Targets()).Items);

            Assert.Equal(@"C:\Compta", item.Destination);
            Assert.Equal(RestoreMode.Merge, item.Mode);
        }

        [Fact]
        public void Sans_son_disque_d_origine_un_dossier_revient_dans_les_documents()
        {
            var files = Restorable(@"Compta" + "\t" + @"D:\Compta");

            var item = Assert.Single(RestoreCatalog.Build(files, Backup, Targets()).Items);

            Assert.Equal(@"C:\Users\Marie\Documents\Autres dossiers\Compta", item.Destination);
        }

        [Fact]
        public void Un_dossier_ne_revient_jamais_sur_le_disque_de_la_sauvegarde()
        {
            // L'ancien disque E: n'existe plus ; E: est aujourd'hui la clé de sauvegarde.
            var files = Restorable(@"Compta" + "\t" + @"E:\Compta");
            files.Directories.Add(@"E:\");

            var item = Assert.Single(RestoreCatalog.Build(files, Backup, Targets()).Items);

            Assert.StartsWith(@"C:\Users\Marie\Documents", item.Destination);
        }

        private static FakeFileSystemGateway Restorable(string mapLine)
        {
            var files = new FakeFileSystemGateway();
            var others = Backup + @"\Autres dossiers";
            files.Directories.Add(Backup);
            files.Directories.Add(others);
            files.Children[others] = new List<string> { others + @"\Compta" };
            files.Texts[Backup + @"\" + ExtraFolders.MapFileName] = mapLine + "\r\n";
            return files;
        }

        private static RestoreTargets Targets() => new RestoreTargets
        {
            Documents = @"C:\Users\Marie\Documents",
            UserProfile = @"C:\Users\Marie",
        };
    }
}
