namespace LDI12.Core.Platform
{
    public enum ElevationState
    {
        Unknown = 0,

        /// <summary>Processus utilisateur standard. C'est le mode de lancement nominal.</summary>
        NotElevated = 1,

        /// <summary>Processus élevé après consentement UAC.</summary>
        Elevated = 2,

        /// <summary>
        /// Processus disposant des droits administrateur sans élévation :
        /// UAC désactivé, ou compte Administrateur intégré. À signaler, c'est un constat de sécurité.
        /// </summary>
        ElevatedByDefault = 3,
    }

    /// <summary>
    /// Point d'entrée unique vers tout ce que les modules doivent savoir de la plateforme.
    /// Aucun module ne détecte la version de Windows par lui-même.
    /// </summary>
    public interface IPlatformInfo
    {
        WindowsProfile Profile { get; }

        IFeatureRegistry Features { get; }

        ElevationState Elevation { get; }

        /// <summary>Vrai si le processus courant peut effectuer des opérations privilégiées.</summary>
        bool IsElevated { get; }
    }
}
