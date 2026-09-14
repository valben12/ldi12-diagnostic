using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Ce que contient réellement un profil Wi-Fi exporté.</summary>
    public enum WifiKeyState
    {
        /// <summary>La clé figure en clair : le profil se réimporte sans la redemander.</summary>
        Clear = 0,

        /// <summary>
        /// La clé figure chiffrée pour cette machine.
        /// </summary>
        /// <remarks>
        /// C'est ce que rend Windows quand l'export n'est pas mené avec les droits
        /// d'administrateur. Le fichier se réimporte, mais la clé ne se déchiffre pas ailleurs.
        /// </remarks>
        Protected = 1,

        /// <summary>Réseau sans clé : ouvert, ou authentifié par un compte d'entreprise.</summary>
        None = 2,
    }

    public sealed class WifiProfile
    {
        public string Name { get; init; } = string.Empty;

        public WifiKeyState Key { get; init; }
    }

    public sealed class WifiExportResult
    {
        public IReadOnlyList<WifiProfile> Profiles { get; init; } = Array.Empty<WifiProfile>();

        /// <summary>Pourquoi rien n'a été exporté, quand rien ne l'a été.</summary>
        public string? Failure { get; init; }

        public int WithClearKey
        {
            get
            {
                var count = 0;
                foreach (var profile in Profiles) if (profile.Key == WifiKeyState.Clear) count++;
                return count;
            }
        }

        public int WithProtectedKey
        {
            get
            {
                var count = 0;
                foreach (var profile in Profiles) if (profile.Key == WifiKeyState.Protected) count++;
                return count;
            }
        }

        public string Describe()
        {
            if (Profiles.Count == 0) return Failure ?? "Aucun profil Wi-Fi exporté.";

            var text = Profiles.Count + " profil(s) Wi-Fi exporté(s)";
            if (WithClearKey > 0) text += ", " + WithClearKey + " avec leur clé en clair";
            if (WithProtectedKey > 0)
                text += ", " + WithProtectedKey + " avec une clé chiffrée pour cette machine seulement " +
                        "(les droits administrateur sont nécessaires pour la lire en clair)";
            return text + ".";
        }
    }

    /// <summary>
    /// Exporte les profils Wi-Fi enregistrés, avec leurs clés.
    /// </summary>
    /// <remarks>
    /// <b>Une option, décochée par défaut.</b> Les clés sortent en clair dans des fichiers XML,
    /// sur le support de sauvegarde : quiconque trouve la clé USB trouve le code du Wi-Fi du
    /// client, et parfois celui de son entreprise. Ce choix appartient au technicien, en
    /// connaissance de cause, jamais à un réglage par défaut.
    /// <para>
    /// Ce qui a été exporté est relu fichier par fichier, et c'est cette relecture qui dit si la
    /// clé est en clair : Windows ne la livre ainsi qu'avec les droits d'administrateur, et le
    /// logiciel ne présume pas de ceux qu'il a.
    /// </para>
    /// </remarks>
    internal static class WifiExport
    {
        public const string Folder = "Wi-Fi";

        private static readonly Regex NameElement =
            new Regex(@"<name>\s*(?<v>[^<]*?)\s*</name>", RegexOptions.CultureInvariant);

        private static readonly Regex ProtectedElement =
            new Regex(@"<protected>\s*(?<v>true|false)\s*</protected>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static async Task<WifiExportResult> RunAsync(
            ActionContext context, string destination, CancellationToken cancellationToken)
        {
            var folder = Path.Combine(destination, Folder);
            if (!context.Files.CreateDirectory(folder))
                return new WifiExportResult { Failure = "Le dossier « " + Folder + " » n'a pas pu être créé sur la destination." };

            // netsh écrit lui-même les fichiers, sans passer par la passerelle : le dossier est
            // neuf, dans une sauvegarde horodatée à la minute, et rien d'autre n'y est rangé.
            var run = await context.Processes.RunAsync(
                new ProcessRequest("netsh.exe", "wlan export profile folder=\"" + folder + "\" key=clear")
                {
                    Timeout = TimeSpan.FromSeconds(60),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            var profiles = new List<WifiProfile>();
            var scan = context.Files.Scan(
                new DirectoryScanRequest(folder) { TopLevelOnly = true, MaxFiles = 2000, Budget = TimeSpan.FromSeconds(15) },
                cancellationToken);

            if (scan.HasValue)
                foreach (var file in scan.Value.Files)
                {
                    if (!file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                    var text = context.Files.ReadText(file.Path);
                    if (text.HasValue) profiles.Add(Parse(text.Value, Path.GetFileNameWithoutExtension(file.Path)));
                }

            profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            if (profiles.Count > 0) return new WifiExportResult { Profiles = profiles };

            return new WifiExportResult { Failure = Explain(run) };
        }

        internal static WifiProfile Parse(string xml, string fallbackName)
        {
            var name = NameElement.Match(xml);
            var hasKey = xml.IndexOf("<keyMaterial>", StringComparison.OrdinalIgnoreCase) >= 0;
            var protection = ProtectedElement.Match(xml);

            var key = !hasKey
                ? WifiKeyState.None
                : protection.Success && string.Equals(protection.Groups["v"].Value, "false", StringComparison.OrdinalIgnoreCase)
                    ? WifiKeyState.Clear
                    : WifiKeyState.Protected;

            return new WifiProfile
            {
                Name = name.Success && name.Groups["v"].Value.Length > 0 ? Unescape(name.Groups["v"].Value) : fallbackName,
                Key = key,
            };
        }

        /// <summary>
        /// Pourquoi rien n'est sorti.
        /// </summary>
        /// <remarks>
        /// Le cas le plus courant n'est pas une panne : une machine sans carte Wi-Fi n'a pas de
        /// service de réseau sans fil, et netsh le dit dans la langue du système. Son message est
        /// repris tel quel plutôt que traduit par une supposition.
        /// </remarks>
        private static string Explain(ProcessResult run)
        {
            if (run.LaunchFailed) return "L'outil netsh n'a pas pu être lancé : aucun profil Wi-Fi exporté.";
            if (run.TimedOut) return "L'outil netsh n'a pas répondu dans le délai : aucun profil Wi-Fi exporté.";

            var message = Repair(FirstLine(run.StandardOutput) ?? FirstLine(run.StandardError));
            return message == null
                ? "Aucun profil Wi-Fi n'est enregistré pour cette machine."
                : "Aucun profil Wi-Fi exporté. Windows répond : « " + message + " »";
        }

        private static string? FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var lines = new List<string>();
            foreach (var line in text.Split('\r', '\n'))
                if (line.Trim().Length > 0) lines.Add(line.Trim());

            return lines.Count == 0 ? null : string.Join(" ", lines.ToArray());
        }

        /// <summary>
        /// Rend lisible un message écrit en UTF-8 et lu comme de l'ANSI.
        /// </summary>
        /// <remarks>
        /// Relevé à l'essai réel, sous Windows 11 : lancé depuis l'application, qui n'a pas de
        /// console, netsh a répondu en UTF-8, et la page de code OEM demandée s'est résolue en
        /// Windows-1252. « réseaux » arrivait en « rÃ©seaux ». Le motif est sans ambiguïté :
        /// ces paires de caractères n'apparaissent pas dans un message français correct. Le texte
        /// est alors ré-encodé en 1252 et relu en UTF-8 ; si la relecture échoue, il reste tel quel.
        /// </remarks>
        internal static string? Repair(string? text)
        {
            if (text == null || (text.IndexOf('Ã') < 0 && text.IndexOf("â€", StringComparison.Ordinal) < 0)) return text;

            try
            {
                var bytes = System.Text.Encoding.GetEncoding(1252).GetBytes(text);
                return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                return text;
            }
        }

        private static string Unescape(string value)
            => value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                .Replace("&apos;", "'").Replace("&amp;", "&");
    }
}
