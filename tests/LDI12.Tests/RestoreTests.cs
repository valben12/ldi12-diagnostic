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
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La restauration d'une sauvegarde : l'aller-retour d'une réinstallation.
    /// </summary>
    /// <remarks>
    /// Demandé depuis l'atelier : « pouvoir d'un clic récupérer les données sauvegardées sur un
    /// disque externe, pour tout réimporter dans Chrome et tout ». La promesse tenue ici est celle
    /// de la sauvegarde, dans l'autre sens : rien ne disparaît de la machine où l'on restaure.
    /// </remarks>
    public class RestoreTests
    {
        private const string Drive = @"E:\";
        private const string Backup = @"E:\LDI12-Sauvegarde-PC-ACCUEIL-2026-09-14-1716";

        private static readonly RestoreTargets Targets = new RestoreTargets
        {
            Desktop = @"C:\Users\Nouveau\Desktop",
            Documents = @"C:\Users\Nouveau\Documents",
            Pictures = @"C:\Users\Nouveau\Pictures",
            Music = @"C:\Users\Nouveau\Music",
            Videos = @"C:\Users\Nouveau\Videos",
            UserProfile = @"C:\Users\Nouveau",
            LocalAppData = @"C:\Users\Nouveau\AppData\Local",
            RoamingAppData = @"C:\Users\Nouveau\AppData\Roaming",
        };

        private static readonly string ChromeData = Path.Combine(Targets.LocalAppData, @"Google\Chrome\User Data");

        // ============================================================ trouver la sauvegarde

        [Fact]
        public async Task Le_disque_suffit_la_sauvegarde_la_plus_recente_est_retrouvee()
        {
            var files = Populated();
            const string older = @"E:\LDI12-Sauvegarde-PC-ACCUEIL-2026-03-02-0915";
            files.WithFile(older, Path.Combine(older, "manifeste.csv"));
            files.Files.Add(Path.Combine(older, "manifeste.csv"));
            files.Children[Drive] = new List<string> { older, Backup, @"E:\Photos" };
            files.Directories.Add(Drive);

            var preview = await Preview(files, source: Drive);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Equal(Backup, Assert.IsType<RestorePlan>(preview.Plan).Backup);
            Assert.Contains(preview.Measurements, line => line.Label == "Autres sauvegardes" && line.Value.StartsWith("1 plus ancienne", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Un_dossier_qui_n_est_pas_une_sauvegarde_est_refuse()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(@"E:\Photos");

            var preview = await Preview(files, source: @"E:\Photos");

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains("Aucune sauvegarde LDI12", preview.Blocker);
        }

        // ============================================================ dossiers personnels

        [Fact]
        public async Task Les_documents_reviennent_a_leur_place_sans_rien_ecraser()
        {
            var files = Populated();
            files.Occupied.Add(Path.Combine(Targets.Documents, "contrat.pdf"));

            var outcome = await Run(files);

            Assert.Contains(files.Copied, copy => copy.Destination == Path.Combine(Targets.Desktop, "note.txt"));
            Assert.DoesNotContain(files.Copied, copy => copy.Destination.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase));

            // Un fichier différent déjà là n'est pas remplacé : il est compté et signalé.
            Assert.DoesNotContain(files.Copied, copy => copy.Destination.EndsWith("contrat.pdf", StringComparison.Ordinal));
            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);

            // Et rien n'est supprimé, nulle part.
            Assert.Empty(files.Deleted);
        }

        // ============================================================ navigateurs

        [Fact]
        public async Task Le_profil_chrome_de_l_installation_neuve_est_mis_de_cote_puis_remplace()
        {
            var files = Populated();
            var fresh = Path.Combine(ChromeData, "Default");
            files.Directories.Add(fresh);

            var outcome = await Run(files);

            var moved = Assert.Single(files.Moved);
            Assert.Equal(fresh, moved.Source);
            Assert.StartsWith(fresh + ".avant-restauration-", moved.Destination, StringComparison.Ordinal);
            Assert.Contains(files.Copied, copy => copy.Destination == Path.Combine(fresh, "Bookmarks"));
            Assert.Contains(outcome.Details, line => line.Contains("mis de côté"));
        }

        [Fact]
        public async Task Un_dossier_qui_refuse_d_etre_mis_de_cote_n_est_pas_melange()
        {
            var files = Populated();
            var fresh = Path.Combine(ChromeData, "Default");
            files.Directories.Add(fresh);
            files.Unmovable.Add(fresh);

            var outcome = await Run(files);

            Assert.DoesNotContain(files.Copied, copy => copy.Destination.StartsWith(fresh + "\\", StringComparison.Ordinal));
            Assert.Contains(outcome.Details, line => line.Contains("Google Chrome / Default : non restauré"));
        }

        [Fact]
        public async Task La_liste_des_profils_garde_la_cle_de_la_nouvelle_installation()
        {
            var files = Populated();
            var localState = Path.Combine(ChromeData, "Local State");
            files.Files.Add(localState);
            files.Texts[localState] = "{\"os_crypt\":{\"encrypted_key\":\"NEUVE\"},\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Personne 1\"}}}}";

            await Run(files);

            var merged = JObject.Parse(files.Written[localState]);
            Assert.Equal("NEUVE", (string?)merged.SelectToken("os_crypt.encrypted_key"));
            Assert.Equal("Cabinet", (string?)merged.SelectToken("profile.info_cache.Default.name"));

            // L'original est gardé à côté avant d'être réécrit.
            Assert.Contains(files.Written.Keys, key => key.StartsWith(localState + ".avant-restauration-", StringComparison.Ordinal));
        }

        [Fact]
        public void Sans_navigateur_lance_la_liste_des_profils_arrive_sans_l_ancienne_cle()
        {
            var merged = JObject.Parse(ChromiumLocalState.Merge(
                "{\"os_crypt\":{\"encrypted_key\":\"ANCIENNE\"},\"profile\":{\"info_cache\":{\"Profile 3\":{\"name\":\"Travail\"}}}}",
                null, new[] { "Profile 3" })!);

            Assert.Null(merged["os_crypt"]);
            Assert.Equal("Travail", (string?)merged.SelectToken("profile.info_cache['Profile 3'].name"));
        }

        [Fact]
        public async Task Un_navigateur_ouvert_n_est_pas_restaure_et_le_reste_passe()
        {
            var files = Populated();
            var processes = new ScriptedProcessRunner(request => request.FileName == "tasklist.exe"
                ? ActionFakes.Result(output: "\"chrome.exe\",\"4242\",\"Console\",\"1\",\"250 000 Ko\"\r\n")
                : ActionFakes.Result());

            var preview = await Preview(files, processes: processes);
            await new Executeur(files, processes).Execute(preview);

            Assert.Contains(preview.Measurements, line => line.Label == "Programmes ouverts" && line.Value.StartsWith("Google Chrome", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Copied, copy => copy.Destination.StartsWith(ChromeData, StringComparison.Ordinal));
            Assert.Contains(files.Copied, copy => copy.Destination == Path.Combine(Targets.Desktop, "note.txt"));
        }

        // ============================================================ autres applications

        [Fact]
        public async Task Les_archives_outlook_arrivent_dans_les_documents()
        {
            var files = Populated();
            var data = Path.Combine(Backup, @"Applications\Outlook\Fichiers de données");
            files.WithFile(data, Path.Combine(data, "archives 2019.pst"));
            files.Children[Path.Combine(Backup, "Applications")].Add(Path.Combine(Backup, @"Applications\Outlook"));
            files.Directories.Add(Path.Combine(Backup, @"Applications\Outlook"));

            await Run(files);

            Assert.Contains(files.Copied, copy => copy.Destination == Path.Combine(Targets.Documents, @"Fichiers Outlook\archives 2019.pst"));
        }

        [Fact]
        public async Task Ce_qui_ne_peut_pas_revenir_est_dit_et_laisse_dans_la_sauvegarde()
        {
            var files = Populated();
            var applications = Path.Combine(Backup, "Applications");
            foreach (var name in new[] { "Pense-bêtes", "Logiciel inconnu" })
            {
                var folder = Path.Combine(applications, name);
                files.WithFile(folder, Path.Combine(folder, "donnees.bin"));
                files.Children[applications].Add(folder);
            }

            var preview = await Preview(files);

            Assert.Contains(preview.WillNotDo, line => line.StartsWith("Pense-bêtes : l'application n'est pas encore installée", StringComparison.Ordinal));
            Assert.Contains(preview.WillNotDo, line => line.Contains("Logiciel inconnu") && line.Contains("non reconnu"));
        }

        // ============================================================ Wi-Fi

        [Fact]
        public async Task Les_profils_wifi_sont_reimportes_pour_ce_compte_et_les_refus_comptes()
        {
            var files = Populated();
            var wifi = Path.Combine(Backup, "Wi-Fi");
            files.WithFile(wifi, Path.Combine(wifi, "Wi-Fi-Maison.xml"));
            files.WithFile(wifi, Path.Combine(wifi, "Wi-Fi-Bureau.xml"));

            var processes = new ScriptedProcessRunner(request =>
                request.FileName == "netsh.exe" && request.Arguments.Contains("Bureau")
                    ? ActionFakes.Result(exitCode: 1, output: "La clé du profil est invalide.")
                    : ActionFakes.Result());

            var outcome = await Run(files, processes: processes);

            var imports = processes.Requests.Where(request => request.FileName == "netsh.exe").ToList();
            Assert.Equal(2, imports.Count);
            Assert.All(imports, request => Assert.Contains("user=current", request.Arguments));
            Assert.Contains(outcome.Details, line => line.StartsWith("Wi-Fi : 1 profil(s) réimporté(s), 1 refusé(s) par Windows (Bureau)", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Decocher_le_wifi_ne_lance_aucun_import()
        {
            var files = Populated();
            var wifi = Path.Combine(Backup, "Wi-Fi");
            files.WithFile(wifi, Path.Combine(wifi, "Wi-Fi-Maison.xml"));
            var processes = new ScriptedProcessRunner(_ => ActionFakes.Result());

            await Run(files, processes: processes, wifi: "0");

            Assert.DoesNotContain(processes.Requests, request => request.FileName == "netsh.exe");
        }

        // ============================================================ logiciels

        [Fact]
        public async Task Les_logiciels_de_l_ancienne_installation_absents_ici_sont_listes()
        {
            var files = Populated();
            var csv = Path.Combine(Backup, ReinstallSheet.SoftwareFileName);
            files.Texts[csv] = "nom;version;éditeur;installé pour;origine\r\n" +
                               "7-Zip 23.01 (x64);23.01;Igor Pavlov;tous les comptes;programme\r\n" +
                               "\"Sage; Compta\";9.2;Sage;tous les comptes;programme\r\n" +
                               "WhatsApp;2.2635.100.0;;ce compte;Microsoft Store\r\n";

            var snapshot = new SystemSnapshot
            {
                Windows = new WindowsSnapshot
                {
                    Software = new SoftwareInventory
                    {
                        Programs = new[] { new InstalledProgram { Name = "7-Zip 24.08 (x64)", Version = "24.08" } },
                    },
                },
            };

            var preview = await Preview(files, snapshot: snapshot);

            var plan = Assert.IsType<RestorePlan>(preview.Plan);
            Assert.Equal(new[] { "Sage; Compta", "WhatsApp" }, plan.MissingSoftware);
            Assert.Contains(preview.WillNotDo, line => line == "À réinstaller : Sage; Compta");
        }

        // ============================================================ passerelle réelle

        [Fact]
        public void Mettre_un_dossier_de_cote_refuse_d_ecraser_une_destination()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-deplacement-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Default"));
                Directory.CreateDirectory(Path.Combine(root, "Default.avant"));
                File.WriteAllText(Path.Combine(root, @"Default\Bookmarks"), "{}");
                var gateway = new FileSystemGateway(NullLogger.Instance);

                Assert.False(gateway.MoveDirectory(Path.Combine(root, "Default"), Path.Combine(root, "Default.avant")));
                Assert.True(File.Exists(Path.Combine(root, @"Default\Bookmarks")));

                Assert.True(gateway.MoveDirectory(Path.Combine(root, "Default"), Path.Combine(root, "Default.apres")));
                Assert.True(File.Exists(Path.Combine(root, @"Default.apres\Bookmarks")));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        [Theory]
        [InlineData("7-Zip 23.01 (x64)", "7-zip")]
        [InlineData("Battlestate Games Launcher 14.7.1.4236", "battlestate games launcher")]
        [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "microsoft visual c++ 2015-2022 redistributable -")]
        [InlineData("Notepad++ v8.6.4", "notepad++")]
        public void Un_nom_de_logiciel_se_rapproche_sans_sa_version(string name, string expected)
            => Assert.Equal(expected, RestoreUserDataAction.Normalize(name));

        // ============================================================ outils

        /// <summary>Une sauvegarde de 1.25.0 : un bureau et un profil Chrome.</summary>
        private static FakeFileSystemGateway Populated()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Backup);
            files.Files.Add(Path.Combine(Backup, "manifeste.csv"));

            var bureau = Path.Combine(Backup, "Bureau");
            files.WithFile(bureau, Path.Combine(bureau, "note.txt"))
                .WithFile(bureau, Path.Combine(bureau, "desktop.ini"));

            var documents = Path.Combine(Backup, "Documents");
            files.WithFile(documents, Path.Combine(documents, "contrat.pdf"));

            var applications = Path.Combine(Backup, "Applications");
            var chrome = Path.Combine(applications, "Google Chrome");
            var profile = Path.Combine(chrome, "Default");
            files.WithFile(profile, Path.Combine(profile, "Bookmarks"))
                .WithFile(profile, Path.Combine(profile, "Preferences"));

            files.Directories.Add(applications);
            files.Directories.Add(chrome);
            files.Children[applications] = new List<string> { chrome };
            files.Children[chrome] = new List<string> { profile };

            var localState = Path.Combine(chrome, "Local State");
            files.Files.Add(localState);
            files.Texts[localState] = "{\"os_crypt\":{\"encrypted_key\":\"ANCIENNE\"},\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Cabinet\"}}}}";

            return files;
        }

        private static ActionContext Context(
            IFileSystemGateway files, string source, IProcessRunner? processes, SystemSnapshot? snapshot, string? wifi)
        {
            var parameters = new Dictionary<string, string> { [RestoreUserDataAction.SourceParameter] = source };
            if (wifi != null) parameters[RestoreUserDataAction.WifiParameter] = wifi;
            return ActionFakes.Context(files: files, processes: processes, snapshot: snapshot, parameters: parameters);
        }

        private static Task<ActionPreview> Preview(
            IFileSystemGateway files, string source = Backup, IProcessRunner? processes = null,
            SystemSnapshot? snapshot = null, string? wifi = null)
            => new RestoreUserDataAction(Targets).PreviewAsync(Context(files, source, processes, snapshot, wifi), CancellationToken.None);

        private static async Task<ActionOutcome> Run(
            FakeFileSystemGateway files, IProcessRunner? processes = null, string? wifi = null)
        {
            var context = Context(files, Backup, processes, null, wifi);
            var action = new RestoreUserDataAction(Targets);
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            return await action.ExecuteAsync(context, preview, null, CancellationToken.None);
        }

        /// <summary>Exécute un relevé déjà fait, avec les mêmes faux.</summary>
        private sealed class Executeur
        {
            private readonly IFileSystemGateway _files;
            private readonly IProcessRunner _processes;

            public Executeur(IFileSystemGateway files, IProcessRunner processes)
            {
                _files = files;
                _processes = processes;
            }

            public Task<ActionOutcome> Execute(ActionPreview preview)
                => new RestoreUserDataAction(Targets).ExecuteAsync(
                    Context(_files, Backup, _processes, null, null), preview, null, CancellationToken.None);
        }
    }
}
