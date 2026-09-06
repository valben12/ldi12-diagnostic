using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Security
{
    /// <summary>
    /// Réglages de sécurité de Windows : pare-feu, contrôle de compte, SmartScreen, bureau à distance.
    /// </summary>
    /// <remarks>
    /// Tout est lu dans le registre plutôt que par <c>netsh advfirewall</c> ou PowerShell : la
    /// lecture est instantanée, ne dépend d'aucun service, et surtout n'est pas traduite. La
    /// sortie de <c>netsh</c> change de mots selon la langue de Windows ; une valeur de registre,
    /// non.
    /// <para>
    /// Aucune de ces clés n'exige de privilèges en lecture : la sonde fonctionne en session
    /// utilisateur normale, ce qui est le mode de lancement nominal du logiciel.
    /// </para>
    /// </remarks>
    public sealed class SecurityPolicyProbe : IDiagnosticProbe
    {
        private const string FirewallPolicy =
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

        private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

        private const string TerminalServer = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

        /// <summary>Nom de clé du profil, puis libellé affiché.</summary>
        private static readonly (string Key, string Label)[] Profiles =
        {
            ("DomainProfile", "Domaine"),
            ("StandardProfile", "Privé"),
            ("PublicProfile", "Public"),
        };

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SecurityPolicy,
            DisplayName = "Réglages de sécurité",
            Category = DiagnosticCategory.Security,
            EstimatedDuration = TimeSpan.FromMilliseconds(80),
            HardTimeout = TimeSpan.FromSeconds(15),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var firewall = ReadFirewall(context);
            var uac = ReadUac(context);
            var remote = ReadRemoteAccess(context);
            var smartScreen = ReadSmartScreen(context);

            context.Draft.SetSecurityPolicy(firewall, uac, remote, smartScreen);

            return Task.FromResult(Conclude(firewall, uac, remote));
        }

        private static ProbeOutcome Conclude(
            IReadOnlyList<FirewallProfileState> firewall, UacState uac, RemoteAccessState remote)
        {
            var off = new List<string>();
            foreach (var profile in firewall)
                if (profile.Enabled.HasValue && !profile.Enabled.Value) off.Add(profile.Profile);

            if (off.Count > 0)
                return ProbeOutcome.Ok("Pare-feu désactivé sur le profil " + string.Join(", ", off.ToArray()) + ".");

            if (uac.Enabled.HasValue && !uac.Enabled.Value)
                return ProbeOutcome.Ok("Le contrôle de compte d'utilisateur est désactivé.");

            if (remote.RemoteDesktopEnabled.Or(false))
                return ProbeOutcome.Ok("Le bureau à distance est activé sur cette machine.");

            if (firewall.Count == 0)
                return ProbeOutcome.Partial("L'état du pare-feu n'a pas pu être lu dans le registre.");

            return ProbeOutcome.Ok("Pare-feu actif sur tous les profils, contrôle de compte en place.");
        }

        /// <summary>
        /// État du pare-feu, profil par profil.
        /// </summary>
        /// <remarks>
        /// Trois profils et non un seul : une machine dont le pare-feu est actif sur le réseau
        /// domestique mais coupé sur le profil public est protégée chez elle et exposée dans un
        /// lieu public. C'est précisément la configuration qu'un client ne soupçonne pas.
        /// <para>
        /// La valeur poussée par stratégie de groupe l'emporte sur le réglage local : elle est
        /// donc lue en premier, sans quoi on afficherait un pare-feu « actif » que la stratégie
        /// a en réalité coupé.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<FirewallProfileState> ReadFirewall(ProbeContext context)
        {
            var states = new List<FirewallProfileState>(Profiles.Length);

            foreach (var (key, label) in Profiles)
            {
                var policy = context.Registry.ReadInt32(
                    RegistryHive.LocalMachine,
                    @"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + key,
                    "EnableFirewall");

                var local = context.Registry.ReadInt32(
                    RegistryHive.LocalMachine, FirewallPolicy + "\\" + key, "EnableFirewall");

                var value = policy ?? local;

                states.Add(new FirewallProfileState
                {
                    Profile = label,
                    Enabled = value == null
                        ? Measured.Missing<bool>(
                            "L'état du pare-feu pour le profil " + label + " n'est pas présent dans le registre.",
                            DataSource.Registry)
                        : Measured.Ok(value.Value != 0, DataSource.Registry),
                });
            }

            return states;
        }

        /// <summary>
        /// Contrôle de compte d'utilisateur.
        /// </summary>
        /// <remarks>
        /// Trois valeurs, parce qu'elles se contredisent utilement : l'UAC peut être activé
        /// (<c>EnableLUA</c>) tout en étant réglé pour ne jamais avertir
        /// (<c>ConsentPromptBehaviorAdmin</c> à 0), ce qui revient en pratique à l'avoir coupé.
        /// Ne lire que la première ferait conclure à une machine protégée.
        /// </remarks>
        private static UacState ReadUac(ProbeContext context)
        {
            var enabled = context.Registry.ReadInt32(RegistryHive.LocalMachine, SystemPolicies, "EnableLUA");
            var prompt = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, SystemPolicies, "ConsentPromptBehaviorAdmin");
            var secureDesktop = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, SystemPolicies, "PromptOnSecureDesktop");

            return new UacState
            {
                Enabled = enabled == null
                    ? Measured.Missing<bool>(
                        "Le réglage du contrôle de compte n'est pas présent dans le registre.", DataSource.Registry)
                    : Measured.Ok(enabled.Value != 0, DataSource.Registry),

                AdminPromptBehavior = prompt == null
                    ? Measured.Missing<int>(
                        "Le niveau d'invite du contrôle de compte n'est pas renseigné.", DataSource.Registry)
                    : Measured.Ok(prompt.Value, DataSource.Registry),

                SecureDesktop = secureDesktop == null
                    ? Measured.Missing<bool>(
                        "L'usage du bureau sécurisé n'est pas renseigné.", DataSource.Registry)
                    : Measured.Ok(secureDesktop.Value != 0, DataSource.Registry),
            };
        }

        private static RemoteAccessState ReadRemoteAccess(ProbeContext context)
        {
            var denied = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, TerminalServer, "fDenyTSConnections");

            var nla = context.Registry.ReadInt32(
                RegistryHive.LocalMachine,
                TerminalServer + @"\WinStations\RDP-Tcp", "UserAuthentication");

            var port = context.Registry.ReadInt32(
                RegistryHive.LocalMachine,
                TerminalServer + @"\WinStations\RDP-Tcp", "PortNumber");

            return new RemoteAccessState
            {
                // fDenyTSConnections = 1 signifie « connexions refusées » : la valeur est inversée.
                RemoteDesktopEnabled = denied == null
                    ? Measured.Missing<bool>(
                        "L'état du bureau à distance n'est pas présent dans le registre.", DataSource.Registry)
                    : Measured.Ok(denied.Value == 0, DataSource.Registry),

                NetworkLevelAuthentication = nla == null
                    ? Measured.Missing<bool>(
                        "Le réglage d'authentification réseau du bureau à distance n'est pas renseigné.",
                        DataSource.Registry)
                    : Measured.Ok(nla.Value != 0, DataSource.Registry),

                Port = port == null
                    ? Measured.Missing<int>("Le port du bureau à distance n'est pas renseigné.", DataSource.Registry)
                    : Measured.Ok(port.Value, DataSource.Registry),
            };
        }

        /// <summary>
        /// Filtre SmartScreen.
        /// </summary>
        /// <remarks>
        /// Quatre emplacements selon la version de Windows et le mode de configuration : valeur
        /// textuelle héritée de Windows 8 (« RequireAdmin », « Prompt », « Off »), deux valeurs de
        /// stratégie, et le réglage par utilisateur de l'évaluation du contenu web. Ils sont lus
        /// du plus contraignant au moins contraignant : une stratégie l'emporte sur un réglage
        /// local, et l'annoncer autrement afficherait un SmartScreen « actif » que la stratégie a
        /// en réalité coupé.
        /// <para>
        /// L'absence des quatre est déclarée comme telle. Sur une installation récente, aucune de
        /// ces valeurs n'existe tant que personne n'y a touché : traduire cette absence en
        /// « désactivé » ferait un faux constat sur la majorité des machines.
        /// </para>
        /// </remarks>
        private static Measured<bool> ReadSmartScreen(ProbeContext context)
        {
            var groupPolicy = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");

            if (groupPolicy != null) return Measured.Ok(groupPolicy.Value != 0, DataSource.Registry);

            var systemPolicy = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, SystemPolicies, "EnableSmartScreen");

            if (systemPolicy != null) return Measured.Ok(systemPolicy.Value != 0, DataSource.Registry);

            var explorerSetting = context.Registry.ReadString(
                RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");

            if (!string.IsNullOrWhiteSpace(explorerSetting))
                return Measured.Ok(
                    !string.Equals(explorerSetting, "Off", StringComparison.OrdinalIgnoreCase), DataSource.Registry);

            var webContent = context.Registry.ReadInt32(
                RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost", "EnableWebContentEvaluation");

            if (webContent != null) return Measured.Ok(webContent.Value != 0, DataSource.Registry);

            return Measured.Missing<bool>(
                "Aucun des quatre emplacements connus ne porte de réglage SmartScreen : " +
                "sur une installation où personne n'y a touché, c'est le cas normal.",
                DataSource.Registry);
        }
    }
}
