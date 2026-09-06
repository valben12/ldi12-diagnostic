using System;
using System.Threading;
using System.Threading.Tasks;

namespace LDI12.Core.Execution
{
    public enum ElevatedChannelState
    {
        /// <summary>Aucune élévation demandée pour l'instant.</summary>
        NotRequested = 0,

        /// <summary>Le processus courant est déjà administrateur : rien à ouvrir.</summary>
        AlreadyElevated = 1,

        /// <summary>Canal ouvert avec l'hôte élevé.</summary>
        Available = 2,

        /// <summary>L'utilisateur a refusé l'invite UAC.</summary>
        Refused = 3,

        Failed = 4,
    }

    /// <summary>
    /// Canal vers l'hôte de sondes élevé.
    /// </summary>
    /// <remarks>
    /// L'application principale reste <c>asInvoker</c> : le logiciel doit fonctionner en session
    /// utilisateur normale. Quand une opération exige réellement des privilèges, on ouvre une
    /// seule fois <c>LDI12.ProbeHost.exe --elevated</c> : une invite UAC pour toute la session,
    /// et non une par action.
    ///
    /// Le canal transporte des lignes de texte. Ce qu'elles signifient est l'affaire de
    /// <c>LDI12.Actions</c> : la couche plateforme connaît les tubes nommés et l'UAC, pas les
    /// actions.
    /// </remarks>
    public interface IElevatedChannel
    {
        ElevatedChannelState State { get; }

        /// <summary>Renseignée dès que l'état n'est ni <c>Available</c> ni <c>AlreadyElevated</c>.</summary>
        string? Reason { get; }

        /// <summary>
        /// Ouvre le canal si nécessaire. Provoque l'invite UAC au premier appel seulement ;
        /// les suivants réutilisent la session ouverte.
        /// </summary>
        Task<ElevatedChannelState> EnsureAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Envoie une requête et attend sa réponse. Les lignes intermédiaires (progression,
        /// journal) sont remises à <paramref name="onNotification"/> au fil de l'eau.
        /// </summary>
        /// <param name="onNotification">
        /// Reçoit chaque ligne et rend <c>true</c> quand elle était une notification : la lecture
        /// continue alors, et l'échange n'est pas terminé.
        /// <para>
        /// C'est un prédicat, et non une simple remise, parce que <b>seul l'appelant connaît le
        /// protocole</b>. Une version antérieure laissait la couche plateforme trancher en
        /// cherchant <c>"type"</c> dans le texte de la ligne : la recherche était sensible à la
        /// casse, le sérialiseur écrivait <c>"Type"</c>, et la première progression était donc
        /// rendue comme si elle était la réponse. L'application annonçait un échec pendant que
        /// l'action, elle, continuait de s'exécuter en session administrateur.
        /// </para>
        /// </param>
        Task<string> SendAsync(
            string request, Func<string, bool>? onNotification, TimeSpan timeout,
            CancellationToken cancellationToken);
    }
}
