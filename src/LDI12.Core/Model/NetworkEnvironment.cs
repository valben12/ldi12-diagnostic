using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Compteurs de la liaison, tels que la pile réseau les tient depuis le démarrage.
    /// </summary>
    /// <remarks>
    /// <b>La seule mesure du logiciel qui parle de la couche physique.</b> Un câble abîmé, une
    /// prise oxydée ou un port de commutateur fatigué ne se voient nulle part ailleurs : la
    /// connexion fonctionne, les tests passent, et la machine rame sans raison apparente parce que
    /// chaque trame perdue est retransmise. Les erreurs, elles, se comptent.
    /// <para>
    /// Les compteurs sont cumulés depuis le démarrage de Windows : bruts, ils ne veulent rien
    /// dire : dix erreurs sur mille paquets est une panne, dix sur dix milliards est une
    /// poussière. C'est <see cref="ErrorsPerMillion"/> qui se lit.
    /// </para>
    /// </remarks>
    public sealed class LinkCounters
    {
        public static readonly LinkCounters None = new LinkCounters();

        public Measured<long> BytesReceived { get; init; }
        public Measured<long> BytesSent { get; init; }
        public Measured<long> PacketsReceived { get; init; }
        public Measured<long> PacketsSent { get; init; }

        /// <summary>Trames reçues abîmées : la cause est presque toujours entre la carte et la prise.</summary>
        public Measured<long> ErrorsReceived { get; init; }

        public Measured<long> ErrorsSent { get; init; }

        /// <summary>
        /// Trames écartées sans être en erreur.
        /// </summary>
        /// <remarks>
        /// Autre chose qu'une erreur : la trame était valide, la machine n'a pas voulu ou pas pu
        /// la traiter. Saturation, tampon plein, pilote en retard. Un défaut de charge, pas de
        /// câble, et les deux ne se réparent pas de la même façon.
        /// </remarks>
        public Measured<long> DiscardsReceived { get; init; }

        public Measured<long> DiscardsSent { get; init; }

        /// <summary>Erreurs pour un million de trames, dans les deux sens. La seule valeur comparable.</summary>
        public Measured<double> ErrorsPerMillion { get; init; }

        public bool HasValue => PacketsReceived.HasValue || PacketsSent.HasValue;
    }

    /// <summary>Classement d'un réseau par Windows, qui commande le pare-feu et la découverte.</summary>
    public enum NetworkCategory
    {
        Unknown = 0,

        /// <summary>Découverte et partages coupés. C'est le réglage par défaut d'un réseau inconnu.</summary>
        Public = 1,

        Private = 2,

        /// <summary>Réseau d'un domaine Active Directory, classé par Windows lui-même.</summary>
        DomainAuthenticated = 3,
    }

    /// <summary>Ce que Windows pense pouvoir joindre depuis un réseau donné.</summary>
    public enum NetworkReach
    {
        Unknown = 0,

        /// <summary>Aucune connectivité : la carte est là, le réseau ne répond pas.</summary>
        None = 1,

        /// <summary>Le réseau local seulement : c'est l'état derrière le globe barré.</summary>
        LocalOnly = 2,

        Internet = 3,
    }

    /// <summary>Un réseau auquel la machine est connectée, vu par le service de localisation.</summary>
    public sealed class NetworkLocation
    {
        public string Name { get; init; } = string.Empty;

        public NetworkCategory Category { get; init; }

        public NetworkReach Reach { get; init; }

        /// <summary>Réseau reconnu comme appartenant à un domaine.</summary>
        public bool Managed { get; init; }
    }

    /// <summary>Où passent les requêtes HTTP, quand elles ne vont pas directement.</summary>
    /// <remarks>
    /// Windows en tient <b>deux</b>, sans lien entre elles : celle de l'utilisateur, que suivent
    /// les navigateurs, et celle de la machine, que suivent les services, Windows Update en
    /// tête. Les confondre fait chercher pendant une heure pourquoi le web fonctionne alors que
    /// les mises à jour échouent.
    /// </remarks>
    public sealed class ProxySettings
    {
        public static readonly ProxySettings NotCollected = new ProxySettings();

        public Measured<bool> Enabled { get; init; }

        public Measured<string> Server { get; init; }

        /// <summary>Adresses jointes en direct malgré le proxy.</summary>
        public Measured<string> Bypass { get; init; }

        /// <summary>Adresse d'un script de configuration automatique (fichier PAC).</summary>
        public Measured<string> AutoConfigUrl { get; init; }

        /// <summary>Détection automatique par WPAD : pratique en entreprise, détournable ailleurs.</summary>
        public Measured<bool> AutoDetect { get; init; }

        public bool Configured =>
            Enabled.Or(false) || !string.IsNullOrWhiteSpace(Server.Or(null!)) ||
            !string.IsNullOrWhiteSpace(AutoConfigUrl.Or(null!));
    }

    /// <summary>Appartenance de la machine à un domaine Active Directory.</summary>
    public sealed class DomainMembership
    {
        public static readonly DomainMembership NotCollected = new DomainMembership();

        public Measured<bool> Joined { get; init; }

        /// <summary>Nom du domaine, ou du groupe de travail quand la machine n'est pas jointe.</summary>
        public Measured<string> Name { get; init; }

        /// <summary>Suffixe DNS principal. Sans lui, aucun nom court d'entreprise ne se résout.</summary>
        public Measured<string> PrimaryDnsSuffix { get; init; }

        /// <summary>Suffixes essayés à la suite pour compléter un nom court.</summary>
        public IReadOnlyList<string> SearchSuffixes { get; init; } = Array.Empty<string>();

        /// <summary>Contrôleur qui a validé la dernière ouverture de session.</summary>
        public Measured<string> LogonServer { get; init; }
    }

    /// <summary>
    /// L'environnement réseau : ce qui décide du comportement, par-dessus ce qui est branché.
    /// </summary>
    /// <remarks>
    /// Tout ce qui est ici explique des pannes que les tests de connectivité ne voient pas. Une
    /// machine peut répondre au ping, résoudre les noms, joindre Internet, et n'ouvrir aucune
    /// page parce qu'un proxy pointe dans le vide, ne voir aucun partage parce que le réseau est
    /// classé public, ou refuser toute session parce que son suffixe DNS a disparu.
    /// </remarks>
    public sealed class NetworkEnvironment
    {
        public IReadOnlyList<NetworkLocation> Locations { get; init; } = Array.Empty<NetworkLocation>();

        public ProxySettings UserProxy { get; init; } = ProxySettings.NotCollected;

        public ProxySettings MachineProxy { get; init; } = ProxySettings.NotCollected;

        public DomainMembership Domain { get; init; } = DomainMembership.NotCollected;
    }
}
