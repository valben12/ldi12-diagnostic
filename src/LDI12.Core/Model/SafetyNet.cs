using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// La protection du système : ce qui permet de revenir à l'état d'avant.
    /// </summary>
    /// <remarks>
    /// L'état déclaré et l'activité réelle sont deux mesures distinctes, et c'est tout l'objet de
    /// ce relevé. Une protection annoncée activée qui n'a créé aucun point depuis six mois, ou
    /// qui échoue à chaque tentative, n'est pas un filet : c'en est l'apparence, et personne ne
    /// s'en aperçoit avant le jour où l'on en a besoin.
    /// </remarks>
    public sealed class RestoreProtection
    {
        public Measured<bool> Enabled { get; init; }

        /// <summary>Part du disque réservée aux points de restauration.</summary>
        public Measured<int> ReservedPercent { get; init; }

        /// <summary>
        /// Dernier point de restauration <b>créé</b>.
        /// </summary>
        /// <remarks>
        /// Créé, et non disponible : Windows supprime les plus anciens quand la réserve est
        /// pleine, et la liste de ceux qui restent demande les privilèges administrateur. La
        /// mesure est donc partielle par nature : elle dit qu'un point a existé à cette date,
        /// pas qu'il existe encore.
        /// </remarks>
        public Measured<DateTimeOffset> LastPointCreated { get; init; }

        public Measured<int> PointsCreated { get; init; }

        /// <summary>
        /// Créations de point qui ont échoué.
        /// </summary>
        /// <remarks>
        /// Le signal le plus utile du relevé : une protection qui échoue laisse la case cochée
        /// dans les réglages de Windows tout en ne protégeant plus rien. C'est aussi ce qui fera
        /// échouer le point de restauration que ce logiciel crée avant chaque réparation.
        /// </remarks>
        public Measured<int> Failures { get; init; }

        public Measured<int> WindowDays { get; init; }
    }

    public enum RecoveryEnvironmentState
    {
        Unknown = 0,

        /// <summary>Installé et actif : la machine sait démarrer en mode réparation.</summary>
        Installed = 1,

        /// <summary>Présent mais désactivé.</summary>
        Disabled = 2,

        /// <summary>Aucun environnement de récupération sur cette machine.</summary>
        Missing = 3,
    }

    /// <summary>
    /// L'environnement de récupération de Windows.
    /// </summary>
    /// <remarks>
    /// C'est ce qui s'ouvre quand Windows ne démarre plus : réparation du démarrage, invite de
    /// commandes, retour à un point de restauration. Sans lui, une machine qui ne démarre plus
    /// n'a plus qu'une clé d'installation pour espoir, et cela se sait avant la panne, ou pas
    /// du tout.
    /// </remarks>
    public sealed class RecoveryEnvironment
    {
        public Measured<RecoveryEnvironmentState> State { get; init; }

        /// <summary>La réparation automatique au démarrage est-elle armée.</summary>
        public Measured<bool> AutomaticRepair { get; init; }

        /// <summary>Emplacement déclaré, quand il l'est.</summary>
        public string? Location { get; init; }
    }

    public enum PersonalFolderKind
    {
        Other = 0,
        Desktop = 1,
        Documents = 2,
        Pictures = 3,
        Downloads = 4,
        Music = 5,
        Videos = 6,
    }

    /// <summary>
    /// Où sont réellement les affaires du client.
    /// </summary>
    /// <remarks>
    /// <b>La question la plus importante avant de reformater quoi que ce soit.</b> Windows permet
    /// de déplacer ces dossiers, et beaucoup de machines le font sans que personne s'en souvienne :
    /// vers un second disque, vers un disque externe, vers OneDrive. Le chemin est lu, jamais
    /// supposé : « les documents sont dans C:\Utilisateurs » est une hypothèse qui a déjà coûté
    /// des données à des ateliers.
    /// </remarks>
    public sealed class PersonalFolder
    {
        public PersonalFolderKind Kind { get; init; }

        public string Path { get; init; } = string.Empty;

        /// <summary>L'emplacement existe-t-il réellement : un disque débranché rend faux.</summary>
        public bool Exists { get; init; }

        /// <summary>Sur le volume système, donc emporté par une réinstallation.</summary>
        public bool OnSystemVolume { get; init; }

        /// <summary>Dans un dossier synchronisé vers le nuage.</summary>
        public bool InCloud { get; init; }
    }

    public enum BackupKind
    {
        Other = 0,

        /// <summary>Historique des fichiers.</summary>
        FileHistory = 1,

        /// <summary>Sauvegarde et restauration, héritée de Windows 7.</summary>
        WindowsBackup = 2,

        /// <summary>OneDrive.</summary>
        Cloud = 3,
    }

    /// <summary>
    /// Un moyen de sauvegarde repéré sur la machine.
    /// </summary>
    /// <remarks>
    /// <b>Repéré, et rien de plus.</b> Savoir qu'un outil est configuré ne dit pas qu'il a
    /// tourné, ni que ce qu'il a sauvegardé est récupérable. Le relevé s'arrête donc à la
    /// présence, et le dit : annoncer « les données sont sauvegardées » sur la foi d'une clé de
    /// registre serait la pire des affirmations que ce logiciel puisse produire.
    /// </remarks>
    public sealed class BackupTool
    {
        public BackupKind Kind { get; init; }

        public string Label { get; init; } = string.Empty;

        /// <summary>Ce qui a été trouvé : dossier, compte, destination déclarée.</summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Ce qui permettrait de revenir en arrière.
    /// </summary>
    /// <remarks>
    /// <b>Le relevé qu'on lit avant de toucher à la machine, pas après.</b> Ce logiciel crée un
    /// point de restauration avant chaque réparation ; encore faut-il que la machine sache en
    /// créer. Et une intervention se décide autrement selon que les documents du client sont sur
    /// le disque qu'on va reformater ou sur un second disque auquel on ne touchera pas.
    /// <para>
    /// Aucune de ces mesures ne demande les privilèges administrateur. La liste des points de
    /// restauration encore présents, elle, les exige : elle n'est donc pas ici, et son absence
    /// est dite plutôt que comblée par une supposition.
    /// </para>
    /// </remarks>
    public sealed class SafetyNet
    {
        public RestoreProtection Restore { get; init; } = new RestoreProtection();

        public RecoveryEnvironment Recovery { get; init; } = new RecoveryEnvironment();

        public IReadOnlyList<PersonalFolder> Folders { get; init; } = Array.Empty<PersonalFolder>();

        public IReadOnlyList<BackupTool> Backups { get; init; } = Array.Empty<BackupTool>();
    }
}
