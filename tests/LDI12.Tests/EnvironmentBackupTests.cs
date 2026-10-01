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
    /// <summary>Les autres navigateurs, Office, le fond d'écran et les polices du compte.</summary>
    public class EnvironmentBackupTests
    {
        private const string Profile = @"E:\Users\Marie";
        private const string Local = Profile + @"\AppData\Local";
        private const string Roaming = Profile + @"\AppData\Roaming";
        private const string Backup = @"F:\LDI12-Sauvegarde-PC-2026-09-30-1400";

        private static readonly RestoreTargets Targets = new RestoreTargets
        {
            Documents = @"C:\Users\Nouveau\Documents",
            Pictures = @"C:\Users\Nouveau\Pictures",
            UserProfile = @"C:\Users\Nouveau",
            LocalAppData = @"C:\Users\Nouveau\AppData\Local",
            RoamingAppData = @"C:\Users\Nouveau\AppData\Roaming",
        };

        [Fact]
        public void Brave_Vivaldi_et_Opera_sont_reconnus()
        {
            var files = new FakeFileSystemGateway();
            foreach (var userData in new[] { Local + @"\BraveSoftware\Brave-Browser\User Data", Local + @"\Vivaldi\User Data" })
            {
                files.Directories.Add(userData);
                files.Children[userData] = new List<string> { userData + @"\Default" };
                files.Files.Add(userData + @"\Default\Preferences");
            }

            files.Files.Add(Roaming + @"\Opera Software\Opera Stable\Preferences");

            var names = AppDataCatalog.Detect(files, ProfileRoots.Offline(Profile)).Select(application => application.Name).ToList();

            Assert.Contains("Brave", names);
            Assert.Contains("Vivaldi", names);
            Assert.Contains("Opera", names);
        }

        [Fact]
        public void Office_le_fond_d_ecran_et_les_polices_partent_avec_les_applications()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Roaming + @"\Microsoft\UProof");
            files.Directories.Add(Roaming + @"\Microsoft\Templates");
            files.Directories.Add(Local + @"\Microsoft\Windows\Fonts");
            files.Files.Add(Roaming + @"\Microsoft\Windows\Themes\TranscodedWallpaper");

            var found = AppDataCatalog.Detect(files, ProfileRoots.Offline(Profile));

            var office = Assert.Single(found, application => application.Name.StartsWith("Office", StringComparison.Ordinal));
            Assert.Contains(office.Sources, source => source.Target == @"Applications\Office\Dictionnaires");
            Assert.Contains(office.Sources, source => source.Target == @"Applications\Office\Modèles");

            var desktop = Assert.Single(found, application => application.Name == "Fond d'écran et polices");
            var fonts = Assert.Single(desktop.Sources, source => source.Target == @"Applications\Environnement\Polices");
            Assert.True(fonts.Keep!("Montserrat.ttf"));
            Assert.False(fonts.Keep!("desktop.ini"));
        }

        [Fact]
        public void A_la_restauration_chaque_reglage_revient_a_sa_place()
        {
            var files = new FakeFileSystemGateway();
            var applications = Backup + @"\Applications";
            files.Directories.Add(Backup);
            files.Directories.Add(applications);
            files.Files.Add(Backup + @"\manifeste.csv");
            files.Children[applications] = new List<string>
            {
                applications + @"\Brave", applications + @"\Office", applications + @"\Environnement", applications + @"\Opera",
            };
            files.Children[applications + @"\Brave"] = new List<string> { applications + @"\Brave\Default" };
            foreach (var folder in new[] { @"Office\Dictionnaires", @"Office\Modèles", @"Environnement\Fond d'écran", @"Environnement\Polices" })
                files.Directories.Add(applications + @"\" + folder);

            var items = RestoreCatalog.Build(files, Backup, Targets).Items;

            Assert.Contains(items, item => item.Destination == @"C:\Users\Nouveau\AppData\Local\BraveSoftware\Brave-Browser\User Data\Default");
            Assert.Contains(items, item => item.Destination == @"C:\Users\Nouveau\AppData\Roaming\Microsoft\UProof");
            Assert.Contains(items, item => item.Destination == @"C:\Users\Nouveau\AppData\Roaming\Opera Software\Opera Stable" &&
                                           item.Mode == RestoreMode.ReplaceFolder);
            Assert.Contains(items, item => item.Destination == @"C:\Users\Nouveau\Pictures\Fond d'écran" &&
                                           item.AfterCopy == RestoreAfterCopy.SetWallpaper);
            Assert.Contains(items, item => item.Destination == @"C:\Users\Nouveau\AppData\Local\Microsoft\Windows\Fonts" &&
                                           item.AfterCopy == RestoreAfterCopy.RegisterFonts);
        }

        [Fact]
        public async Task Les_polices_restaurees_sont_inscrites_pour_le_compte()
        {
            var files = new FakeFileSystemGateway();
            var applications = Backup + @"\Applications";
            var fonts = applications + @"\Environnement\Polices";
            files.Directories.Add(Backup);
            files.Directories.Add(applications);
            files.Files.Add(Backup + @"\manifeste.csv");
            files.Children[applications] = new List<string> { applications + @"\Environnement" };
            files.WithFile(fonts, fonts + @"\Montserrat.ttf", 200_000);

            var processes = new ScriptedProcessRunner(request => ActionFakes.Result(output: "OK\r\n"));
            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [RestoreUserDataAction.SourceParameter] = Backup,
            });

            var action = new RestoreUserDataAction(Targets);
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(outcome.Details, line => line.StartsWith("Polices : 1 police(s) inscrite(s)", StringComparison.Ordinal));
            var script = Assert.Single(processes.Requests, request => request.FileName == "powershell.exe" &&
                                                                       Decode(request.Arguments).Contains("New-ItemProperty"));
            Assert.Contains(@"'C:\Users\Nouveau\AppData\Local\Microsoft\Windows\Fonts\Montserrat.ttf'", Decode(script.Arguments));
            Assert.Contains("'Montserrat (TrueType)'", Decode(script.Arguments));
        }

        private static string Decode(string arguments)
        {
            var encoded = arguments.Substring(arguments.LastIndexOf(' ') + 1);
            try { return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)); }
            catch (FormatException) { return string.Empty; }
        }
    }
}
