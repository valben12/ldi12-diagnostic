using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Audit de configuration des protections de Windows.
    /// </summary>
    /// <remarks>
    /// Aucune de ces règles ne prétend dire si la machine est infectée : le logiciel ne cherche
    /// pas de menace et n'ouvre aucun fichier de l'utilisateur. Elles disent si les protections
    /// que Windows expose sont en place et à jour, ce qui est vérifiable, et ce qu'un client ne
    /// peut pas constater seul.
    /// <para>
    /// Deux précautions de rédaction se retrouvent partout ici. D'abord, un réglage absent du
    /// registre ne vaut jamais « désactivé » : la règle n'est pas évaluée. Ensuite, aucune de ces
    /// conclusions n'est formulée comme un reproche : la plupart de ces réglages ont été changés
    /// par un logiciel tiers ou par un dépanneur précédent, pas par le client.
    /// </para>
    /// </remarks>
    internal static class SecurityAuditRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Security;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SEC-004", "Antivirus actif", Cat, Antivirus, "security.antivirus");
            yield return new Rule("SEC-005", "Fraîcheur des signatures", Cat, Signatures, "security.antivirus");
            yield return new Rule("SEC-006", "Pare-feu Windows", Cat, Firewall);
            yield return new Rule("SEC-007", "Contrôle de compte d'utilisateur", Cat, Uac);
            yield return new Rule("SEC-008", "Bureau à distance", Cat, RemoteDesktop);
            yield return new Rule("SEC-009", "Comptes sans mot de passe", Cat, PasswordlessAccounts, "security.accounts");
            yield return new Rule("SEC-010", "Comptes administrateurs", Cat, AdministratorAccounts, "security.accounts");
        }

        /// <summary>
        /// Un antivirus est-il actif ?
        /// </summary>
        /// <remarks>
        /// Le cas intéressant n'est pas « aucun antivirus » (rare) mais « un antivirus installé
        /// qui ne protège plus » : abonnement expiré, protection en temps réel coupée par un
        /// logiciel tiers, ou deux antivirus qui se neutralisent. Chacun laisse au client
        /// l'impression d'être protégé.
        /// </remarks>
        private static RuleResult Antivirus(RuleContext c)
        {
            var products = new List<SecurityProductInfo>();
            foreach (var product in c.Snapshot.Security.Products)
                if (product.Kind == SecurityProductKind.Antivirus) products.Add(product);

            if (products.Count == 0)
                return RuleResult.NotEvaluated(
                    "Aucun produit n'est déclaré au Centre de sécurité : l'état de la protection n'a pas pu être établi.");

            var active = new List<string>();
            var expired = new List<string>();
            var disabled = new List<string>();

            foreach (var product in products)
            {
                if (!product.State.HasValue) continue;
                switch (product.State.Value)
                {
                    case ProtectionState.Enabled: active.Add(product.Name); break;
                    case ProtectionState.Expired: expired.Add(product.Name); break;
                    case ProtectionState.Disabled: disabled.Add(product.Name); break;
                }
            }

            if (active.Count == 0 && expired.Count == 0 && disabled.Count == 0)
                return RuleResult.NotEvaluated("Aucun produit n'a rendu son état sous une forme interprétable.");

            if (expired.Count > 0 && active.Count == 0)
                return RuleResult.Of(c.Finding(Severity.Problem,
                    "L'antivirus installé n'est plus valide",
                    "Produit expiré : " + string.Join(", ", expired.ToArray()) +
                    ". Aucun autre antivirus actif n'est déclaré.",
                    "Votre antivirus est bien installé, son icône est visible, mais son abonnement a expiré : " +
                    "il ne protège plus. C'est le cas le plus trompeur, parce que rien ne le signale au quotidien.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Antivirus expiré", string.Join(", ", expired.ToArray()),
                            products[0].State.Source, "actif"))));

            if (active.Count == 0)
                return RuleResult.Of(c.Finding(Severity.Problem,
                    "Aucun antivirus actif",
                    disabled.Count > 0
                        ? "Produit(s) installé(s) mais désactivé(s) : " + string.Join(", ", disabled.ToArray()) + "."
                        : "Aucun antivirus actif déclaré au Centre de sécurité.",
                    "Aucune protection antivirus n'est active sur cette machine. " +
                    (disabled.Count > 0
                        ? "Un antivirus est installé mais éteint : il suffit souvent de le rallumer."
                        : "Windows en fournit un gratuitement, il suffit de l'activer."),
                    evidence: RuleContext.Ev(
                        Evidence.Of("Antivirus actif", "aucun", products[0].State.Source, "au moins un"))));

            if (active.Count > 1)
                return RuleResult.Of(c.Finding(Severity.Warning,
                    "Plusieurs antivirus fonctionnent en même temps",
                    active.Count + " antivirus actifs : " + string.Join(", ", active.ToArray()) + ".",
                    "Deux antivirus tournent en même temps sur cette machine. Ils se surveillent l'un l'autre, " +
                    "ralentissent l'ordinateur, et se gênent au point de laisser passer ce que chacun aurait vu seul. " +
                    "Il vaut mieux n'en garder qu'un.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Antivirus actifs", string.Join(", ", active.ToArray()),
                            products[0].State.Source, "un seul"))));

            return RuleResult.Clean;
        }

        /// <summary>
        /// Depuis quand les signatures datent-elles ?
        /// </summary>
        /// <remarks>
        /// La question qui compte sur une machine restée éteinte plusieurs mois, cas le plus
        /// fréquent en dépannage. La protection se déclare active, et elle l'est ; simplement,
        /// elle ne connaît rien de ce qui est apparu depuis.
        /// </remarks>
        private static RuleResult Signatures(RuleContext c)
        {
            var age = c.Snapshot.Security.Defender.SignatureAgeDays;
            if (!age.IsReliable)
                return RuleResult.NotEvaluated(
                    age.Reason ?? "L'ancienneté des signatures antivirus n'a pas pu être lue.");

            if (age.Value < c.T.SignatureAgeDaysWarning) return RuleResult.Clean;

            var severity = age.Value >= c.T.SignatureAgeDaysProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem ? c.T.SignatureAgeDaysProblem : c.T.SignatureAgeDaysWarning;

            return RuleResult.Of(c.Finding(severity,
                "Les signatures de l'antivirus sont anciennes",
                "Signatures vieilles de " + age.Value + " jour(s) (seuil " + threshold + ").",
                "La protection est bien active, mais sa liste des menaces connues date de " + age.Value +
                " jours. Elle ne reconnaît donc pas ce qui est apparu depuis. Une mise à jour la remet à niveau " +
                "en quelques minutes.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Âge des signatures", age.Value + " jours", age.Source, threshold + " jours")),
                recommendations: RuleContext.Rec(Rec.RunWindowsUpdate)));
        }

        /// <summary>
        /// Pare-feu, profil par profil.
        /// </summary>
        /// <remarks>
        /// Trois profils et non un seul : une machine dont le pare-feu est actif à la maison mais
        /// coupé sur le profil public est protégée chez elle et exposée dans un lieu public.
        /// C'est précisément la configuration qu'on ne soupçonne pas.
        /// </remarks>
        private static RuleResult Firewall(RuleContext c)
        {
            var profiles = c.Snapshot.Security.Firewall;
            if (profiles.Count == 0)
                return RuleResult.NotEvaluated("L'état du pare-feu n'a pas pu être lu.");

            var off = new List<string>();
            var read = 0;

            foreach (var profile in profiles)
            {
                if (!profile.Enabled.HasValue) continue;
                read++;
                if (!profile.Enabled.Value) off.Add(profile.Profile);
            }

            if (read == 0)
                return RuleResult.NotEvaluated("Aucun profil de pare-feu n'a rendu son état.");

            if (off.Count == 0) return RuleResult.Clean;

            var all = off.Count == read;
            var publicOff = off.Contains("Public");

            return RuleResult.Of(c.Finding(all || publicOff ? Severity.Problem : Severity.Warning,
                all ? "Le pare-feu est désactivé" : "Le pare-feu est désactivé sur certains réseaux",
                "Profil(s) désactivé(s) : " + string.Join(", ", off.ToArray()) + " sur " + read + " lus.",
                all
                    ? "Le pare-feu de Windows est éteint. Il filtre normalement ce qui essaie d'entrer depuis " +
                      "le réseau : sans lui, la machine répond à tout ce qu'on lui demande."
                    : "Le pare-feu est actif sur certains réseaux et éteint sur d'autres, notamment " +
                      string.Join(" et ", off.ToArray()) + ". La machine est donc protégée à certains endroits " +
                      "et pas à d'autres, sans que rien ne le signale.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Profils désactivés", string.Join(", ", off.ToArray()),
                        profiles[0].Enabled.Source, "aucun"))));
        }

        /// <summary>
        /// Contrôle de compte.
        /// </summary>
        /// <remarks>
        /// Deux réglages, et le second est le piège : l'UAC peut être activé tout en étant réglé
        /// pour ne jamais avertir, ce qui revient en pratique à l'avoir coupé. Ne lire que le
        /// premier ferait conclure à une machine protégée.
        /// </remarks>
        private static RuleResult Uac(RuleContext c)
        {
            var uac = c.Snapshot.Security.Uac;

            if (!uac.Enabled.HasValue)
                return RuleResult.NotEvaluated(uac.Enabled.Reason ?? "Le réglage du contrôle de compte n'a pas pu être lu.");

            if (!uac.Enabled.Value)
                return RuleResult.Of(c.Finding(Severity.Problem,
                    "Le contrôle de compte d'utilisateur est désactivé",
                    "EnableLUA vaut 0 : aucune élévation n'est demandée, et tout programme lancé " +
                    "hérite des droits administrateur.",
                    "Windows demande normalement une confirmation avant qu'un programme modifie l'ordinateur. " +
                    "Cette confirmation est désactivée : un programme peut donc tout changer sans rien demander.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Contrôle de compte", "désactivé", uac.Enabled.Source, "activé"))));

            if (uac.AdminPromptBehavior.IsReliable && uac.AdminPromptBehavior.Value == 0)
                return RuleResult.Of(c.Finding(Severity.Warning,
                    "Le contrôle de compte n'avertit jamais",
                    "EnableLUA vaut 1 mais ConsentPromptBehaviorAdmin vaut 0 : l'élévation est " +
                    "accordée sans invite. En pratique, la protection ne s'exerce pas.",
                    "La confirmation avant qu'un programme modifie l'ordinateur est réglée au niveau le plus bas : " +
                    "elle est comptée comme active, mais elle ne s'affiche jamais.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Niveau d'invite", "0, jamais avertir",
                            uac.AdminPromptBehavior.Source, "2 ou plus"))));

            return RuleResult.Clean;
        }

        /// <summary>
        /// Bureau à distance.
        /// </summary>
        /// <remarks>
        /// Activé n'est pas un défaut : beaucoup de machines professionnelles en ont besoin. Le
        /// constat est donc informatif tant que l'authentification réseau est en place, et ne
        /// devient un avertissement que lorsqu'elle ne l'est pas.
        /// </remarks>
        private static RuleResult RemoteDesktop(RuleContext c)
        {
            var remote = c.Snapshot.Security.RemoteAccess;

            if (!remote.RemoteDesktopEnabled.HasValue)
                return RuleResult.NotEvaluated(
                    remote.RemoteDesktopEnabled.Reason ?? "L'état du bureau à distance n'a pas pu être lu.");

            if (!remote.RemoteDesktopEnabled.Value) return RuleResult.Clean;

            var nla = remote.NetworkLevelAuthentication;
            var protectedByNla = nla.Or(true);

            return RuleResult.Of(c.Finding(protectedByNla ? Severity.Info : Severity.Warning,
                protectedByNla
                    ? "Le bureau à distance est activé"
                    : "Le bureau à distance est activé sans authentification préalable",
                "fDenyTSConnections vaut 0" +
                (nla.HasValue
                    ? ", authentification au niveau réseau " + (nla.Value ? "activée" : "désactivée")
                    : ", état de l'authentification réseau non lu") +
                (remote.Port.IsReliable ? ", port " + remote.Port.Value : string.Empty) + ".",
                protectedByNla
                    ? "Cette machine peut être pilotée à distance. C'est normal si c'est voulu ; " +
                      "si personne ne s'en sert, autant désactiver la fonction."
                    : "Cette machine peut être pilotée à distance, et la vérification d'identité qui devrait " +
                      "précéder la connexion est désactivée. C'est le réglage qu'il faut corriger en priorité.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Bureau à distance", "activé", remote.RemoteDesktopEnabled.Source))));
        }

        /// <summary>
        /// Comptes actifs ouvrables sans mot de passe.
        /// </summary>
        /// <remarks>
        /// Seuls les comptes <b>actifs</b> comptent : les comptes intégrés désactivés (Invité,
        /// DefaultAccount, WDAGUtilityAccount) portent souvent cet indicateur sans représenter
        /// le moindre risque, et les signaler ferait crier au loup sur toutes les machines.
        /// </remarks>
        private static RuleResult PasswordlessAccounts(RuleContext c)
        {
            var accounts = c.Snapshot.Security.Accounts;
            if (accounts.Count == 0)
                return RuleResult.NotEvaluated("Les comptes locaux n'ont pas pu être énumérés.");

            var exposed = new List<string>();
            var administrators = new List<string>();

            foreach (var account in accounts)
            {
                if (!account.Enabled.Or(false)) continue;
                if (!account.NoPasswordRequired.Or(false)) continue;

                exposed.Add(account.Name);
                if (account.IsAdministrator.Or(false)) administrators.Add(account.Name);
            }

            if (exposed.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(administrators.Count > 0 ? Severity.Problem : Severity.Warning,
                administrators.Count > 0
                    ? "Un compte administrateur s'ouvre sans mot de passe"
                    : "Un compte s'ouvre sans mot de passe",
                "Compte(s) actif(s) sans mot de passe requis : " + string.Join(", ", exposed.ToArray()) +
                (administrators.Count > 0
                    ? ", dont administrateur(s) : " + string.Join(", ", administrators.ToArray()) + "."
                    : "."),
                administrators.Count > 0
                    ? "Un compte qui a tous les droits sur cet ordinateur peut être ouvert sans mot de passe. " +
                      "Toute personne ayant accès à la machine (ou à son disque) peut donc tout y faire."
                    : "Un compte de cet ordinateur peut être ouvert sans mot de passe. C'est sans conséquence " +
                      "si la machine ne quitte jamais votre domicile ; c'en est une autre sinon.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Comptes sans mot de passe", string.Join(", ", exposed.ToArray()),
                        accounts[0].NoPasswordRequired.Source, "aucun"))));
        }

        /// <summary>
        /// Nombre de comptes administrateurs actifs.
        /// </summary>
        /// <remarks>
        /// Constat d'information et non d'alerte : sur un poste familial, plusieurs
        /// administrateurs sont la règle plus que l'exception. Le signaler sert à ouvrir la
        /// conversation, pas à faire peur, d'où une sévérité qui ne coûte aucun point.
        /// </remarks>
        private static RuleResult AdministratorAccounts(RuleContext c)
        {
            var accounts = c.Snapshot.Security.Accounts;
            if (accounts.Count == 0)
                return RuleResult.NotEvaluated("Les comptes locaux n'ont pas pu être énumérés.");

            var administrators = new List<string>();
            var read = 0;

            foreach (var account in accounts)
            {
                if (!account.IsAdministrator.HasValue) continue;
                read++;
                if (account.Enabled.Or(false) && account.IsAdministrator.Value) administrators.Add(account.Name);
            }

            if (read == 0)
                return RuleResult.NotEvaluated("L'appartenance au groupe Administrateurs n'a pas pu être établie.");

            if (administrators.Count < c.T.AdministratorAccountsWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Plusieurs comptes disposent des droits administrateur",
                administrators.Count + " compte(s) administrateur(s) actif(s) : " +
                string.Join(", ", administrators.ToArray()) + " (seuil " + c.T.AdministratorAccountsWarning + ").",
                administrators.Count + " comptes de cet ordinateur peuvent tout y modifier. Sur un poste " +
                "familial c'est courant ; sur un poste professionnel, il vaut mieux qu'un seul compte ait ces droits " +
                "et que l'usage quotidien passe par un compte ordinaire.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Comptes administrateurs", administrators.Count.ToString(),
                        accounts[0].IsAdministrator.Source, c.T.AdministratorAccountsWarning.ToString()))));
        }
    }
}
