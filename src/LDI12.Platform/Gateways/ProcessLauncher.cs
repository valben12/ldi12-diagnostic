using System;
using System.ComponentModel;
using System.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Lance un outil et rend la main. Ne capture rien : ces outils ont leur propre fenêtre.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute</c> est indispensable ici, et pour deux raisons : c'est ce qui permet
    /// d'ouvrir un fichier <c>.msc</c> ou <c>.cpl</c> sans invoquer <c>mmc.exe</c> à la main, et
    /// c'est le seul chemin par lequel le verbe <c>runas</c> déclenche l'invite UAC.
    /// <para>
    /// Un lancement discret s'en passe quand il le peut : sans le shell, la fenêtre n'est pas
    /// créée du tout, alors que <c>WindowStyle</c> ne fait que la masquer, et la masquer laisse
    /// tout de même apparaître un cadre noir le temps d'une image. Ce chemin sans shell n'est
    /// pas ouvert à un lancement élevé, où seul le verbe <c>runas</c> obtient l'invite UAC.
    /// </para>
    /// </remarks>
    public sealed class ProcessLauncher : IProcessLauncher
    {
        private const string Category = "Platform.Launcher";

        /// <summary>Code d'erreur Win32 rendu quand l'utilisateur ferme l'invite UAC.</summary>
        private const int ErrorCancelled = 1223;

        private readonly IScopedLogger _log;

        public ProcessLauncher(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public LaunchResult Launch(LaunchRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var shell = request.Elevated || !request.Hidden;

            var startInfo = new ProcessStartInfo(request.FileName, request.Arguments)
            {
                UseShellExecute = shell,
                CreateNoWindow = request.Hidden,
            };

            if (request.Hidden) startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            if (request.Elevated) startInfo.Verb = "runas";

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    // Le shell a rendu la main sans créer de processus : l'outil a été
                    // rattaché à une instance déjà ouverte, ce qui est un succès.
                    _log.Debug("Lancement de " + request.FileName + " confié à une instance existante.");
                    return LaunchResult.Ok(0);
                }

                _log.Info("Lancé : " + request.FileName + " " + request.Arguments);
                return LaunchResult.Ok(process.Id);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                _log.Info("Élévation refusée pour " + request.FileName + ".");
                return LaunchResult.Denied();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException ||
                                       ex is System.IO.FileNotFoundException)
            {
                _log.Warn("Lancement impossible de " + request.FileName + ".", ex);
                return LaunchResult.Error(ex.Message);
            }
        }
    }
}
