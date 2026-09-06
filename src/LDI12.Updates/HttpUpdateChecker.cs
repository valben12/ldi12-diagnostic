using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Updates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.Updates
{
    /// <summary>
    /// La seule sortie réseau du logiciel en dehors des sondes réseau.
    /// </summary>
    /// <remarks>
    /// <b>Trois choses ne bougeront pas.</b> Rien d'identifiant ne part : la requête ne porte que
    /// la version, le canal, l'architecture et la famille de Windows. Un réseau absent n'est pas
    /// une erreur mais une mesure absente, avec sa raison. Et rien n'est cru sans la signature :
    /// le manifeste exploité est celui qu'on reconstruit à partir des octets signés, jamais le
    /// corps lisible de la réponse.
    /// <para>
    /// Le délai est court et il n'y a qu'une tentative : une vérification de mise à jour n'a pas
    /// à faire attendre qui que ce soit, encore moins un technicien chez un client.
    /// </para>
    /// </remarks>
    public sealed class HttpUpdateChecker : IUpdateChecker, IDisposable
    {
        private const string Category = "Updates";

        /// <summary>Adresse du manifeste. Le nom du logiciel s'y ajoute.</summary>
        public const string DefaultEndpoint = "https://ldi12.fr/api/updates/v1/";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly ILdiLogger _logger;
        private readonly string _endpoint;
        private readonly HttpMessageHandler? _handler;

        private HttpClient? _client;

        public HttpUpdateChecker(ILdiLogger logger, string? endpoint = null, HttpMessageHandler? handler = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint!;
            _handler = handler;
        }

        public async Task<Measured<UpdateManifest>> CheckAsync(
            UpdateQuery query, CancellationToken cancellationToken)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            string body;

            try
            {
                using (var response = await Client()
                           .GetAsync(Address(query), HttpCompletionOption.ResponseContentRead, cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return Explain(response.StatusCode);

                    body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Le délai de cinq secondes s'est écoulé. C'est le cas ordinaire chez un client
                // dont la connexion est en panne : rien à signaler, rien à journaliser.
                return Offline("le serveur n'a pas répondu dans le délai.");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is WebException ||
                                       ex is System.IO.IOException)
            {
                return Offline(Describe(ex));
            }

            return Interpret(body);
        }

        /// <summary>
        /// Reconstruit le manifeste à partir des octets signés, et seulement s'ils le sont.
        /// </summary>
        /// <remarks>
        /// L'ordre compte, et il est le seul défendable : décoder, vérifier, puis analyser. Lire
        /// d'abord le corps de la réponse pour « voir de quoi il s'agit » reviendrait à faire
        /// confiance à ce qu'on n'a pas encore vérifié.
        /// </remarks>
        internal Measured<UpdateManifest> Interpret(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return Measured.Missing<UpdateManifest>(
                    "Le serveur a répondu sans contenu.", DataSource.Cli);

            JObject envelope;
            try
            {
                envelope = JObject.Parse(body!);
            }
            catch (JsonException)
            {
                return Measured.Missing<UpdateManifest>(
                    "La réponse du serveur n'a pas pu être lue : elle est incomplète ou tronquée.",
                    DataSource.Cli);
            }

            var signature = envelope["signature"] as JObject;
            var payload = UpdateSignature.Decode(signature?["payload"]?.Value<string>());
            var value = UpdateSignature.Decode(signature?["value"]?.Value<string>());
            var keyId = signature?["keyId"]?.Value<string>();

            var verdict = UpdateSignature.Verify(payload, value, keyId);
            if (verdict != SignatureVerdict.Valid)
            {
                // Le seul cas de cette classe qui mérite d'être journalisé : une signature fausse
                // décrit quelqu'un qui s'interpose, pas une box en panne.
                if (verdict == SignatureVerdict.Invalid)
                    _logger.Error(Category, "Manifeste de mise à jour refusé : signature invalide.");
                else
                    _logger.Warn(Category, "Manifeste de mise à jour refusé : " + verdict + ".");

                return Measured.Missing<UpdateManifest>(UpdateSignature.Describe(verdict), DataSource.Cli);
            }

            try
            {
                var signed = JObject.Parse(Encoding.UTF8.GetString(payload!));
                return Measured.Ok(Read(signed), DataSource.Cli);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException ||
                                       ex is DecoderFallbackException)
            {
                _logger.Warn(Category, "Manifeste signé illisible : " + ex.Message);
                return Measured.Missing<UpdateManifest>(
                    "La réponse du serveur est signée mais illisible.", DataSource.Cli);
            }
        }

        /// <summary>
        /// L'adresse interrogée, construite champ par champ.
        /// </summary>
        /// <remarks>
        /// Écrite en clair plutôt qu'assemblée par une bibliothèque, pour qu'un technicien puisse
        /// lire ici, sans outil, exactement ce qui quitte la machine. La version de Windows
        /// réelle n'y figure pas : seule la famille part, et la version précise ne sert qu'à
        /// juger localement le <c>minOs</c>.
        /// </remarks>
        internal string Address(UpdateQuery query)
        {
            var url = new StringBuilder(_endpoint);
            if (!_endpoint.EndsWith("/", StringComparison.Ordinal)) url.Append('/');

            url.Append(Uri.EscapeDataString(query.Product));
            url.Append("?version=").Append(Uri.EscapeDataString(query.Version ?? string.Empty));
            url.Append("&channel=").Append(Uri.EscapeDataString(query.Channel ?? "stable"));
            url.Append("&arch=").Append(Uri.EscapeDataString(query.Architecture ?? "any"));
            url.Append("&os=").Append(Uri.EscapeDataString(query.OsFamily ?? string.Empty));

            return url.ToString();
        }

        // ------------------------------------------------------------ lecture

        private static UpdateManifest Read(JObject signed) => new UpdateManifest
        {
            Product = signed["product"]?.Value<string>() ?? string.Empty,
            Name = signed["name"]?.Value<string>() ?? string.Empty,
            Channel = signed["channel"]?.Value<string>() ?? "stable",
            CheckedAt = Date(signed["checkedAt"]),
            Current = signed["current"]?.Value<string>(),
            UpdateAvailable = signed["updateAvailable"]?.Value<bool>() ?? false,
            Latest = ReadRelease(signed["latest"] as JObject),
        };

        private static UpdateRelease? ReadRelease(JObject? latest)
        {
            if (latest == null) return null;

            var highlights = new List<string>();
            if (latest["highlights"] is JArray array)
            {
                foreach (var item in array)
                {
                    var text = item?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(text)) highlights.Add(text!);
                }
            }

            return new UpdateRelease
            {
                Version = latest["version"]?.Value<string>() ?? string.Empty,
                Title = latest["title"]?.Value<string>() ?? string.Empty,
                Summary = latest["summary"]?.Value<string>() ?? string.Empty,
                Highlights = highlights,
                ReleasedAt = Date(latest["releasedAt"]),
                Mandatory = latest["mandatory"]?.Value<bool>() ?? false,
                MinOs = latest["minOs"]?.Value<string>() ?? string.Empty,
                NotesUrl = latest["notesUrl"]?.Value<string>() ?? string.Empty,
                PageUrl = latest["pageUrl"]?.Value<string>() ?? string.Empty,
                Download = ReadDownload(latest["download"] as JObject),
            };
        }

        private static UpdateDownload? ReadDownload(JObject? download)
        {
            if (download == null) return null;

            var url = download["url"]?.Value<string>() ?? string.Empty;

            // Un manifeste authentique qui proposerait autre chose qu'une adresse chiffrée
            // décrirait un serveur mal configuré : on ne télécharge pas d'exécutable en clair.
            // Seule exception, la même que pour l'adresse du serveur : un trafic qui ne quitte
            // pas la machine n'a rien à chiffrer, et c'est ce qui rend la chaîne éprouvable
            // avant que le site ne la diffuse.
            if (!IsAcceptable(url)) return null;

            return new UpdateDownload
            {
                Url = url,
                FileName = download["filename"]?.Value<string>() ?? string.Empty,
                Size = download["size"]?.Value<long>() ?? 0,
                Sha256 = (download["sha256"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant(),
                Kind = download["kind"]?.Value<string>() ?? string.Empty,
                Arch = download["arch"]?.Value<string>() ?? string.Empty,
                Signed = download["signed"]?.Value<bool>() ?? false,
            };
        }

        /// <summary>
        /// L'adresse d'un exécutable, telle qu'on accepte de la suivre.
        /// </summary>
        /// <remarks>
        /// Ce filtre ne remplace ni la signature du manifeste, ni le contrôle de l'empreinte du
        /// fichier : c'est une troisième barrière, et la plus faible des trois. Elle existe pour
        /// qu'un serveur mal configuré ne fasse pas descendre un exécutable en clair sur le
        /// réseau d'un client.
        /// </remarks>
        internal static bool IsAcceptable(string url)
            => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

        private static DateTimeOffset? Date(JToken? token)
        {
            var text = token?.Value<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;

            return DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        // ------------------------------------------------------------ échecs

        /// <summary>
        /// Un échec réseau, formulé comme une information et non comme un incident.
        /// </summary>
        /// <remarks>
        /// C'est chez le client dont la box est en panne que ce logiciel sert le plus. Y afficher
        /// une erreur parce qu'un serveur de mise à jour n'a pas répondu serait exactement le
        /// mauvais message, au mauvais moment.
        /// </remarks>
        private static Measured<UpdateManifest> Offline(string reason)
            => Measured.Missing<UpdateManifest>(
                "Vérification impossible : " + reason + " Cela n'empêche rien : le diagnostic " +
                "fonctionne entièrement hors ligne.", DataSource.Cli);

        private static Measured<UpdateManifest> Explain(HttpStatusCode status)
        {
            switch ((int)status)
            {
                case 404:
                    return Measured.Missing<UpdateManifest>(
                        "Le serveur ne diffuse pas ce logiciel pour le moment.", DataSource.Cli);
                case 429:
                    return Measured.Missing<UpdateManifest>(
                        "Trop de vérifications depuis ce réseau. Réessayez dans une minute.",
                        DataSource.Cli);
                default:
                    return Offline("le serveur a répondu « " + (int)status + " ».");
            }
        }

        private static string Describe(Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;

            if (message.IndexOf("nom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("resolv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("known", StringComparison.OrdinalIgnoreCase) >= 0)
                return "le nom ldi12.fr n'a pas pu être résolu.";

            if (message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("sécuris", StringComparison.OrdinalIgnoreCase) >= 0)
                return "la liaison sécurisée n'a pas pu être établie.";

            return "aucune connexion n'a abouti.";
        }

        // ------------------------------------------------------------ transport

        private HttpClient Client()
        {
            if (_client != null) return _client;

            EnableModernTls();

            _client = _handler == null ? new HttpClient() : new HttpClient(_handler, false);
            _client.Timeout = Timeout;
            _client.DefaultRequestHeaders.Add("User-Agent", "LDI12-Diagnostic");
            _client.DefaultRequestHeaders.Add("Accept", "application/json");

            return _client;
        }

        /// <summary>
        /// TLS 1.2 sur Windows 7.
        /// </summary>
        /// <remarks>
        /// Sans ce geste, la requête part en TLS 1.0 et le serveur la refuse : une panne qui
        /// n'apparaîtrait que sur les vieilles machines, c'est-à-dire précisément chez les
        /// clients. Le même geste figure déjà deux fois dans ce dépôt.
        /// </remarks>
        internal static void EnableModernTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Windows 7 sans la mise à jour qui apporte TLS 1.2 : la vérification échouera,
                // et Describe() dira « la liaison sécurisée n'a pas pu être établie ».
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
