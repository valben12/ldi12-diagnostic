using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Ce qui s'est mal passé sur cette machine, et de quelle nature.</summary>
    public enum IncidentKind
    {
        Unknown = 0,

        /// <summary>Un programme s'est arrêté brutalement.</summary>
        ProgramCrash = 1,

        /// <summary>Un programme a cessé de répondre et Windows l'a fermé.</summary>
        ProgramHang = 2,

        /// <summary>Un service s'est arrêté sans qu'on le lui demande.</summary>
        ServiceCrash = 3,

        /// <summary>Écran bleu.</summary>
        BlueScreen = 4,

        /// <summary>Erreur matérielle signalée par la machine elle-même.</summary>
        HardwareError = 5,
    }

    /// <summary>
    /// Un programme ou un service qui échoue, vu sur toute la fenêtre.
    /// </summary>
    /// <remarks>
    /// Le regroupement est ce qui rend la liste exploitable : un navigateur qui plante trente
    /// fois est <b>un</b> constat, pas trente. Les dates de première et de dernière occurrence
    /// sont conservées parce qu'elles portent la seule question qui compte : est-ce ancien, ou
    /// est-ce en train d'arriver ?
    /// </remarks>
    public sealed class FailingProgram
    {
        /// <summary>Nom du programme ou du service, tel que Windows l'a enregistré.</summary>
        public string Name { get; init; } = string.Empty;

        public IncidentKind Kind { get; init; }

        public int Count { get; init; }

        public DateTimeOffset FirstSeen { get; init; }

        public DateTimeOffset LastSeen { get; init; }

        /// <summary>
        /// Le composant nommé par Windows au moment du plantage.
        /// </summary>
        /// <remarks>
        /// Souvent la meilleure piste du relevé : un module graphique désigne un pilote, un
        /// module du programme lui-même désigne le programme. Absent pour un blocage ou un
        /// service, où Windows ne le renseigne pas.
        /// </remarks>
        public string? Module { get; init; }
    }

    /// <summary>Un incident daté, à sa date.</summary>
    /// <remarks>
    /// Le pendant de <see cref="FailingProgram"/> : celui-ci répond à « qui », celui-là à
    /// « quand ». Les deux viennent de la même lecture : les séparer évite de reconstituer une
    /// chronologie à partir de compteurs, ce qui reviendrait à l'inventer.
    /// </remarks>
    public sealed class Incident
    {
        public DateTimeOffset Date { get; init; }

        public IncidentKind Kind { get; init; }

        /// <summary>Le programme, le service, ou ce que l'incident concerne.</summary>
        public string Subject { get; init; } = string.Empty;
    }

    /// <summary>
    /// La stabilité de la machine sur la durée.
    /// </summary>
    /// <remarks>
    /// <b>« Depuis quand ? » est la première question du client, et la dernière à laquelle un
    /// relevé instantané sait répondre.</b> Tout le reste du logiciel mesure un état ; ce module
    /// mesure une histoire. Il lit les journaux d'application et de service sur quatre-vingt-dix
    /// jours, et rend deux choses : qui échoue, et quand.
    /// <para>
    /// Rien n'y est déduit. Un incident non enregistré par Windows n'existe pas ici, et une
    /// machine réinstallée la semaine dernière n'a pas d'histoire, ce qui se dit, plutôt que de
    /// se lire comme une machine saine.
    /// </para>
    /// </remarks>
    public sealed class StabilityInfo
    {
        /// <summary>Fenêtre réellement couverte par la lecture.</summary>
        public Measured<int> WindowDays { get; init; }

        /// <summary>
        /// Ancienneté du plus ancien incident lisible.
        /// </summary>
        /// <remarks>
        /// Distincte de la fenêtre : un journal purgé ou une machine récente ne couvre pas
        /// quatre-vingt-dix jours, et l'absence d'incident ancien ne veut alors rien dire. Sans
        /// cette mesure, « aucun incident avant le 20 août » se lirait comme une amélioration
        /// alors que le journal commence le 20 août.
        /// </remarks>
        public Measured<DateTimeOffset> OldestEntry { get; init; }

        public IReadOnlyList<FailingProgram> Programs { get; init; } = Array.Empty<FailingProgram>();

        public IReadOnlyList<Incident> Incidents { get; init; } = Array.Empty<Incident>();

        public Measured<int> ProgramCrashes { get; init; }

        public Measured<int> ProgramHangs { get; init; }

        public Measured<int> ServiceCrashes { get; init; }
    }
}
