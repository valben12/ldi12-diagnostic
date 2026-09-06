using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace LDI12.Publishing.Klarvi
{
    /// <summary>
    /// Adresse et jeton du service Klarvi.
    /// </summary>
    /// <remarks>
    /// <b>Dans un fichier, et non dans l'écran des réglages.</b> Un jeton d'accès n'est pas un
    /// seuil : il ne se règle pas devant un client, il s'installe une fois sur la machine de
    /// l'atelier. Le mettre dans l'interface l'exposerait sur chaque capture d'écran et dans
    /// chaque démonstration.
    /// <para>
    /// L'absence du fichier est l'état normal, pas une erreur : le logiciel fonctionne hors
    /// ligne, et la publication est une option de l'atelier.
    /// </para>
    /// </remarks>
    public sealed class KlarviSettings
    {
        /// <summary>Adresse du service, sans barre oblique finale.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Jeton d'accès, présenté en en-tête <c>Authorization</c>.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Espace de travail Klarvi, quand le compte en distingue plusieurs.</summary>
        public string? Workspace { get; set; }

        /// <summary>
        /// Délai au bout duquel un envoi est abandonné.
        /// </summary>
        /// <remarks>
        /// Court à dessein : le technicien est chez un client, souvent sur un partage de
        /// connexion, et une fenêtre qui attend deux minutes sans rien dire ressemble à un
        /// plantage. Un envoi qui échoue se relance ; un logiciel figé, non.
        /// </remarks>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>Emplacement attendu du fichier de configuration.</summary>
        public static string DefaultPath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LDI12", "Diagnostic");

                return Path.Combine(root, "klarvi.json");
            }
        }

        /// <summary>
        /// Relit la configuration, ou explique son absence.
        /// </summary>
        /// <remarks>
        /// Un fichier illisible n'est jamais confondu avec un fichier absent : le premier demande
        /// une correction, le second est l'état par défaut du logiciel.
        /// </remarks>
        public static KlarviSettings? Load(string path, out string? problem)
        {
            problem = null;

            try
            {
                if (!File.Exists(path))
                {
                    problem =
                        "Aucune configuration Klarvi sur cette machine. Le fichier attendu est « " +
                        path + " » ; tant qu'il n'existe pas, rien n'est envoyé nulle part.";
                    return null;
                }

                var settings = JsonConvert.DeserializeObject<KlarviSettings>(File.ReadAllText(path));
                if (settings == null)
                {
                    problem = "Le fichier de configuration Klarvi est vide.";
                    return null;
                }

                problem = settings.Validate();
                return problem == null ? settings : null;
            }
            catch (JsonException ex)
            {
                problem = "Le fichier de configuration Klarvi n'est pas lisible : " + ex.Message;
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = "Le fichier de configuration Klarvi n'a pas pu être ouvert : " + ex.Message;
                return null;
            }
        }

        /// <summary>Ce qui manque, en une phrase, ou rien.</summary>
        internal string? Validate()
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                return "La configuration Klarvi ne contient pas d'adresse de service.";

            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
                return "L'adresse du service Klarvi n'est pas une adresse valide : « " + BaseUrl + " ».";

            // Un dossier de diagnostic part avec un jeton d'accès : en clair sur le réseau, les
            // deux seraient lisibles par n'importe qui sur le même point d'accès.
            if (uri.Scheme != Uri.UriSchemeHttps)
                return "L'adresse du service Klarvi doit être en HTTPS : un dossier de diagnostic " +
                       "et un jeton d'accès ne voyagent pas en clair.";

            if (string.IsNullOrWhiteSpace(Token))
                return "La configuration Klarvi ne contient pas de jeton d'accès.";

            if (TimeoutSeconds < 5 || TimeoutSeconds > 300)
                return "Le délai d'attente Klarvi doit être compris entre 5 et 300 secondes ; il " +
                       "vaut " + TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + ".";

            return null;
        }

        internal Uri Endpoint()
            => new Uri(BaseUrl.TrimEnd('/') + "/interventions", UriKind.Absolute);
    }
}
