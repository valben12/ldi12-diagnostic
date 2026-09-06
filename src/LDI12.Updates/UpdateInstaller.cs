using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;

namespace LDI12.Updates
{
    /// <summary>
    /// La mise en place de la nouvelle version, et le retour à l'écran.
    /// </summary>
    /// <remarks>
    /// <b>Le problème, et la façon dont il est résolu.</b> Windows verrouille un exécutable tant
    /// qu'il tourne : un programme ne peut pas s'écraser lui-même. La solution habituelle est un
    /// fichier de commandes déposé dans le dossier temporaire, qui attend la fin du processus
    /// puis recopie. Elle a deux défauts sérieux : un fichier de commandes se lit dans la page
    /// de codes du terminal, donc un chemin contenant un accent (<c>Téléchargements</c>, par
    /// exemple) casse tout ; et le morceau de code qui remplace l'exécutable n'est vérifié par
    /// personne.
    /// <para>
    /// Ici, c'est <b>la nouvelle version elle-même</b> qui fait le travail. Elle a été téléchargée
    /// et son empreinte contrôlée ; on la lance depuis le dossier temporaire avec
    /// <c>--appliquer-maj</c>, elle attend que l'ancienne se termine, se recopie à sa place, et
    /// relance. Aucun script, aucune question d'encodage, et le code qui agit est celui dont on
    /// a vérifié l'empreinte.
    /// </para>
    /// <para>
    /// Rien de tout cela ne demande de privilèges, tant que l'emplacement de l'exécutable est
    /// accessible en écriture, ce que <see cref="CanReplace"/> vérifie <b>avant</b> de proposer
    /// quoi que ce soit. Sur une clé USB protégée en écriture, ou sous <c>Program Files</c> sans
    /// élévation, le logiciel le dit au lieu d'échouer à mi-parcours.
    /// </para>
    /// </remarks>
    public sealed class UpdateInstaller
    {
        private const string Category = "Updates";

        /// <summary>Argument par lequel une version fraîchement téléchargée prend la place de l'ancienne.</summary>
        public const string ApplySwitch = "--appliquer-maj";

        private readonly ILdiLogger _logger;

        public UpdateInstaller(ILdiLogger logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>Chemin de l'exécutable en cours d'exécution.</summary>
        public static string? RunningExecutable()
        {
            try
            {
                // MainModule plutôt que Assembly.Location : sous un hôte, le second peut désigner
                // une bibliothèque et non le fichier que l'utilisateur a lancé.
                using (var current = Process.GetCurrentProcess())
                {
                    var path = current.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException ||
                                       ex is System.ComponentModel.Win32Exception ||
                                       ex is NotSupportedException)
            {
                // Repli : suffisant pour un exécutable unique sans hôte.
            }

            try
            {
                var location = Assembly.GetEntryAssembly()?.Location;
                return string.IsNullOrWhiteSpace(location) ? null : location;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// L'exécutable peut-il être remplacé sur place.
        /// </summary>
        /// <remarks>
        /// Le test porte sur le dossier et non sur le fichier : un exécutable qui tourne est
        /// verrouillé en écriture, donc l'ouvrir échouerait même là où le remplacement
        /// fonctionnera parfaitement une fois le processus terminé. Ce qui compte est de pouvoir
        /// écrire à côté de lui.
        /// </remarks>
        public Measured<bool> CanReplace(string? executable = null)
        {
            var path = executable ?? RunningExecutable();

            if (string.IsNullOrWhiteSpace(path))
                return Measured.Missing<bool>(
                    "L'emplacement du logiciel n'a pas pu être déterminé.", DataSource.FileSystem);

            string? folder;
            try
            {
                folder = Path.GetDirectoryName(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                return Measured.Missing<bool>("Le chemin du logiciel est illisible.", DataSource.FileSystem);
            }

            if (string.IsNullOrEmpty(folder))
                return Measured.Missing<bool>("Le chemin du logiciel est illisible.", DataSource.FileSystem);

            var probe = Path.Combine(folder!, ".ldi12-maj-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }

                File.Delete(probe);
                return Measured.Ok(true, DataSource.FileSystem);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                TryDelete(probe);

                return Measured.Missing<bool>(
                    "Le logiciel est dans un emplacement où il ne peut pas se remplacer lui-même (" +
                    folder + "). Copiez la nouvelle version à la main, ou lancez le logiciel depuis " +
                    "un dossier où vous pouvez écrire.", DataSource.FileSystem);
            }
        }

        /// <summary>
        /// Lance la version téléchargée pour qu'elle prenne la place de celle-ci.
        /// </summary>
        /// <remarks>
        /// L'appelant doit terminer immédiatement après : tant que ce processus vit, la copie ne
        /// peut pas avoir lieu, et la nouvelle version attend.
        /// </remarks>
        public bool Handover(string stagedExecutable, string? target = null)
        {
            if (string.IsNullOrWhiteSpace(stagedExecutable)) throw new ArgumentNullException(nameof(stagedExecutable));

            var destination = target ?? RunningExecutable();
            if (string.IsNullOrWhiteSpace(destination))
            {
                _logger.Error(Category, "Mise en place impossible : emplacement du logiciel inconnu.");
                return false;
            }

            try
            {
                var arguments = ApplySwitch + " \"" + destination + "\" " +
                                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

                var start = new ProcessStartInfo(stagedExecutable, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                    CreateNoWindow = true,
                };

                Process.Start(start);
                _logger.Info(Category, "Mise en place confiée à la nouvelle version : " + stagedExecutable);
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                                       ex is InvalidOperationException || ex is IOException)
            {
                _logger.Error(Category, "La nouvelle version n'a pas pu être lancée.", ex);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Un fichier de test d'un octet qui survit n'est pas un incident.
            }
        }
    }

    /// <summary>Ce qu'a donné la mise en place, vue depuis la nouvelle version.</summary>
    public sealed class ApplyOutcome
    {
        public bool Succeeded { get; init; }

        public string Message { get; init; } = string.Empty;

        /// <summary>Chemin relancé, quand la mise en place a abouti.</summary>
        public string? Relaunched { get; init; }
    }

    /// <summary>
    /// Le travail que fait la nouvelle version quand on la lance avec <c>--appliquer-maj</c>.
    /// </summary>
    /// <remarks>
    /// Trois gestes, et une seule règle : ne jamais laisser la machine sans logiciel utilisable.
    /// Si la copie échoue, l'ancienne version est toujours là et on la relance : le technicien
    /// retrouve exactement l'outil qu'il avait, avec un message qui explique.
    /// </remarks>
    public static class UpdateApplier
    {
        private const string Category = "Updates";

        /// <summary>Au-delà, l'ancienne version ne se terminera pas : on renonce plutôt que d'attendre.</summary>
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Le verrou du fichier peut survivre quelques instants à la fin du processus.</summary>
        private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Lit les arguments de la ligne de commande. Nul si ce n'est pas une mise en place.</summary>
        public static (string Target, int ProcessId)? Parse(string[]? args)
        {
            if (args == null) return null;

            for (var index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], UpdateInstaller.ApplySwitch, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (index + 2 >= args.Length) return null;

                var target = args[index + 1];
                if (!int.TryParse(args[index + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    return null;

                return string.IsNullOrWhiteSpace(target) ? null : ((string, int)?)(target, pid);
            }

            return null;
        }

        public static async Task<ApplyOutcome> ApplyAsync(
            ILdiLogger logger, string target, int processId, CancellationToken cancellationToken)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var staged = UpdateInstaller.RunningExecutable();
            if (string.IsNullOrWhiteSpace(staged))
                return Fail(logger, "La nouvelle version n'a pas su déterminer son propre emplacement.", target);

            await WaitForExitAsync(processId, cancellationToken).ConfigureAwait(false);

            var copied = await CopyAsync(logger, staged!, target, cancellationToken).ConfigureAwait(false);
            if (!copied)
                return Fail(logger,
                    "La nouvelle version n'a pas pu prendre la place de l'ancienne : le fichier était " +
                    "encore utilisé, ou l'emplacement est protégé en écriture.", target);

            logger.Info(Category, "Nouvelle version mise en place : " + target);
            Relaunch(logger, target);

            // Le fichier temporaire ne s'efface pas lui-même : il est en cours d'exécution. Le
            // dossier de préparation est vidé au prochain démarrage de la version installée.
            return new ApplyOutcome
            {
                Succeeded = true,
                Relaunched = target,
                Message = "La mise à jour est en place.",
            };
        }

        /// <summary>
        /// Vide le dossier de préparation.
        /// </summary>
        /// <remarks>
        /// Appelé au démarrage ordinaire, jamais pendant la mise en place : à ce moment-là, le
        /// fichier qu'il faudrait effacer est celui qui exécute ce code.
        /// </remarks>
        public static void CleanStaging(ILdiLogger logger)
        {
            // Les deux emplacements possibles, et non le seul actif : un logiciel qui a été
            // lancé une fois depuis un dossier non inscriptible a pu déposer sa préparation dans
            // le dossier temporaire de la machine d'un client. Il la reprend en partant.
            Empty(logger, UpdateDownloader.Portable());
            Empty(logger, UpdateDownloader.Fallback);
        }

        private static void Empty(ILdiLogger logger, string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return;

            try
            {
                if (!Directory.Exists(folder)) return;

                foreach (var file in Directory.GetFiles(folder))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Un fichier encore verrouillé sera repris au prochain démarrage.
                    }
                }

                // Le dossier lui-même part avec : sur une clé, il ne doit rien rester d'autre
                // que l'exécutable et le réglage de mise à jour.
                if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                logger.Warn(Category, "Le dossier de préparation n'a pas pu être vidé : " + folder);
            }
        }

        private static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + ExitTimeout;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (var process = Process.GetProcessById(processId))
                    {
                        if (process.HasExited) return;
                    }
                }
                catch (ArgumentException)
                {
                    // Le processus n'existe plus : c'est exactement ce qu'on attendait.
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<bool> CopyAsync(
            ILdiLogger logger, string staged, string target, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + CopyTimeout;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    File.Copy(staged, target, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    logger.Debug(Category, "Copie repoussée : " + ex.Message);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }

        private static void Relaunch(ILdiLogger logger, string target)
        {
            try
            {
                var start = new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(target) ?? Path.GetTempPath(),
                };

                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                                       ex is InvalidOperationException || ex is IOException)
            {
                logger.Error(Category, "Le logiciel n'a pas pu être relancé après la mise à jour.", ex);
            }
        }

        /// <summary>
        /// Échec de mise en place : l'ancienne version est intacte, on la remet à l'écran.
        /// </summary>
        /// <remarks>
        /// C'est la règle qui compte le plus dans ce fichier. Laisser un technicien devant une
        /// machine sans outil, chez un client, parce qu'une mise à jour a échoué, serait un
        /// défaut bien plus grave que la mise à jour manquée elle-même.
        /// </remarks>
        private static ApplyOutcome Fail(ILdiLogger logger, string message, string target)
        {
            logger.Error(Category, "Mise en place refusée : " + message);

            if (File.Exists(target)) Relaunch(logger, target);

            return new ApplyOutcome { Succeeded = false, Message = message };
        }
    }
}
