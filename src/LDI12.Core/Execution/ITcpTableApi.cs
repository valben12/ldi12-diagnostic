using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Un port sur lequel un programme de cette machine attend des connexions.</summary>
    public sealed class TcpListener
    {
        public int Port { get; init; }

        /// <summary>Adresse d'écoute. « 0.0.0.0 » signifie « toutes les interfaces ».</summary>
        public string Address { get; init; } = string.Empty;

        public int ProcessId { get; init; }

        /// <summary>
        /// Le service est joignable depuis le réseau, et pas seulement depuis la machine.
        /// </summary>
        /// <remarks>
        /// La distinction qui change tout : un service qui n'écoute que sur 127.0.0.1 ne regarde
        /// personne d'autre que lui-même, quel que soit son port. C'est le même programme, le
        /// même port, et deux situations sans rapport.
        /// </remarks>
        public bool AllInterfaces { get; init; }
    }

    /// <summary>Ce que la machine écoute, et combien de connexions elle tient.</summary>
    public sealed class TcpTable
    {
        public IReadOnlyList<TcpListener> Listeners { get; init; } = Array.Empty<TcpListener>();

        public int EstablishedCount { get; init; }
    }

    /// <summary>
    /// Les points d'écoute TCP, avec le processus qui les tient.
    /// </summary>
    /// <remarks>
    /// Par l'API native, parce qu'elle seule rend le port <b>et</b> son propriétaire en une fois.
    /// « Le port 3389 est ouvert » sans savoir par quoi n'apprend rien à personne.
    /// </remarks>
    public interface ITcpTableApi
    {
        Measured<TcpTable> Read();
    }
}
