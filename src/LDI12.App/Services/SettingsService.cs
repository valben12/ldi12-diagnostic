using System;
using System.IO;
using LDI12.Core.Logging;
using LDI12.Engine.Profile;
using LDI12.Reports.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.App.Services
{
    /// <summary>
    /// Barème actif du poste : celui d'origine, ou celui que le technicien a ajusté.
    /// </summary>
    /// <remarks>
    /// Un seul fichier, à côté des journaux, et rien dans le registre : l'outil se copie sur une
    /// clé USB et doit pouvoir disparaître sans laisser de trace sur la machine du client.
    /// <para>
    /// Un fichier illisible n'est jamais silencieusement ignoré. Repartir du barème d'origine
    /// sans le dire ferait rendre des scores différents de ceux attendus, sans que rien
    /// n'explique pourquoi : le pire cas pour un outil dont toute la valeur tient à ce qu'on
    /// puisse lui faire confiance.
    /// </para>
    /// </remarks>
    public sealed class SettingsService
    {
        private const string Category = "Settings";
        private const string FileName = "profil.json";
        private const string PreferencesFileName = "reglages.json";
        private const string UpdatesFileName = "ldi12-maj.json";

        private readonly ILdiLogger _logger;

        public SettingsService(ILdiLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Path = ResolvePath(FileName);
            PreferencesPath = ResolvePath(PreferencesFileName);
            UpdatesPath = ResolveUpdatesPath();
            Active = DiagnosticProfile.Default;
        }

        /// <summary>Barème utilisé par les analyses. Jamais nul.</summary>
        public DiagnosticProfile Active { get; private set; }

        /// <summary>Chemin du barème enregistré. Nul si le dossier n'a pas pu être déterminé.</summary>
        public string? Path { get; }

        public string? PreferencesPath { get; }

        /// <summary>
        /// Le technicien a autorisé le chargement du pilote de capteurs matériels.
        /// </summary>
        /// <remarks>
        /// Faux à l'installation, et le reste tant que personne ne l'a explicitement changé :
        /// activer cette couche charge un pilote noyau sur la machine du client.
        /// </remarks>
        public bool AdvancedSensors { get; private set; }

        /// <summary>Ce que le technicien a décidé de la vérification des mises à jour.</summary>
        public UpdatePreferences Updates { get; private set; } = new UpdatePreferences();

        /// <summary>
        /// Fichier des réglages de mise à jour. À côté de l'exécutable quand c'est possible.
        /// </summary>
        /// <remarks>
        /// <b>Le seul réglage qui voyage avec la clé USB, et il le doit.</b> Les autres restent
        /// par machine : autoriser un pilote noyau ne se transporte pas d'un client à l'autre.
        /// Celui-ci est l'inverse : un technicien qui a répondu une fois à la question ne doit
        /// pas se la voir reposer sur chacun des postes où il branche sa clé, et surtout, un
        /// refus doit rester un refus sur toutes les machines qu'il visite.
        /// <para>
        /// Sur une clé protégée en écriture, ou quand le logiciel tourne depuis un dossier
        /// système, on retombe sur le dossier de l'utilisateur : le réglage redevient local,
        /// ce qui est le comportement le moins surprenant.
        /// </para>
        /// </remarks>
        public string? UpdatesPath { get; }

        /// <summary>Vrai quand un barème ajusté est en vigueur.</summary>
        public bool IsCustom { get; private set; }

        /// <summary>Renseignée quand un fichier existe mais n'a pas pu être relu.</summary>
        public string? LoadError { get; private set; }

        public event EventHandler? Changed;

        /// <summary>Relit le barème et les préférences enregistrés. Appelé une fois, au démarrage.</summary>
        public void Load()
        {
            LoadError = null;
            AdvancedSensors = ReadPreferences();
            Updates = ReadUpdates();
            _logger.Info(Category, "Réglages de mise à jour : " + (UpdatesPath ?? "aucun fichier") +
                                   ", accord " + (Updates.Consent?.ToString() ?? "non demandé") + ".");

            if (Path == null || !File.Exists(Path))
            {
                Active = DiagnosticProfile.Default;
                IsCustom = false;
                return;
            }

            try
            {
                Active = ProfileSerializer.Load<DiagnosticProfile>(Path);
                IsCustom = true;
                _logger.Info(Category, "Barème ajusté chargé : " + Path);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                       ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
            {
                Active = DiagnosticProfile.Default;
                IsCustom = false;
                LoadError = "Le barème enregistré n'a pas pu être relu (" + ex.Message +
                            "). Les seuils d'origine sont utilisés.";
                _logger.Error(Category, "Barème illisible, retour au barème d'origine.", ex);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Enregistre un barème ajusté et le rend actif.</summary>
        public bool Save(DiagnosticProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (Path == null) return false;

            try
            {
                ProfileSerializer.Save(profile, Path);
                Active = profile;
                IsCustom = true;
                LoadError = null;
                _logger.Info(Category, "Barème ajusté enregistré.");
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LoadError = "Le barème n'a pas pu être enregistré : " + ex.Message;
                _logger.Error(Category, "Enregistrement du barème impossible.", ex);
                return false;
            }
        }

        /// <summary>Revient au barème d'origine et supprime le fichier ajusté.</summary>
        public void Reset()
        {
            try
            {
                if (Path != null && File.Exists(Path)) File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LoadError = "Le barème ajusté n'a pas pu être supprimé : " + ex.Message;
                _logger.Warn(Category, "Suppression du barème impossible.", ex);
            }

            Active = DiagnosticProfile.Default;
            IsCustom = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Autorise ou révoque le chargement du pilote de capteurs, et l'enregistre.
        /// </summary>
        /// <remarks>
        /// Le choix est persistant parce qu'un technicien qui l'a fait une fois sur son propre
        /// poste d'atelier n'a pas à le refaire à chaque lancement. Il reste par machine : un
        /// outil copié sur la clé USB d'un confrère repart désactivé.
        /// </remarks>
        public void SetAdvancedSensors(bool enabled)
        {
            AdvancedSensors = enabled;

            if (PreferencesPath == null) return;

            try
            {
                var directory = System.IO.Path.GetDirectoryName(PreferencesPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

                File.WriteAllText(
                    PreferencesPath,
                    "{" + Environment.NewLine +
                    "  \"advancedSensors\": " + (enabled ? "true" : "false") + Environment.NewLine +
                    "}" + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));

                _logger.Info(Category, "Capteurs matériels " + (enabled ? "autorisés" : "désactivés") + ".");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(Category, "La préférence de capteurs n'a pas pu être enregistrée.", ex);
            }
        }

        /// <summary>Enregistre les réglages de mise à jour tels qu'ils sont maintenant.</summary>
        public void SaveUpdates(UpdatePreferences updates)
        {
            Updates = updates ?? throw new ArgumentNullException(nameof(updates));

            if (UpdatesPath == null) return;

            try
            {
                var directory = System.IO.Path.GetDirectoryName(UpdatesPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

                var document = new JObject
                {
                    ["consent"] = Updates.Consent.HasValue
                        ? (JToken)Updates.Consent.Value
                        : JValue.CreateNull(),
                    ["channel"] = Updates.Channel,
                    ["lastCheckUtc"] = Updates.LastCheckUtc.HasValue
                        ? (JToken)Updates.LastCheckUtc.Value.ToString(
                            "o", System.Globalization.CultureInfo.InvariantCulture)
                        : JValue.CreateNull(),
                    ["skippedVersion"] = Updates.SkippedVersion ?? string.Empty,
                };

                File.WriteAllText(
                    UpdatesPath,
                    document.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Une clé retirée pendant l'écriture n'est pas un incident : le réglage sera
                // simplement redemandé.
                _logger.Warn(Category, "Les réglages de mise à jour n'ont pas pu être enregistrés.", ex);
            }
        }

        private UpdatePreferences ReadUpdates()
        {
            if (UpdatesPath == null || !File.Exists(UpdatesPath)) return new UpdatePreferences();

            try
            {
                var document = JObject.Parse(File.ReadAllText(UpdatesPath));
                var consent = document["consent"];
                var last = document["lastCheckUtc"]?.Value<string>();
                var skipped = document["skippedVersion"]?.Value<string>();

                return new UpdatePreferences
                {
                    Consent = consent == null || consent.Type == JTokenType.Null
                        ? (bool?)null
                        : consent.Value<bool>(),
                    Channel = document["channel"]?.Value<string>() == "beta" ? "beta" : "stable",
                    LastCheckUtc = DateTimeOffset.TryParse(
                        last, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : (DateTimeOffset?)null,
                    SkippedVersion = string.IsNullOrWhiteSpace(skipped) ? null : skipped,
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is Newtonsoft.Json.JsonException)
            {
                _logger.Warn(Category, "Les réglages de mise à jour n'ont pas pu être relus.", ex);
                return new UpdatePreferences();
            }
        }

        /// <summary>
        /// À côté de l'exécutable si l'on peut y écrire, sinon dans le dossier de l'utilisateur.
        /// </summary>
        /// <remarks>
        /// Le test est une écriture réelle, et non une supposition sur le chemin : une clé USB
        /// protégée en écriture et un dossier <c>Program Files</c> se ressemblent beaucoup vus
        /// depuis un chemin, et pas du tout vus depuis un fichier qu'on essaie de créer.
        /// </remarks>
        private string? ResolveUpdatesPath()
        {
            try
            {
                var executable = LDI12.Updates.UpdateInstaller.RunningExecutable();
                var folder = executable == null ? null : System.IO.Path.GetDirectoryName(executable);

                if (!string.IsNullOrEmpty(folder))
                {
                    var candidate = System.IO.Path.Combine(folder!, UpdatesFileName);

                    if (File.Exists(candidate)) return candidate;

                    var probe = System.IO.Path.Combine(folder!, ".ldi12-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        stream.WriteByte(0);

                    File.Delete(probe);
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                // Emplacement non inscriptible : le réglage redevient local à la machine.
            }

            return ResolvePath(UpdatesFileName);
        }

        private bool ReadPreferences()
        {
            if (PreferencesPath == null || !File.Exists(PreferencesPath)) return false;

            try
            {
                // Un seul réglage, lu sans dépendance : le fichier reste éditable à la main par
                // un technicien qui préfère cela à un écran.
                var text = File.ReadAllText(PreferencesPath);
                return text.IndexOf("\"advancedSensors\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       text.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(Category, "Les préférences n'ont pas pu être relues.", ex);
                return false;
            }
        }

        private static string? ResolvePath(string fileName)
        {
            try
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LDI12", "Diagnostic", fileName);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                return null;
            }
        }
    }
}
