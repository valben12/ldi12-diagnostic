namespace LDI12.Core.Diagnostics
{
    /// <summary>
    /// État de disponibilité d'une donnée ou d'une fonctionnalité.
    /// C'est la traduction en code des indicateurs ✅ / ⚠️ / ❌ de l'interface.
    /// </summary>
    /// <remarks>
    /// <see cref="Unknown"/> vaut 0 pour que la valeur par défaut d'une structure
    /// non initialisée ne se fasse jamais passer pour une donnée disponible.
    /// </remarks>
    public enum Availability
    {
        /// <summary>Non déterminé, la collecte n'a pas encore eu lieu.</summary>
        Unknown = 0,

        /// <summary>✅ Donnée obtenue et fiable.</summary>
        Available = 1,

        /// <summary>⚠️ Donnée obtenue mais incomplète ou approximative.</summary>
        Partial = 2,

        /// <summary>🔒 Donnée accessible, mais uniquement avec des privilèges administrateur.</summary>
        RequiresElevation = 3,

        /// <summary>❌ Donnée impossible à obtenir sur cette machine ou cette version de Windows.</summary>
        Unavailable = 4,
    }

    /// <summary>
    /// Provenance d'une donnée. Affichée dans le niveau « données techniques » du rapport :
    /// un chiffre dont on ne peut pas dire d'où il vient n'est pas défendable devant un client.
    /// </summary>
    public enum DataSource
    {
        Unknown = 0,

        /// <summary>API Win32 / NT appelée directement. Source privilégiée : rapide et non localisée.</summary>
        NativeApi = 1,

        /// <summary>Registre Windows.</summary>
        Registry = 2,

        /// <summary>WMI / CIM. Riche mais lent et faillible sur machine dégradée.</summary>
        Wmi = 3,

        /// <summary>Sortie d'un outil en ligne de commande. Dernier recours (sortie localisée).</summary>
        Cli = 4,

        /// <summary>Compteur de performance Windows.</summary>
        PerformanceCounter = 5,

        /// <summary>Journal d'événements Windows.</summary>
        EventLog = 6,

        /// <summary>Système de fichiers (présence, taille, date, contenu de journal).</summary>
        FileSystem = 7,

        /// <summary>Valeur déduite d'autres mesures plutôt que lue directement.</summary>
        Inferred = 8,
    }

    /// <summary>
    /// Gravité d'un constat. Les définitions opérationnelles sont fixées dans
    /// docs/04-moteur-diagnostic.md et ne doivent pas être réinterprétées par règle.
    /// </summary>
    public enum Severity
    {
        /// <summary>Constat notable, aucune action attendue.</summary>
        Info = 0,

        /// <summary>Peut dégrader l'expérience, à surveiller.</summary>
        Warning = 1,

        /// <summary>Cause identifiée d'un dysfonctionnement, action recommandée.</summary>
        Problem = 2,

        /// <summary>Risque de perte de données ou d'arrêt de la machine.</summary>
        Critical = 3,
    }

    /// <summary>Dimension de diagnostic. Sert aussi d'axe de scoring.</summary>
    public enum DiagnosticCategory
    {
        /// <summary>Plateforme et compatibilité : n'entre pas dans le score.</summary>
        Platform = 0,
        Hardware = 1,
        Storage = 2,
        Windows = 3,
        Network = 4,
        Security = 5,
        Performance = 6,
    }
}
