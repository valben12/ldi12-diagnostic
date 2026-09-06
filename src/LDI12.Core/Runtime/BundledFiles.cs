using System;

namespace LDI12.Core.Runtime
{
    /// <summary>
    /// Accès aux fichiers embarqués dans l'exécutable, pour le code qui n'y a pas accès lui-même.
    /// </summary>
    /// <remarks>
    /// Le résolveur qui embarque les dépendances vit nécessairement dans l'exécutable hôte et non
    /// dans une bibliothèque : il doit être en place avant que la première bibliothèque soit
    /// chargée. Les couches basses ne peuvent donc pas l'appeler directement, alors que l'une
    /// d'elles en a besoin : le canal d'élévation doit retrouver l'hôte de sondes, qui est un
    /// second exécutable et se trouve embarqué comme le reste.
    /// <para>
    /// Un délégué posé au démarrage par l'hôte est la forme la plus simple qui n'inverse aucune
    /// dépendance. Nul en compilation ordinaire, où les fichiers sont côte à côte sur le disque :
    /// l'appelant retombe alors sur sa recherche habituelle.
    /// </para>
    /// </remarks>
    public static class BundledFiles
    {
        /// <summary>
        /// Préfixe des ressources embarquées. Doit rester identique à celui d'
        /// <c>AssemblyBundle</c>, dont ce projet ne peut pas dépendre.
        /// </summary>
        public const string ResourcePrefix = "LDI12.Bundle.";

        /// <summary>Posé au démarrage par l'exécutable hôte. Rend le chemin du fichier écrit, ou nul.</summary>
        public static Func<string, string?>? Extractor { get; set; }

        /// <summary>
        /// Même chose, dans un dossier temporaire que l'appelant s'engage à effacer.
        /// </summary>
        /// <remarks>
        /// L'écriture ordinaire dépose le fichier dans le profil de l'utilisateur et l'y laisse :
        /// c'est ce qu'il faut pour l'hôte élevé, qui doit survivre à l'invite UAC et que le
        /// technicien a explicitement demandé. L'isolation des sondes, elle, se déclenche à
        /// chaque analyse et sans que personne ne la demande, y déposer cinq mégaoctets sur la
        /// machine d'un client reviendrait à installer quelque chose sans le dire.
        /// </remarks>
        public static Func<string, string?>? TemporaryExtractor { get; set; }

        /// <summary>
        /// Écrit un fichier embarqué sur le disque, s'il y en a un.
        /// </summary>
        /// <remarks>
        /// Ne lève jamais : l'absence de fichier embarqué est l'état normal d'une compilation de
        /// développement, et l'appelant a toujours un autre chemin à essayer.
        /// </remarks>
        public static string? Extract(string fileName) => Invoke(Extractor, fileName);

        /// <summary>Écrit un fichier embarqué dans un dossier temporaire, s'il y en a un.</summary>
        public static string? ExtractTemporary(string fileName) => Invoke(TemporaryExtractor, fileName);

        private static string? Invoke(Func<string, string?>? extractor, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            try
            {
                return extractor?.Invoke(fileName);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
