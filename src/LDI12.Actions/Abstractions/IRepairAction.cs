using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Platform;

namespace LDI12.Actions
{
    /// <summary>
    /// Tout ce dont une action a le droit de se servir. Comme pour les sondes, aucun accès
    /// direct à l'OS : les passerelles portent les délais, la journalisation et l'annulation.
    /// </summary>
    public sealed class ActionContext
    {
        public ActionContext(
            IPlatformInfo platform,
            IProcessRunner processes,
            IProcessLauncher launcher,
            IFileSystemGateway files,
            IRegistryGateway registry,
            ISystemRestoreGateway restore,
            ILdiLogger logger,
            SystemSnapshot? snapshot = null,
            IReadOnlyDictionary<string, string>? parameters = null,
            ISecretProtector? secrets = null)
        {
            Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            Processes = processes ?? throw new ArgumentNullException(nameof(processes));
            Launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            Files = files ?? throw new ArgumentNullException(nameof(files));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Restore = restore ?? throw new ArgumentNullException(nameof(restore));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Snapshot = snapshot;
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Secrets = secrets ?? NoSecretProtector.Instance;
        }

        public IPlatformInfo Platform { get; }
        public IProcessRunner Processes { get; }
        public IProcessLauncher Launcher { get; }
        public IFileSystemGateway Files { get; }
        public IRegistryGateway Registry { get; }
        public ISystemRestoreGateway Restore { get; }
        public ILdiLogger Logger { get; }

        /// <summary>Le chiffrement de Windows pour le compte sous lequel l'action tourne.</summary>
        public ISecretProtector Secrets { get; }

        /// <summary>
        /// Le dernier diagnostic, quand il existe.
        /// </summary>
        /// <remarks>
        /// Une action lit le diagnostic, elle ne le refait pas : la vérification du disque doit
        /// savoir quel volume porte Windows, et le redémarrage d'un service doit savoir dans quel
        /// état il a été trouvé. Nul si aucune analyse n'a encore été menée.
        /// </remarks>
        public SystemSnapshot? Snapshot { get; }

        /// <summary>Ce que le technicien a choisi dans l'écran : volume, service, dossiers à nettoyer.</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public string? Parameter(string key)
            => Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        public ActionContext With(IReadOnlyDictionary<string, string> parameters)
            => new ActionContext(Platform, Processes, Launcher, Files, Registry, Restore, Logger, Snapshot, parameters, Secrets);

        public ActionContext With(SystemSnapshot? snapshot)
            => new ActionContext(Platform, Processes, Launcher, Files, Registry, Restore, Logger, snapshot, Parameters, Secrets);
    }

    /// <summary>
    /// Unité d'intervention : elle modifie la machine.
    /// </summary>
    /// <remarks>
    /// Pendant en écriture de <c>IDiagnosticProbe</c>. La séparation est portée par le typage, et
    /// la prévisualisation obligatoire aussi : <see cref="ExecuteAsync"/> exige l'objet produit
    /// par <see cref="PreviewAsync"/>. Il n'existe donc aucun chemin de code par lequel une
    /// action puisse s'exécuter sans que ce qu'elle allait faire ait d'abord été établi.
    /// </remarks>
    public interface IRepairAction
    {
        ActionDescriptor Descriptor { get; }

        /// <summary>
        /// Peut-on l'exécuter sur cette machine ? Réponse immédiate, sans rien lancer :
        /// l'écran doit pouvoir griser une action et dire pourquoi avant tout clic.
        /// </summary>
        ActionReadiness CheckReadiness(ActionContext context);

        /// <summary>Établit ce qui sera fait, en mesurant l'état réel de la machine.</summary>
        Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken);

        Task<ActionOutcome> ExecuteAsync(
            ActionContext context,
            ActionPreview preview,
            IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken);
    }
}
