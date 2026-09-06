using System;
using System.Collections.Generic;

namespace LDI12.Core.Model
{
    /// <summary>Ce que ce logiciel a écrit sur la machine, et pourquoi.</summary>
    public enum FootprintKind
    {
        Other = 0,

        /// <summary>Journal technique de l'application elle-même.</summary>
        Logs = 1,

        /// <summary>Barème de seuils ajusté par le technicien.</summary>
        Settings = 2,

        /// <summary>Diagnostics archivés, qui servent à la comparaison avant / après.</summary>
        History = 3,

        /// <summary>Journal des interventions menées sur cette machine.</summary>
        Interventions = 4,

        /// <summary>Hôte de sondes écrit dans le profil lors d'une élévation.</summary>
        ExtractedHost = 5,
    }

    /// <summary>
    /// Un emplacement écrit par ce logiciel.
    /// </summary>
    /// <remarks>
    /// Ni le contenu ni les noms de fichiers ne figurent ici : un chemin, un nombre, une taille
    /// et une date suffisent à dire ce qui a été écrit. C'est la même règle que pour le relevé
    /// des données à sauvegarder, ce qui n'est pas nécessaire au constat ne se transporte pas.
    /// </remarks>
    public sealed class FootprintItem
    {
        public FootprintKind Kind { get; init; }

        /// <summary>Ce que c'est, en français, pour le technicien qui le montre au client.</summary>
        public string Label { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public bool Exists { get; init; }

        public int FileCount { get; init; }

        public long SizeBytes { get; init; }

        public DateTimeOffset? LastWrite { get; init; }

        /// <summary>
        /// Ce qu'on perd en l'effaçant.
        /// </summary>
        /// <remarks>
        /// Aucun de ces emplacements n'est nécessaire au fonctionnement du logiciel : il les
        /// recrée. Mais l'historique porte les diagnostics passés et le journal porte les
        /// interventions menées : les effacer supprime des preuves de travail, et cela se dit
        /// avant, pas après.
        /// </remarks>
        public string Consequence { get; init; } = string.Empty;
    }

    /// <summary>
    /// L'empreinte du logiciel sur la machine du client.
    /// </summary>
    /// <remarks>
    /// <b>Un outil de diagnostic doit pouvoir dire ce qu'il a laissé.</b> Celui-ci écrit dans un
    /// seul dossier du profil de l'utilisateur (journaux, barème, diagnostics archivés, journal
    /// d'intervention) et rien ailleurs : ni base de registre, ni service, ni tâche planifiée,
    /// ni dossier de programmes. Le relevé le montre, et l'effacement le retire.
    /// </remarks>
    public sealed class Footprint
    {
        /// <summary>Dossier racine, celui que tout le reste occupe.</summary>
        public string Root { get; init; } = string.Empty;

        public IReadOnlyList<FootprintItem> Items { get; init; } = Array.Empty<FootprintItem>();

        public long TotalBytes { get; init; }

        public int TotalFiles { get; init; }

        /// <summary>Renseignée si le relevé n'a pas pu être établi entièrement.</summary>
        public string? Limitation { get; init; }
    }
}
