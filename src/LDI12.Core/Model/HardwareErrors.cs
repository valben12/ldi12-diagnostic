using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Ce que Windows a retenu d'une erreur matérielle : le matériel l'a-t-il rattrapée, ou non.
    /// </summary>
    /// <remarks>
    /// La distinction est la seule qui compte pour le technicien. Une erreur <b>corrigée</b> n'a
    /// rien cassé (le code correcteur de la mémoire ou le cache du processeur a rattrapé le
    /// bit fautif) mais elle indique un composant qui commence à fatiguer. Une erreur
    /// <b>irrécupérable</b> a arrêté la machine.
    /// </remarks>
    public enum HardwareErrorKind
    {
        Unknown = 0,
        Corrected = 1,
        Uncorrected = 2,

        /// <summary>Événement matériel signalé sans qualification d'erreur.</summary>
        Informational = 3,
    }

    /// <summary>
    /// Une famille d'erreurs matérielles signalées, regroupées par identifiant d'événement.
    /// </summary>
    /// <remarks>
    /// Comme pour les journaux, le regroupement est ce qui rend la donnée lisible : une barrette
    /// qui commence à lâcher produit des centaines d'erreurs corrigées identiques. C'est un
    /// signal, pas des centaines.
    /// </remarks>
    public sealed class HardwareErrorGroup
    {
        public int EventId { get; init; }
        public HardwareErrorKind Kind { get; init; }
        public int Count { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }

        /// <summary>
        /// Le libellé écrit par Windows, montré tel quel.
        /// </summary>
        /// <remarks>
        /// Il nomme le composant concerné (mémoire, cache, bus PCI Express) dans la langue de
        /// la machine. Il est <b>affiché</b> et jamais analysé : la qualification vient de
        /// l'identifiant d'événement, qui, lui, ne dépend d'aucune traduction.
        /// </remarks>
        public string? Sample { get; init; }
    }

    /// <summary>Verdict du diagnostic mémoire de Windows.</summary>
    public enum MemoryTestOutcome
    {
        /// <summary>Le test n'a jamais été exécuté sur cette machine.</summary>
        NeverRun = 0,

        NoErrors = 1,
        ErrorsFound = 2,

        /// <summary>Test interrompu ou incomplet : il ne dit rien, ni dans un sens ni dans l'autre.</summary>
        Interrupted = 3,

        Unknown = 4,
    }

    /// <summary>Dernier passage du diagnostic mémoire de Windows.</summary>
    public sealed class MemoryTestRun
    {
        public DateTimeOffset Date { get; init; }
        public int EventId { get; init; }
        public MemoryTestOutcome Outcome { get; init; }

        /// <summary>Phrase écrite par Windows, reproduite telle quelle.</summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Un rapport de plantage présent sur le disque.
    /// </summary>
    /// <remarks>
    /// Seuls le chemin, la date et la taille sont relevés. Le contenu d'un vidage mémoire est
    /// une photographie de la mémoire de la machine au moment du plantage : ce logiciel ne
    /// l'ouvre pas, ne le copie pas et ne l'envoie nulle part.
    /// </remarks>
    public sealed class CrashDump
    {
        public string Path { get; init; } = string.Empty;
        public DateTimeOffset Date { get; init; }
        public long SizeBytes { get; init; }

        /// <summary>Vidage complet ou noyau (MEMORY.DMP), par opposition à un mini-vidage.</summary>
        public bool IsFullDump { get; init; }
    }

    /// <summary>
    /// Ce que la machine a déjà enregistré sur son propre matériel.
    /// </summary>
    /// <remarks>
    /// <b>Trois sources, une seule question.</b> Les erreurs matérielles signalées par Windows,
    /// le résultat du dernier test mémoire, et les rapports de plantage présents sur le disque
    /// répondent tous à « ce matériel a-t-il déjà donné des signes ? ». Aucune ne demande de
    /// privilèges, aucune ne demande de connexion, et toutes existent déjà sur la machine du
    /// client avant même que le technicien n'arrive.
    /// </remarks>
    public sealed class HardwareErrorsInfo
    {
        /// <summary>Fenêtre d'analyse des erreurs matérielles, en jours.</summary>
        public Measured<int> WindowDays { get; init; }

        public IReadOnlyList<HardwareErrorGroup> Errors { get; init; } = Array.Empty<HardwareErrorGroup>();

        public Measured<int> CorrectedCount { get; init; }
        public Measured<int> UncorrectedCount { get; init; }

        /// <summary>Dernier passage du diagnostic mémoire, s'il a déjà eu lieu.</summary>
        public MemoryTestRun? LastMemoryTest { get; init; }

        public Measured<MemoryTestOutcome> MemoryTest { get; init; }

        /// <summary>
        /// Windows écrit-il un rapport lors d'un écran bleu.
        /// </summary>
        /// <remarks>
        /// Réponse indispensable avant de promettre une analyse : sur une machine où les vidages
        /// sont désactivés, aucun écran bleu ne laissera jamais quoi que ce soit à analyser.
        /// </remarks>
        public Measured<bool> CrashDumpsEnabled { get; init; }

        /// <summary>Réglage de vidage, en clair.</summary>
        public Measured<string> CrashDumpMode { get; init; }

        public IReadOnlyList<CrashDump> CrashDumps { get; init; } = Array.Empty<CrashDump>();
    }
}
