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
    /// <summary>Les pilotes se choisissent partout : sur ce PC, sur le disque d'un PC client, à la restauration.</summary>
    public class DriverChoiceTests
    {
        private const string Inf =
            "; Pilote d'essai\r\n" +
            "[Version]\r\n" +
            "Signature = \"$WINDOWS NT$\"\r\n" +
            "Class = Display ; carte graphique\r\n" +
            "Provider = %NVIDIA%\r\n" +
            "DriverVer = 03/01/2024,31.0.15.5222\r\n" +
            "\r\n" +
            "[Manufacturer]\r\n" +
            "%NVIDIA% = NVIDIA_Devices, NTamd64.10.0\r\n" +
            "\r\n" +
            "[NVIDIA_Devices.NTamd64.10.0]\r\n" +
            "%NVIDIA_DEV.2504% = Section001, PCI\\VEN_10DE&DEV_2504\r\n" +
            "\r\n" +
            "[Strings]\r\n" +
            "NVIDIA = \"NVIDIA\"\r\n" +
            "NVIDIA_DEV.2504 = \"NVIDIA GeForce RTX 3060\"\r\n";

        private const string Packages =
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SYS-1\\DriverDatabase\\DriverInfFiles\\oem3.inf\r\n" +
            "    (par défaut)    REG_MULTI_SZ    nvlt.inf_amd64_old\\0nvlt.inf_amd64_abc\r\n" +
            "    Active    REG_SZ    nvlt.inf_amd64_abc\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SYS-1\\DriverDatabase\\DriverInfFiles\\oem4.inf\r\n" +
            "    (par défaut)    REG_MULTI_SZ    rt640x64.inf_amd64_def\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SYS-1\\DriverDatabase\\DriverInfFiles\\machine.inf\r\n" +
            "    (par défaut)    REG_MULTI_SZ    machine.inf_amd64_x\r\n" +
            "\r\nHKEY_LOCAL_MACHINE\\LDI12-HL-SYS-1\\DriverDatabase\\DriverInfFiles\\oem5.inf\r\n" +
            "    Active    REG_SZ    ..\\..\\Windows\r\n";

        [Fact]
        public void Un_inf_dit_ce_qu_il_installe()
        {
            var summary = OfflineDrivers.Parse(Inf);

            Assert.Equal("Display", summary.DeviceClass);
            Assert.Equal("NVIDIA", summary.Provider);
            Assert.Equal("31.0.15.5222", summary.Version);
            Assert.Equal("NVIDIA GeForce RTX 3060", summary.Device);
            Assert.Equal("NVIDIA GeForce RTX 3060", summary.Label("oem3.inf"));
        }

        [Fact]
        public void Le_registre_designe_le_paquet_en_service_de_chaque_pilote_tiers()
        {
            var map = OfflineDrivers.ParsePackages(Packages);

            Assert.Equal("nvlt.inf_amd64_abc", map["oem3.inf"]);
            Assert.Equal("rt640x64.inf_amd64_def", map["oem4.inf"]);
            Assert.False(map.ContainsKey("machine.inf"));

            // Un nom qui sortirait du magasin de pilotes n'est pas retenu.
            Assert.False(map.ContainsKey("oem5.inf"));
        }

        [Fact]
        public void Les_pilotes_d_un_disque_se_listent_depuis_ses_fichiers_inf()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(@"E:\Windows\INF", @"E:\Windows\INF\oem3.inf")
                .WithFile(@"E:\Windows\INF", @"E:\Windows\INF\machine.inf")
                .WithFile(@"E:\Windows\INF", @"E:\Windows\INF\oem3.PNF");
            files.Texts[@"E:\Windows\INF\oem3.inf"] = Inf;

            var driver = Assert.Single(OfflineDrivers.List(files, @"E:\"));

            Assert.Equal("oem3.inf", driver.InfName);
            Assert.Equal("NVIDIA GeForce RTX 3060", driver.Label);
            Assert.Equal("Display", driver.DeviceClass);
        }

        [Fact]
        public async Task Seuls_les_pilotes_coches_d_un_autre_Windows_partent_avec_la_sauvegarde()
        {
            const string repository = @"E:\Windows\System32\DriverStore\FileRepository\";
            var files = new FakeFileSystemGateway()
                .WithFile(@"E:\Users\Marie\Documents", @"E:\Users\Marie\Documents\lettre.docx", 4000)
                .WithFile(repository + "nvlt.inf_amd64_abc", repository + @"nvlt.inf_amd64_abc\nvlt.inf", 9000)
                .WithFile(repository + "rt640x64.inf_amd64_def", repository + @"rt640x64.inf_amd64_def\rt640x64.inf", 3000);
            files.Files.Add(@"E:\Windows\System32\config\SYSTEM");
            files.Files.Add(@"E:\Windows\System32\config\SOFTWARE");
            files.Directories.Add(@"F:\");

            var processes = new ScriptedProcessRunner(request =>
                request.Arguments.Contains("DriverInfFiles") ? ActionFakes.Result(output: Packages) : ActionFakes.Result());

            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"F:\",
                [BackupUserDataAction.ProfileParameter] = @"E:\Users\Marie",
                [BackupUserDataAction.ApplicationsParameter] = "0",
                [BackupUserDataAction.OfflineDriversParameter] = DriverBackup.Encode(new[]
                {
                    new DriverChoice { InfName = "oem3.inf", Label = "NVIDIA GeForce RTX 3060" },
                    new DriverChoice { InfName = "oem9.inf", Label = "Absent" },
                }),
            });

            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.True(preview.CanExecute, preview.Blocker ?? preview.Summary);
            Assert.Contains(preview.Measurements, line => line.Label == "Pilotes introuvables" && line.Value.Contains("oem9.inf"));
            Assert.DoesNotContain(processes.Requests, request => request.FileName.Contains("dism"));

            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Pilotes\oem3\nvlt.inf", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Copied, copy => copy.Source.Contains("rt640x64"));
            Assert.Contains(files.Written, pair => pair.Key.EndsWith(@"\Pilotes\" + DriverBackup.ListFileName, StringComparison.Ordinal) &&
                                                   pair.Value.Contains("oem3.inf;NVIDIA GeForce RTX 3060"));
        }

        [Fact]
        public async Task A_la_restauration_seuls_les_pilotes_coches_sont_reinstalles()
        {
            const string backup = @"F:\LDI12-Sauvegarde-PC-2026-09-30-1400";
            var files = new FakeFileSystemGateway()
                .WithFile(backup + @"\Pilotes\oem3", backup + @"\Pilotes\oem3\nvlt.inf")
                .WithFile(backup + @"\Pilotes\oem4", backup + @"\Pilotes\oem4\rt640x64.inf");
            files.Directories.Add(backup);
            files.Directories.Add(backup + @"\Pilotes");
            files.Files.Add(backup + @"\manifeste.csv");
            files.Children[backup + @"\Pilotes"] = new List<string> { backup + @"\Pilotes\oem3", backup + @"\Pilotes\oem4" };
            files.Texts[backup + @"\Pilotes\oem3\nvlt.inf"] = Inf;

            var all = RestoreDriversAction.Packages(files, backup);
            Assert.Equal(2, all.Count);
            Assert.StartsWith("NVIDIA GeForce RTX 3060, version 31.0.15.5222", all[0].Label);

            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [RestoreDriversAction.SourceParameter] = backup,
                [RestoreDriversAction.PackagesParameter] = "oem4",
            });

            var preview = await new RestoreDriversAction().PreviewAsync(context, CancellationToken.None);

            var plan = Assert.IsType<DriverRestorePlan>(preview.Plan);
            Assert.Equal(new[] { backup + @"\Pilotes\oem4" }, plan.Packages.Select(package => package.Folder));
        }
    }
}
