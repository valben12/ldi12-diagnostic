using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Un port sur lequel un programme de cette machine attend des connexions.</summary>
    public sealed class ListeningPort
    {
        public int Port { get; init; }

        /// <summary>Adresse d'écoute. « 0.0.0.0 » signifie « toutes les interfaces ».</summary>
        public string Address { get; init; } = string.Empty;

        public int ProcessId { get; init; }

        public Measured<string> ProcessName { get; init; }

        /// <summary>Le service est joignable depuis le réseau, et pas seulement depuis la machine.</summary>
        public bool AllInterfaces { get; init; }

        /// <summary>Usage habituel de ce port, quand il en a un. Jamais une certitude.</summary>
        public string? Service { get; init; }
    }

    /// <summary>Ce que la machine écoute, et combien de connexions elle tient.</summary>
    public sealed class ListeningPortsInfo
    {
        public IReadOnlyList<ListeningPort> Ports { get; init; } = Array.Empty<ListeningPort>();

        public Measured<int> Count { get; init; }

        /// <summary>Connexions sortantes ou entrantes en cours. Un ordre de grandeur, pas un inventaire.</summary>
        public Measured<int> Established { get; init; }
    }

    /// <summary>
    /// Ce qu'un numéro de port veut dire, d'ordinaire.
    /// </summary>
    /// <remarks>
    /// <b>Une convention, jamais une preuve.</b> Rien n'empêche un programme d'écouter sur le
    /// port 3389 sans être le bureau à distance, ni le bureau à distance d'être déplacé ailleurs.
    /// Le libellé sert à orienter la lecture : c'est le nom du programme, à côté, qui dit la
    /// vérité. La liste s'arrête volontairement aux ports qu'un technicien reconnaît et dont la
    /// présence change une décision.
    /// </remarks>
    public static class WellKnown
    {
        private static readonly Dictionary<int, string> Ports = new Dictionary<int, string>
        {
            { 21, "Transfert de fichiers (FTP)" },
            { 22, "Console sécurisée (SSH)" },
            { 23, "Console en clair (Telnet)" },
            { 25, "Courrier sortant (SMTP)" },
            { 53, "Résolution de noms (DNS)" },
            { 80, "Site web (HTTP)" },
            { 135, "Appel de procédure distante (RPC)" },
            { 139, "Partage de fichiers, ancienne voie (NetBIOS)" },
            { 443, "Site web sécurisé (HTTPS)" },
            { 445, "Partage de fichiers et d'imprimantes (SMB)" },
            { 1433, "Base de données SQL Server" },
            { 3306, "Base de données MySQL" },
            { 3389, "Bureau à distance" },
            { 5432, "Base de données PostgreSQL" },
            { 5900, "Prise en main à distance (VNC)" },
            { 8080, "Site web, port secondaire" },
        };

        public static string? Describe(int port)
            => Ports.TryGetValue(port, out var name) ? name : null;

        /// <summary>
        /// Ports dont l'ouverture sur le réseau mérite d'être signalée.
        /// </summary>
        /// <remarks>
        /// Ni une liste de menaces ni un scanner de sécurité : ce logiciel n'est pas un
        /// antivirus. Ce sont les portes d'entrée qu'un technicien vérifie sur un poste qu'il ne
        /// connaît pas, parce qu'elles ouvrent la machine entière (prise en main, partage de
        /// fichiers, console) et qu'elles sont souvent restées ouvertes par oubli.
        /// </remarks>
        public static bool IsEntryPoint(int port)
            => port == 22 || port == 23 || port == 445 || port == 139 ||
               port == 3389 || port == 5900 || port == 1433 || port == 3306 || port == 5432;
    }
}
