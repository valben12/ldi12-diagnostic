using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Collectors.Windows;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Platform.Gateways;
using Microsoft.Win32;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La sauvegarde avant réinstallation : données d'applications, profils Wi-Fi et fiche.
    /// </summary>
    /// <remarks>
    /// Demandé depuis l'atelier : « en cliquant simplement sur le bouton, copier toutes les
    /// données pour pouvoir faire une réinstallation sans se prendre la tête ». Ces tests tiennent
    /// les deux moitiés de la promesse : ce qui compte part, et ce qui ne peut pas partir est dit.
    /// </remarks>
    public class AppDataBackupTests
    {
        private const string Destination = @"E:\Sauvegarde";

        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string ChromeData = Path.Combine(Local, @"Google\Chrome\User Data");
        private static readonly string ChromeProfile = Path.Combine(ChromeData, "Default");

        // ============================================================ navigateurs

        [Fact]
        public async Task Un_profil_chrome_part_sans_ses_caches()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var outcome = await Run(files);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Applications\Google Chrome\Default\Bookmarks", StringComparison.Ordinal));
            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Applications\Google Chrome\Local State", StringComparison.Ordinal));

            // Le cache fait des gigaoctets et se reconstruit à la première navigation.
            Assert.DoesNotContain(files.Copied, copy => copy.Source.Contains(@"\Cache\"));
            Assert.DoesNotContain(files.Copied, copy => copy.Source.Contains(@"\Service Worker\CacheStorage\"));
            Assert.Contains(files.Copied, copy => copy.Source.Contains(@"\Service Worker\Database\"));

            // Les profils techniques du navigateur ne sont ceux de personne.
            Assert.DoesNotContain(files.Copied, copy => copy.Source.Contains("System Profile"));
        }

        [Fact]
        public async Task Les_mots_de_passe_du_navigateur_sont_annonces_comme_ne_suivant_pas()
        {
            // Le piège de la réinstallation : le profil est copié, les mots de passe sont dedans,
            // et ils ne se déchiffreront jamais sur le Windows réinstallé.
            var files = WithChrome(new FakeFileSystemGateway());
            var preview = await Preview(files);

            Assert.Contains(preview.WillDo, line => line.Contains("Google Chrome (1 profil)") && line.Contains("sans les caches"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("Mots de passe de Google Chrome"));
        }

        [Fact]
        public async Task Un_navigateur_ouvert_est_signale_avant_la_copie()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var processes = new ScriptedProcessRunner(request => request.FileName == "tasklist.exe"
                ? ActionFakes.Result(output: "\"System\",\"4\",\"Services\",\"0\",\"1 024 Ko\"\r\n\"chrome.exe\",\"4242\",\"Console\",\"1\",\"250 000 Ko\"\r\n")
                : ActionFakes.Result());

            var preview = await Preview(files, processes: processes);

            var caution = Assert.Single(preview.Measurements, line => line.Label == "Programmes ouverts");
            Assert.Equal(PreviewLineKind.Caution, caution.Kind);
            Assert.Contains("Google Chrome", caution.Value);
        }

        // ============================================================ messagerie et autres

        [Fact]
        public async Task Outlook_copie_les_archives_pst_et_laisse_les_ost()
        {
            var outlook = Path.Combine(Local, @"Microsoft\Outlook");
            var signatures = Path.Combine(Roaming, @"Microsoft\Signatures");

            var files = new FakeFileSystemGateway()
                .WithFile(outlook, Path.Combine(outlook, "archives 2019.pst"), 900_000_000)
                .WithFile(outlook, Path.Combine(outlook, "client@exemple.fr.ost"), 6_000_000_000)
                .WithFile(signatures, Path.Combine(signatures, "Cabinet.htm"), 4_000);

            await Run(files);

            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Outlook\Fichiers de données\archives 2019.pst", StringComparison.Ordinal));
            Assert.Contains(files.Copied, copy => copy.Destination.EndsWith(@"\Outlook\Signatures\Cabinet.htm", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Copied, copy => copy.Source.EndsWith(".ost", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Teams_est_trouve_et_rien_n_en_est_copie()
        {
            // Les conversations sont chez Microsoft : copier le cache ne sauverait rien, et le
            // dire vaut mieux que de le copier pour faire bonne mesure.
            var teams = Path.Combine(Local, @"Packages\MSTeams_8wekyb3d8bbwe");
            var files = WithChrome(new FakeFileSystemGateway());
            files.WithFile(teams, Path.Combine(teams, @"LocalCache\cache.bin"));

            var preview = await Preview(files);

            Assert.Contains(preview.WillNotDo, line => line.StartsWith("Microsoft Teams : rien à copier", StringComparison.Ordinal) &&
                                                       line.Contains("serveurs de Microsoft"));
            var plan = Assert.IsType<BackupPlan>(preview.Plan);
            Assert.DoesNotContain(plan.Folders, folder => folder.Application == "Microsoft Teams");
        }

        [Fact]
        public async Task Decocher_les_applications_ne_copie_que_les_dossiers_personnels()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var desktop = UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;
            files.WithFile(desktop, Path.Combine(desktop, "note.txt"));

            var preview = await Preview(files, applications: "0");
            await new BackupUserDataAction().ExecuteAsync(Context(files, applications: "0"), preview, null, CancellationToken.None);

            Assert.Contains(files.Copied, copy => copy.Source.EndsWith("note.txt", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Copied, copy => copy.Destination.Contains(@"\Applications\"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("données d'applications"));
        }

        [Fact]
        public async Task Decocher_les_dossiers_personnels_ne_copie_que_les_applications()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var desktop = UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;
            files.WithFile(desktop, Path.Combine(desktop, "note.txt"));

            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = Destination,
                [BackupUserDataAction.PersonalParameter] = "0",
            });
            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.DoesNotContain(files.Copied, copy => copy.Source.EndsWith("note.txt", StringComparison.Ordinal));
            Assert.Contains(files.Copied, copy => copy.Destination.Contains(@"\Applications\Google Chrome\"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("dossiers personnels"));
        }

        // ============================================================ Wi-Fi

        [Fact]
        public async Task Sans_la_case_cochee_aucun_profil_wifi_n_est_exporte()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var processes = new ScriptedProcessRunner(_ => ActionFakes.Result());

            await Run(files, processes: processes);

            Assert.DoesNotContain(processes.Requests, request => request.FileName == "netsh.exe");
            Assert.DoesNotContain(files.Created, path => path.EndsWith(@"\Wi-Fi", StringComparison.Ordinal));
        }

        [Fact]
        public async Task La_case_cochee_exporte_les_profils_et_dit_lesquels_ont_leur_cle_en_clair()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var processes = new ScriptedProcessRunner(request =>
            {
                if (request.FileName != "netsh.exe") return ActionFakes.Result();

                var folder = Regex.Match(request.Arguments, "folder=\"(?<f>[^\"]+)\"").Groups["f"].Value;
                Profile(files, folder, "Maison", Wlan("Maison", "wpa2psk", "<keyMaterial>secret123</keyMaterial>", "false"));
                Profile(files, folder, "Bureau", Wlan("Bureau", "wpa2psk", "<keyMaterial>01000000D08C9DDF</keyMaterial>", "true"));
                Profile(files, folder, "Gare", Wlan("Gare", "open", string.Empty, null));
                return ActionFakes.Result(output: "Le profil d'interface est enregistré.");
            });

            var outcome = await Run(files, processes: processes, wifi: "1");

            var netsh = Assert.Single(processes.Requests, request => request.FileName == "netsh.exe");
            Assert.Contains("key=clear", netsh.Arguments);
            Assert.Contains(outcome.Details, line => line.Contains("3 profil(s) Wi-Fi exporté(s), 1 avec leur clé en clair, 1 avec une clé chiffrée"));

            var sheet = Sheet(files);
            Assert.Contains("Maison", sheet);
            Assert.Contains("chiffrée pour cette machine", sheet);
        }

        [Fact]
        public async Task Une_machine_sans_wifi_le_dit_avec_les_mots_de_windows()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var processes = new ScriptedProcessRunner(request => request.FileName == "netsh.exe"
                ? ActionFakes.Result(exitCode: 1, output: "Le service Configuration automatique des réseaux locaux sans fil (wlansvc)\r\nn'est pas en cours d'exécution.\r\n")
                : ActionFakes.Result());

            var outcome = await Run(files, processes: processes, wifi: "1");

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(outcome.Details, line => line.Contains("(wlansvc) n'est pas en cours d'exécution"));
        }

        [Fact]
        public void Un_message_utf8_lu_comme_de_l_ansi_redevient_lisible()
        {
            // Tel que relevé à l'essai réel dans l'application.
            Assert.Equal(
                "Le service Configuration automatique des réseaux locaux sans fil (wlansvc) n’est pas en cours d’exécution.",
                WifiExport.Repair("Le service Configuration automatique des rÃ©seaux locaux sans fil (wlansvc) nâ€™est pas en cours dâ€™exÃ©cution."));

            // Un message déjà juste ne bouge pas.
            Assert.Equal("Aucun réseau à exporter.", WifiExport.Repair("Aucun réseau à exporter."));
        }

        [Theory]
        [InlineData("<keyMaterial>abc</keyMaterial>", "false", WifiKeyState.Clear)]
        [InlineData("<keyMaterial>01000000D08C</keyMaterial>", "true", WifiKeyState.Protected)]
        [InlineData("", null, WifiKeyState.None)]
        public void Un_profil_exporte_est_relu_pour_savoir_ce_qu_il_contient(string key, string? protection, WifiKeyState expected)
        {
            var profile = WifiExport.Parse(Wlan("Box &amp; Cie", "wpa2psk", key, protection), "secours");

            Assert.Equal("Box & Cie", profile.Name);
            Assert.Equal(expected, profile.Key);
        }

        // ============================================================ fiche

        [Fact]
        public async Task La_fiche_liste_les_logiciels_avec_leurs_versions_et_ce_qui_reste_a_faire()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            var snapshot = new SystemSnapshot
            {
                Machine = new MachineIdentity { MachineName = "PC-ACCUEIL" },
                Windows = new WindowsSnapshot
                {
                    Software = new SoftwareInventory
                    {
                        Programs = new[]
                        {
                            new InstalledProgram { Name = "Sage <Compta>", Version = "9.2.1", Publisher = "Sage", Scope = SoftwareScope.Machine },
                            new InstalledProgram { Name = "Zoom", Version = "6.1", Scope = SoftwareScope.User },
                        },
                        StoreApps = new[] { new StoreApp { Name = "WhatsApp", PackageName = "5319275A.WhatsAppDesktop", Version = "2.2635.100.0" } },
                        HiddenEntries = Measured.Ok(120, DataSource.Registry),
                    },
                },
            };

            await Run(files, snapshot: snapshot);

            var sheet = Sheet(files);
            Assert.Contains("PC-ACCUEIL", sheet);
            Assert.Contains("Sage &lt;Compta&gt;", sheet);
            Assert.DoesNotContain("<Compta>", sheet);
            Assert.Contains("9.2.1", sheet);
            Assert.Contains("WhatsApp", sheet);
            Assert.Contains("Mots de passe de Google Chrome", sheet);
            Assert.Contains("Licences des logiciels payants", sheet);

            // Décoché, le Wi-Fi n'est pas passé sous silence : il reste à régler avec le client.
            Assert.Contains("Codes Wi-Fi : non exportés", sheet);

            var csv = Assert.Single(files.Written, entry => entry.Key.EndsWith(ReinstallSheet.SoftwareFileName, StringComparison.Ordinal)).Value;
            Assert.Contains("Sage <Compta>;9.2.1;Sage;tous les comptes;programme", csv);
            Assert.Contains("WhatsApp;2.2635.100.0;;ce compte;Microsoft Store", csv);
        }

        [Fact]
        public async Task Sans_analyse_la_fiche_dit_qu_elle_ne_peut_pas_lister_les_logiciels()
        {
            var files = WithChrome(new FakeFileSystemGateway());

            var preview = await Preview(files);
            await Execute(files, preview);

            Assert.Contains(preview.Measurements, line => line.Label == "Logiciels installés" && line.Kind == PreviewLineKind.Caution);
            Assert.Contains("n'a pas pu être établie", Sheet(files));
            Assert.DoesNotContain(files.Written, entry => entry.Key.EndsWith(ReinstallSheet.SoftwareFileName, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Un_fichier_tenu_ouvert_par_le_navigateur_est_nomme_dans_la_fiche()
        {
            var files = WithChrome(new FakeFileSystemGateway());
            files.LockedFiles.Add(Path.Combine(ChromeProfile, "History"));

            var outcome = await Run(files);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("Google Chrome / Default : 1 fichier(s) refusé(s) parce que le programme était ouvert", Sheet(files));
        }

        // ============================================================ inventaire du Store

        [Fact]
        public void Les_applications_du_store_sont_filtrees_et_gardent_leur_derniere_version()
        {
            const string repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            var registry = new ScriptedRegistry();

            void Package(string key, string root, string name)
            {
                registry.SubKeys.Add(key);
                registry.Strings[repository + "\\" + key + "|PackageRootFolder"] = root;
                registry.Strings[repository + "\\" + key + "|DisplayName"] = name;
            }

            Package("MSTeams_25100.1.1.1_x64__8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\MSTeams_25100", "Microsoft Teams");
            Package("MSTeams_25255.703.3978.7153_x64__8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\MSTeams_25255", "Microsoft Teams");
            Package("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\VCLibs", "Microsoft Visual C++ Runtime");
            Package("Microsoft.Windows.Photos_2024.1.0.0_x64__8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\Photos", "@{Microsoft.Windows.Photos?ms-resource://Photos/AppName}");
            Package("Microsoft.Windows.FilePicker_10.0.1.0_neutral__cw5n1h2txyewy", @"C:\Windows\SystemApps\FilePicker", "Sélecteur");
            Package("5319275A.WhatsAppDesktop_2.2635.100.0_neutral_split.scale-100_cv1g1gvanyjgm", @"C:\Program Files\WindowsApps\WA", "WhatsApp");
            Package("38985CA0.BO6DLC06BetaPack01_0.0.10.0_x64__5bkah9njm3e9g", @"C:\Program Files\WindowsApps\BO6", "BO6 DLC06 Beta Pack 01");

            var context = new ProbeContext(new FakePlatformInfo(), new FakeProcessRunner(), new FakeWmiGateway(), registry, NullLogger.Instance);
            var apps = SoftwareProbe.CollectStoreApps(context, CancellationToken.None);

            var teams = Assert.Single(apps);
            Assert.Equal("Microsoft Teams", teams.Name);
            Assert.Equal("25255.703.3978.7153", teams.Version);
        }

        // ============================================================ passerelle réelle

        [Fact]
        public void Le_balayage_ecarte_les_sous_dossiers_exclus_a_toute_profondeur_annoncee()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-exclusion-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Cache"));
                Directory.CreateDirectory(Path.Combine(root, @"Service Worker\CacheStorage"));
                Directory.CreateDirectory(Path.Combine(root, @"Service Worker\Database"));
                Directory.CreateDirectory(Path.Combine(root, @"Extensions\Cache"));
                Directory.CreateDirectory(Path.Combine(root, @"WebStorage\12\CacheStorage"));
                Directory.CreateDirectory(Path.Combine(root, @"WebStorage\12\IndexedDB"));
                File.WriteAllText(Path.Combine(root, @"WebStorage\12\CacheStorage\d"), "x");
                File.WriteAllText(Path.Combine(root, @"WebStorage\12\IndexedDB\e"), "x");
                File.WriteAllText(Path.Combine(root, "Bookmarks"), "{}");
                File.WriteAllText(Path.Combine(root, @"Cache\data_1"), "x");
                File.WriteAllText(Path.Combine(root, @"Service Worker\CacheStorage\a"), "x");
                File.WriteAllText(Path.Combine(root, @"Service Worker\Database\b"), "x");
                File.WriteAllText(Path.Combine(root, @"Extensions\Cache\c"), "x");

                var scan = new FileSystemGateway(NullLogger.Instance).Scan(
                    new DirectoryScanRequest(root) { ExcludeRelative = AppDataCatalog.ChromiumCaches },
                    CancellationToken.None);

                var names = scan.Value.Files.Select(file => file.Path.Substring(root.Length + 1)).ToList();

                Assert.Contains("Bookmarks", names);
                Assert.Contains(@"Service Worker\Database\b", names);

                // Relatif à la racine, pas un nom cherché partout : un dossier « Cache » rangé
                // dans une extension lui appartient, et part avec elle.
                Assert.Contains(@"Extensions\Cache\c", names);
                Assert.DoesNotContain(@"Cache\data_1", names);
                Assert.DoesNotContain(@"Service Worker\CacheStorage\a", names);

                // Le joker vaut un dossier numéroté par site, et seulement son cache.
                Assert.DoesNotContain(@"WebStorage\12\CacheStorage\d", names);
                Assert.Contains(@"WebStorage\12\IndexedDB\e", names);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        // ============================================================ outils

        private static FakeFileSystemGateway WithChrome(FakeFileSystemGateway files)
        {
            files.WithFile(ChromeData, Path.Combine(ChromeData, "Local State"))
                .WithFile(ChromeData, Path.Combine(ChromeData, @"Safe Browsing\liste.bin"))
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, "Bookmarks"))
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, "History"))
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, "Preferences"))
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, @"Cache\Cache_Data\f_000001"), 50_000_000)
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, @"Service Worker\CacheStorage\abc\1"), 80_000_000)
                .WithFile(ChromeProfile, Path.Combine(ChromeProfile, @"Service Worker\Database\000003.log"));

            var system = Path.Combine(ChromeData, "System Profile");
            files.Children[ChromeData] = new List<string> { ChromeProfile, system, Path.Combine(ChromeData, "Safe Browsing") };
            files.Files.Add(Path.Combine(ChromeProfile, "Preferences"));
            files.Files.Add(Path.Combine(system, "Preferences"));
            files.WithFile(system, Path.Combine(system, "Preferences"));
            return files;
        }

        private static void Profile(FakeFileSystemGateway files, string folder, string name, string xml)
        {
            var path = Path.Combine(folder, "Wi-Fi-" + name + ".xml");
            files.WithFile(folder, path, xml.Length);
            files.Texts[path] = xml;
        }

        private static string Wlan(string name, string authentication, string key, string? protection)
            => "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
               "<name>" + name + "</name><SSIDConfig><SSID><name>" + name + "</name></SSID></SSIDConfig>" +
               "<MSM><security><authEncryption><authentication>" + authentication + "</authentication></authEncryption>" +
               (key.Length == 0 ? string.Empty : "<sharedKey><keyType>passPhrase</keyType><protected>" + protection + "</protected>" + key + "</sharedKey>") +
               "</security></MSM></WLANProfile>";

        private static string Sheet(FakeFileSystemGateway files)
            => Assert.Single(files.Written, entry => entry.Key.EndsWith(ReinstallSheet.FileName, StringComparison.Ordinal)).Value;

        private static ActionContext Context(
            IFileSystemGateway files, IProcessRunner? processes = null, SystemSnapshot? snapshot = null,
            string? applications = null, string? wifi = null)
        {
            var parameters = new Dictionary<string, string> { [BackupUserDataAction.DestinationParameter] = Destination };
            if (applications != null) parameters[BackupUserDataAction.ApplicationsParameter] = applications;
            if (wifi != null) parameters[BackupUserDataAction.WifiParameter] = wifi;

            return ActionFakes.Context(files: files, processes: processes, snapshot: snapshot, parameters: parameters);
        }

        private static Task<ActionPreview> Preview(
            IFileSystemGateway files, IProcessRunner? processes = null, SystemSnapshot? snapshot = null,
            string? applications = null, string? wifi = null)
            => new BackupUserDataAction().PreviewAsync(Context(files, processes, snapshot, applications, wifi), CancellationToken.None);

        private static Task<ActionOutcome> Execute(IFileSystemGateway files, ActionPreview preview, IProcessRunner? processes = null)
            => new BackupUserDataAction().ExecuteAsync(Context(files, processes), preview, null, CancellationToken.None);

        private static async Task<ActionOutcome> Run(
            FakeFileSystemGateway files, IProcessRunner? processes = null, SystemSnapshot? snapshot = null, string? wifi = null)
        {
            var context = Context(files, processes, snapshot, wifi: wifi);
            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            return await action.ExecuteAsync(context, preview, null, CancellationToken.None);
        }

        /// <summary>Registre en mémoire : une liste de sous-clés et des chaînes par « clé|valeur ».</summary>
        private sealed class ScriptedRegistry : IRegistryGateway
        {
            public List<string> SubKeys { get; } = new List<string>();

            public Dictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
                => Strings.TryGetValue(subKey + "|" + valueName, out var value) ? value : null;

            public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => true;

            public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => hive == RegistryHive.CurrentUser ? SubKeys : (IReadOnlyList<string>)Array.Empty<string>();

            public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => Array.Empty<string>();
        }
    }
}
