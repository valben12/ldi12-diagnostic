using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Nature d'un produit de sécurité déclaré au Centre de sécurité Windows.</summary>
    public enum SecurityProductKind
    {
        Unknown = 0,
        Antivirus = 1,
        Antispyware = 2,
        Firewall = 3,
    }

    /// <summary>
    /// État d'une protection.
    /// </summary>
    /// <remarks>
    /// <see cref="Expired"/> mérite d'être distingué d'<see cref="Disabled"/> : un antivirus dont
    /// l'abonnement a expiré est installé, visible dans la barre des tâches, et ne protège plus.
    /// C'est le cas que le client ne voit jamais tout seul.
    /// </remarks>
    public enum ProtectionState
    {
        Unknown = 0,
        Enabled = 1,
        Disabled = 2,
        Expired = 3,
    }

    public sealed class SecurityProductInfo
    {
        public string Name { get; init; } = string.Empty;

        public SecurityProductKind Kind { get; init; }

        public Measured<ProtectionState> State { get; init; }

        /// <summary>Base de signatures à jour, quand le produit le déclare.</summary>
        public Measured<bool> UpToDate { get; init; }

        /// <summary>Produit intégré à Windows plutôt qu'installé par l'utilisateur.</summary>
        public bool IsBuiltIn { get; init; }
    }

    /// <summary>
    /// État de Microsoft Defender, lu séparément du Centre de sécurité.
    /// </summary>
    /// <remarks>
    /// Le Centre de sécurité dit qu'un antivirus est actif ; il ne dit pas depuis quand ses
    /// signatures datent. Sur une machine restée éteinte des mois (le cas le plus fréquent en
    /// dépannage), la protection est « active » et pourtant aveugle aux menaces récentes.
    /// </remarks>
    public sealed class DefenderStatus
    {
        public Measured<bool> AntivirusEnabled { get; init; }
        public Measured<bool> RealTimeProtectionEnabled { get; init; }
        public Measured<DateTimeOffset> SignatureDate { get; init; }
        public Measured<int> SignatureAgeDays { get; init; }
        public Measured<DateTimeOffset> LastScan { get; init; }
        public Measured<bool> TamperProtectionEnabled { get; init; }
    }

    public sealed class FirewallProfileState
    {
        /// <summary>Domaine, Privé ou Public : le profil public est celui des réseaux inconnus.</summary>
        public string Profile { get; init; } = string.Empty;

        public Measured<bool> Enabled { get; init; }
    }

    /// <summary>
    /// Contrôle de compte d'utilisateur.
    /// </summary>
    /// <remarks>
    /// Deux réglages distincts, souvent confondus : l'UAC peut être activé mais réglé pour ne
    /// jamais avertir, ce qui revient pratiquement à l'avoir désactivé. Les mesurer séparément
    /// permet de le dire.
    /// </remarks>
    public sealed class UacState
    {
        public Measured<bool> Enabled { get; init; }

        /// <summary>Niveau d'invite pour les administrateurs (0 = jamais avertir).</summary>
        public Measured<int> AdminPromptBehavior { get; init; }

        /// <summary>Bureau sécurisé pendant l'invite : sans lui, un logiciel peut imiter la fenêtre.</summary>
        public Measured<bool> SecureDesktop { get; init; }
    }

    public sealed class LocalAccountInfo
    {
        public string Name { get; init; } = string.Empty;
        public Measured<bool> Enabled { get; init; }
        public Measured<bool> IsAdministrator { get; init; }

        /// <summary>Compte ouvrable sans mot de passe. Sur un poste partagé, c'est une porte ouverte.</summary>
        public Measured<bool> NoPasswordRequired { get; init; }

        public Measured<bool> PasswordNeverExpires { get; init; }
    }

    public sealed class RemoteAccessState
    {
        public Measured<bool> RemoteDesktopEnabled { get; init; }

        /// <summary>
        /// Authentification au niveau du réseau : sans elle, une session est ouverte avant même
        /// que l'utilisateur se soit identifié.
        /// </summary>
        public Measured<bool> NetworkLevelAuthentication { get; init; }

        public Measured<int> Port { get; init; }
    }

    /// <summary>
    /// Dimension Sécurité de l'instantané.
    /// </summary>
    /// <remarks>
    /// Le logiciel n'est pas un antivirus et ne le devient pas ici : il ne recherche aucune
    /// menace, ne lit aucun fichier de l'utilisateur et n'émet aucun verdict sur la propreté de
    /// la machine. Il constate l'état des protections que Windows expose (active ou non, à jour
    /// ou non), ce qui relève de l'audit de configuration et non de la détection.
    /// </remarks>
    public sealed class SecuritySnapshot
    {
        public IReadOnlyList<SecurityProductInfo> Products { get; init; } = Array.Empty<SecurityProductInfo>();

        public DefenderStatus Defender { get; init; } = new DefenderStatus();

        public IReadOnlyList<FirewallProfileState> Firewall { get; init; } = Array.Empty<FirewallProfileState>();

        public UacState Uac { get; init; } = new UacState();

        public IReadOnlyList<LocalAccountInfo> Accounts { get; init; } = Array.Empty<LocalAccountInfo>();

        public RemoteAccessState RemoteAccess { get; init; } = new RemoteAccessState();

        /// <summary>Filtre SmartScreen de Windows, quand la version le propose.</summary>
        public Measured<bool> SmartScreenEnabled { get; init; }
    }
}
