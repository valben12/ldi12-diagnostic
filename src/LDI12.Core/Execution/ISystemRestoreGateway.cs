using System.Threading;
using System.Threading.Tasks;

namespace LDI12.Core.Execution
{
    public enum RestorePointState
    {
        /// <summary>Point de restauration créé.</summary>
        Created = 0,

        /// <summary>La protection du système est désactivée sur le volume Windows.</summary>
        Disabled = 1,

        /// <summary>
        /// Windows refuse de créer un second point dans les 24 heures.
        /// </summary>
        /// <remarks>
        /// Ce n'est pas un échec : un point récent existe déjà et protège tout autant. Le dire
        /// ainsi évite au technicien de croire qu'il intervient sans filet.
        /// </remarks>
        Throttled = 2,

        /// <summary>Privilèges administrateur requis.</summary>
        RequiresElevation = 3,

        /// <summary>Fonction absente de cette édition ou de cette version de Windows.</summary>
        Unsupported = 4,

        Failed = 5,
    }

    public sealed class RestorePointResult
    {
        public RestorePointState State { get; init; }

        /// <summary>Phrase affichable telle quelle.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Vrai si la machine dispose bien d'un point de retour, qu'il vienne d'être créé ou non.</summary>
        public bool ProtectionInPlace => State == RestorePointState.Created || State == RestorePointState.Throttled;
    }

    /// <summary>
    /// Création d'un point de restauration système avant une intervention.
    /// </summary>
    /// <remarks>
    /// Passerelle dédiée plutôt qu'une méthode générique d'invocation WMI : ouvrir l'appel de
    /// méthodes WMI à tout le code ferait de la passerelle de lecture un canal d'écriture.
    /// </remarks>
    public interface ISystemRestoreGateway
    {
        Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken);
    }
}
