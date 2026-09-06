using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Logging;
using LDI12.Core.Publishing;
using LDI12.Publishing.Klarvi;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La publication vers Klarvi : la seule partie du logiciel qui fasse sortir des données de
    /// la machine du client.
    /// </summary>
    public class PublishingTests
    {
        [Fact]
        public void Sans_configuration_rien_ne_part_et_la_raison_nomme_le_fichier_attendu()
        {
            // L'état par défaut du logiciel est hors ligne : l'absence de configuration n'est pas
            // une panne, et le dire ainsi évite de chercher une erreur là où il n'y en a pas.
            var absent = Path.Combine(Path.GetTempPath(), "klarvi-inexistant-" + Guid.NewGuid() + ".json");
            var publisher = new KlarviPublisher(NullLogger.Instance, absent);

            Assert.False(publisher.Configured.Or(false));
            Assert.Contains("klarvi-inexistant", publisher.Configured.Reason);
            Assert.Contains("rien n'est envoyé", publisher.Configured.Reason);
        }

        [Fact]
        public async Task Un_publieur_non_configure_ne_touche_jamais_au_reseau()
        {
            var handler = new SpyHandler(HttpStatusCode.OK, "{}");
            var publisher = new KlarviPublisher(
                NullLogger.Instance, null, "Klarvi n'est pas configuré sur cette machine.", handler);

            var outcome = await publisher.PublishAsync(Request(), null, CancellationToken.None);

            Assert.False(outcome.Published);
            Assert.Equal(0, handler.Calls);
        }

        [Theory]
        [InlineData("http://klarvi.example", "HTTPS")]
        [InlineData("", "adresse de service")]
        public void Une_adresse_qui_ne_protege_pas_les_donnees_est_refusee(string url, string expected)
        {
            // Un dossier de diagnostic et un jeton d'accès sur le même réseau qu'un client :
            // en clair, les deux seraient lisibles par n'importe qui sur le point d'accès.
            var settings = new KlarviSettings { BaseUrl = url, Token = "jeton" };
            var publisher = new KlarviPublisher(NullLogger.Instance, settings, null, null);

            Assert.False(publisher.Configured.Or(false));
            Assert.Contains(expected, publisher.Configured.Reason);
        }

        [Fact]
        public void Ce_qui_part_est_decrit_ligne_par_ligne_et_ne_contient_pas_le_jeton()
        {
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, null);
            var lines = publisher.DescribeWhatIsSent(Request());

            Assert.Contains(lines, line => line.Contains("PC-ATELIER"));
            Assert.Contains(lines, line => line.Contains("DOSSIER-42"));
            Assert.Contains(lines, line => line.Contains("Bilan client"));
            Assert.Contains(lines, line => line.Contains("Rien d'autre"));

            // Le jeton d'accès de l'atelier n'a rien à faire dans une liste qu'on lit à l'écran
            // devant un client.
            Assert.DoesNotContain(lines, line => line.Contains("jeton-secret"));
        }

        [Fact]
        public void L_empreinte_est_annoncee_pour_ce_qu_elle_est()
        {
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, null);
            var line = Assert.Single(publisher.DescribeWhatIsSent(Request()).Where(l => l.Contains("empreinte")));

            Assert.Contains("condensé", line);
            Assert.Contains("pas un numéro de série", line);
        }

        [Fact]
        public async Task Un_envoi_porte_le_jeton_et_le_document_annonces()
        {
            var handler = new SpyHandler(HttpStatusCode.OK, "{\"reference\":\"KLV-2026-118\"}");
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, handler);

            var outcome = await publisher.PublishAsync(Request(), null, CancellationToken.None);

            Assert.True(outcome.Published, outcome.Summary);
            Assert.Equal("KLV-2026-118", outcome.Reference);
            Assert.Contains("KLV-2026-118", outcome.Summary);

            Assert.Equal("Bearer", handler.Scheme);
            Assert.Equal("jeton-secret", handler.Parameter);
            Assert.Contains("interventions", handler.Uri!.AbsolutePath);
            Assert.Contains("PC-ATELIER", handler.Body);
            Assert.Contains("bilan.html", handler.Body);
        }

        [Fact]
        public async Task Un_service_qui_repond_autre_chose_qu_attendu_n_invente_pas_d_echec()
        {
            // Le dossier a été accepté : l'absence de référence dans la réponse n'en fait pas un
            // envoi raté.
            var handler = new SpyHandler(HttpStatusCode.OK, "Créé.");
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, handler);

            var outcome = await publisher.PublishAsync(Request(), null, CancellationToken.None);

            Assert.True(outcome.Published);
            Assert.Null(outcome.Reference);
        }

        [Fact]
        public async Task Un_jeton_refuse_dit_quoi_faire_et_ne_montre_aucun_code()
        {
            var handler = new SpyHandler(HttpStatusCode.Unauthorized, string.Empty);
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, handler);

            var outcome = await publisher.PublishAsync(Request(), null, CancellationToken.None);

            Assert.False(outcome.Published);
            Assert.Contains("renouveler", outcome.Summary);
            Assert.DoesNotContain("401", outcome.Summary);
        }

        [Fact]
        public async Task Un_service_indisponible_dit_que_le_dossier_reste_sur_la_machine()
        {
            var handler = new SpyHandler(HttpStatusCode.ServiceUnavailable, string.Empty);
            var publisher = new KlarviPublisher(NullLogger.Instance, Settings(), null, handler);

            var outcome = await publisher.PublishAsync(Request(), null, CancellationToken.None);

            Assert.False(outcome.Published);
            Assert.Contains("reste sur cette machine", outcome.Summary);
        }

        [Fact]
        public void Un_echec_de_connexion_securisee_designe_la_bonne_cause()
        {
            // Sous Windows 7 sans la mise à jour TLS 1.2, l'échec ressemble à une panne de réseau
            // et n'en est pas une. Envoyer le technicien vérifier la box serait le faire perdre
            // une heure.
            var failure = new HttpRequestException(
                "An error occurred while sending the request.",
                new Exception("The request was aborted: Could not create SSL/TLS secure channel."));

            var message = KlarviPublisher.Describe(failure);

            Assert.Contains("TLS 1.2", message);
            Assert.Contains("Windows 7", message);
        }

        [Fact]
        public void Une_machine_hors_ligne_est_distinguee_d_un_service_qui_refuse()
        {
            var message = KlarviPublisher.Describe(new HttpRequestException("No such host is known."));

            Assert.Contains("hors ligne", message);
            Assert.DoesNotContain("TLS", message);
        }

        [Fact]
        public void Un_fichier_de_configuration_illisible_n_est_pas_un_fichier_absent()
        {
            // Le premier demande une correction, le second est l'état par défaut du logiciel.
            var path = Path.Combine(Path.GetTempPath(), "klarvi-casse-" + Guid.NewGuid() + ".json");
            File.WriteAllText(path, "{ ceci n'est pas du JSON");

            try
            {
                KlarviSettings.Load(path, out var problem);

                Assert.NotNull(problem);
                Assert.Contains("pas lisible", problem);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static KlarviSettings Settings()
            => new KlarviSettings { BaseUrl = "https://klarvi.example/api", Token = "jeton-secret" };

        private static PublicationRequest Request()
            => new PublicationRequest
            {
                MachineName = "PC-ATELIER",
                ClientReference = "DOSSIER-42",
                Technician = "Valentin",
                Fingerprint = "3c22928c8be9c1d7",
                Score = 78,
                Documents = new[]
                {
                    new PublicationDocument
                    {
                        FileName = "bilan.html",
                        Title = "Bilan client",
                        MediaType = "text/html",
                        Content = Encoding.UTF8.GetBytes("<html><body>Bilan</body></html>"),
                    },
                },
            };

        /// <summary>
        /// Gestionnaire de substitution : il retient ce qui serait parti et rend ce qu'on lui dit.
        /// </summary>
        /// <remarks>
        /// C'est ce qui rend la publication vérifiable sans Klarvi. Un service qui n'existe pas
        /// encore ne doit pas empêcher de prouver qu'un envoi porte bien ce qu'il annonce et
        /// qu'un refus est bien expliqué.
        /// </remarks>
        private sealed class SpyHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;

            public SpyHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public int Calls { get; private set; }
            public Uri? Uri { get; private set; }
            public string? Scheme { get; private set; }
            public string? Parameter { get; private set; }
            public string Body { get; private set; } = string.Empty;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                Uri = request.RequestUri;
                Scheme = request.Headers.Authorization?.Scheme;
                Parameter = request.Headers.Authorization?.Parameter;
                Body = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

                return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            }
        }
    }
}
