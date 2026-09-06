using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Updates
{
    /// <summary>Ce que le logiciel dit de lui-même en interrogeant le serveur.</summary>
    /// <remarks>
    /// La liste est fermée et courte, et c'est le point important : la requête ne transmet rien
    /// qui puisse désigner une machine ou une personne. Pas de nom d'ordinateur, pas
    /// d'identifiant d'installation, pas de relevé. Le serveur n'en tire que des compteurs
    /// agrégés par jour.
    /// </remarks>
    public sealed class UpdateQuery
    {
        public string Product { get; init; } = "ldi12-diagnostic";

        /// <summary>Version installée. Sert au serveur à décider s'il y a mieux.</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>« stable » ou « beta ».</summary>
        public string Channel { get; init; } = "stable";

        /// <summary>« x86 », « x64 », « arm64 » ou « any ».</summary>
        public string Architecture { get; init; } = "any";

        /// <summary>Famille de Windows : « win7 », « win10 », « win11 », « winserver ».</summary>
        public string OsFamily { get; init; } = string.Empty;

        /// <summary>Version réelle de Windows, pour juger le <c>minOs</c>. Ne quitte pas la machine.</summary>
        public string OsVersion { get; init; } = string.Empty;

        /// <summary>Version que le technicien a explicitement écartée. Ne quitte pas la machine.</summary>
        public string? SkippedVersion { get; init; }
    }

    /// <summary>
    /// La vérification de mise à jour.
    /// </summary>
    /// <remarks>
    /// Le retour est un <see cref="Measured{T}"/> et non une exception, parce qu'un réseau
    /// absent n'est pas une erreur : ce logiciel sert précisément chez les clients dont la box
    /// est en panne. « Absent, et voici pourquoi » est exactement ce qu'il faut dire, et le
    /// reste du logiciel sait déjà l'afficher discrètement.
    /// </remarks>
    public interface IUpdateChecker
    {
        Task<Measured<UpdateManifest>> CheckAsync(UpdateQuery query, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Ce qu'il faut conclure d'un manifeste, une fois la machine prise en compte.
    /// </summary>
    /// <remarks>
    /// Le serveur dit ce qui existe ; c'est ici qu'on décide ce que cela vaut sur ce poste-ci.
    /// Séparé de la requête pour être vérifiable sans réseau.
    /// </remarks>
    public static class UpdatePlan
    {
        /// <summary>
        /// La phrase qui annonce une version disponible.
        /// </summary>
        /// <remarks>
        /// Le titre et le résumé d'une version peuvent être vides : c'est le cas de la première
        /// version publiée sur le serveur, constaté et non supposé. Les concaténer sans regarder
        /// donnait « La version 1.19.0 est disponible, . », ce qui n'est pas une phrase.
        /// </remarks>
        private static string Announce(UpdateRelease release)
        {
            var subject = !string.IsNullOrWhiteSpace(release.Title)
                ? release.Title
                : release.Summary;

            return string.IsNullOrWhiteSpace(subject)
                ? "La version " + release.Version + " est disponible."
                : "La version " + release.Version + " est disponible : " + subject.TrimEnd('.') + ".";
        }

        public static UpdateOutlook Decide(UpdateManifest? manifest, UpdateQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            var release = manifest?.Latest;

            if (manifest == null || release == null || release.Download == null)
            {
                return new UpdateOutlook
                {
                    Decision = UpdateDecision.Nothing,
                    Message = "Aucune version n'est publiée pour le moment.",
                };
            }

            if (!VersionCompare.IsNewer(release.Version, query.Version))
            {
                return new UpdateOutlook
                {
                    Decision = UpdateDecision.UpToDate,
                    Release = release,
                    Message = "Vous avez la dernière version (" + query.Version + ").",
                };
            }

            // L'exigence de système passe avant tout le reste : proposer une version qui ne
            // démarrera pas serait pire que de ne rien proposer.
            if (!VersionCompare.Satisfies(query.OsVersion, release.MinOs))
            {
                return new UpdateOutlook
                {
                    Decision = UpdateDecision.TooOldForThisWindows,
                    Release = release,
                    Message = "La version " + release.Version + " demande un Windows plus récent que celui " +
                              "de cette machine. La version " + query.Version + " reste la dernière " +
                              "disponible ici.",
                };
            }

            // Une version écartée ne revient pas, sauf si elle corrige quelque chose qu'on ne
            // laisse pas traîner, auquel cas le choix du technicien ne s'applique pas.
            if (!release.Mandatory &&
                !string.IsNullOrEmpty(query.SkippedVersion) &&
                string.Equals(query.SkippedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateOutlook
                {
                    Decision = UpdateDecision.Skipped,
                    Release = release,
                    Message = "La version " + release.Version + " a été écartée sur ce poste.",
                };
            }

            return new UpdateOutlook
            {
                Decision = release.Mandatory ? UpdateDecision.Required : UpdateDecision.Offer,
                Release = release,
                Message = release.Mandatory
                    ? "La version " + release.Version + " corrige un défaut qui rend cette version-ci " +
                      "peu fiable : elle s'installe sans attendre."
                    : Announce(release),
            };
        }
    }
}
