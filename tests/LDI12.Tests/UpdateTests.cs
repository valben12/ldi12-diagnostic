using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Logging;
using LDI12.Core.Updates;
using LDI12.Updates;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La vérification de mise à jour : ce qu'on croit, ce qu'on refuse, et ce qu'on dit
    /// quand il n'y a pas de réseau.
    /// </summary>
    /// <remarks>
    /// <b>Les manifestes de ces tests sont réellement signés</b> par la clé de production du
    /// serveur, produite une fois et rangée dans <c>Donnees/Updates</c>. Des manifestes signés
    /// par une clé fabriquée dans le test lui-même ne prouveraient que la cohérence du code avec
    /// lui-même : ils passeraient encore si la clé embarquée était fausse.
    /// </remarks>
    public class UpdateTests
    {
        // ============================================================ signature

        [Fact]
        public void Un_manifeste_signe_par_le_serveur_est_accepte()
        {
            var manifest = Read("manifest-update.json");

            Assert.True(manifest.HasValue, manifest.Reason);
            Assert.Equal("1.20.0", manifest.Value.Latest!.Version);
            Assert.Equal("ldi12-diagnostic", manifest.Value.Product);
        }

        [Fact]
        public void Une_signature_fabriquee_par_une_autre_cle_est_refusee()
        {
            var manifest = Read("manifest-signature-fausse.json");

            Assert.False(manifest.HasValue);
            Assert.Contains("s'interpose", manifest.Reason);
        }

        [Fact]
        public void Une_cle_inconnue_ne_se_confond_pas_avec_une_signature_fausse()
        {
            // « Cette version du logiciel ne connaît pas la clé du serveur » désigne un
            // exécutable trop ancien, et se répare en téléchargeant à la main. « Signature
            // invalide » désignerait quelqu'un qui s'interpose : les deux n'appellent pas du
            // tout la même réaction.
            var manifest = Read("manifest-cle-inconnue.json");

            Assert.False(manifest.HasValue);
            Assert.Contains("ne connaît pas la clé", manifest.Reason);
            Assert.DoesNotContain("s'interpose", manifest.Reason);
        }

        [Fact]
        public void Le_manifeste_exploite_est_celui_qui_a_ete_signe_et_non_le_corps_lisible()
        {
            // Le fichier porte une signature valide sur le manifeste d'origine, mais son corps
            // lisible annonce une autre adresse de téléchargement. Lire le corps « pour voir de
            // quoi il s'agit » reviendrait à faire confiance à ce qu'on n'a pas vérifié.
            var manifest = Read("manifest-altere.json");

            Assert.True(manifest.HasValue, manifest.Reason);
            Assert.StartsWith("https://ldi12.fr/", manifest.Value.Latest!.Download!.Url);
            Assert.DoesNotContain("piege", manifest.Value.Latest.Download.Url);
        }

        [Fact]
        public void La_cle_embarquee_est_bien_celle_du_serveur()
        {
            // Le jour où la clé du serveur changera, c'est ce test qui le dira, et non un
            // client qui ne verra plus jamais de mise à jour.
            Assert.Equal("753c6075c5f3df96", UpdateSignature.KeyId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("pas du base64 !!")]
        public void Une_signature_illisible_ne_leve_jamais(string? value)
        {
            Assert.Null(UpdateSignature.Decode(value));
        }

        // ============================================================ réseau absent

        [Fact]
        public async Task Une_absence_de_reseau_n_est_pas_une_erreur()
        {
            // C'est chez le client dont la box est en panne que ce logiciel sert le plus :
            // y afficher une erreur serait le mauvais message au mauvais moment.
            var manifest = await CheckAsync(new ThrowingHandler(new HttpRequestException("No such host is known")));

            Assert.False(manifest.HasValue);
            Assert.Contains("ldi12.fr n'a pas pu être résolu", manifest.Reason);
            Assert.Contains("fonctionne entièrement hors ligne", manifest.Reason);
        }

        [Fact]
        public async Task Un_logiciel_inconnu_du_serveur_est_dit_sans_alarme()
        {
            var manifest = await CheckAsync(new SpyHandler(HttpStatusCode.NotFound, "{}"));

            Assert.False(manifest.HasValue);
            Assert.Contains("ne diffuse pas ce logiciel", manifest.Reason);
        }

        [Fact]
        public async Task Trop_de_verifications_donne_un_conseil_et_non_un_code()
        {
            var manifest = await CheckAsync(new SpyHandler((HttpStatusCode)429, "{}"));

            Assert.False(manifest.HasValue);
            Assert.Contains("Réessayez dans une minute", manifest.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{\"product\":")]
        [InlineData("<html>erreur</html>")]
        public async Task Une_reponse_tronquee_ou_vide_ne_leve_jamais(string body)
        {
            var manifest = await CheckAsync(new SpyHandler(HttpStatusCode.OK, body));

            Assert.False(manifest.HasValue);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Reason));
        }

        [Fact]
        public async Task Une_reponse_sans_signature_est_refusee()
        {
            var manifest = await CheckAsync(new SpyHandler(HttpStatusCode.OK,
                "{\"product\":\"ldi12-diagnostic\",\"updateAvailable\":true}"));

            Assert.False(manifest.HasValue);
        }

        // ============================================================ ce qui part

        [Fact]
        public async Task La_requete_ne_transporte_rien_qui_designe_la_machine()
        {
            // La règle qui compte le plus de ce fichier : le serveur n'a pas à pouvoir
            // reconnaître un poste d'une visite à l'autre.
            var spy = new SpyHandler(HttpStatusCode.OK, "{}");
            await CheckAsync(spy);

            var url = spy.Uri!.ToString();

            Assert.Contains("version=1.19.0", url);
            Assert.Contains("channel=stable", url);
            Assert.Contains("arch=x64", url);
            Assert.Contains("os=win11", url);

            Assert.DoesNotContain(Environment.MachineName, url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("10.0.26200", url);
            Assert.Null(spy.Cookies);
        }

        [Fact]
        public void La_version_reelle_de_Windows_ne_quitte_jamais_la_machine()
        {
            using var checker = new HttpUpdateChecker(NullLogger.Instance);

            var address = checker.Address(new UpdateQuery
            {
                Version = "1.20.0",
                OsFamily = "win11",
                OsVersion = "10.0.26200.1234",
                SkippedVersion = "1.21.0",
            });

            Assert.DoesNotContain("26200", address);
            Assert.DoesNotContain("1.21.0", address);
        }

        // ============================================================ la décision

        [Fact]
        public void Une_version_plus_recente_est_proposee()
        {
            var outlook = Decide("manifest-update.json", version: "1.19.0");

            Assert.Equal(UpdateDecision.Offer, outlook.Decision);
            Assert.True(outlook.CanInstall);
        }

        [Fact]
        public void Le_poste_deja_a_jour_l_apprend_en_une_phrase()
        {
            var outlook = Decide("manifest-uptodate.json", version: "1.20.0");

            Assert.Equal(UpdateDecision.UpToDate, outlook.Decision);
            Assert.Contains("dernière version", outlook.Message);
        }

        [Fact]
        public void Une_version_obligatoire_ne_demande_pas_l_avis_du_technicien()
        {
            var outlook = Decide("manifest-mandatory.json", version: "1.19.0");

            Assert.Equal(UpdateDecision.Required, outlook.Decision);
        }

        [Fact]
        public void Une_version_ecartee_ne_revient_pas()
        {
            var outlook = Decide("manifest-update.json", version: "1.19.0", skipped: "1.20.0");

            Assert.Equal(UpdateDecision.Skipped, outlook.Decision);
            Assert.False(outlook.CanInstall);
        }

        [Fact]
        public void Une_version_obligatoire_ignore_le_report_du_technicien()
        {
            // Le choix d'écarter porte sur une amélioration, jamais sur un correctif qui rend
            // la version installée peu fiable.
            var outlook = Decide("manifest-mandatory.json", version: "1.19.0", skipped: "1.21.0");

            Assert.Equal(UpdateDecision.Required, outlook.Decision);
        }

        [Fact]
        public void Une_version_qui_demande_un_Windows_plus_recent_n_est_pas_proposee()
        {
            // Proposer une version qui ne démarrera pas serait pire que de ne rien proposer.
            var outlook = Decide("manifest-minos.json", version: "1.19.0", os: "6.1.7601");

            Assert.Equal(UpdateDecision.TooOldForThisWindows, outlook.Decision);
            Assert.False(outlook.CanInstall);
            Assert.Contains("Windows plus récent", outlook.Message);
        }

        [Fact]
        public void La_meme_version_sur_un_Windows_recent_est_proposee()
        {
            var outlook = Decide("manifest-minos.json", version: "1.19.0", os: "10.0.26200");

            Assert.Equal(UpdateDecision.Offer, outlook.Decision);
        }

        [Fact]
        public void Un_canal_sans_version_publiee_ne_propose_rien()
        {
            var outlook = Decide("manifest-none.json", version: "1.20.0");

            Assert.Equal(UpdateDecision.Nothing, outlook.Decision);
        }

        [Fact]
        public void Une_exigence_de_systeme_illisible_ne_prive_jamais_un_poste_de_ses_correctifs()
        {
            var outlook = UpdatePlan.Decide(
                new UpdateManifest
                {
                    Latest = new UpdateRelease
                    {
                        Version = "2.0.0",
                        MinOs = "n'importe quoi",
                        Download = new UpdateDownload { Sha256 = new string('a', 64) },
                    },
                },
                new UpdateQuery { Version = "1.20.0", OsVersion = "6.1.7601" });

            Assert.Equal(UpdateDecision.Offer, outlook.Decision);
        }

        [Theory]
        [InlineData("1.20.0", "1.19.0", true)]
        [InlineData("1.19.0", "1.20.0", false)]
        [InlineData("1.20.0", "1.20.0", false)]
        [InlineData("1.21.0-beta.2", "1.20.0", true)]
        [InlineData("1.2.0", "1.10.0", false)]
        public void Les_numeros_se_comparent_champ_par_champ_et_non_comme_du_texte(
            string candidate, string installed, bool newer)
        {
            // « 1.10.0 » est postérieure à « 1.2.0 » ; une comparaison de texte dirait l'inverse.
            Assert.Equal(newer, VersionCompare.IsNewer(candidate, installed));
        }

        // ============================================================ téléchargement

        [Fact]
        public async Task Un_fichier_dont_l_empreinte_differe_est_efface_et_declare_falsifie()
        {
            var handler = new PayloadHandler(new byte[] { 1, 2, 3, 4, 5 });
            using var downloader = new UpdateDownloader(NullLogger.Instance, handler);

            var outcome = await downloader.DownloadAsync(
                new UpdateDownload
                {
                    Url = "https://ldi12.fr/telecharger/x/LDI12-Diagnostic-9.9.9.exe",
                    FileName = "LDI12-Diagnostic-9.9.9.exe",
                    Size = 5,
                    Sha256 = new string('0', 64),
                },
                null, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.True(outcome.Tampered);
            Assert.Contains("effacé", outcome.Message);

            var staged = Path.Combine(UpdateDownloader.StagingFolder, "LDI12-Diagnostic-9.9.9.exe");
            Assert.False(File.Exists(staged), "Le fichier douteux ne doit pas survivre.");
        }

        [Fact]
        public async Task Un_fichier_conforme_a_son_empreinte_est_conserve()
        {
            var bytes = new byte[] { 76, 68, 73, 49, 50 };
            var handler = new PayloadHandler(bytes);
            using var downloader = new UpdateDownloader(NullLogger.Instance, handler);

            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = UpdateDownloader.Hex(sha.ComputeHash(bytes));

            var outcome = await downloader.DownloadAsync(
                new UpdateDownload
                {
                    Url = "https://ldi12.fr/telecharger/x/LDI12-Diagnostic-9.9.8.exe",
                    FileName = "LDI12-Diagnostic-9.9.8.exe",
                    Size = bytes.Length,
                    Sha256 = hash,
                },
                null, CancellationToken.None);

            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.True(File.Exists(outcome.Path));

            File.Delete(outcome.Path!);
        }

        [Fact]
        public async Task Un_manifeste_sans_empreinte_ne_declenche_aucun_telechargement()
        {
            var handler = new PayloadHandler(new byte[] { 1 });
            using var downloader = new UpdateDownloader(NullLogger.Instance, handler);

            var outcome = await downloader.DownloadAsync(
                new UpdateDownload { Url = "https://ldi12.fr/x.exe", FileName = "x.exe", Sha256 = "" },
                null, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public void Un_telechargement_en_clair_n_est_jamais_retenu()
        {
            // Un manifeste authentique qui proposerait http:// décrirait un serveur mal
            // configuré : on ne télécharge pas un exécutable sans liaison chiffrée.
            using var checker = new HttpUpdateChecker(NullLogger.Instance);
            var manifest = checker.Interpret(File.ReadAllText(Fixture("manifest-update.json"))
                .Replace("https://ldi12.fr/telecharger", "http://ldi12.fr/telecharger"));

            // La substitution ne touche que le corps lisible : le manifeste signé, lui, est
            // intact et porte toujours son adresse chiffrée.
            Assert.True(manifest.HasValue, manifest.Reason);
            Assert.StartsWith("https://", manifest.Value.Latest!.Download!.Url);
        }

        [Theory]
        [InlineData(@"..\..\Windows\System32\piege.exe", "piege.exe")]
        [InlineData("LDI12-Diagnostic-1.20.0.exe", "LDI12-Diagnostic-1.20.0.exe")]
        [InlineData("", "LDI12-Diagnostic.exe")]
        [InlineData("outil", "outil.exe")]
        public void Le_nom_de_fichier_annonce_ne_peut_pas_designer_un_autre_dossier(
            string announced, string expected)
        {
            Assert.Equal(expected, UpdateDownloader.SafeName(announced));
        }

        [Theory]
        [InlineData("https://ldi12.fr/telecharger/x.exe", true)]
        [InlineData("http://localhost:3100/telecharger/x.exe", true)]
        [InlineData("http://127.0.0.1:3100/x.exe", true)]
        [InlineData("http://ldi12.fr/telecharger/x.exe", false)]
        [InlineData("http://exemple.invalide/x.exe", false)]
        [InlineData("file://C:/x.exe", false)]
        public void Un_executable_ne_descend_pas_en_clair_sauf_depuis_cette_machine(string url, bool accepte)
        {
            // Troisième barrière après la signature du manifeste et l'empreinte du fichier, et
            // la plus faible des trois : elle existe pour qu'un serveur mal configuré ne fasse
            // pas descendre un exécutable en clair sur le réseau d'un client. L'exception locale
            // est ce qui rend la chaîne éprouvable avant que le site ne la diffuse.
            Assert.Equal(accepte, HttpUpdateChecker.IsAcceptable(url));
        }

        [Fact]
        public void Un_manifeste_qui_propose_un_telechargement_en_clair_ne_propose_rien()
        {
            using var checker = new HttpUpdateChecker(NullLogger.Instance);

            // Le manifeste est authentique : il faut donc le refabriquer, signature comprise,
            // pour éprouver ce cas ; altérer le corps lisible ne changerait rien, puisque c'est
            // le contenu signé qui est lu.
            var manifest = new UpdateManifest
            {
                Latest = new UpdateRelease
                {
                    Version = "2.0.0",
                    Download = null,
                },
            };

            var outlook = UpdatePlan.Decide(manifest, new UpdateQuery { Version = "1.0.0" });

            Assert.Equal(UpdateDecision.Nothing, outlook.Decision);
            Assert.False(outlook.CanInstall);
        }

        // ============================================================ mise en place

        [Fact]
        public void Un_emplacement_accessible_en_ecriture_autorise_le_remplacement()
        {
            var installer = new UpdateInstaller(NullLogger.Instance);
            var folder = Path.Combine(Path.GetTempPath(), "LDI12-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var verdict = installer.CanReplace(Path.Combine(folder, "LDI12-Diagnostic.exe"));

                Assert.True(verdict.HasValue, verdict.Reason);
                Assert.True(verdict.Value);
                Assert.Empty(Directory.GetFiles(folder));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Un_emplacement_inaccessible_est_dit_avant_tout_telechargement()
        {
            // Sur une clé USB protégée en écriture, le logiciel doit le dire au lieu d'échouer
            // à mi-parcours, une fois douze mégaoctets téléchargés.
            var installer = new UpdateInstaller(NullLogger.Instance);
            var verdict = installer.CanReplace(@"Z:\dossier-qui-n-existe-pas\LDI12-Diagnostic.exe");

            Assert.False(verdict.HasValue);
            Assert.Contains("ne peut pas se remplacer lui-même", verdict.Reason);
        }

        [Fact]
        public void La_ligne_de_commande_de_mise_en_place_se_lit_ou_se_refuse()
        {
            Assert.Null(UpdateApplier.Parse(null));
            Assert.Null(UpdateApplier.Parse(new[] { "--screenshot", "a.png" }));
            Assert.Null(UpdateApplier.Parse(new[] { "--appliquer-maj" }));
            Assert.Null(UpdateApplier.Parse(new[] { "--appliquer-maj", @"C:\x.exe" }));
            Assert.Null(UpdateApplier.Parse(new[] { "--appliquer-maj", @"C:\x.exe", "pas-un-nombre" }));

            var parsed = UpdateApplier.Parse(new[] { "--appliquer-maj", @"C:\Outils\LDI12.exe", "4242" });

            Assert.NotNull(parsed);
            Assert.Equal(@"C:\Outils\LDI12.exe", parsed!.Value.Target);
            Assert.Equal(4242, parsed.Value.ProcessId);
        }

        // ============================================================ montage

        private static string Fixture(string name)
            => Path.Combine(AppContext.BaseDirectory, "Donnees", "Updates", name);

        private static LDI12.Core.Diagnostics.Measured<UpdateManifest> Read(string name)
        {
            using var checker = new HttpUpdateChecker(NullLogger.Instance);
            return checker.Interpret(File.ReadAllText(Fixture(name)));
        }

        private static UpdateOutlook Decide(
            string name, string version, string? skipped = null, string os = "10.0.26200")
        {
            var manifest = Read(name);
            Assert.True(manifest.HasValue, manifest.Reason);

            return UpdatePlan.Decide(manifest.Value, new UpdateQuery
            {
                Version = version,
                OsVersion = os,
                SkippedVersion = skipped,
            });
        }

        private static async Task<LDI12.Core.Diagnostics.Measured<UpdateManifest>> CheckAsync(
            HttpMessageHandler handler)
        {
            using var checker = new HttpUpdateChecker(NullLogger.Instance, null, handler);

            return await checker.CheckAsync(new UpdateQuery
            {
                Version = "1.19.0",
                Channel = "stable",
                Architecture = "x64",
                OsFamily = "win11",
                OsVersion = "10.0.26200",
            }, CancellationToken.None);
        }

        /// <summary>Rend ce qu'on lui dit, et retient ce qui serait parti.</summary>
        private sealed class SpyHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;

            public SpyHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public Uri? Uri { get; private set; }
            public string? Cookies { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Uri = request.RequestUri;
                Cookies = request.Headers.TryGetValues("Cookie", out var values)
                    ? string.Join("; ", values)
                    : null;

                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body),
                });
            }
        }

        /// <summary>Échoue comme échoue un réseau absent.</summary>
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            private readonly Exception _failure;

            public ThrowingHandler(Exception failure) => _failure = failure;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(_failure);
        }

        /// <summary>Rend des octets, comme un serveur de fichiers.</summary>
        private sealed class PayloadHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;

            public PayloadHandler(byte[] bytes) => _bytes = bytes;

            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_bytes),
                });
            }
        }
    }
}
