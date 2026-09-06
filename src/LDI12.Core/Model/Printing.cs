using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Ce que l'imprimante répond quand on lui demande si elle peut imprimer.</summary>
    public enum PrinterAvailability
    {
        Unknown = 0,

        /// <summary>Prête.</summary>
        Ready = 1,

        /// <summary>Hors connexion : la machine ne la joint pas, ou on lui a dit de ne pas essayer.</summary>
        Offline = 2,

        /// <summary>Bourrage, capot ouvert, bac de sortie plein : une intervention physique.</summary>
        Error = 3,

        /// <summary>Plus de papier.</summary>
        PaperOut = 4,

        /// <summary>Plus d'encre ou de toner.</summary>
        InkOut = 5,

        /// <summary>Mise en pause par l'utilisateur ou par Windows.</summary>
        Paused = 6,
    }

    /// <summary>
    /// Une imprimante installée.
    /// </summary>
    /// <remarks>
    /// <b>« Hors connexion » recouvre deux situations que le client vit de la même façon et que
    /// le technicien ne traite pas du tout pareil.</b> Windows peut ne pas joindre l'imprimante 
    /// (câble, réseau, imprimante éteinte) ou bien quelqu'un a coché « Utiliser l'imprimante hors
    /// connexion » dans son menu, et Windows n'essaie même plus. Le second cas se règle d'un
    /// clic, le premier non : les deux sont donc distingués ici plutôt que fondus dans un même
    /// mot.
    /// </remarks>
    public sealed class PrinterInfo
    {
        public string Name { get; init; } = string.Empty;

        public bool IsDefault { get; init; }

        public bool IsNetwork { get; init; }

        public PrinterAvailability Availability { get; init; }

        /// <summary>Le mode « hors connexion » a été demandé, il n'est pas subi.</summary>
        public bool OfflineByChoice { get; init; }

        public string? Driver { get; init; }

        public string? Port { get; init; }
    }

    /// <summary>
    /// L'impression : le spouleur, les imprimantes, la file d'attente.
    /// </summary>
    /// <remarks>
    /// <b>« Je ne peux plus imprimer » est l'une des trois pannes les plus racontées en atelier</b>,
    /// et rien dans ce logiciel ne la regardait. Trois causes couvrent presque tous les cas : le
    /// service d'impression est arrêté, l'imprimante est hors connexion, ou un travail bloqué en
    /// tête de file empêche les suivants de sortir.
    /// </remarks>
    public sealed class PrintingInfo
    {
        /// <summary>État du spouleur : sans lui, rien ne s'imprime, quelle que soit l'imprimante.</summary>
        public Measured<string> SpoolerState { get; init; }

        public Measured<bool> SpoolerRunning { get; init; }

        public IReadOnlyList<PrinterInfo> Printers { get; init; } = Array.Empty<PrinterInfo>();

        /// <summary>Travaux en attente, toutes imprimantes confondues.</summary>
        public Measured<int> PendingJobs { get; init; }

        /// <summary>
        /// Fichiers restés dans le dossier de spoule.
        /// </summary>
        /// <remarks>
        /// Demande les privilèges administrateur : le dossier est fermé à l'utilisateur courant.
        /// L'absence est déclarée, jamais remplacée par un zéro : « aucun fichier bloqué » et
        /// « je n'ai pas pu regarder » ne s'écrivent pas de la même façon.
        /// </remarks>
        public Measured<int> SpoolFiles { get; init; }

        /// <summary>Erreurs d'impression enregistrées par Windows sur la période.</summary>
        public Measured<int> RecentErrors { get; init; }

        public Measured<int> ErrorWindowDays { get; init; }
    }
}
