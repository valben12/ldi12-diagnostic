using System;
using System.Collections.Generic;

namespace LDI12.Reports.Html
{
    /// <summary>
    /// Ce que le technicien ajoute au rapport au moment de l'export.
    /// </summary>
    /// <remarks>
    /// Ces informations n'entrent pas dans l'instantané. Un instantané décrit une machine à un
    /// instant donné et doit rester tel quel : deux exports du même diagnostic, l'un pour le
    /// dossier interne et l'autre pour le client, portent des mentions différentes sans que la
    /// mesure change d'un octet. C'est aussi ce qui permet de rouvrir un JSON archivé et de
    /// réémettre un rapport à un autre nom.
    /// </remarks>
    public sealed class ReportContext
    {
        /// <summary>Nom du technicien signataire. Facultatif.</summary>
        public string? Technician { get; init; }

        /// <summary>Référence du dossier client. Facultatif.</summary>
        public string? ClientReference { get; init; }

        /// <summary>Observation manuscrite ajoutée en tête de rapport. Facultative.</summary>
        public string? Note { get; init; }

        /// <summary>Date d'édition. Distincte de la date du diagnostic.</summary>
        public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.Now;

        /// <summary>
        /// Ce qui a été fait sur la machine pendant l'intervention, en clair.
        /// </summary>
        /// <remarks>
        /// Fourni par l'appelant plutôt que lu ici : un instantané décrit une machine, il ne
        /// contient pas ce qu'on lui a fait subir ensuite. Deux rédactions, parce que ce ne sont
        /// pas les mêmes lecteurs : le technicien veut l'identifiant de l'action et son état, le
        /// client veut savoir ce qui a été touché chez lui.
        /// </remarks>
        public IReadOnlyList<string> TechnicianLog { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> ClientLog { get; init; } = Array.Empty<string>();

        public static ReportContext Default => new ReportContext();
    }
}
