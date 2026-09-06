using System;
using System.Collections.Generic;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Ce qui explique une panne réseau que les tests de connectivité ne voient pas.
    /// </summary>
    /// <remarks>
    /// <b>Les constats de ce fichier ne portent pas sur la liaison, mais sur ce qui la commande.</b>
    /// Une machine peut passer tous les tests précédents et n'ouvrir aucune page, ne montrer aucun
    /// partage, ou refuser toute ouverture de session. Ce sont les pannes les plus longues à
    /// trouver précisément parce que tout le reste va bien.
    /// <para>
    /// Deux d'entre eux sont propres à l'entreprise (le proxy de la machine et les serveurs DNS
    /// d'un poste de domaine) et se taisent entièrement sur un poste de particulier. C'est
    /// voulu : une règle qui ne s'applique pas se tait, elle ne se contente pas de ne rien
    /// trouver.
    /// </para>
    /// </remarks>
    internal static class NetworkEnvironmentRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        /// <summary>Les deux proxys décrivent le même réglage vu de deux côtés.</summary>
        private const string ProxyFamily = "network.proxy";

        /// <summary>Le classement du réseau et sa portée viennent tous deux du même service.</summary>
        private const string LocationFamily = "network.location";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("NET-016", "Erreurs sur la liaison", Cat, LinkErrors, "network.link");
            yield return new Rule("NET-017", "Trames écartées", Cat, LinkDiscards, "network.link");
            yield return new Rule("NET-018", "Proxy de session", Cat, UserProxy, ProxyFamily);
            yield return new Rule("NET-019", "Proxy de la machine", Cat, MachineProxy, ProxyFamily);
            yield return new Rule("NET-020", "Portée du réseau", Cat, Reach, LocationFamily);
            yield return new Rule("NET-021", "Classement du réseau", Cat, Category, LocationFamily);
            yield return new Rule("NET-022", "Passerelles concurrentes", Cat, Gateways);
            yield return new Rule("NET-023", "Serveurs DNS d'un poste de domaine", Cat, DomainDns);
            yield return new Rule("NET-024", "Suffixe DNS du domaine", Cat, DomainSuffix);
        }

        // ============================================================ la liaison elle-même

        /// <summary>
        /// Des trames arrivent abîmées.
        /// </summary>
        /// <remarks>
        /// <b>Le seul constat du logiciel qui désigne un câble.</b> Rien d'autre ne le fait :
        /// la connexion fonctionne, les tests passent, le débit annoncé est bon, et la machine
        /// rame parce que chaque trame perdue est retransmise. La cause est presque toujours
        /// entre la carte et la prise (câble pincé, connecteur oxydé, port de commutateur
        /// fatigué) et elle se corrige en trois minutes une fois qu'on sait où regarder.
        /// </remarks>
        private static RuleResult LinkErrors(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                if (!adapter.IsUp) continue;

                var rate = adapter.Counters.ErrorsPerMillion;
                if (!rate.IsReliable) continue;

                evaluated = true;
                if (rate.Value < c.T.LinkErrorsPerMillionWarning) continue;

                var severity = rate.Value >= c.T.LinkErrorsPerMillionProblem ? Severity.Problem : Severity.Warning;
                var errors = adapter.Counters.ErrorsReceived.Or(0) + adapter.Counters.ErrorsSent.Or(0);

                findings.Add(c.Finding(severity,
                    "La liaison réseau perd des trames",
                    adapter.Name + " : " + Fmt.Number(errors) + " trame(s) en erreur, soit " +
                    Rate(rate.Value) + " pour un million (seuil " +
                    Rate(c.T.LinkErrorsPerMillionWarning) + ").",
                    "Des données arrivent abîmées sur cette carte réseau et doivent être renvoyées. " +
                    "La connexion fonctionne, mais elle travaille deux fois. La cause se trouve presque " +
                    "toujours entre la machine et la prise : un câble pincé ou trop long, un connecteur " +
                    "abîmé, ou un port défectueux sur la box ou le commutateur. Essayer un autre câble et " +
                    "un autre port est le contrôle le plus rapide.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Trames en erreur", Fmt.Number(errors), DataSource.NativeApi),
                        Evidence.Of("Taux", Rate(rate.Value) + " par million, dans les deux sens",
                            DataSource.NativeApi, Rate(c.T.LinkErrorsPerMillionWarning))),
                    recommendations: RuleContext.Rec(Rec.CheckNetworkCabling)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated(
                    "Aucune carte connectée n'expose de compteur d'erreurs exploitable.");
        }

        /// <summary>
        /// Des trames valides sont écartées.
        /// </summary>
        /// <remarks>
        /// Autre chose qu'une erreur, et le constat le dit : la trame était bonne, la machine n'a
        /// pas pu la traiter. Saturation, tampon plein, pilote en retard. Confondre les deux
        /// enverrait changer un câble parfaitement sain.
        /// </remarks>
        private static RuleResult LinkDiscards(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                if (!adapter.IsUp) continue;

                var counters = adapter.Counters;
                if (!counters.DiscardsReceived.IsReliable && !counters.DiscardsSent.IsReliable) continue;

                var packets = counters.PacketsReceived.Or(0) + counters.PacketsSent.Or(0);
                if (packets < MinimumPackets) continue;

                evaluated = true;

                var discards = counters.DiscardsReceived.Or(0) + counters.DiscardsSent.Or(0);
                if (discards == 0) continue;

                var rate = 1_000_000d * discards / (packets + discards);
                if (rate < c.T.LinkDiscardsPerMillionWarning) continue;

                findings.Add(c.Finding(Severity.Warning,
                    "Des données réseau sont écartées faute d'être traitées à temps",
                    adapter.Name + " : " + Fmt.Number(discards) + " trame(s) écartée(s), soit " +
                    Rate(rate) + " pour un million (seuil " +
                    Rate(c.T.LinkDiscardsPerMillionWarning) + ").",
                    "Ces données n'étaient pas abîmées : la machine n'a simplement pas su les traiter assez " +
                    "vite et les a laissées tomber. Ce n'est pas un problème de câble mais de charge : un " +
                    "pilote de carte réseau ancien, ou une machine sollicitée au-delà de ce qu'elle peut " +
                    "suivre. Mettre à jour le pilote de la carte est le premier geste.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Trames écartées", Fmt.Number(discards), DataSource.NativeApi,
                            Rate(c.T.LinkDiscardsPerMillionWarning) + " par million")),
                    recommendations: RuleContext.Rec(Rec.UpdateNetworkDriver)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune carte connectée n'a vu passer assez de trafic pour conclure.");
        }

        /// <summary>En deçà, un seul paquet écarté ferait un taux spectaculaire et sans aucun sens.</summary>
        private const long MinimumPackets = 10_000;

        // ============================================================ le proxy

        /// <summary>
        /// Un proxy est configuré pour la session.
        /// </summary>
        /// <remarks>
        /// En entreprise, c'est normal et le constat reste en information. Sur un poste de
        /// particulier, un proxy est presque toujours le reste d'un logiciel indésirable : c'est
        /// la manière la plus simple de détourner tout le trafic d'un navigateur sans rien
        /// installer de visible. Le même fait, deux lectures, et c'est l'appartenance à un
        /// domaine qui les sépare.
        /// </remarks>
        private static RuleResult UserProxy(RuleContext c)
        {
            var proxy = c.Network.Environment.UserProxy;
            if (!proxy.Enabled.IsReliable && !proxy.AutoConfigUrl.HasValue)
                return RuleResult.NotEvaluated("La configuration de proxy de la session n'a pas pu être lue.");

            if (!proxy.Configured) return RuleResult.Clean;

            var managed = c.Network.Environment.Domain.Joined.Or(false);
            var target = proxy.Server.HasValue
                ? proxy.Server.Value
                : proxy.AutoConfigUrl.Or("configuration automatique");

            return RuleResult.Of(c.Finding(managed ? Severity.Info : Severity.Warning,
                "Le trafic web de cette session passe par un serveur mandataire",
                "Proxy de session : " + target +
                (proxy.Bypass.HasValue ? " (exceptions : " + proxy.Bypass.Value + ")" : string.Empty) + ".",
                managed
                    ? "Cette machine appartient à un domaine : un serveur mandataire y est la règle, et ce " +
                      "constat est là pour mémoire. À vérifier tout de même si le web ne répond plus alors " +
                      "que le reste du réseau fonctionne, un mandataire injoignable coupe tout, en silence."
                    : "Les pages web de cette session ne sont pas demandées directement : elles passent par " +
                      "un intermédiaire. Sur un poste qui n'appartient à aucun réseau d'entreprise, ce " +
                      "réglage est rarement voulu : c'est la manière la plus simple de détourner ou de " +
                      "surveiller la navigation sans rien installer de visible. À confirmer avec le client " +
                      "avant de le retirer.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Serveur mandataire", target, DataSource.Registry)),
                recommendations: managed ? null : RuleContext.Rec(Rec.ReviewProxySettings)));
        }

        /// <summary>
        /// Le proxy de la machine, celui que suivent les services.
        /// </summary>
        /// <remarks>
        /// Windows en tient deux, sans lien entre elles. Le navigateur suit celle de
        /// l'utilisateur, Windows Update celle de la machine. Quand elles divergent, le web
        /// fonctionne et les mises à jour échouent, et on cherche du côté de Windows Update
        /// pendant une heure.
        /// </remarks>
        private static RuleResult MachineProxy(RuleContext c)
        {
            var environment = c.Network.Environment;
            var machine = environment.MachineProxy;
            var user = environment.UserProxy;

            if (!machine.Enabled.IsReliable)
                return RuleResult.NotEvaluated("La configuration de proxy de la machine n'a pas pu être lue.");

            var machineTarget = machine.Configured ? machine.Server.Or("proxy sans serveur nommé") : null;
            var userTarget = user.Configured ? user.Server.Or("proxy sans serveur nommé") : null;

            if (machineTarget == null && userTarget == null) return RuleResult.Clean;
            if (string.Equals(machineTarget, userTarget, StringComparison.OrdinalIgnoreCase)) return RuleResult.Clean;

            var technical = new StringBuilder();
            technical.Append("Machine : ").Append(machineTarget ?? "accès direct");
            technical.Append(" · Session : ").Append(userTarget ?? "accès direct").Append('.');

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Les services de Windows et le navigateur ne sortent pas par le même chemin",
                technical.ToString(),
                "Windows garde deux configurations de mandataire sans lien entre elles : celle de la " +
                "session, que suivent les navigateurs, et celle de la machine, que suivent les services, " +
                "Windows Update en premier. Elles ne concordent pas ici. C'est la cause classique d'une " +
                "machine qui navigue parfaitement mais n'installe plus aucune mise à jour.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Proxy de la machine", machineTarget ?? "accès direct", DataSource.Registry),
                    Evidence.Of("Proxy de la session", userTarget ?? "accès direct", DataSource.Registry)),
                recommendations: RuleContext.Rec(Rec.ReviewProxySettings)));
        }

        // ============================================================ le réseau lui-même

        /// <summary>
        /// Windows ne voit pas Internet sur ce réseau.
        /// </summary>
        /// <remarks>
        /// C'est l'état derrière le globe barré de la zone de notification. Windows le sait, il
        /// l'affiche, et jusqu'ici le logiciel ne le lui demandait pas ; il refaisait ses propres
        /// tests, qui ne disent pas la même chose : un ping qui passe ne prouve pas que Windows
        /// considère le réseau comme utilisable, et c'est cette appréciation-là qui décide du
        /// comportement de la moitié des applications.
        /// </remarks>
        private static RuleResult Reach(RuleContext c)
        {
            var locations = c.Network.Environment.Locations;
            if (locations.Count == 0)
                return RuleResult.NotEvaluated(
                    "Le service de localisation réseau n'a signalé aucun réseau connecté.");

            var findings = new List<Finding>();

            foreach (var location in locations)
            {
                if (location.Reach == NetworkReach.Internet || location.Reach == NetworkReach.Unknown) continue;

                var none = location.Reach == NetworkReach.None;

                findings.Add(c.Finding(none ? Severity.Problem : Severity.Warning,
                    none
                        ? "Windows ne joint rien sur ce réseau"
                        : "Windows ne voit pas Internet sur ce réseau",
                    "Réseau « " + location.Name + " » : " +
                    (none ? "aucune connectivité" : "réseau local seulement") + ".",
                    none
                        ? "La carte est connectée, mais Windows n'atteint rien du tout, ni Internet, ni les " +
                          "autres machines du réseau. C'est ce que signale l'icône barrée à côté de l'horloge."
                        : "Les autres machines du réseau sont joignables, mais pas Internet. C'est ce que " +
                          "signale le globe à côté de l'horloge. La cause est en général au-delà de cette " +
                          "machine : box, ligne, ou pare-feu du réseau.",
                    subject: location.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Portée vue par Windows",
                            none ? "aucune" : "réseau local seulement", DataSource.NativeApi, "Internet"))));
            }

            return RuleResult.Of(findings);
        }

        /// <summary>
        /// Le réseau est classé public, ce qui coupe la découverte et les partages.
        /// </summary>
        /// <remarks>
        /// Le classement public est le bon réglage par défaut, et il le reste sur un réseau
        /// inconnu : le constat ne dit donc pas de le changer. Il existe parce que « je ne vois
        /// plus l'imprimante » et « je n'accède plus au serveur » ont ici leur explication la
        /// plus fréquente, et qu'elle est invisible autrement.
        /// </remarks>
        private static RuleResult Category(RuleContext c)
        {
            var locations = c.Network.Environment.Locations;
            if (locations.Count == 0)
                return RuleResult.NotEvaluated(
                    "Le service de localisation réseau n'a signalé aucun réseau connecté.");

            var findings = new List<Finding>();

            foreach (var location in locations)
            {
                if (location.Category != NetworkCategory.Public) continue;

                findings.Add(c.Finding(Severity.Info,
                    "Ce réseau est classé public : partages et découverte sont coupés",
                    "Réseau « " + location.Name + " » classé public.",
                    "Windows applique à ce réseau les règles d'un lieu inconnu : la machine n'y est pas " +
                    "visible et n'y voit pas les autres. C'est la bonne protection dans un hôtel ou un café, " +
                    "et l'explication la plus fréquente d'une imprimante réseau ou d'un dossier partagé qui " +
                    "a « disparu » à la maison ou au bureau.",
                    subject: location.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Classement", "Public", DataSource.NativeApi))));
            }

            return RuleResult.Of(findings);
        }

        /// <summary>
        /// Deux cartes portent une passerelle par défaut.
        /// </summary>
        /// <remarks>
        /// L'une des pannes d'entreprise les plus coûteuses à trouver : Windows choisit par
        /// métrique, le choix peut changer au redémarrage, et le poste sort par la mauvaise porte
        /// un jour sur deux. Le symptôme (« ça marchait hier ») n'oriente vers rien.
        /// <para>
        /// Une liaison VPN active en est la cause légitime la plus fréquente : le constat la
        /// nomme au lieu de laisser croire à un défaut de configuration.
        /// </para>
        /// </remarks>
        private static RuleResult Gateways(RuleContext c)
        {
            var count = c.Network.DefaultGatewayCount;
            if (count <= 1) return RuleResult.Clean;

            var names = new List<string>();
            var vpn = false;

            foreach (var adapter in c.Network.Adapters)
            {
                if (!adapter.IsUp || adapter.Gateways.Count == 0) continue;
                if (adapter.Kind == NetworkAdapterKind.Loopback) continue;

                names.Add(adapter.Name);
                if (adapter.Kind == NetworkAdapterKind.Vpn || adapter.Kind == NetworkAdapterKind.Tunnel) vpn = true;
            }

            return RuleResult.Of(c.Finding(vpn ? Severity.Info : Severity.Warning,
                vpn
                    ? "Plusieurs sorties réseau, dont une liaison VPN"
                    : "Plusieurs sorties réseau sont actives en même temps",
                count + " interfaces portent une passerelle par défaut : " + string.Join(", ", names) + ".",
                vpn
                    ? "Une liaison VPN est montée en même temps que la connexion habituelle. C'est le " +
                      "fonctionnement normal d'un tunnel, et cela explique à la fois des serveurs DNS " +
                      "inattendus et une latence plus élevée. À garder en tête en lisant le reste des mesures."
                    : "Cette machine a plusieurs chemins possibles vers l'extérieur et Windows en choisit un " +
                      "selon une priorité qui peut changer d'un démarrage à l'autre. C'est l'explication " +
                      "classique d'un poste qui fonctionne un jour et plus le lendemain sans que rien n'ait " +
                      "été touché. Une seule sortie doit rester active, ou les priorités doivent être fixées.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Passerelles actives", count.ToString(), DataSource.NativeApi, "1"))));
        }

        // ============================================================ le domaine

        /// <summary>
        /// Un poste de domaine qui interroge des serveurs DNS publics.
        /// </summary>
        /// <remarks>
        /// Une erreur d'entreprise fréquente et lourde de conséquences : les serveurs publics ne
        /// connaissent aucun nom interne. Ouverture de session lente ou impossible, stratégies de
        /// groupe non appliquées, partages introuvables, trois symptômes sans rapport apparent
        /// pour une seule cause.
        /// <para>
        /// La règle ne se déclenche que sur une machine jointe à un domaine. Sur un poste de
        /// particulier, un DNS public est un choix ordinaire et parfaitement sain.
        /// </para>
        /// </remarks>
        private static RuleResult DomainDns(RuleContext c)
        {
            if (!c.Network.Environment.Domain.Joined.Or(false))
                return RuleResult.NotEvaluated(
                    "Cette machine n'appartient à aucun domaine : la question ne se pose pas.");

            var public_ = new List<string>();
            var seen = false;

            foreach (var adapter in c.Network.Adapters)
            {
                if (!adapter.IsUp || adapter.Gateways.Count == 0) continue;

                foreach (var server in adapter.DnsServers)
                {
                    seen = true;
                    if (!IsPrivate(server) && !public_.Contains(server)) public_.Add(server);
                }
            }

            if (!seen)
                return RuleResult.NotEvaluated("Aucun serveur DNS n'est déclaré sur les interfaces actives.");

            if (public_.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Un poste du domaine interroge des serveurs DNS extérieurs",
                "Serveurs hors du réseau local : " + string.Join(", ", public_) + ".",
                "Cette machine appartient à un domaine, et une partie de ses résolutions de noms part vers " +
                "des serveurs extérieurs qui ne connaissent aucun nom interne. Les conséquences n'ont " +
                "aucun rapport apparent entre elles : ouverture de session longue ou impossible, stratégies " +
                "de groupe non appliquées, partages et imprimantes introuvables. Un poste de domaine " +
                "n'interroge que les contrôleurs du domaine.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Serveurs DNS extérieurs", string.Join(", ", public_), DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Rec.ReviewDomainDns)));
        }

        /// <summary>
        /// Le suffixe DNS principal a disparu d'un poste de domaine.
        /// </summary>
        /// <remarks>
        /// Sans lui, aucun nom court ne se complète : « serveur » ne devient jamais
        /// « serveur.entreprise.local ». La machine reste jointe, la session s'ouvre encore avec
        /// des identifiants en cache, et plus rien d'interne n'est joignable par son nom.
        /// </remarks>
        private static RuleResult DomainSuffix(RuleContext c)
        {
            var domain = c.Network.Environment.Domain;
            if (!domain.Joined.IsReliable)
                return RuleResult.NotEvaluated("L'appartenance à un domaine n'a pas pu être déterminée.");

            if (!domain.Joined.Value)
                return RuleResult.NotEvaluated(
                    "Cette machine n'appartient à aucun domaine : la question ne se pose pas.");

            if (domain.PrimaryDnsSuffix.HasValue) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Ce poste du domaine n'a plus de suffixe DNS principal",
                "Domaine : " + domain.Name.Or("nom inconnu") + ", aucun suffixe DNS principal configuré.",
                "Cette machine appartient à un domaine mais ne sait plus compléter les noms courts : " +
                "« serveur » ne devient jamais « serveur.domaine.local ». Rien d'interne n'est plus " +
                "joignable par son nom, alors que la session continue de s'ouvrir grâce aux identifiants " +
                "gardés en mémoire. C'est une panne qui se déclare progressivement, et dont la cause est " +
                "ici.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Suffixe DNS principal", "absent", DataSource.Registry))));
        }

        /// <summary>
        /// Adresse d'un réseau privé, au sens de la RFC 1918.
        /// </summary>
        /// <remarks>
        /// Volontairement grossier, et suffisant : on cherche à distinguer « serveur du réseau
        /// local » de « serveur sur Internet », pas à valider un plan d'adressage. Une adresse
        /// IPv6 ou illisible est traitée comme locale : le doute profite au silence, parce qu'un
        /// constat de ce poids ne doit pas se déclencher sur une incertitude.
        /// </remarks>
        private static bool IsPrivate(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return true;
            if (address.IndexOf(':') >= 0) return true;

            var parts = address.Split('.');
            if (parts.Length != 4) return true;
            if (!int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second)) return true;

            if (first == 10 || first == 127) return true;
            if (first == 192 && second == 168) return true;
            if (first == 172 && second >= 16 && second <= 31) return true;
            if (first == 169 && second == 254) return true;

            return false;
        }

        private static string Rate(double value)
            => value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
    }
}
