using System;

namespace LDI12.Core.Execution
{
    public sealed class LaunchRequest
    {
        public LaunchRequest(string fileName, string arguments = "")
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Arguments = arguments ?? string.Empty;
        }

        public string FileName { get; }
        public string Arguments { get; }

        /// <summary>Demande l'élévation : une invite UAC s'affiche, que l'utilisateur peut refuser.</summary>
        public bool Elevated { get; init; }

        /// <summary>
        /// Programme lancé sans fenêtre.
        /// </summary>
        /// <remarks>
        /// Réservé aux programmes de ce logiciel qui travaillent pour lui : l'hôte de sondes est
        /// une application console, et son lancement faisait donc surgir une fenêtre noire à
        /// chaque analyse. Les consoles de Windows, elles, sont lancées <i>pour</i> être vues et
        /// ne passent jamais par ce chemin.
        /// </remarks>
        public bool Hidden { get; init; }
    }

    public sealed class LaunchResult
    {
        public bool Started { get; init; }
        public int? ProcessId { get; init; }

        /// <summary>L'utilisateur a refusé l'invite UAC. Ce n'est pas une erreur, c'est une réponse.</summary>
        public bool Refused { get; init; }

        public string? Reason { get; init; }

        public static LaunchResult Ok(int processId) => new LaunchResult { Started = true, ProcessId = processId };

        public static LaunchResult Denied() => new LaunchResult { Refused = true, Reason = "Élévation refusée." };

        public static LaunchResult Error(string reason) => new LaunchResult { Reason = reason };
    }

    /// <summary>
    /// Lance un outil et rend la main immédiatement : à distinguer de <see cref="IProcessRunner"/>,
    /// qui attend la fin et capture la sortie.
    /// </summary>
    /// <remarks>
    /// Les consoles Windows (services.msc, l'observateur d'événements) n'ont ni sortie à capturer
    /// ni fin à attendre : le technicien les garde ouvertes à côté du diagnostic.
    /// </remarks>
    public interface IProcessLauncher
    {
        LaunchResult Launch(LaunchRequest request);
    }
}
