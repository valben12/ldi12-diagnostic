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
using LDI12.Platform.Gateways;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les mots de passe de Chrome et d'Edge suivent la sauvegarde : leur clé est déchiffrée par
    /// l'ancien compte, puis rechiffrée pour le nouveau.
    /// </summary>
    public class BrowserPasswordTests
    {
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string ChromeData = Path.Combine(Local, @"Google\Chrome\User Data");

        [Fact]
        public void La_cle_se_dechiffre_par_son_compte_et_se_rechiffre_pour_un_autre()
        {
            var oldAccount = new FakeSecretProtector(1);
            var newAccount = new FakeSecretProtector(2);

            var key = BrowserKeys.Extract(oldAccount, LocalState(oldAccount, Key));
            Assert.Equal(Key, key);

            // Recopiée telle quelle, la clé ne s'ouvre pas pour le nouveau compte : c'était la perte.
            Assert.Null(BrowserKeys.Extract(newAccount, LocalState(oldAccount, Key)));

            var installed = BrowserKeys.Install(newAccount, "{\"os_crypt\":{\"app_bound_encrypted_key\":\"QVBQQg==\"},\"x\":1}", key!);
            Assert.NotNull(installed);
            Assert.Equal(Key, BrowserKeys.Extract(newAccount, installed!));

            // La clé propre au navigateur de cette installation n'est pas touchée.
            Assert.Equal("QVBQQg==", (string?)JObject.Parse(installed!).SelectToken("os_crypt.app_bound_encrypted_key"));
        }

        [Fact]
        public void Le_fichier_de_cle_se_relit()
            => Assert.Equal(Key, BrowserKeys.Read("\uFEFF" + BrowserKeys.Document(Key)));

        [Fact]
        public void Une_cle_absente_ou_sans_marque_DPAPI_est_refusee()
        {
            var secrets = new FakeSecretProtector();
            Assert.Null(BrowserKeys.Extract(secrets, "{}"));
            Assert.Null(BrowserKeys.Extract(secrets, "pas du json"));
            Assert.Null(BrowserKeys.Extract(secrets, "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String(Key) + "\"}}"));
        }

        [Fact]
        public void DPAPI_rend_ce_qu_il_a_chiffre()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            var dpapi = new DpapiSecretProtector();
            var key = BrowserKeys.Extract(dpapi, BrowserKeys.Install(dpapi, "{}", Key)!);
            Assert.Equal(Key, key);
        }

        [Fact]
        public async Task La_sauvegarde_emporte_la_cle_quand_on_le_demande()
        {
            var secrets = new FakeSecretProtector();
            var files = WithChrome(new FakeFileSystemGateway(), LocalState(secrets, Key));

            var outcome = await Backup(files, secrets, passwords: "1");

            Assert.Contains(outcome.Details, line => line.Contains("clé emportée pour Google Chrome"));
            var deposited = Assert.Single(files.Written, pair => pair.Key.EndsWith(@"\Applications\Google Chrome\" + BrowserKeys.FileName, StringComparison.Ordinal));
            Assert.Equal(Key, BrowserKeys.Read(deposited.Value));
        }

        [Fact]
        public async Task Sans_l_option_la_cle_ne_quitte_pas_la_machine()
        {
            var secrets = new FakeSecretProtector();
            var files = WithChrome(new FakeFileSystemGateway(), LocalState(secrets, Key));

            await Backup(files, secrets, passwords: null);

            Assert.DoesNotContain(files.Written, pair => pair.Key.EndsWith(BrowserKeys.FileName, StringComparison.Ordinal));
        }

        [Fact]
        public async Task La_restauration_remet_la_cle_rechiffree_pour_le_nouveau_compte()
        {
            const string backup = @"E:\LDI12-Sauvegarde-PC-2026-09-30-1400";
            var targets = new RestoreTargets
            {
                Documents = @"C:\Users\Nouveau\Documents",
                UserProfile = @"C:\Users\Nouveau",
                LocalAppData = @"C:\Users\Nouveau\AppData\Local",
                RoamingAppData = @"C:\Users\Nouveau\AppData\Roaming",
            };

            var files = new FakeFileSystemGateway();
            files.Directories.Add(backup);
            files.Files.Add(backup + @"\manifeste.csv");
            var applications = backup + @"\Applications";
            var chrome = applications + @"\Google Chrome";
            var profile = chrome + @"\Default";
            files.WithFile(profile, profile + @"\Bookmarks").WithFile(profile, profile + @"\Preferences");
            files.Directories.Add(applications);
            files.Directories.Add(chrome);
            files.Children[applications] = new List<string> { chrome };
            files.Children[chrome] = new List<string> { profile };
            files.Files.Add(chrome + @"\Local State");
            files.Texts[chrome + @"\Local State"] = "{\"os_crypt\":{\"encrypted_key\":\"ANCIENNE\"},\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Cabinet\"}}}}";
            files.Files.Add(chrome + @"\" + BrowserKeys.FileName);
            files.Texts[chrome + @"\" + BrowserKeys.FileName] = BrowserKeys.Document(Key);

            var newAccount = new FakeSecretProtector(2);
            var context = ActionFakes.Context(files: files, secrets: newAccount, parameters: new Dictionary<string, string>
            {
                [RestoreUserDataAction.SourceParameter] = backup,
            });

            var action = new RestoreUserDataAction(targets);
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.Contains(preview.WillDo, line => line.Contains("clé des mots de passe de la sauvegarde"));
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            var written = Assert.Single(files.Written, pair => pair.Key.EndsWith(@"Chrome\User Data\Local State", StringComparison.Ordinal));
            Assert.Equal(Key, BrowserKeys.Extract(newAccount, written.Value));
            Assert.Contains(outcome.Details, line => line.Contains("rechiffrée pour ce compte"));
        }

        private static string LocalState(ISecretProtector account, byte[] key)
        {
            var blob = account.Protect(key)!;
            var wrapped = Encoding.ASCII.GetBytes("DPAPI").Concat(blob).ToArray();
            return "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String(wrapped) + "\"},\"profile\":{}}";
        }

        private static FakeFileSystemGateway WithChrome(FakeFileSystemGateway files, string localState)
        {
            var profile = Path.Combine(ChromeData, "Default");
            var state = Path.Combine(ChromeData, "Local State");
            files.WithFile(ChromeData, state)
                .WithFile(profile, Path.Combine(profile, "Bookmarks"))
                .WithFile(profile, Path.Combine(profile, "Preferences"));
            files.Children[ChromeData] = new List<string> { profile };
            files.Files.Add(Path.Combine(profile, "Preferences"));
            files.Texts[state] = localState;
            files.Directories.Add(@"E:\");
            return files;
        }

        private static async Task<ActionOutcome> Backup(FakeFileSystemGateway files, ISecretProtector secrets, string? passwords)
        {
            var parameters = new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\",
                [BackupUserDataAction.PersonalParameter] = "0",
            };
            if (passwords != null) parameters[BackupUserDataAction.BrowserPasswordsParameter] = passwords;

            var context = ActionFakes.Context(files: files, secrets: secrets, parameters: parameters);
            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.True(preview.CanExecute, preview.Blocker ?? preview.Summary);
            return await action.ExecuteAsync(context, preview, null, CancellationToken.None);
        }
    }
}
