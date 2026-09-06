using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Model;

namespace LDI12.Core.Probes
{
    /// <summary>Ce qu'une sonde exécutée ailleurs a produit.</summary>
    public sealed class IsolatedOutcome
    {
        public ProbeStatus Status { get; init; }

        public string? Message { get; init; }

        public string? ExceptionSummary { get; init; }

        /// <summary>
        /// Temps passé dans la sonde, mesuré dans le processus isolé.
        /// </summary>
        /// <remarks>
        /// Mesuré là-bas et pas ici, parce que les sondes isolées se suivent : le temps mesuré
        /// dans le processus appelant comprendrait l'attente de son tour, et le compte rendu
        /// annoncerait cinq secondes pour un module qui en a pris un dixième. Une durée affichée
        /// doit être celle du travail.
        /// </remarks>
        public long DurationMs { get; init; }

        /// <summary>Sections que la sonde a écrites, à rejouer dans le relevé du parent.</summary>
        public IReadOnlyList<DraftSection> Writes { get; init; } = Array.Empty<DraftSection>();
    }

    /// <summary>
    /// Exécute une sonde dans un processus séparé, que l'appelant peut tuer.
    /// </summary>
    /// <remarks>
    /// <b>C'est la parade au premier risque du projet</b> : une sonde qui se bloque sur une
    /// machine abîmée : dépôt WMI corrompu, disque qui ne répond plus, service de planification
    /// figé. Le délai maximal suffit à ce que l'analyse se termine, mais il n'abandonne qu'une
    /// tâche : le fil d'exécution, lui, reste bloqué pour la durée de la session, avec ce qu'il
    /// tient encore. Un processus, on le tue.
    /// <para>
    /// Le contrat rend <c>null</c> plutôt que d'échouer quand l'isolation n'est pas disponible :
    /// l'hôte introuvable ou impossible à lancer ne doit pas priver le technicien d'un module
    /// qui fonctionnerait très bien dans le processus courant.
    /// </para>
    /// </remarks>
    public interface IProbeIsolationHost
    {
        /// <summary>
        /// Exécute la sonde dans le processus isolé.
        /// </summary>
        /// <param name="input">Sections déjà collectées, dont la sonde peut dépendre.</param>
        /// <returns><c>null</c> si l'isolation n'a pas pu être obtenue.</returns>
        Task<IsolatedOutcome?> RunAsync(
            ProbeDescriptor descriptor,
            RunMode mode,
            IReadOnlyList<DraftSection> input,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Sérialisation des sections du relevé.
    /// </summary>
    /// <remarks>
    /// Le noyau ne référence aucune bibliothèque tierce et ne sait donc pas sérialiser. Il sait
    /// en revanche <i>demander</i> qu'on le fasse : avec les mêmes réglages que l'archivage d'un
    /// diagnostic, sans quoi une mesure absente traverserait la frontière de processus en
    /// perdant la raison de son absence.
    /// </remarks>
    public interface ISnapshotCodec
    {
        string Serialize(object value);

        object? Deserialize(string json, Type type);
    }
}
