using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Logging;
using LDI12.Core.Updates;

namespace LDI12.Updates
{
    /// <summary>Ce qu'a donné un téléchargement.</summary>
    public sealed class DownloadOutcome
    {
        public bool Succeeded { get; init; }

        /// <summary>Chemin du fichier obtenu, quand tout s'est bien passé.</summary>
        public string? Path { get; init; }

        /// <summary>Phrase écrite pour être affichée telle quelle.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// L'empreinte du fichier reçu ne correspond pas à celle annoncée.
        /// </summary>
        /// <remarks>
        /// Distingué d'un simple échec parce que la réaction n'est pas la même : une coupure se
        /// retente, un fichier qui n'est pas celui annoncé décrit un réseau où quelqu'un
        /// s'interpose, et rien ne doit être exécuté.
        /// </remarks>
        public bool Tampered { get; init; }

        public static DownloadOutcome Ok(string path)
            => new DownloadOutcome { Succeeded = true, Path = path, Message = "Fichier vérifié." };

        public static DownloadOutcome Failed(string message)
            => new DownloadOutcome { Succeeded = false, Message = message };
    }

    /// <summary>
    /// Le téléchargement de la nouvelle version, et le contrôle de ce qui a été reçu.
    /// </summary>
    /// <remarks>
    /// <b>L'empreinte est calculée pendant l'écriture, pas après.</b> Relire le fichier une
    /// seconde fois laisserait un intervalle, court mais réel, pendant lequel il pourrait être
    /// remplacé. Ici, ce qui est haché est exactement le flot d'octets qui part sur le disque.
    /// <para>
    /// Un fichier dont l'empreinte ne correspond pas est effacé sur-le-champ : il ne doit pas
    /// rester un exécutable douteux dans le dossier temporaire d'un client.
    /// </para>
    /// </remarks>
    public sealed class UpdateDownloader : IDisposable
    {
        private const string Category = "Updates";

        /// <summary>Un exécutable de ce logiciel pèse une douzaine de mégaoctets ; au-delà, ce n'en est pas un.</summary>
        private const long MaximumSize = 200L * 1024 * 1024;

        private readonly ILdiLogger _logger;
        private readonly HttpMessageHandler? _handler;

        private HttpClient? _client;

        public UpdateDownloader(ILdiLogger logger, HttpMessageHandler? handler = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handler = handler;
        }

        /// <summary>
        /// Dossier où la nouvelle version est déposée avant d'être mise en place.
        /// </summary>
        /// <remarks>
        /// <b>À côté de l'exécutable d'abord, et c'est le cas qui compte.</b> Ce logiciel se
        /// transporte sur une clé USB et se branche chez des clients : y télécharger douze
        /// mégaoctets dans le dossier temporaire de leur machine laisserait un exécutable
        /// derrière soi, sur un poste qui ne nous appartient pas. Déposé sur la clé, tout reste
        /// sur la clé, et la copie qui suit se fait d'un dossier à l'autre du même volume.
        /// <para>
        /// Le repli sur le dossier temporaire ne sert que lorsque l'emplacement du logiciel n'est
        /// pas inscriptible : dans ce cas la mise à jour ne pourra de toute façon pas se poser
        /// seule, et <see cref="UpdateInstaller.CanReplace"/> l'aura déjà dit.
        /// </para>
        /// </remarks>
        public static string StagingFolder => Portable() ?? Fallback;

        /// <summary>Dossier de repli, sur la machine qui exécute le logiciel.</summary>
        public static string Fallback
            => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LDI12", "maj");

        /// <summary>Dossier voisin de l'exécutable, ou <c>null</c> si l'on ne peut pas y écrire.</summary>
        internal static string? Portable()
        {
            try
            {
                var executable = UpdateInstaller.RunningExecutable();
                var folder = executable == null ? null : System.IO.Path.GetDirectoryName(executable);
                if (string.IsNullOrEmpty(folder)) return null;

                var candidate = System.IO.Path.Combine(folder!, ".ldi12-maj");
                var directory = Directory.CreateDirectory(candidate);

                // Discret sur une clé qu'un client peut ouvrir : ce dossier n'est pas le sien.
                try
                {
                    directory.Attributes |= FileAttributes.Hidden;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Un système de fichiers sans attribut caché, certaines clés en exFAT
                    // montées par un pilote tiers. Sans conséquence.
                }

                var probe = System.IO.Path.Combine(candidate, ".test");
                using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.WriteByte(0);

                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return null;
            }
        }

        public async Task<DownloadOutcome> DownloadAsync(
            UpdateDownload download, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (download == null) throw new ArgumentNullException(nameof(download));

            if (string.IsNullOrWhiteSpace(download.Sha256) || download.Sha256.Length != 64)
                return DownloadOutcome.Failed(
                    "Le serveur n'a pas annoncé d'empreinte pour ce fichier : rien n'est téléchargé.");

            if (download.Size > MaximumSize)
                return DownloadOutcome.Failed(
                    "Le fichier annoncé est anormalement gros : rien n'est téléchargé.");

            string target;
            try
            {
                Directory.CreateDirectory(StagingFolder);
                target = System.IO.Path.Combine(StagingFolder, SafeName(download.FileName));
                if (File.Exists(target)) File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException)
            {
                return DownloadOutcome.Failed(
                    "Le dossier temporaire n'est pas accessible : " + ex.Message);
            }

            try
            {
                var hash = await FetchAsync(download, target, progress, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(hash, download.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Discard(target);
                    _logger.Error(Category,
                        "Empreinte du fichier téléchargé différente de celle annoncée : fichier effacé.");

                    return new DownloadOutcome
                    {
                        Succeeded = false,
                        Tampered = true,
                        Message = "Le fichier reçu n'est pas celui que le serveur avait annoncé. Il a été " +
                                  "effacé, et rien n'a été installé. Sur un réseau d'entreprise ou public, " +
                                  "cela arrive quand un équipement modifie les téléchargements.",
                    };
                }

                _logger.Info(Category, "Nouvelle version téléchargée et vérifiée : " + target);
                return DownloadOutcome.Ok(target);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Discard(target);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException ||
                                       ex is OperationCanceledException ||
                                       ex is UnauthorizedAccessException)
            {
                Discard(target);
                return DownloadOutcome.Failed(
                    "Le téléchargement s'est interrompu. Réessayez, ou récupérez le fichier depuis " +
                    "ldi12.fr.");
            }
        }

        /// <summary>Récupère le fichier et rend son empreinte, calculée au fil de l'écriture.</summary>
        private async Task<string> FetchAsync(
            UpdateDownload download, string target, IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using (var response = await Client()
                       .GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var expected = response.Content.Headers.ContentLength ?? download.Size;

                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                           81920, useAsync: true))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    long written = 0;

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0) break;

                        written += read;
                        if (written > MaximumSize)
                            throw new IOException("Le fichier dépasse la taille attendue.");

                        sha.TransformBlock(buffer, 0, read, null, 0);
                        await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);

                        if (progress != null && expected > 0)
                            progress.Report(Math.Min(1d, (double)written / expected));
                    }

                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return Hex(sha.Hash);
                }
            }
        }

        /// <summary>
        /// Le nom de fichier annoncé, ramené à ce qu'il prétend être.
        /// </summary>
        /// <remarks>
        /// Le manifeste est signé, donc ce nom vient bien du serveur, mais un nom qui
        /// contiendrait un chemin écrirait ailleurs que dans le dossier temporaire. La règle est
        /// simple et ne coûte rien : on ne garde que le nom, et l'extension est imposée.
        /// </remarks>
        internal static string SafeName(string? announced)
        {
            var name = string.IsNullOrWhiteSpace(announced)
                ? "LDI12-Diagnostic.exe"
                : System.IO.Path.GetFileName(announced!.Trim());

            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            if (name.Length == 0) name = "LDI12-Diagnostic.exe";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            return name;
        }

        internal static string Hex(byte[]? bytes)
        {
            if (bytes == null) return string.Empty;

            var text = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private void Discard(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(Category, "Le fichier téléchargé n'a pas pu être effacé : " + path);
            }
        }

        private HttpClient Client()
        {
            if (_client != null) return _client;

            HttpUpdateChecker.EnableModernTls();

            _client = _handler == null ? new HttpClient() : new HttpClient(_handler, false);

            // Plus généreux que la vérification : douze mégaoctets sur une liaison lente
            // demandent plus de cinq secondes, et ce téléchargement-ci a été demandé.
            _client.Timeout = TimeSpan.FromMinutes(10);
            _client.DefaultRequestHeaders.Add("User-Agent", "LDI12-Diagnostic");

            return _client;
        }

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
