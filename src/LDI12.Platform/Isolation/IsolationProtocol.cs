using System;
using System.Collections.Generic;

namespace LDI12.Platform.Isolation
{
    /// <summary>
    /// Protocole du canal d'isolation : une ligne de JSON par message, dans les deux sens.
    /// </summary>
    /// <remarks>
    /// Aussi pauvre que celui du canal élevé, et pour une raison voisine : l'hôte isolé n'accepte
    /// que des <b>identifiants de sondes de son propre catalogue</b>. Il n'exécute jamais du code
    /// qu'on lui envoie, et les sections qu'il reçoit sont reconstruites sous des types nommés
    /// par le noyau, jamais d'après un nom de type transmis.
    /// <para>
    /// Chaque section voyage sous forme de texte JSON déjà sérialisé plutôt que d'objet
    /// polymorphe : le message extérieur reste un type fixe, et rien dans le flux ne peut décider
    /// de ce qui sera instancié à l'arrivée.
    /// </para>
    /// </remarks>
    public static class IsolationProtocol
    {
        public const string OpRun = "run";
        public const string OpClose = "close";

        /// <summary>Argument attendu par <c>LDI12.ProbeHost.exe</c> pour tenir ce rôle.</summary>
        public const string HostArgument = "--isolate";
    }

    public sealed class IsolationSection
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Valeur de la section, déjà sérialisée.</summary>
        public string Json { get; set; } = string.Empty;
    }

    public sealed class IsolationRequest
    {
        public string Op { get; set; } = string.Empty;

        /// <summary>Identifiant de la sonde à exécuter, tel qu'il figure au catalogue.</summary>
        public string? Probe { get; set; }

        public string? Mode { get; set; }

        /// <summary>Sections déjà collectées, dont la sonde peut dépendre.</summary>
        public List<IsolationSection>? Input { get; set; }
    }

    public sealed class IsolationResponse
    {
        public string Status { get; set; } = string.Empty;

        public string? Message { get; set; }

        public string? Exception { get; set; }

        /// <summary>Temps passé dans la sonde elle-même, mesuré chez l'hôte.</summary>
        public long DurationMs { get; set; }

        public List<IsolationSection> Writes { get; set; } = new List<IsolationSection>();
    }
}
