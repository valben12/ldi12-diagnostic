using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Updates;
using LDI12.Updates;

namespace LDI12.App.Services
{
    /// <summary>Ce que le technicien a décidé de la vérification des mises à jour.</summary>
    public sealed class UpdatePreferences
    {
        /// <summary>
        /// Nul tant que la question n'a pas été posée.
        /// </summary>
        /// <remarks>
        /// Trois états et non deux, délibérément : « pas encore répondu » et « répondu non » ne
        /// s'écrivent pas de la même façon et n'ont pas la même suite. Sans ce troisième état, la
        /// question reviendrait à chaque lancement chez qui a dit non.
        /// </remarks>
        public bool? Consent { get; init; }

        public string Channel { get; init; } = "stable";

        public DateTimeOffset? LastCheckUtc { get; init; }

        /// <summary>Version que le technicien a explicitement écartée.</summary>
        public string? SkippedVersion { get; init; }

        public UpdatePreferences With(
            bool? consent = null, string? channel = null,
            DateTimeOffset? lastCheck = null, string? skipped = null)
            => new UpdatePreferences
            {
                Consent = consent ?? Consent,
                Channel = channel ?? Channel,
                LastCheckUtc = lastCheck ?? LastCheckUtc,
                SkippedVersion = skipped ?? SkippedVersion,
            };
    }

    /// <summary>
    /// La vérification de mise à jour, du côté de l'application.
    /// </summary>
    /// <remarks>
    /// <b>Ce service ne sort jamais sur le réseau de sa propre initiative.</b> Il ne le fait que
    /// si le technicien a répondu oui à la question posée une fois, ou s'il vient de cliquer sur
    /// « Vérifier maintenant ». Et quand il sort, un échec n'est jamais un incident : ce logiciel
    /// sert précisément chez les clients dont la connexion est en panne.
    /// </remarks>
    public sealed class UpdateService
    {
        private const string Category = "Updates";

        /// <summary>Une vérification par jour suffit : le serveur met sa réponse en cache cinq minutes.</summary>
        private static readonly TimeSpan Interval = TimeSpan.FromHours(20);

        private readonly SettingsService _settings;
        private readonly ILdiLogger _logger;
        private readonly IUpdateChecker _checker;
        private readonly UpdateInstaller _installer;

        public UpdateService(SettingsService settings, ILdiLogger logger, IUpdateChecker? checker = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _checker = checker ?? new HttpUpdateChecker(logger, Endpoint(logger));
            _installer = new UpdateInstaller(logger);
        }

        /// <summary>La question n'a jamais été posée sur ce poste.</summary>
        public bool NeedsConsent => !_settings.Updates.Consent.HasValue;

        /// <summary>La vérification au démarrage est autorisée.</summary>
        public bool Enabled => _settings.Updates.Consent == true;

        /// <summary>Enregistre la réponse à la question posée au premier lancement.</summary>
        public void Answer(bool accepted)
        {
            _settings.SaveUpdates(_settings.Updates.With(consent: accepted));
            _logger.Info(Category, "Vérification au démarrage " + (accepted ? "acceptée" : "refusée") + ".");
        }

        /// <summary>
        /// Interroge le serveur, et rend ce qu'il faut en faire ici.
        /// </summary>
        /// <param name="manual">
        /// Vrai quand le technicien vient de le demander : la vérification part alors même si
        /// elle a déjà eu lieu aujourd'hui, et même si le réglage est décoché.
        /// </param>
        public async Task<UpdateOutlook?> CheckAsync(bool manual, CancellationToken cancellationToken)
        {
            if (!manual)
            {
                if (!Enabled) return null;

                var last = _settings.Updates.LastCheckUtc;
                if (last.HasValue && DateTimeOffset.UtcNow - last.Value < Interval) return null;
            }

            var query = BuildQuery();
            var manifest = await _checker.CheckAsync(query, cancellationToken).ConfigureAwait(false);

            _settings.SaveUpdates(_settings.Updates.With(lastCheck: DateTimeOffset.UtcNow));

            if (!manifest.HasValue)
            {
                // Aucune trace alarmiste : une box en panne n'est pas un incident du logiciel.
                _logger.Info(Category, "Vérification sans réponse : " + manifest.Reason);
                return new UpdateOutlook
                {
                    Decision = UpdateDecision.Nothing,
                    Message = manifest.Reason ?? "Vérification impossible.",
                };
            }

            var outlook = UpdatePlan.Decide(manifest.Value, query);
            _logger.Info(Category, "Vérification " + outlook.Decision + " : " + outlook.Message);
            return outlook;
        }

        /// <summary>
        /// Peut-on remplacer l'exécutable là où il se trouve.
        /// </summary>
        /// <remarks>
        /// Posé <b>avant</b> de télécharger, et pas après : découvrir qu'une clé USB est protégée
        /// en écriture une fois douze mégaoctets récupérés serait une perte de temps chez un
        /// client, et un message d'échec là où il fallait un mode d'emploi.
        /// </remarks>
        public Measured<bool> CanInstall() => _installer.CanReplace();

        /// <summary>
        /// Télécharge la version proposée, vérifie son empreinte, la met en place et relance.
        /// </summary>
        /// <remarks>
        /// Si tout va bien, cette méthode ne rend pas la main de façon utile : l'application est
        /// priée de se fermer immédiatement après, pour que la nouvelle version puisse prendre
        /// la place de l'ancienne.
        /// </remarks>
        public async Task<InstallOutcome> InstallAsync(
            UpdateOutlook outlook, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (outlook == null) throw new ArgumentNullException(nameof(outlook));

            var download = outlook.Release?.Download;
            if (download == null)
                return InstallOutcome.Failed("Le serveur n'a proposé aucun fichier à télécharger.");

            var writable = CanInstall();
            if (!writable.HasValue)
                return InstallOutcome.Failed(writable.Reason ?? "L'emplacement du logiciel n'est pas modifiable.");

            using (var downloader = new UpdateDownloader(_logger))
            {
                var received = await downloader
                    .DownloadAsync(download, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!received.Succeeded) return InstallOutcome.Failed(received.Message, received.Tampered);

                return _installer.Handover(received.Path!)
                    ? InstallOutcome.HandedOver()
                    : InstallOutcome.Failed(
                        "La nouvelle version a été téléchargée et vérifiée, mais elle n'a pas pu être " +
                        "lancée. Elle se trouve ici : " + received.Path);
            }
        }

        /// <summary>Retient une version pour ne plus la proposer.</summary>
        public void Skip(string version)
        {
            _settings.SaveUpdates(_settings.Updates.With(skipped: version));
            _logger.Info(Category, "Version écartée sur ce poste : " + version + ".");
        }

        /// <summary>
        /// Adresse du serveur de mise à jour, éventuellement remplacée par une autre.
        /// </summary>
        /// <remarks>
        /// <b>Cette porte ne peut pas servir à installer autre chose que du LDI12.</b> Quel que
        /// soit le serveur interrogé, le manifeste doit être signé par la clé compilée dans cet
        /// exécutable et le fichier reçu doit porter l'empreinte que ce manifeste annonce : un
        /// serveur de rechange peut refuser de répondre, il ne peut pas faire installer un autre
        /// programme. C'est cette propriété qui rend le réglage acceptable.
        /// <para>
        /// Il sert à deux choses : éprouver la chaîne complète avant que ldi12.fr ne diffuse le
        /// logiciel, et permettre à un parc d'entreprise de servir ses postes depuis son propre
        /// miroir. L'usage est journalisé, parce qu'un technicien qui reprend une machine doit
        /// pouvoir savoir d'où elle se met à jour.
        /// </para>
        /// </remarks>
        private static string? Endpoint(ILdiLogger logger)
        {
            var custom = Environment.GetEnvironmentVariable("LDI12_UPDATE_ENDPOINT");
            if (string.IsNullOrWhiteSpace(custom)) return null;

            var trimmed = custom!.Trim();

            var acceptable = trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

            if (!acceptable)
            {
                logger.Warn(Category,
                    "Adresse de mise à jour ignorée : une liaison chiffrée est exigée hors de cette machine.");
                return null;
            }

            logger.Info(Category, "Serveur de mise à jour de rechange : " + trimmed);
            return trimmed;
        }

        /// <summary>Ce que le logiciel dit de lui-même, et rien d'autre.</summary>
        internal UpdateQuery BuildQuery() => new UpdateQuery
        {
            Product = "ldi12-diagnostic",
            Version = InstalledVersion(),
            Channel = _settings.Updates.Channel,
            Architecture = Architecture(),
            OsFamily = OsFamily(),
            OsVersion = Environment.OSVersion.Version.ToString(),
            SkippedVersion = _settings.Updates.SkippedVersion,
        };

        /// <summary>
        /// Version de l'exécutable, telle que la publication l'y a inscrite.
        /// </summary>
        /// <remarks>
        /// L'empreinte de commit que <c>publish.ps1</c> ajoute après un « + » est retirée : le
        /// serveur attend un numéro de version, et la lui envoyer entière ferait de chaque
        /// construction une version distincte dans ses compteurs.
        /// </remarks>
        internal static string InstalledVersion()
        {
            try
            {
                var informational = Assembly.GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational!.IndexOf('+');
                    return plus > 0 ? informational.Substring(0, plus) : informational;
                }

                var version = Assembly.GetEntryAssembly()?.GetName().Version;
                return version == null
                    ? string.Empty
                    : version.Major + "." + version.Minor + "." + version.Build;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is BadImageFormatException)
            {
                return string.Empty;
            }
        }

        private static string Architecture()
        {
            // Le processus est compilé en AnyCPU : ce qui compte pour choisir un fichier est la
            // largeur dans laquelle il tourne réellement, pas celle du système.
            var machine = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty;
            if (machine.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0) return "arm64";

            return Environment.Is64BitProcess ? "x64" : "x86";
        }

        /// <summary>
        /// Famille de Windows, et rien de plus fin.
        /// </summary>
        /// <remarks>
        /// La liste est fermée côté serveur comme ici. Une chaîne libre finirait par ressembler à
        /// une empreinte de machine, ce que cet échange s'interdit.
        /// </remarks>
        private static string OsFamily()
        {
            var version = Environment.OSVersion.Version;

            if (version.Major >= 10)
                return version.Build >= 22000 ? "win11" : "win10";

            if (version.Major == 6)
            {
                switch (version.Minor)
                {
                    case 1: return "win7";
                    case 2:
                    case 3: return "win8";
                }
            }

            return string.Empty;
        }
    }

    /// <summary>Ce qu'a donné une tentative d'installation.</summary>
    public sealed class InstallOutcome
    {
        /// <summary>La main a été passée à la nouvelle version : l'application doit se fermer.</summary>
        public bool ShouldQuit { get; init; }

        public bool Succeeded { get; init; }

        public string Message { get; init; } = string.Empty;

        /// <summary>Le fichier reçu n'était pas celui qui avait été annoncé.</summary>
        public bool Tampered { get; init; }

        public static InstallOutcome HandedOver()
            => new InstallOutcome
            {
                Succeeded = true,
                ShouldQuit = true,
                Message = "Mise à jour prête : le logiciel va redémarrer.",
            };

        public static InstallOutcome Failed(string message, bool tampered = false)
            => new InstallOutcome { Succeeded = false, Message = message, Tampered = tampered };
    }
}
