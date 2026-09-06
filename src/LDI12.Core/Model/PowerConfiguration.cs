using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Plan d'alimentation actif.
    /// </summary>
    /// <remarks>
    /// Reconnu par son identifiant, qui ne dépend d'aucune langue, et non par son nom affiché :
    /// celui-ci est une chaîne indirecte pointant dans une ressource traduite. Un plan livré par
    /// un constructeur ou créé sur place ne correspond à aucun identifiant connu : il est alors
    /// <see cref="Custom"/>, et non rangé de force dans une case.
    /// </remarks>
    public enum PowerPlanKind
    {
        Unknown = 0,
        Balanced = 1,
        HighPerformance = 2,
        PowerSaver = 3,
        Ultimate = 4,
        Custom = 5,
    }

    /// <summary>Configuration du fichier d'échange.</summary>
    public enum PageFileMode
    {
        Unknown = 0,

        /// <summary>Windows choisit la taille, le réglage normal.</summary>
        SystemManaged = 1,

        /// <summary>Taille imposée par quelqu'un.</summary>
        FixedSize = 2,

        /// <summary>Aucun fichier d'échange : la machine n'a que sa mémoire vive.</summary>
        Disabled = 3,
    }

    /// <summary>
    /// Les réglages qui brident une machine sans rien casser.
    /// </summary>
    /// <remarks>
    /// <b>Ce sont les pannes qu'aucun contrôle matériel ne trouve.</b> Un portable dont le plan
    /// d'alimentation limite le processeur à cinquante pour cent fonctionne parfaitement : les
    /// disques sont sains, la mémoire est saine, les journaux sont vides, et la machine met deux
    /// fois plus de temps à tout faire. Ces réglages se posent en trois clics, souvent par un
    /// utilitaire d'optimisation, et personne ne se souvient de les avoir posés.
    /// </remarks>
    public sealed class PowerConfiguration
    {
        public Measured<PowerPlanKind> Plan { get; init; }

        /// <summary>Identifiant du plan, utile quand il n'est pas connu.</summary>
        public Measured<string> PlanId { get; init; }

        /// <summary>
        /// Fréquence maximale autorisée au processeur, en pourcentage, sur secteur.
        /// </summary>
        /// <remarks>
        /// Une valeur absente du registre n'est pas une valeur manquante : elle signifie que le
        /// réglage d'origine s'applique, c'est-à-dire cent pour cent. La mesure est donc rendue
        /// comme <see cref="DataSource.Inferred"/>, connue, mais déduite plutôt que lue.
        /// </remarks>
        public Measured<int> ProcessorMaximumOnAc { get; init; }

        public Measured<int> ProcessorMinimumOnAc { get; init; }

        /// <summary>Même réglage sur batterie, où un bridage est en revanche légitime.</summary>
        public Measured<int> ProcessorMaximumOnBattery { get; init; }

        public Measured<PageFileMode> PageFile { get; init; }

        /// <summary>Réglage du fichier d'échange, tel qu'il est écrit.</summary>
        public Measured<string> PageFileSetting { get; init; }
    }

    /// <summary>
    /// Entretien du stockage : ce que Windows fait, ou ne fait plus, pour les disques.
    /// </summary>
    public sealed class StorageMaintenance
    {
        /// <summary>
        /// Notification de suppression aux disques à mémoire flash.
        /// </summary>
        /// <remarks>
        /// Sans elle, un SSD ignore quels blocs sont libres et ralentit durablement à mesure
        /// qu'il se remplit. Comme pour le bridage du processeur, l'absence de valeur signifie
        /// le réglage d'origine, activée.
        /// </remarks>
        public Measured<bool> TrimEnabled { get; init; }
    }
}
