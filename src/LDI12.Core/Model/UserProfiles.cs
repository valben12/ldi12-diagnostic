using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Ce que Windows fait du profil d'un compte.</summary>
    public enum ProfileState
    {
        Unknown = 0,

        Normal = 1,

        /// <summary>
        /// Windows n'a pas su charger ce profil et l'a mis de côté.
        /// </summary>
        /// <remarks>
        /// La clé de registre est renommée avec le suffixe <c>.bak</c> et un profil neuf est créé
        /// à la place. Le dossier d'origine, lui, est toujours là : rien n'est perdu, tout est
        /// simplement ailleurs. C'est exactement ce que le client décrit par « j'ai tout perdu ».
        /// </remarks>
        SetAside = 2,

        /// <summary>
        /// Profil provisoire, effacé à la fermeture de session.
        /// </summary>
        /// <remarks>
        /// Le cas le plus grave que ce logiciel puisse relever sans qu'aucun matériel soit en
        /// cause : tout ce que l'utilisateur enregistre disparaît au redémarrage, et rien à
        /// l'écran ne le lui dit à part une notification qu'il a fermée il y a trois semaines.
        /// </remarks>
        Temporary = 3,

        /// <summary>Le dossier du profil n'existe plus, la clé si.</summary>
        Orphaned = 4,
    }

    /// <summary>Un profil utilisateur, tel que Windows l'enregistre.</summary>
    public sealed class UserProfile
    {
        public string Sid { get; init; } = string.Empty;

        /// <summary>
        /// Nom du compte, quand il existe encore.
        /// </summary>
        /// <remarks>
        /// Absent pour un compte supprimé : c'est précisément ce qui identifie un profil resté
        /// derrière lui. Absent aussi pour un compte de domaine sur une machine qui ne joint plus
        /// son contrôleur : les deux se distinguent par la présence du dossier, pas par le nom.
        /// </remarks>
        public Measured<string> AccountName { get; init; }

        public string Path { get; init; } = string.Empty;

        public Measured<bool> FolderExists { get; init; }

        /// <summary>Dernière fermeture de session enregistrée pour ce profil.</summary>
        public Measured<DateTimeOffset> LastUsed { get; init; }

        /// <summary>Profil chargé en ce moment : une session est ouverte pour ce compte.</summary>
        public bool Loaded { get; init; }

        /// <summary>Profil de la session qui exécute le diagnostic.</summary>
        public bool IsCurrent { get; init; }

        public ProfileState State { get; init; }
    }

    /// <summary>
    /// Les comptes qui ont ouvert une session sur cette machine, et l'état de leurs profils.
    /// </summary>
    /// <remarks>
    /// <b>Un relevé qui répond à trois questions d'atelier différentes.</b> « J'ai tout perdu » :
    /// un profil mis de côté, dont le dossier est intact. « La machine rame » : trois sessions
    /// verrouillées qui gardent chacune leur mémoire. « Le disque est plein » : quatre profils
    /// d'anciens salariés que personne n'a jamais supprimés.
    /// </remarks>
    public sealed class ProfileInventory
    {
        public IReadOnlyList<UserProfile> Profiles { get; init; } = Array.Empty<UserProfile>();

        /// <summary>Profils chargés, donc sessions ouvertes, la session courante comprise.</summary>
        public Measured<int> LoadedCount { get; init; }
    }
}
