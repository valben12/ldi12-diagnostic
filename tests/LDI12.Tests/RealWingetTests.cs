using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Backup;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le relevé des applications, confronté au vrai winget plutôt qu'à un faux.
    /// </summary>
    /// <remarks>
    /// Le défaut qui a motivé ce fichier ne se voit sur aucun double : winget avait été appelé
    /// correctement, avec un chemin existant et des guillemets en place, et il refusait quand même
    /// d'écrire. C'est sa lecture de l'argument qui était en cause, pas la nôtre. Un essai qui ne
    /// lance pas le vrai outil ne pouvait pas le montrer.
    /// </remarks>
    public class RealWingetTests
    {
        [WingetFact]
        public async Task Le_releve_aboutit_meme_quand_le_dossier_temporaire_est_accentue()
        {
            // « Françoise & Patrick » : le vrai nom du compte Windows sur lequel le relevé échouait,
            // avec son accent, son esperluette et ses espaces. L'accent seul suffit à faire rendre
            // à winget « 0x80070003 : le chemin d'accès spécifié est introuvable ».
            var racine = Path.Combine(Path.GetTempPath(), "ldi12-essai-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var dossier = Path.Combine(racine, "Françoise & Patrick", "AppData", "Local", "Temp");
            var export = Path.Combine(dossier, "ldi12-winget-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                var listing = await WingetApplications.ListAsync(
                    new ProcessRunner(NullLogger.Instance),
                    new FileSystemGateway(NullLogger.Instance),
                    export,
                    CancellationToken.None);

                Assert.True(File.Exists(export),
                    "winget devait écrire sa liste dans « " + export + " ». Échec rapporté : " + listing.Failure);

                // Une machine peut n'avoir aucun logiciel connu de winget : c'est un relevé vide,
                // pas une panne. Seul un échec rapporté condamne.
                Assert.True(listing.Failure == null || listing.Failure.Contains("reconnaît aucun"),
                    "Relevé en échec : " + listing.Failure);
            }
            finally
            {
                try { Directory.Delete(racine, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Ne tourne que là où winget existe : il n'est pas sur toutes les machines.</summary>
        private sealed class WingetFactAttribute : FactAttribute
        {
            public WingetFactAttribute()
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    Skip = "winget n'existe que sous Windows.";
                    return;
                }

                if (!Present()) Skip = "winget n'est pas installé sur cette machine.";
            }

            private static bool Present()
            {
                try
                {
                    var chemins = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';');
                    foreach (var dossier in chemins)
                    {
                        if (dossier.Length == 0) continue;
                        if (File.Exists(Path.Combine(dossier.Trim('"'), "winget.exe"))) return true;
                    }

                    return false;
                }
                catch (ArgumentException) { return false; }
                catch (IOException) { return false; }
            }
        }
    }
}
