using System;
using System.Collections.Generic;
using System.Threading;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Un fichier relevé par un balayage, avec ce qui permet de vérifier qu'il n'a pas bougé.</summary>
    public sealed class FileEntry
    {
        public string Path { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTime LastWriteUtc { get; init; }

        /// <summary>
        /// Fichier annoncé par le nuage mais absent de ce disque.
        /// </summary>
        /// <remarks>
        /// Décisif pour une copie : ouvrir un tel fichier ne le lit pas, il le <i>télécharge</i>.
        /// Sauvegarder un dossier OneDrive de deux cents gigaoctets déclencherait deux cents
        /// gigaoctets de téléchargement à l'insu du technicien, sur la ligne du client, et
        /// remplirait au passage le disque qu'on s'apprête à réinstaller.
        /// </remarks>
        public bool CloudOnly { get; init; }

        public override string ToString() => Path;
    }

    /// <summary>Une copie demandée, et ce qu'on exige d'elle.</summary>
    public sealed class FileCopyRequest
    {
        public FileCopyRequest(FileEntry source, string destination)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        }

        public FileEntry Source { get; }

        public string Destination { get; }

        /// <summary>
        /// Relire la copie et comparer son empreinte à celle de la source.
        /// </summary>
        /// <remarks>
        /// <b>Une sauvegarde non vérifiée n'est pas une sauvegarde.</b> Un disque externe qui
        /// commence à lâcher accepte les écritures et rend autre chose à la relecture ; le
        /// technicien ne s'en aperçoit qu'au moment de restaurer, c'est-à-dire quand la source
        /// n'existe plus. La relecture coûte une lecture de plus par fichier, et c'est le prix
        /// de la seule phrase qui compte : « j'ai vérifié ».
        /// </remarks>
        public bool Verify { get; init; } = true;
    }

    public enum FileCopyOutcome
    {
        Copied = 0,

        /// <summary>Déjà présent à l'identique : taille et date correspondent.</summary>
        AlreadyPresent = 1,

        /// <summary>Un fichier différent porte déjà ce nom. Jamais écrasé.</summary>
        Conflict = 2,

        /// <summary>Fichier ouvert par un autre programme.</summary>
        Locked = 3,

        NotFound = 4,

        /// <summary>Taille ou date modifiées depuis le relevé : ce n'est plus le fichier annoncé.</summary>
        Changed = 5,

        Denied = 6,

        /// <summary>Plus de place sur la destination.</summary>
        NoSpace = 7,

        PathTooLong = 8,

        /// <summary>La relecture ne correspond pas à la source. La copie a été retirée.</summary>
        VerificationFailed = 9,

        /// <summary>Fichier présent seulement dans le nuage : le copier le téléchargerait.</summary>
        CloudOnly = 10,

        Failed = 11,
    }

    public sealed class FileCopyResult
    {
        public FileCopyOutcome Outcome { get; init; }

        /// <summary>Octets réellement écrits. Nul pour tout ce qui n'a pas été copié.</summary>
        public long Bytes { get; init; }

        public string? Reason { get; init; }

        public static FileCopyResult Of(FileCopyOutcome outcome, long bytes = 0, string? reason = null)
            => new FileCopyResult { Outcome = outcome, Bytes = bytes, Reason = reason };
    }

    public sealed class DirectoryScanRequest
    {
        public DirectoryScanRequest(string root) => Root = root ?? throw new ArgumentNullException(nameof(root));

        public string Root { get; }

        /// <summary>Sous-dossiers à explorer. Vide : tout le contenu de la racine.</summary>
        public IReadOnlyList<string> Subdirectories { get; init; } = Array.Empty<string>();

        /// <summary>Ne retenir que les fichiers dont la dernière écriture est antérieure. Null : tous.</summary>
        public DateTime? OlderThanUtc { get; init; }

        /// <summary>Plafond de fichiers relevés : au-delà, le balayage est marqué tronqué.</summary>
        public int MaxFiles { get; init; } = 80000;

        /// <summary>Budget de temps. Un dossier temporaire de 300 000 fichiers ne doit pas figer l'écran.</summary>
        public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(12);

        /// <summary>
        /// Ne relever que le premier niveau, sans descendre dans les sous-dossiers.
        /// </summary>
        /// <remarks>
        /// Sert quand un dossier est relevé en même temps que ses sous-dossiers, chacun pour son
        /// compte : sans cela, ce qu'ils contiennent serait compté deux fois, et proposé deux fois
        /// à la suppression.
        /// </remarks>
        public bool TopLevelOnly { get; init; }

        /// <summary>
        /// Sous-dossiers à ne pas parcourir, en chemin relatif à la racine.
        /// </summary>
        /// <remarks>
        /// Sert à la sauvegarde des données d'applications : un profil Chrome de deux cents
        /// mégaoctets traîne souvent un cache de plusieurs gigaoctets et de dizaines de milliers de
        /// fichiers. Les écarter après le balayage coûterait le parcours entier ; les écarter
        /// pendant, rien.
        /// <para>
        /// Un segment « * » remplace exactement un nom de dossier : <c>WebStorage\*\CacheStorage</c>.
        /// </para>
        /// </remarks>
        public IReadOnlyList<string> ExcludeRelative { get; init; } = Array.Empty<string>();
    }

    /// <summary>Ce qu'un balayage a réellement trouvé, et ce qu'il n'a pas pu regarder.</summary>
    public sealed class DirectoryScan
    {
        public string Root { get; init; } = string.Empty;
        public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();
        public long TotalBytes { get; init; }

        /// <summary>Dossiers refusés faute de droits : comptés, jamais passés sous silence.</summary>
        public int InaccessibleDirectories { get; init; }

        /// <summary>
        /// Points de reparse ignorés (jonctions, liens symboliques).
        /// </summary>
        /// <remarks>
        /// Les suivre ferait sortir le nettoyage du dossier annoncé : une jonction placée dans
        /// <c>%TEMP%</c> pointerait vers les documents de l'utilisateur, et la prévisualisation
        /// aurait montré autre chose que ce qui serait supprimé.
        /// </remarks>
        public int SkippedReparsePoints { get; init; }

        /// <summary>Le plafond ou le budget a été atteint : la liste est partielle.</summary>
        public bool Truncated { get; init; }

        public int FileCount => Files.Count;
    }

    public sealed class DirectoryMeasureRequest
    {
        public DirectoryMeasureRequest(string root) => Root = root ?? throw new ArgumentNullException(nameof(root));

        public string Root { get; }

        /// <summary>
        /// Budget de temps. Un dossier de photos de vingt ans ne doit pas figer l'écran.
        /// </summary>
        /// <remarks>
        /// Plus large que celui du nettoyage : un relevé qui existe pour dire « il y a 240 Go à
        /// sauvegarder » perd tout son sens s'il s'arrête au tiers.
        /// </remarks>
        public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Sous-dossiers à ne pas parcourir, en chemin complet.
        /// </summary>
        /// <remarks>
        /// Sert à peser « le reste du profil » sans repasser sur les dossiers déjà mesurés. Sans
        /// cela, un dossier d'images de deux cents gigaoctets serait parcouru deux fois pour
        /// obtenir une soustraction.
        /// </remarks>
        public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Ce que pèse un dossier, sans retenir ce qu'il contient.
    /// </summary>
    /// <remarks>
    /// <b>On compte, on ne lit pas, et on ne retient aucun nom.</b> C'est la différence avec le
    /// balayage du nettoyage, qui garde la liste des fichiers parce qu'il doit ensuite prouver
    /// qu'il supprime exactement ce qu'il a montré. Ici, rien ne sera supprimé : garder deux cent
    /// mille chemins de fichiers personnels en mémoire (et risquer de les voir passer dans un
    /// journal ou un rapport) serait une prise de risque sans contrepartie.
    /// </remarks>
    public sealed class DirectoryMeasure
    {
        public string Root { get; init; } = string.Empty;

        /// <summary>Taille annoncée par les fichiers, qu'ils soient présents sur ce disque ou non.</summary>
        public long TotalBytes { get; init; }

        /// <summary>
        /// Ce qui occupe réellement ce disque.
        /// </summary>
        /// <remarks>
        /// Distinct du total à cause des fichiers en ligne : OneDrive affiche un fichier de deux
        /// gigaoctets qui n'est pas sur la machine. Copier le dossier ne les copierait pas.
        /// </remarks>
        public long OnDiskBytes { get; init; }

        /// <summary>Taille des fichiers présents dans le nuage et absents de ce disque.</summary>
        public long CloudOnlyBytes { get; init; }

        public int FileCount { get; init; }

        public int CloudOnlyFileCount { get; init; }

        /// <summary>Dossiers refusés faute de droits : comptés, jamais passés sous silence.</summary>
        public int InaccessibleDirectories { get; init; }

        /// <summary>Jonctions et liens ignorés, les suivre compterait deux fois les mêmes données.</summary>
        public int SkippedReparsePoints { get; init; }

        /// <summary>Le budget a été atteint : le total est un minimum, pas une mesure.</summary>
        public bool Truncated { get; init; }
    }

    public enum FileDeletion
    {
        Deleted = 0,

        /// <summary>Fichier ouvert par un autre processus. Cas normal, pas une erreur.</summary>
        Locked = 1,

        /// <summary>Déjà disparu entre la prévisualisation et l'exécution.</summary>
        NotFound = 2,

        Denied = 3,

        /// <summary>Taille ou date modifiées depuis la prévisualisation : ce n'est plus le fichier montré.</summary>
        Changed = 4,

        Failed = 5,
    }

    public sealed class RecycleBinState
    {
        public long SizeBytes { get; init; }
        public long ItemCount { get; init; }
    }

    /// <summary>
    /// Accès au système de fichiers pour le nettoyage.
    /// </summary>
    /// <remarks>
    /// Seule passerelle de l'application autorisée à supprimer quoi que ce soit. Elle ne décide
    /// jamais de ce qui est supprimable : <c>LDI12.Actions</c> le fait, et lui passe des fichiers
    /// déjà relevés : la suppression vérifie que chacun est resté identique à ce qui a été montré
    /// au technicien.
    /// </remarks>
    public interface IFileSystemGateway
    {
        bool DirectoryExists(string path);

        /// <summary>Présence d'un fichier, sert à savoir si une console Windows est installée.</summary>
        bool FileExists(string path);

        /// <summary>
        /// Sous-dossiers immédiats, pour développer les racines à joker.
        /// </summary>
        /// <remarks>
        /// Les caches de navigateurs vivent sous un dossier de profil dont le nom varie
        /// (« Default », « Profile 1 », un identifiant Firefox) : sans cette lecture, le
        /// nettoyage ne verrait que le profil principal et laisserait les autres.
        /// </remarks>
        IReadOnlyList<string> EnumerateDirectories(string path);

        Measured<DirectoryScan> Scan(DirectoryScanRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Pèse un dossier sans retenir ce qu'il contient.
        /// </summary>
        /// <remarks>
        /// Opération distincte de <see cref="Scan"/>, et non un paramètre de celui-ci : ce qui
        /// change n'est pas un réglage mais ce qui sort de la méthode. L'une rend des chemins
        /// parce qu'elle prépare une suppression ; l'autre rend des totaux parce qu'elle regarde
        /// des données personnelles auxquelles personne n'a à toucher.
        /// </remarks>
        Measured<DirectoryMeasure> Measure(DirectoryMeasureRequest request, CancellationToken cancellationToken);

        /// <summary>Supprime un fichier relevé, à condition qu'il soit resté celui qui a été relevé.</summary>
        FileDeletion Delete(FileEntry entry);

        /// <summary>
        /// Copie un fichier relevé vers une destination, sans jamais écraser ni modifier la source.
        /// </summary>
        /// <remarks>
        /// La seule opération de ce logiciel qui écrit des données du client. Trois règles la
        /// gouvernent : la source n'est jamais touchée, un fichier différent portant déjà le nom
        /// de la destination n'est jamais écrasé, et une copie qui ne se relit pas identique est
        /// retirée plutôt que laissée en place : un fichier à moitié écrit ressemble à une
        /// sauvegarde, et c'est pire que pas de sauvegarde du tout.
        /// </remarks>
        FileCopyResult Copy(FileCopyRequest request, CancellationToken cancellationToken);

        /// <summary>Crée un dossier et ses parents. Rend faux si l'emplacement le refuse.</summary>
        bool CreateDirectory(string path);

        /// <summary>Place libre sur le volume qui porte ce chemin.</summary>
        Measured<long> FreeSpace(string path);

        /// <summary>Écrit un fichier texte, manifeste de sauvegarde, note de restitution.</summary>
        /// <remarks>
        /// Réservé aux fichiers que ce logiciel produit pour le technicien et qu'il dépose à
        /// côté de son travail. Il n'écrase jamais un fichier existant.
        /// </remarks>
        bool WriteText(string path, string content);

        /// <summary>Contenu d'un fichier texte, ou l'absence expliquée.</summary>
        /// <remarks>
        /// Ajoutée pour une opération qui doit relire ce qu'elle a annoncé : entre la
        /// prévisualisation d'un fichier hosts et son remplacement, quelques secondes passent, et
        /// rien ne garantit que les lignes montrées au technicien soient encore les mêmes.
        /// </remarks>
        Measured<string> ReadText(string path);

        /// <summary>
        /// Remplace le contenu d'un fichier existant.
        /// </summary>
        /// <remarks>
        /// Séparée de <see cref="WriteText"/>, qui refuse d'écraser : ce refus est une propriété
        /// de sûreté qu'il ne faut pas perdre pour le seul cas où l'écrasement est l'objet même
        /// de l'opération.
        /// </remarks>
        bool ReplaceText(string path, string content);

        /// <summary>Supprime les dossiers devenus vides sous la racine. Ne touche jamais la racine.</summary>
        int RemoveEmptyDirectories(string root, CancellationToken cancellationToken);

        Measured<RecycleBinState> ReadRecycleBin();

        /// <summary>Vide la corbeille de tous les volumes. Retourne faux si l'appel système a échoué.</summary>
        bool EmptyRecycleBin();
    }
}
