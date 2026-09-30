using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce qui accompagne les données dans une sauvegarde : les pilotes, la liste des applications,
    /// et les supports sur lesquels on la dépose.
    /// </summary>
    public class BackupExtrasTests
    {
        private const string Backup = @"E:\LDI12-Sauvegarde-PC-2026-09-30-1400";

        // ============================================================ pilotes : ce qu'on coche

        [Fact]
        public void Seuls_les_pilotes_tiers_sont_proposes_un_par_paquet()
        {
            var choices = DriverBackup.Choices(Snapshot(
                Driver("NVIDIA GeForce RTX 3060", "oem12.inf", "Display"),
                Driver("Contrôleur audio NVIDIA", "oem12.inf", "Display"),
                Driver("Souris HID", "msmouse.inf", "Mouse"),
                Driver("Realtek PCIe GbE", "oem4.inf", "Net")));

            Assert.Equal(new[] { "oem12.inf", "oem4.inf" }, choices.Select(c => c.InfName));
            Assert.Contains("NVIDIA GeForce RTX 3060", choices[0].Label);
            Assert.Contains("Contrôleur audio NVIDIA", choices[0].Label);
        }

        [Fact]
        public void Sans_analyse_aucun_pilote_n_est_propose()
            => Assert.Empty(DriverBackup.Choices(null));

        [Fact]
        public void Un_nom_de_pilote_qui_n_est_pas_un_oem_inf_ne_passe_pas_dans_une_commande()
        {
            // Le nom est collé dans la ligne de commande de pnputil : rien d'autre ne doit y entrer.
            var decoded = DriverBackup.Decode("oem3.inf\tCarte\n" + "oem4.inf & del C:\\x\tPiège\n" + "machine.inf\tWindows\n");

            Assert.Equal("oem3.inf", Assert.Single(decoded).InfName);
        }

        [Fact]
        public void La_liste_cochee_traverse_le_canal_eleve_sans_perte()
        {
            var sent = new[]
            {
                new DriverChoice { InfName = "oem12.inf", Label = "NVIDIA GeForce", DeviceClass = "Display", Version = "31.0" },
                new DriverChoice { InfName = "oem4.inf", Label = "Realtek\tPCIe" },
            };

            var received = DriverBackup.Decode(DriverBackup.Encode(sent));

            Assert.Equal(2, received.Count);
            Assert.Equal("NVIDIA GeForce", received[0].Label);
            Assert.Equal("31.0", received[0].Version);
            Assert.Equal("Realtek PCIe", received[1].Label);
        }

        // ============================================================ pilotes : l'export

        [Fact]
        public void L_export_des_pilotes_demande_les_droits_administrateur()
        {
            var context = ActionFakes.Context(
                platform: new FakePlatformInfo(elevated: false),
                files: Destination(),
                parameters: ExportParameters());

            Assert.Equal(ActionAvailability.NeedsElevation, new ExportDriversAction().CheckReadiness(context).Availability);
        }

        [Fact]
        public void Sous_windows_7_l_export_est_indisponible_et_dit_quoi_faire_a_la_place()
        {
            var context = ActionFakes.Context(
                platform: new FakePlatformInfo(build: 7601, elevated: true),
                files: Destination(),
                parameters: ExportParameters());

            var readiness = new ExportDriversAction().CheckReadiness(context);

            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
            Assert.Contains("FileRepository", readiness.Workaround);
        }

        [Fact]
        public async Task Chaque_pilote_coche_est_exporte_dans_son_propre_dossier()
        {
            var files = Destination();
            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result(0));
            var context = ActionFakes.Context(processes: runner, files: files, parameters: ExportParameters());
            var action = new ExportDriversAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal(2, runner.Requests.Count);
            Assert.All(runner.Requests, request => Assert.Equal("pnputil.exe", request.FileName));
            Assert.Contains(@"/export-driver oem12.inf """ + Backup + @"\Pilotes\oem12""", runner.Requests[0].Arguments);

            var list = files.Written[Backup + @"\Pilotes\" + DriverBackup.ListFileName];
            Assert.Contains("oem12.inf;NVIDIA GeForce", list);
        }

        [Fact]
        public async Task Un_pilote_refuse_n_empeche_pas_les_autres()
        {
            var runner = new ScriptedProcessRunner(request =>
                request.Arguments.Contains("oem4.inf") ? ActionFakes.Result(5, "Accès refusé.") : ActionFakes.Result(0));
            var context = ActionFakes.Context(processes: runner, files: Destination(), parameters: ExportParameters());
            var action = new ExportDriversAction();

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                null, CancellationToken.None);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains(outcome.Details, line => line.Contains("oem4.inf") && line.Contains("Accès refusé"));
        }

        [Fact]
        public async Task Avant_windows_10_1607_tous_les_pilotes_partent_par_dism()
        {
            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result(0));
            var context = ActionFakes.Context(
                platform: new FakePlatformInfo(build: 9600, elevated: true),
                processes: runner, files: Destination(), parameters: ExportParameters());
            var action = new ExportDriversAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(preview.WillDo, line => line.Contains("DISM"));
            Assert.Equal("dism.exe", Assert.Single(runner.Requests).FileName);
        }

        // ============================================================ pilotes : la réinstallation

        [Fact]
        public async Task Les_pilotes_de_la_sauvegarde_sont_reinstalles_un_par_un_avec_leur_nom()
        {
            var files = WithBackup();
            files.Directories.Add(Backup + @"\Pilotes");
            files.Children[Backup + @"\Pilotes"] = new List<string> { Backup + @"\Pilotes\oem12", Backup + @"\Pilotes\oem4" };
            files.WithFile(Backup + @"\Pilotes\oem12", Backup + @"\Pilotes\oem12\nvlt.inf");
            files.WithFile(Backup + @"\Pilotes\oem4", Backup + @"\Pilotes\oem4\rt640x64.inf");
            files.Texts[Backup + @"\Pilotes\pilotes.csv"] = "inf;périphériques;classe;version\r\noem12.inf;NVIDIA GeForce;Display;31.0\r\n";

            var runner = new ScriptedProcessRunner(request =>
                ActionFakes.Result(request.Arguments.Contains("oem12") ? 3010 : 0));
            var context = ActionFakes.Context(processes: runner, files: files,
                parameters: Parameters(RestoreDriversAction.SourceParameter, @"E:\"));
            var action = new RestoreDriversAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(preview.WillDo, line => line.Contains("NVIDIA GeForce"));
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.True(outcome.RestartRequired);
            Assert.All(runner.Requests, request => Assert.Contains("/add-driver", request.Arguments));
        }

        [Fact]
        public async Task Une_sauvegarde_sans_pilotes_n_a_rien_a_reinstaller()
        {
            var context = ActionFakes.Context(files: WithBackup(),
                parameters: Parameters(RestoreDriversAction.SourceParameter, @"E:\"));

            var preview = await new RestoreDriversAction().PreviewAsync(context, CancellationToken.None);

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
        }

        // ============================================================ applications

        [Fact]
        public void Seule_la_source_winget_est_retenue_et_les_identifiants_douteux_sont_ecartes()
        {
            const string export = @"{
              ""Sources"": [
                { ""Packages"": [ { ""PackageIdentifier"": ""VideoLAN.VLC"" }, { ""PackageIdentifier"": ""Mozilla.Firefox"" },
                                  { ""PackageIdentifier"": ""x & shutdown /s"" } ],
                  ""SourceDetails"": { ""Name"": ""winget"" } },
                { ""Packages"": [ { ""PackageIdentifier"": ""9WZDNCRFJ3PS"" } ],
                  ""SourceDetails"": { ""Name"": ""msstore"" } }
              ] }";

            Assert.Equal(new[] { "Mozilla.Firefox", "VideoLAN.VLC" }, WingetApplications.Parse(export));
        }

        [Fact]
        public void Le_fichier_depose_se_relit_comme_un_export_de_winget()
        {
            var document = WingetApplications.Document(new[] { "Mozilla.Firefox", "7zip.7zip" });

            Assert.Equal(new[] { "7zip.7zip", "Mozilla.Firefox" }, WingetApplications.Parse(document));
        }

        [Fact]
        public void Un_export_illisible_ne_donne_aucune_application()
            => Assert.Empty(WingetApplications.Parse("pas du json"));

        [Fact]
        public async Task Sans_winget_la_reinstallation_des_applications_dit_quoi_installer()
        {
            var files = WithBackup();
            files.Texts[Backup + @"\" + WingetApplications.FileName] = WingetApplications.Document(new[] { "Mozilla.Firefox" });

            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result(launchFailed: true));
            var context = ActionFakes.Context(processes: runner, files: files,
                parameters: Parameters(RestoreApplicationsAction.SourceParameter, @"E:\"));

            var preview = await new RestoreApplicationsAction().PreviewAsync(context, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains("Programme d'installation d'application", preview.Blocker);
        }

        [Fact]
        public async Task Une_application_deja_presente_n_est_pas_un_echec()
        {
            var files = WithBackup();
            files.Texts[Backup + @"\" + WingetApplications.FileName] =
                WingetApplications.Document(new[] { "Mozilla.Firefox", "VideoLAN.VLC", "Notepad++.Notepad++" });

            var runner = new ScriptedProcessRunner(request =>
                request.Arguments.Contains("VideoLAN.VLC") ? ActionFakes.Result(WingetApplications.AlreadyInstalled)
                : request.Arguments.Contains("Notepad++") ? ActionFakes.Result(-1)
                : ActionFakes.Result(0));
            var context = ActionFakes.Context(processes: runner, files: files,
                parameters: Parameters(RestoreApplicationsAction.SourceParameter, @"E:\"));
            var action = new RestoreApplicationsAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("1 application(s) réinstallée(s), 1 déjà présente(s), 1 en échec", outcome.Summary);
            Assert.Contains(runner.Requests, request => request.Arguments.StartsWith("install --id Mozilla.Firefox -e -s winget"));
        }

        // ============================================================ la sauvegarde elle-même

        [Fact]
        public void Une_sauvegarde_qui_ne_contient_que_des_pilotes_est_reconnue()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(@"E:\");
            files.Directories.Add(Backup);
            files.Children[@"E:\"] = new List<string> { Backup };
            files.Files.Add(Backup + @"\Pilotes\pilotes.csv");

            Assert.Equal(Backup, RestoreCatalog.Find(files, @"E:\", out _));
            Assert.Equal((1, (DateTime?)new DateTime(2026, 9, 30, 14, 0, 0)), DestinationDrives.Backups(files, @"E:\"));
        }

        [Fact]
        public void Un_disque_en_fat32_est_signale_avant_d_etre_choisi()
            => Assert.True(new DestinationDrive { Root = @"F:\", Format = "FAT32" }.IsFat32);

        // ============================================================ mise en place

        private static SystemSnapshot Snapshot(params DriverInfo[] drivers)
            => new SystemSnapshot { Windows = new WindowsSnapshot { Drivers = drivers } };

        private static DriverInfo Driver(string name, string inf, string deviceClass)
            => new DriverInfo { DeviceName = name, InfName = inf, DeviceClass = deviceClass };

        private static FakeFileSystemGateway Destination()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(@"E:\");
            return files;
        }

        private static FakeFileSystemGateway WithBackup()
        {
            var files = Destination();
            files.Directories.Add(Backup);
            files.Children[@"E:\"] = new List<string> { Backup };
            files.Files.Add(Backup + @"\manifeste.csv");
            return files;
        }

        private static Dictionary<string, string> ExportParameters()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ExportDriversAction.DestinationParameter] = Backup,
                [ExportDriversAction.DriversParameter] = DriverBackup.Encode(new[]
                {
                    new DriverChoice { InfName = "oem12.inf", Label = "NVIDIA GeForce" },
                    new DriverChoice { InfName = "oem4.inf", Label = "Realtek PCIe GbE" },
                }),
            };

        private static Dictionary<string, string> Parameters(string key, string value)
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value };
    }
}
