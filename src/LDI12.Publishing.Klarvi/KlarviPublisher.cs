using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Publishing;
using Newtonsoft.Json;

namespace LDI12.Publishing.Klarvi
{
    /// <summary>
    /// Dépôt d'un dossier de diagnostic dans Klarvi.
    /// </summary>
    /// <remarks>
    /// <b>Le contrat est provisoire, et il est confiné ici.</b> L'interface de Klarvi n'est pas
    /// arrêtée au moment où ce code est écrit : ce qui est implémenté est la convention la plus
    /// ordinaire : un envoi multipart vers <c>{base}/interventions</c>, un jeton porteur en
    /// en-tête, une réponse JSON qui rend une référence. Tout ce qui dépend de cette convention
    /// tient dans cette classe ; le reste du logiciel ne connaît que <see cref="IReportPublisher"/>
    /// et ne bougera pas quand le contrat sera fixé.
    /// <para>
    /// Trois règles ne bougeront pas non plus, elles : rien ne part sans un geste, ce qui part est
    /// montré avant, et un échec est expliqué en français plutôt que rendu sous son code.
    /// </para>
    /// </remarks>
    public sealed class KlarviPublisher : IReportPublisher, IDisposable
    {
        private const string Category = "Klarvi";

        private readonly ILdiLogger _logger;
        private readonly KlarviSettings? _settings;
        private readonly string? _problem;
        private readonly HttpMessageHandler? _handler;

        private HttpClient? _client;

        /// <summary>Relit la configuration de l'atelier, sans rien envoyer.</summary>
        public KlarviPublisher(ILdiLogger logger, string? configurationPath = null)
            : this(logger, KlarviSettings.Load(configurationPath ?? KlarviSettings.DefaultPath, out var problem), problem, null)
        {
        }

        /// <summary>
        /// Construction explicite, pour les tests.
        /// </summary>
        /// <remarks>
        /// Le gestionnaire HTTP est injectable pour que la publication soit vérifiable sans Klarvi :
        /// un service qui n'existe pas encore ne doit pas empêcher de prouver qu'un échec est bien
        /// expliqué et qu'un envoi porte bien ce qu'il annonce.
        /// </remarks>
        public KlarviPublisher(
            ILdiLogger logger, KlarviSettings? settings, string? problem, HttpMessageHandler? handler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings;
            _problem = problem ?? settings?.Validate();
            _handler = handler;
        }

        public string DisplayName => "Klarvi";

        public Measured<bool> Configured => _settings != null && _problem == null
            ? Measured.Ok(true, DataSource.FileSystem)
            : Measured.Missing<bool>(_problem ?? "Klarvi n'est pas configuré sur cette machine.",
                DataSource.FileSystem);

        /// <summary>
        /// Ce qui quitterait la machine, en clair.
        /// </summary>
        /// <remarks>
        /// Construite à partir de la requête réelle et non d'une liste écrite à la main : une
        /// description qui décrirait autre chose que ce qui part serait pire que pas de
        /// description du tout.
        /// </remarks>
        public IReadOnlyList<string> DescribeWhatIsSent(PublicationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var lines = new List<string>
            {
                "Le nom de la machine : « " + request.MachineName + " ».",
            };

            if (!string.IsNullOrWhiteSpace(request.ClientReference))
                lines.Add("La référence du dossier : « " + request.ClientReference + " ».");

            if (!string.IsNullOrWhiteSpace(request.Technician))
                lines.Add("Le nom du technicien : « " + request.Technician + " ».");

            if (request.Score.HasValue)
                lines.Add("La note du diagnostic : " + request.Score.Value.ToString(CultureInfo.CurrentCulture) + " sur 100.");

            if (!string.IsNullOrWhiteSpace(request.Fingerprint))
                lines.Add(
                    "L'empreinte de la machine : « " + request.Fingerprint + " ». C'est un condensé, " +
                    "pas un numéro de série ; il sert à rapprocher deux passages du même poste.");

            foreach (var document in request.Documents)
                lines.Add(
                    document.Title + " : le document complet, " + Weight(document.Content.Length) +
                    ", tel qu'il est remis au client.");

            if (request.Documents.Count == 0)
                lines.Add("Aucun document : seul le dossier serait créé.");

            lines.Add(
                "Rien d'autre. Ni le diagnostic brut, ni les comptes Windows, ni les réseaux sans " +
                "fil relevés dans le logement ne quittent la machine.");

            return lines;
        }

        public async Task<PublicationOutcome> PublishAsync(
            PublicationRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (_settings == null || _problem != null)
                return PublicationOutcome.Failed(_problem ?? "Klarvi n'est pas configuré sur cette machine.");

            var log = _logger.For(Category);

            try
            {
                progress?.Report("Envoi du dossier vers Klarvi…");

                using (var content = Build(request))
                using (var response = await Client().PostAsync(_settings.Endpoint(), content, cancellationToken)
                           .ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var refused = Describe(response.StatusCode);
                        log.Warn("Klarvi a refusé l'envoi : " + (int)response.StatusCode + " " + response.StatusCode);
                        return PublicationOutcome.Failed(refused);
                    }

                    var reference = Reference(body);
                    log.Info("Dossier déposé dans Klarvi" + (reference == null ? "." : " sous la référence " + reference + "."));

                    return new PublicationOutcome
                    {
                        Published = true,
                        Reference = reference,
                        Summary = reference == null
                            ? "Dossier déposé dans Klarvi."
                            : "Dossier déposé dans Klarvi sous la référence " + reference + ".",
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return PublicationOutcome.Failed("Envoi annulé. Rien n'a été déposé.");
            }
            catch (HttpRequestException ex)
            {
                log.Warn("L'envoi vers Klarvi a échoué : " + ex.Message);
                return PublicationOutcome.Failed(Describe(ex));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UriFormatException)
            {
                log.Warn("L'envoi vers Klarvi a échoué : " + ex.Message);
                return PublicationOutcome.Failed("L'envoi n'a pas pu être tenté : " + ex.Message);
            }
        }

        private HttpClient Client()
        {
            if (_client != null) return _client;

            // TLS 1.2 explicitement : sous Windows 7 et Windows 8, .NET Framework retombe sinon
            // sur des protocoles que plus aucun service n'accepte, et l'échec ressemble à une
            // panne de réseau alors qu'il n'en est pas une.
            EnableModernTls();

            _client = _handler == null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            _client.Timeout = TimeSpan.FromSeconds(_settings!.TimeoutSeconds);
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.Token);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("LDI12-Diagnostic");

            if (!string.IsNullOrWhiteSpace(_settings.Workspace))
                _client.DefaultRequestHeaders.Add("X-Klarvi-Workspace", _settings.Workspace);

            return _client;
        }

        internal static void EnableModernTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Windows 7 sans la mise à jour qui apporte TLS 1.2 : l'envoi échouera, et
                // Describe(HttpRequestException) dira pourquoi plutôt que de parler de réseau.
            }
        }

        private MultipartFormDataContent Build(PublicationRequest request)
        {
            var content = new MultipartFormDataContent();

            var envelope = JsonConvert.SerializeObject(new
            {
                machine = request.MachineName,
                clientReference = request.ClientReference,
                technician = request.Technician,
                issuedAt = request.IssuedAt,
                fingerprint = request.Fingerprint,
                score = request.Score,
                tool = "LDI12 Diagnostic",
            });

            content.Add(new StringContent(envelope, Encoding.UTF8, "application/json"), "intervention");

            foreach (var document in request.Documents)
            {
                var file = new ByteArrayContent(document.Content);
                file.Headers.ContentType = new MediaTypeHeaderValue(document.MediaType);
                content.Add(file, "documents", document.FileName);
            }

            return content;
        }

        /// <summary>Référence rendue par le service, quand la réponse en porte une.</summary>
        internal static string? Reference(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                var parsed = JsonConvert.DeserializeObject<KlarviResponse>(body);
                return string.IsNullOrWhiteSpace(parsed?.Reference) ? parsed?.Id : parsed!.Reference;
            }
            catch (JsonException)
            {
                // Un service qui répond autre chose que ce qu'on attend a tout de même accepté
                // l'envoi : l'absence de référence n'en fait pas un échec.
                return null;
            }
        }

        /// <summary>
        /// Ce qu'un refus veut dire, en français.
        /// </summary>
        /// <remarks>
        /// « 401 » ne veut rien dire pour la personne qui lit l'écran, et « échec de la
        /// publication » ne dit pas quoi faire. Chacune de ces phrases nomme le geste qui corrige.
        /// </remarks>
        internal static string Describe(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized =>
                "Klarvi a refusé le jeton d'accès. Il a expiré, ou il a été révoqué : il faut le " +
                "renouveler dans le fichier de configuration.",
            HttpStatusCode.Forbidden =>
                "Le jeton est reconnu mais n'autorise pas le dépôt de dossiers.",
            HttpStatusCode.NotFound =>
                "L'adresse configurée ne correspond à aucun service Klarvi. Il faut vérifier " +
                "l'adresse du fichier de configuration.",
            HttpStatusCode.RequestEntityTooLarge =>
                "Le dossier est trop volumineux pour le service. Envoyer moins de documents à la fois.",
            HttpStatusCode.ServiceUnavailable =>
                "Le service Klarvi est momentanément indisponible. Le dossier reste sur cette " +
                "machine : l'envoi peut être relancé plus tard.",
            _ => "Klarvi n'a pas accepté l'envoi. Le dossier reste sur cette machine et rien n'est perdu.",
        };

        internal static string Describe(HttpRequestException exception)
        {
            var message = Flatten(exception);

            if (message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("sécurisé", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "La connexion sécurisée avec Klarvi n'a pas pu être établie. Sur une machine " +
                       "Windows 7 ou 8, la cause habituelle est l'absence de la mise à jour Windows " +
                       "qui apporte TLS 1.2 ; le dossier reste sur cette machine.";
            }

            return "Klarvi n'a pas répondu. La machine est peut-être hors ligne, ou le service " +
                   "injoignable depuis ce réseau. Le dossier reste sur cette machine.";
        }

        private static string Flatten(Exception exception)
        {
            var builder = new StringBuilder();
            for (var current = exception; current != null; current = current.InnerException)
                builder.Append(current.Message).Append(' ');

            return builder.ToString();
        }

        private static string Weight(int bytes)
        {
            if (bytes >= 1024 * 1024)
                return (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " Mo";

            return Math.Max(1, bytes / 1024) + " Ko";
        }

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }

        private sealed class KlarviResponse
        {
            [JsonProperty("id")]
            public string? Id { get; set; }

            [JsonProperty("reference")]
            public string? Reference { get; set; }
        }
    }
}
