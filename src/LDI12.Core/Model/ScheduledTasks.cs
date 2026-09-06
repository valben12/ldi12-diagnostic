using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Ce qui déclenche une tâche planifiée.
    /// </summary>
    /// <remarks>
    /// Les valeurs viennent de l'énumération du planificateur, qui est numérique : elles ne
    /// dépendent donc pas de la langue de la machine. Seuls les deux premiers déclencheurs font
    /// d'une tâche un programme de démarrage ; les autres sont regroupés parce que la nuance
    /// entre « chaque semaine » et « chaque mois » ne change rien au diagnostic.
    /// </remarks>
    public enum TaskTriggerKind
    {
        Unknown = 0,

        /// <summary>À l'ouverture d'une session.</summary>
        Logon = 1,

        /// <summary>Au démarrage de Windows, avant toute session.</summary>
        Boot = 2,

        /// <summary>À heure ou à date fixe : une fois, chaque jour, chaque semaine, chaque mois.</summary>
        Scheduled = 3,

        /// <summary>Sur événement du journal.</summary>
        Event = 4,

        /// <summary>Quand la machine est inactive.</summary>
        Idle = 5,

        /// <summary>À l'enregistrement de la tâche, c'est-à-dire une fois, à l'installation.</summary>
        Registration = 6,

        /// <summary>Verrouillage, déverrouillage, connexion à distance.</summary>
        SessionChange = 7,
    }

    /// <summary>
    /// Ce que vaut le code rendu par la dernière exécution d'une tâche.
    /// </summary>
    /// <remarks>
    /// <b>Un code non nul n'est pas un échec.</b> Le planificateur range dans le même champ ses
    /// propres états (« en cours d'exécution », « jamais exécutée ») et le code de sortie du
    /// programme lancé. Constaté sur la machine de développement : cinq tâches sur quinze
    /// portaient un code non nul, et pas une seule n'avait échoué.
    /// </remarks>
    public enum TaskResultKind
    {
        Unknown = 0,

        Success = 1,

        /// <summary>État du planificateur lui-même, sans rapport avec une réussite ou un échec.</summary>
        Informational = 2,

        /// <summary>Le programme lancé s'est terminé sur un code de sortie non nul.</summary>
        ProgramError = 3,

        /// <summary>Windows n'a pas pu exécuter la tâche.</summary>
        TaskError = 4,
    }

    /// <summary>
    /// Une tâche planifiée, réduite à ce qui sert au diagnostic.
    /// </summary>
    /// <remarks>
    /// L'auteur de la tâche n'y figure pas, volontairement : c'est le seul champ du planificateur
    /// qui porte régulièrement le nom d'une personne, et il n'apprend rien que le chemin du
    /// programme lancé ne dise déjà.
    /// </remarks>
    public sealed class ScheduledTaskInfo
    {
        /// <summary>Chemin complet dans le planificateur, tel qu'on l'y retrouve.</summary>
        public string Path { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        /// <summary>
        /// Tâche rangée sous le dossier <c>\Microsoft\</c> du planificateur.
        /// </summary>
        /// <remarks>
        /// C'est un emplacement, pas un éditeur, et le libellé le dit ainsi partout : Windows
        /// dépose lui-même quelques tâches à la racine (la machine de développement en porte
        /// trois) et les annoncer comme « ajoutées par un logiciel » serait une déduction que
        /// rien ne soutient.
        /// </remarks>
        public bool InMicrosoftFolder { get; init; }

        public IReadOnlyList<TaskTriggerKind> Triggers { get; init; } = Array.Empty<TaskTriggerKind>();

        /// <summary>Délai après le déclencheur, quand la tâche en déclare un.</summary>
        public int? DelaySeconds { get; init; }

        /// <summary>Intervalle de répétition, quand la tâche se répète.</summary>
        public int? RepetitionMinutes { get; init; }

        /// <summary>
        /// Programme lancé. Nul lorsque la tâche appelle un composant logiciel et non un
        /// exécutable : auquel cas il n'y a pas de fichier dont on puisse constater l'absence.
        /// </summary>
        public string? ImagePath { get; init; }

        /// <summary>Vrai si le programme lancé n'existe plus : tâche orpheline.</summary>
        public bool TargetMissing { get; init; }

        /// <summary>Nulle si la tâche n'a jamais été exécutée.</summary>
        public DateTimeOffset? LastRun { get; init; }

        public int? LastResult { get; init; }

        public TaskResultKind ResultKind { get; init; }

        /// <summary>
        /// Vrai si cette tâche est un programme de démarrage comme les autres.
        /// </summary>
        /// <remarks>
        /// Défini ici, et pas dans la sonde ni dans les règles, parce que les deux s'en servent :
        /// la sonde pour verser la tâche dans la liste de démarrage, les règles pour ne pas
        /// constater deux fois la même tâche orpheline, une fois comme programme de démarrage,
        /// une fois comme tâche.
        /// </remarks>
        public bool RunsAtStartup
        {
            get
            {
                if (!Enabled) return false;
                foreach (var trigger in Triggers)
                    if (trigger == TaskTriggerKind.Logon || trigger == TaskTriggerKind.Boot) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Les tâches planifiées de la machine.
    /// </summary>
    /// <remarks>
    /// <b>Seules les tâches hors du dossier <c>\Microsoft\</c> sont listées</b> ; les autres
    /// sont comptées. Une machine ordinaire en enregistre près de deux cents, dont neuf sur dix
    /// rangées là par Windows : les lister toutes noierait la quinzaine qu'un logiciel a déposée,
    /// et qui est la seule dont le technicien puisse faire quelque chose. C'est la même ligne que
    /// pour l'inventaire logiciel : constater ce qui a été ajouté, sans juger ce que fait
    /// Windows.
    /// </remarks>
    public sealed class ScheduledTaskInventory
    {
        public Measured<int> TotalCount { get; init; }

        public Measured<int> MicrosoftFolderCount { get; init; }

        /// <summary>Tâches enregistrées hors du dossier <c>\Microsoft\</c>.</summary>
        public IReadOnlyList<ScheduledTaskInfo> Tasks { get; init; } = Array.Empty<ScheduledTaskInfo>();

        /// <summary>
        /// Tâches dont la lecture a été refusée.
        /// </summary>
        /// <remarks>
        /// Une session normale ne voit pas tout le planificateur. Le nombre est retenu pour que
        /// le rapport puisse dire « il en reste que je n'ai pas pu regarder » plutôt que de
        /// laisser croire à un inventaire complet.
        /// </remarks>
        public Measured<int> Unreadable { get; init; }
    }
}
