using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Publishing
{
    /// <summary>Un document joint à une publication.</summary>
    public sealed class PublicationDocument
    {
        public string FileName { get; init; } = string.Empty;

        /// <summary>Titre lisible : « Bilan client », « Comparatif avant / après ».</summary>
        public string Title { get; init; } = string.Empty;

        public string MediaType { get; init; } = "text/html";

        public byte[] Content { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Ce qui quitterait la machine.
    /// </summary>
    /// <remarks>
    /// Assemblée par l'appelant et montrée au technicien <b>avant</b> tout envoi. Un dossier de
    /// diagnostic porte le nom du client, celui de sa machine et son numéro de série ; l'envoyer
    /// est une décision, et une décision se prend en sachant ce qu'elle recouvre.
    /// </remarks>
    public sealed class PublicationRequest
    {
        public string MachineName { get; init; } = string.Empty;

        /// <summary>Référence du dossier client : c'est par elle que Klarvi rapproche l'envoi.</summary>
        public string? ClientReference { get; init; }

        public string? Technician { get; init; }

        public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.Now;

        /// <summary>Empreinte de la machine, quand elle a pu être établie. Jamais son numéro de série.</summary>
        public string? Fingerprint { get; init; }

        public int? Score { get; init; }

        public IReadOnlyList<PublicationDocument> Documents { get; init; } = Array.Empty<PublicationDocument>();
    }

    public sealed class PublicationOutcome
    {
        public bool Published { get; init; }

        /// <summary>Phrase affichée au technicien, dans les deux cas.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>Référence rendue par le service, quand il en rend une.</summary>
        public string? Reference { get; init; }

        public static PublicationOutcome Failed(string summary)
            => new PublicationOutcome { Published = false, Summary = summary };
    }

    /// <summary>
    /// Dépôt d'un dossier de diagnostic vers un service externe.
    /// </summary>
    /// <remarks>
    /// <b>Le cœur ne connaît que cette interface.</b> L'implémentation HTTP vit dans un projet à
    /// part, que ni les sondes, ni le moteur, ni les rapports ne référencent : un logiciel dont la
    /// promesse est de fonctionner hors ligne ne doit pas pouvoir acquérir une dépendance réseau
    /// par inadvertance, et la seule garantie solide contre cela est qu'il n'y ait pas de chemin
    /// de compilation qui y mène.
    /// <para>
    /// Rien n'est jamais envoyé sans un geste explicite. Il n'y a ni envoi automatique en fin
    /// d'analyse, ni vérification de connexion au démarrage, ni réessai en arrière-plan : un
    /// diagnostic contient des données du client, et le technicien décide de chaque envoi.
    /// </para>
    /// </remarks>
    public interface IReportPublisher
    {
        /// <summary>Nom du service, tel qu'il s'affiche.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Le service est configuré, ou la raison pour laquelle il ne l'est pas.
        /// </summary>
        /// <remarks>
        /// Une mesure et non un booléen : « pas d'adresse de service » et « jeton absent » ne se
        /// corrigent pas de la même façon, et un bouton grisé sans phrase ne dit ni l'un ni l'autre.
        /// </remarks>
        Measured<bool> Configured { get; }

        /// <summary>
        /// Ce qui quitterait la machine, en clair, avant que quoi que ce soit ne parte.
        /// </summary>
        IReadOnlyList<string> DescribeWhatIsSent(PublicationRequest request);

        Task<PublicationOutcome> PublishAsync(
            PublicationRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
    }
}
