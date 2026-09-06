using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Où une entrée de désinstallation a été trouvée.</summary>
    public enum SoftwareScope
    {
        /// <summary>Installé pour tous les utilisateurs, vue 64 bits.</summary>
        Machine = 0,

        /// <summary>Installé pour tous les utilisateurs, vue 32 bits d'un Windows 64 bits.</summary>
        Machine32 = 1,

        /// <summary>
        /// Installé pour le seul utilisateur courant.
        /// </summary>
        /// <remarks>
        /// La distinction compte en dépannage : ces programmes n'apparaissent pas dans
        /// « Programmes et fonctionnalités » ouvert depuis un autre compte, et c'est là que se
        /// logent les installations que personne n'assume : celles qui arrivent par un
        /// téléchargement plutôt que par une décision.
        /// </remarks>
        User = 2,
    }

    /// <summary>Un programme installé, tel que le registre le déclare.</summary>
    public sealed class InstalledProgram
    {
        public string Name { get; init; } = string.Empty;

        public string? Publisher { get; init; }

        public string? Version { get; init; }

        public SoftwareScope Scope { get; init; }

        /// <summary>
        /// Date d'installation déclarée.
        /// </summary>
        /// <remarks>
        /// Manquante une fois sur trois : beaucoup d'installeurs ne l'écrivent pas. C'est une
        /// mesure comme une autre : absente, elle le dit, et rien ne la reconstitue.
        /// </remarks>
        public Measured<DateTimeOffset> InstalledOn { get; init; }

        /// <summary>
        /// Encombrement déclaré par l'installeur.
        /// </summary>
        /// <remarks>
        /// Toujours <see cref="Availability.Partial"/> : c'est une valeur écrite une fois pour
        /// toutes au moment de l'installation, que rien ne met à jour ensuite. Elle donne un
        /// ordre de grandeur, jamais une taille.
        /// </remarks>
        public Measured<long> EstimatedSizeBytes { get; init; }

        /// <summary>Clé de registre d'origine, la seule façon de retrouver l'entrée exacte.</summary>
        public string RegistryKey { get; init; } = string.Empty;
    }

    /// <summary>
    /// Ce qui est installé sur la machine.
    /// </summary>
    /// <remarks>
    /// <b>Lu dans le registre, jamais par <c>Win32_Product</c>.</b> Énumérer cette classe WMI
    /// déclenche une reconfiguration MSI de chaque logiciel installé : jusqu'à vingt minutes de
    /// blocage, et des installations cassées au passage. La clé <c>Uninstall</c> donne la même
    /// liste en quelques dizaines de millisecondes, et c'est celle que lit « Programmes et
    /// fonctionnalités ».
    /// </remarks>
    public sealed class SoftwareInventory
    {
        public IReadOnlyList<InstalledProgram> Programs { get; init; } = Array.Empty<InstalledProgram>();

        /// <summary>
        /// Entrées écartées parce qu'elles décrivent une mise à jour ou un composant système.
        /// </summary>
        /// <remarks>
        /// Comptées et non masquées : une machine dont la liste tombe de trois cents entrées à
        /// quatre-vingts n'a pas perdu deux cent vingt logiciels, et le technicien qui compare
        /// avec « Programmes et fonctionnalités » doit pouvoir retrouver ses chiffres.
        /// </remarks>
        public Measured<int> HiddenEntries { get; init; }

        /// <summary>Ce que l'inventaire n'a pas pu voir, et pourquoi.</summary>
        public string? Limitation { get; init; }
    }
}
