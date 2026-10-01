using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les autres disques : celui d'un PC en panne, branché à l'atelier, ou un disque de données.</summary>
    public class OtherDisksTests
    {
        private const string Uninstall =
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SW-1\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Ciel\r\n" +
            "    DisplayName    REG_SZ    Ciel Compta\r\n" +
            "    DisplayVersion    REG_SZ    24.1\r\n" +
            "    Publisher    REG_SZ    Sage\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SW-1\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Runtime\r\n" +
            "    DisplayName    REG_SZ    Composant\r\n" +
            "    SystemComponent    REG_DWORD    0x1\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SW-1\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\KB123\r\n" +
            "    DisplayName    REG_SZ    Mise à jour\r\n" +
            "    ParentKeyName    REG_SZ    Office\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SW-1\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SansNom\r\n" +
            "    NoRemove    REG_DWORD    0x1\r\n";

        [Fact]
        public void Un_disque_avec_Windows_se_sauvegarde_par_comptes()
        {
            var files = new FakeFileSystemGateway();
            files.Files.Add(@"E:\Windows\System32\config\SOFTWARE");
            files.Children[@"E:\Users"] = new List<string> { @"E:\Users\Marie", @"E:\Users\Public", @"E:\Users\Default" };
            files.Children[@"E:\"] = new List<string>
            {
                @"E:\Windows", @"E:\Users", @"E:\Program Files", @"E:\Compta", @"E:\$Recycle.Bin",
                @"E:\System Volume Information", @"E:\LDI12-Sauvegarde-PC-2026-09-30-1400",
            };

            var volume = SourceVolumes.Describe(files, @"E:\", "Client", DestinationKind.Fixed, 500, 200, isSystem: false);

            Assert.True(volume.HasWindows);
            Assert.Equal(new[] { "Marie" }, volume.Profiles.Select(profile => profile.Name));
            Assert.Equal(new[] { @"E:\Compta" }, volume.Folders);
            Assert.Contains("1 compte", volume.Description);
        }

        [Fact]
        public void Un_disque_de_donnees_se_sauvegarde_par_dossiers()
        {
            var files = new FakeFileSystemGateway();
            files.Children[@"D:\"] = new List<string> { @"D:\Photos", @"D:\$RECYCLE.BIN", @"D:\Recovery", @"D:\Windows" };

            var volume = SourceVolumes.Describe(files, @"D:\", "Data", DestinationKind.Fixed, 500, 200, isSystem: false);

            Assert.False(volume.HasWindows);
            Assert.Empty(volume.Profiles);
            // Sans Windows, un dossier « Windows » n'est qu'un dossier comme un autre.
            Assert.Equal(new[] { @"D:\Photos", @"D:\Windows" }, volume.Folders);
            Assert.StartsWith("Données", volume.Description);
        }

        [Fact]
        public void Les_logiciels_se_lisent_comme_le_panneau_de_configuration_les_montre()
        {
            var entry = Assert.Single(OfflineWindows.ParseUninstall(Uninstall));

            Assert.Equal("Ciel Compta", entry.Name);
            Assert.Equal("24.1", entry.Version);
            Assert.Equal("Sage", entry.Publisher);
        }

        [Fact]
        public void Une_valeur_du_registre_se_lit_dans_reg_query()
        {
            var output = "\r\nHKEY_LOCAL_MACHINE\\X\\ComputerName\r\n    ComputerName    REG_SZ    PC-CLIENT\r\n";

            Assert.Equal("PC-CLIENT", OfflineWindows.Value(output, "ComputerName"));
            Assert.Null(OfflineWindows.Value(output, "Absent"));
        }

        [Fact]
        public async Task Le_compte_d_un_autre_Windows_se_sauvegarde_a_son_nom_et_le_registre_est_toujours_decharge()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(@"E:\Users\Marie\Documents", @"E:\Users\Marie\Documents\lettre.docx", 4000);
            files.Files.Add(@"E:\Windows\System32\config\SYSTEM");
            files.Files.Add(@"E:\Windows\System32\config\SOFTWARE");
            files.Directories.Add(@"F:\");

            var processes = new ScriptedProcessRunner(request =>
            {
                var arguments = request.Arguments ?? string.Empty;
                if (arguments.Contains(@"\ComputerName\ComputerName"))
                    return ActionFakes.Result(output: "    ComputerName    REG_SZ    PC-CLIENT\r\n");
                if (arguments.Contains(@"\CurrentVersion\Uninstall"))
                    return ActionFakes.Result(output: arguments.Contains("WOW6432Node") ? string.Empty : Uninstall);
                if (arguments.Contains(@"\Windows NT\CurrentVersion"))
                    return ActionFakes.Result(output: "    ProductName    REG_SZ    Windows 10 Pro\r\n    CurrentBuild    REG_SZ    22631\r\n");
                return ActionFakes.Result();
            });

            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"F:\",
                [BackupUserDataAction.ProfileParameter] = @"E:\Users\Marie",
                [BackupUserDataAction.ApplicationsParameter] = "0",
                [BackupUserDataAction.WifiParameter] = "1",
            });

            var preview = await new BackupUserDataAction().PreviewAsync(context, CancellationToken.None);

            Assert.True(preview.CanExecute, preview.Blocker ?? preview.Summary);
            Assert.Contains(preview.Measurements, line => line.Label == "Destination" &&
                                                          line.Value.Contains(@"F:\LDI12-Sauvegarde-PC-CLIENT-Marie-"));
            Assert.Contains(preview.Measurements, line => line.Label == "Source" && line.Value.Contains("Marie"));

            // Chaque ruche chargée est déchargée : un registre resté chargé le resterait jusqu'au redémarrage.
            var loads = processes.Requests.Count(request => request.Arguments.StartsWith("load", StringComparison.Ordinal));
            var unloads = processes.Requests.Count(request => request.Arguments.StartsWith("unload", StringComparison.Ordinal));
            Assert.Equal(2, loads);
            Assert.Equal(loads, unloads);

            // Les clés Wi-Fi d'un Windows qui ne tourne pas sont chiffrées pour sa machine : pas d'export.
            Assert.DoesNotContain(processes.Requests, request => request.FileName.Contains("netsh"));
        }

        [Fact]
        public async Task Un_registre_illisible_n_empeche_pas_la_sauvegarde()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(@"E:\Users\Marie\Documents", @"E:\Users\Marie\Documents\lettre.docx", 4000);
            files.Files.Add(@"E:\Windows\System32\config\SYSTEM");
            files.Files.Add(@"E:\Windows\System32\config\SOFTWARE");
            files.Directories.Add(@"F:\");

            var processes = new ScriptedProcessRunner(request => ActionFakes.Result(exitCode: 1));
            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"F:\",
                [BackupUserDataAction.ProfileParameter] = @"E:\Users\Marie",
                [BackupUserDataAction.ApplicationsParameter] = "0",
            });

            var preview = await new BackupUserDataAction().PreviewAsync(context, CancellationToken.None);

            Assert.True(preview.CanExecute, preview.Blocker ?? preview.Summary);
            Assert.Contains(preview.Measurements, line => line.Label == "Destination" &&
                                                          line.Value.Contains(@"F:\LDI12-Sauvegarde-Disque-E-Marie-"));
            Assert.Contains(preview.Measurements, line => line.Label == "Registre de ce Windows");
            Assert.DoesNotContain(processes.Requests, request => request.Arguments.StartsWith("unload", StringComparison.Ordinal));
        }

        [Fact]
        public void Les_sauvegardes_d_un_disque_se_listent_de_la_plus_recente_a_la_plus_ancienne()
        {
            var files = new FakeFileSystemGateway();
            const string older = @"F:\LDI12-Sauvegarde-SALON-2026-09-29-1000";
            const string newer = @"F:\LDI12-Sauvegarde-PC-CLIENT-Marie-2026-09-30-1400";
            files.Directories.Add(@"F:\");
            files.Children[@"F:\"] = new List<string> { older, newer, @"F:\Films" };
            files.Files.Add(older + @"\manifeste.csv");
            files.Files.Add(newer + @"\" + BackupState.FileName);
            files.Texts[newer + @"\" + BackupState.FileName] =
                BackupState.Render(BackupProgressState.Interrupted, "PC-CLIENT", "Marie");

            var list = RestoreCatalog.List(files, @"F:\");

            Assert.Equal(new[] { newer, older }, list.Select(entry => entry.Path));
            Assert.Equal("PC-CLIENT · Marie", list[0].Title);
            Assert.Contains("incomplète", list[0].Description);
            Assert.Equal("SALON", list[1].Title);
            Assert.Equal(new DateTime(2026, 9, 29, 10, 0, 0), list[1].Date);
        }
    }
}
