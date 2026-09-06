using System;
using System.Collections.Generic;

namespace LDI12.Core.Updates
{
    /// <summary>Le fichier proposé au téléchargement, et de quoi vérifier qu'on a bien reçu celui-là.</summary>
    public sealed class UpdateDownload
    {
        public string Url { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public long Size { get; init; }

        /// <summary>
        /// Empreinte attendue, en hexadécimal minuscule.
        /// </summary>
        /// <remarks>
        /// C'est elle qui porte la confiance, pas le transport : un réseau d'entreprise qui
        /// déchiffre le trafic peut servir un autre exécutable, il ne peut pas lui donner cette
        /// empreinte-là sans casser la signature du manifeste qui l'annonce.
        /// </remarks>
        public string Sha256 { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string Arch { get; init; } = string.Empty;

        /// <summary>Le fichier porte une signature de code. Faux tant que le certificat n'est pas acheté.</summary>
        public bool Signed { get; init; }
    }

    /// <summary>Une version publiée, telle que le serveur la décrit.</summary>
    public sealed class UpdateRelease
    {
        public string Version { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        /// <summary>Trois points marquants au plus : ce qui tient dans un bandeau.</summary>
        public IReadOnlyList<string> Highlights { get; init; } = Array.Empty<string>();

        public DateTimeOffset? ReleasedAt { get; init; }

        /// <summary>
        /// Version que le technicien n'a pas à pouvoir écarter.
        /// </summary>
        /// <remarks>
        /// Réservée à ce qui rend une version précédente dangereuse ou fausse, un constat qui
        /// se trompe, une opération qui abîme. Une amélioration, même importante, ne l'est pas.
        /// </remarks>
        public bool Mandatory { get; init; }

        /// <summary>Version minimale de Windows, sous la forme « 6.1 » ou « 10.0.19041 ».</summary>
        public string MinOs { get; init; } = string.Empty;

        public string NotesUrl { get; init; } = string.Empty;

        public string PageUrl { get; init; } = string.Empty;

        public UpdateDownload? Download { get; init; }
    }

    /// <summary>
    /// Ce que le serveur répond quand on lui demande s'il existe mieux que ce qui tourne.
    /// </summary>
    /// <remarks>
    /// L'objet reconstruit ici ne vient <b>jamais</b> du corps lisible de la réponse : il est
    /// analysé à partir des octets signés, et seulement après que la signature a été vérifiée.
    /// Les champs lisibles de la réponse HTTP ne servent qu'à l'inspection humaine.
    /// </remarks>
    public sealed class UpdateManifest
    {
        public string Product { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Channel { get; init; } = "stable";

        public DateTimeOffset? CheckedAt { get; init; }

        /// <summary>La version que le client a annoncée, telle que le serveur l'a comprise.</summary>
        public string? Current { get; init; }

        public bool UpdateAvailable { get; init; }

        /// <summary>Nul tant qu'aucune version n'est publiée sur ce canal.</summary>
        public UpdateRelease? Latest { get; init; }
    }

    /// <summary>Ce qu'il faut faire de ce que le serveur a répondu.</summary>
    public enum UpdateDecision
    {
        /// <summary>Rien à proposer : le poste porte déjà la dernière version.</summary>
        UpToDate,

        /// <summary>Une version existe, et le technicien décide.</summary>
        Offer,

        /// <summary>Une version existe et corrige quelque chose qu'on ne laisse pas traîner.</summary>
        Required,

        /// <summary>Une version existe mais demande un Windows plus récent que celui-ci.</summary>
        TooOldForThisWindows,

        /// <summary>Le technicien a écarté cette version, et elle n'est pas obligatoire.</summary>
        Skipped,

        /// <summary>Le serveur ne publie rien sur ce canal, ou aucun fichier n'est joint.</summary>
        Nothing,
    }

    /// <summary>
    /// La conclusion tirée d'un manifeste vérifié, et la phrase qui va avec.
    /// </summary>
    /// <remarks>
    /// Séparée du manifeste parce qu'elle dépend de la machine (sa version de Windows, la
    /// version que le technicien a écartée) et non du serveur. Le serveur dit ce qui existe ;
    /// c'est ici qu'on décide ce que cela vaut ici.
    /// </remarks>
    public sealed class UpdateOutlook
    {
        public UpdateDecision Decision { get; init; }

        public UpdateRelease? Release { get; init; }

        /// <summary>Une phrase, écrite pour être affichée telle quelle.</summary>
        public string Message { get; init; } = string.Empty;

        public bool CanInstall => Decision == UpdateDecision.Offer || Decision == UpdateDecision.Required;
    }

    /// <summary>
    /// Comparaison de deux numéros de version, et de deux versions de Windows.
    /// </summary>
    /// <remarks>
    /// Écrite plutôt que déléguée à <see cref="Version"/> pour une raison précise : un numéro
    /// de version peut porter un suffixe de pré-publication (« 1.21.0-beta.2 ») que
    /// <see cref="Version"/> refuse en levant une exception. Ici, le suffixe est simplement
    /// ignoré pour la comparaison, ce qui suffit à décider s'il faut proposer quelque chose.
    /// </remarks>
    public static class VersionCompare
    {
        /// <summary>Compare deux numéros. Négatif si <paramref name="left"/> est plus ancien.</summary>
        public static int Compare(string? left, string? right)
        {
            var a = Parts(left);
            var b = Parts(right);

            for (var index = 0; index < 4; index++)
            {
                if (a[index] != b[index]) return a[index] < b[index] ? -1 : 1;
            }

            return 0;
        }

        /// <summary><paramref name="candidate"/> est postérieure à <paramref name="installed"/>.</summary>
        public static bool IsNewer(string? candidate, string? installed) => Compare(installed, candidate) < 0;

        /// <summary>La machine satisfait la version minimale demandée.</summary>
        /// <remarks>
        /// Une exigence vide ou illisible n'est jamais une raison de refuser une mise à jour :
        /// dans le doute, la version est proposée. Un serveur qui envoie n'importe quoi ne doit
        /// pas pouvoir priver un poste de ses correctifs.
        /// </remarks>
        public static bool Satisfies(string? osVersion, string? minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum)) return true;
            if (string.IsNullOrWhiteSpace(osVersion)) return true;

            return Compare(osVersion, minimum) >= 0;
        }

        private static int[] Parts(string? version)
        {
            var parts = new int[4];
            if (string.IsNullOrWhiteSpace(version)) return parts;

            // Le suffixe de pré-publication ne participe pas à la comparaison.
            var text = version!.Trim();
            var dash = text.IndexOf('-');
            if (dash > 0) text = text.Substring(0, dash);

            var fields = text.Split('.');
            for (var index = 0; index < fields.Length && index < 4; index++)
            {
                if (int.TryParse(fields[index], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0)
                {
                    parts[index] = value;
                }
            }

            return parts;
        }
    }
}
