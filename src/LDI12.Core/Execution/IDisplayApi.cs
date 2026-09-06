using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Une sortie graphique active, telle que le pilote la présente.</summary>
    public sealed class AttachedDisplay
    {
        /// <summary>« \\.\DISPLAY1 ».</summary>
        public string Output { get; init; } = string.Empty;

        /// <summary>
        /// Identifiant de la dalle, normalisé pour être comparable à celui du registre.
        /// </summary>
        /// <remarks>
        /// De la forme <c>ACR052C\5&amp;22dd8a13&amp;0&amp;UID4352</c>. C'est la clé qui relie ce
        /// que le pilote affiche à ce que la dalle déclare dans son EDID : sans elle, on sait
        /// qu'un écran est en 1920 × 1080 sans savoir si c'est sa définition.
        /// </remarks>
        public string MonitorId { get; init; } = string.Empty;

        public bool IsPrimary { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int RefreshHz { get; init; }

        /// <summary>Fréquence la plus élevée que le pilote propose à cette définition.</summary>
        public int MaxRefreshHz { get; init; }
    }

    /// <summary>
    /// Ce que le pilote graphique envoie réellement à chaque écran.
    /// </summary>
    /// <remarks>
    /// Par l'API native et non par WMI : <c>Win32_VideoController</c> décrit une carte, pas une
    /// sortie, et rend un seul mode même quand trois écrans sont branchés. Or la question posée :
    /// « cet écran-là est-il à sa définition ? », se pose écran par écran.
    /// </remarks>
    public interface IDisplayApi
    {
        Measured<IReadOnlyList<AttachedDisplay>> ReadAttached();
    }
}
