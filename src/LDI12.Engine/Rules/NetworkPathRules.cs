using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Ce qui décide du chemin d'un paquet, et ce qui décide du sens d'un nom.
    /// </summary>
    /// <remarks>
    /// <b>Les pannes où tout est correct et où rien ne marche.</b> Le reste du logiciel sait dire
    /// qu'une machine a une adresse, une passerelle joignable et un serveur DNS qui répond. Ces
    /// règles répondent à la question qui vient ensuite, et à laquelle rien dans Windows ne
    /// répond : pourquoi, malgré tout cela, ce site-là ne s'ouvre pas, ce serveur-là ne se joint
    /// pas, ce NAS-là a disparu.
    /// </remarks>
    internal static class NetworkPathRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        internal const string RoutingFamily = "network.routing";
        internal const string NamesFamily = "network.names";
        internal const string SharingFamily = "network.sharing";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("NET-025", "Routes de sortie concurrentes", Cat, CompetingDefaults, RoutingFamily);
            yield return new Rule("NET-026", "Routes posées à la main", Cat, ManualRoutes, RoutingFamily);
            yield return new Rule("NET-027", "Redirections dans le fichier hosts", Cat, HostsEntries, NamesFamily);
            yield return new Rule("NET-028", "Domaines de mise à jour détournés", Cat, HostsConsequential, NamesFamily);
            yield return new Rule("NET-029", "Catalogue Winsock modifié", Cat, WinsockForeign, NamesFamily);
            yield return new Rule("NET-030", "Serveur DNS inconnu", Cat, UnknownDns, NamesFamily);
            yield return new Rule("NET-031", "Partage de fichiers de première génération", Cat, LegacySharing, SharingFamily);
            yield return new Rule("NET-032", "Découverte du voisinage arrêtée", Cat, Discovery, SharingFamily);
            yield return new Rule("NET-033", "NetBIOS coupé sur toutes les interfaces", Cat, Netbios, SharingFamily);
        }

        // ============================================================ routage

        /// <summary>
        /// Plusieurs routes de sortie, et Windows en choisit une.
        /// </summary>
        /// <remarks>
        /// Le cas typique n'est pas une erreur de configuration mais un tunnel : un client VPN
        /// pose sa propre route de sortie et laisse celle de la carte en place. Selon les
        /// métriques, le trafic part par l'une ou par l'autre, et le choix peut changer au
        /// redémarrage. Le symptôme est une machine qui « marche un jour sur deux ».
        /// <para>
        /// Une route de sortie par carte active reste normal sur un poste à deux réseaux : c'est
        /// leur présence sur des cartes différentes avec des métriques proches qui pose problème,
        /// et la règle le dit plutôt que de compter.
        /// </para>
        /// </remarks>
        private static RuleResult CompetingDefaults(RuleContext c)
        {
            var routing = c.Network.Paths.Routing;
            if (!routing.Count.IsReliable)
                return RuleResult.NotEvaluated(
                    routing.Count.Reason ?? "La table de routage n'a pas été relevée.");

            var defaults = routing.DefaultRoutes;
            if (defaults.Count < 2) return RuleResult.Clean;

            var described = new List<string>();
            var lowest = int.MaxValue;
            var second = int.MaxValue;

            foreach (var route in defaults)
            {
                described.Add((route.InterfaceName ?? "interface " + route.InterfaceIndex) +
                              " vers " + route.NextHop + " (métrique " + route.Metric + ")");

                if (route.Metric < lowest) { second = lowest; lowest = route.Metric; }
                else if (route.Metric < second) { second = route.Metric; }
            }

            // Des métriques très écartées décrivent une hiérarchie voulue, une carte de secours,
            // par exemple. C'est leur proximité qui rend le choix instable.
            var close = second - lowest <= 10;

            return RuleResult.Of(c.Finding(
                close ? Severity.Warning : Severity.Info,
                "Plusieurs chemins de sortie coexistent",
                defaults.Count + " route(s) par défaut : " + string.Join(" · ", described) + ".",
                "Cette machine a plusieurs façons de sortir vers Internet, et Windows en choisit une " +
                (close
                    ? "d'après des priorités très proches : le choix peut changer d'un démarrage à l'autre, ce " +
                      "qui donne un ordinateur qui fonctionne un jour et pas le lendemain sans que rien n'ait " +
                      "été touché. C'est presque toujours un logiciel de réseau privé qui a laissé son chemin " +
                      "en place."
                    : "en priorité. La hiérarchie est nette, donc le comportement est stable : c'est le cas " +
                      "normal d'un poste qui dispose d'une liaison de secours."),
                evidence: RuleContext.Ev(
                    Evidence.Of("Routes de sortie", defaults.Count.ToString(CultureInfo.CurrentCulture),
                        DataSource.NativeApi),
                    Evidence.Of("Écart de priorité",
                        (second - lowest).ToString(CultureInfo.CurrentCulture), DataSource.NativeApi)),
                recommendations: close ? RuleContext.Rec(Rec.ReviewStaticRoutes) : null));
        }

        /// <summary>
        /// Des routes que quelqu'un a posées, et que rien ne retirera tout seul.
        /// </summary>
        /// <remarks>
        /// Une route rendue permanente survit aux redémarrages, aux changements de box et à une
        /// réinitialisation de la pile réseau. Quand elle a été posée pour un serveur qui a
        /// déménagé, ou par un client VPN désinstallé depuis, elle envoie tout un sous-réseau
        /// dans le vide, et le poste est le seul du bureau à ne pas joindre ce serveur.
        /// </remarks>
        private static RuleResult ManualRoutes(RuleContext c)
        {
            var routing = c.Network.Paths.Routing;
            if (!routing.Count.IsReliable)
                return RuleResult.NotEvaluated(
                    routing.Count.Reason ?? "La table de routage n'a pas été relevée.");

            var manual = routing.Manual;
            if (manual.Count == 0) return RuleResult.Clean;

            var described = new List<string>();
            foreach (var route in manual)
                described.Add(route.Prefix + " via " + route.NextHop +
                              " (" + (route.InterfaceName ?? "interface " + route.InterfaceIndex) + ")");

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des chemins ont été ajoutés à la main",
                manual.Count + " route(s) statique(s) : " + string.Join(" · ", described) + ".",
                "Quelqu'un a indiqué à cet ordinateur un passage particulier pour joindre certaines " +
                "machines. C'est parfois volontaire, un serveur d'entreprise, une imprimante sur un autre " +
                "réseau. C'est parfois le reliquat d'un logiciel de réseau privé désinstallé, et dans ce cas " +
                "l'ordinateur cherche encore à passer par une porte qui n'existe plus. Ces chemins ne " +
                "disparaissent ni au redémarrage, ni en changeant de box.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Routes posées à la main",
                        manual.Count.ToString(CultureInfo.CurrentCulture), DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Rec.ReviewStaticRoutes)));
        }

        // ============================================================ noms

        /// <summary>
        /// Le fichier hosts contient des lignes actives.
        /// </summary>
        /// <remarks>
        /// Le constat est neutre parce que le contenu, lui, ne l'est pas toujours : un
        /// développeur y met ses noms de test, un technicien y bloque une régie publicitaire. Ce
        /// qui compte est que le technicien sache que ce fichier n'est pas vide : c'est la
        /// première chose à regarder quand un seul site ne s'ouvre pas, et la dernière à laquelle
        /// on pense.
        /// </remarks>
        private static RuleResult HostsEntries(RuleContext c)
        {
            var hosts = c.Network.Paths.Hosts;
            if (!hosts.ActiveCount.IsReliable)
                return RuleResult.NotEvaluated(
                    hosts.ActiveCount.Reason ?? "Le fichier hosts n'a pas été lu.");

            if (hosts.ActiveCount.Value == 0) return RuleResult.Clean;

            var blocked = 0;
            var redirected = 0;
            var names = new List<string>();

            foreach (var entry in hosts.Entries)
            {
                if (entry.Target == HostsTarget.Redirected) redirected++; else blocked++;
                foreach (var name in entry.Names) if (names.Count < 8) names.Add(name);
            }

            // Les domaines conséquents relèvent d'un constat distinct, plus grave : celui-ci ne
            // le répète pas.
            if (Consequential(hosts).Count > 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(
                redirected > 0 ? Severity.Warning : Severity.Info,
                "Des noms de sites sont détournés sur cette machine",
                hosts.ActiveCount.Value + " ligne(s) active(s) : " + blocked + " blocage(s), " +
                redirected + " redirection(s). " + string.Join(", ", names) +
                (names.Count < CountNames(hosts) ? "…" : string.Empty),
                "Un fichier de configuration de Windows contient des noms de sites traités à part. Ce " +
                "fichier l'emporte sur tout le reste : ni la box, ni le fournisseur d'accès, ni le " +
                "navigateur ne peuvent le contredire, et rien ne le signale à l'écran. C'est parfois " +
                "voulu : bloquer de la publicité, tester un site avant sa mise en ligne. C'est parfois un " +
                "logiciel qui s'est servi de ce fichier sans le dire.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Lignes actives",
                        hosts.ActiveCount.Value.ToString(CultureInfo.CurrentCulture), DataSource.FileSystem),
                    Evidence.Of("Fichier", hosts.Path ?? "non relevé", DataSource.FileSystem)),
                recommendations: RuleContext.Rec(Rec.CleanHostsFile)));
        }

        /// <summary>
        /// Le fichier hosts détourne des domaines dont la coupure produit un symptôme trompeur.
        /// </summary>
        /// <remarks>
        /// <b>Le seul constat de cette famille qui vaille un problème.</b> « Windows ne se met plus
        /// à jour » et « l'antivirus ne se met plus à jour » envoient un technicien vers le
        /// service de mise à jour, la connexion, le mandataire, et personne ne va lire un fichier
        /// texte de quatre lignes. Le logiciel ne qualifie pas le responsable : il dit ce qui est
        /// écrit et ce que cela empêche.
        /// </remarks>
        private static RuleResult HostsConsequential(RuleContext c)
        {
            var hosts = c.Network.Paths.Hosts;
            if (!hosts.ActiveCount.IsReliable)
                return RuleResult.NotEvaluated(
                    hosts.ActiveCount.Reason ?? "Le fichier hosts n'a pas été lu.");

            var hit = Consequential(hosts);
            if (hit.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Des services de mise à jour sont coupés par le fichier hosts",
                hit.Count + " domaine(s) détourné(s) : " + string.Join(", ", hit) + ".",
                "Des adresses de mise à jour (celles de Windows, ou celles d'un antivirus) sont " +
                "renvoyées vers le vide par un fichier de configuration local. La machine croit chercher " +
                "ces sites et ne les trouve jamais : les mises à jour échouent sans message clair, et la " +
                "cause paraît venir de partout ailleurs. Ces lignes ne se mettent pas là toutes seules ; " +
                "elles accompagnent d'ordinaire un logiciel installé hors de son circuit habituel.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Domaines détournés",
                        hit.Count.ToString(CultureInfo.CurrentCulture), DataSource.FileSystem),
                    Evidence.Of("Fichier", hosts.Path ?? "non relevé", DataSource.FileSystem)),
                recommendations: RuleContext.Rec(Rec.CleanHostsFile, Rec.RunWindowsUpdate)));
        }

        private static List<string> Consequential(HostsFile hosts)
        {
            var hit = new List<string>();
            foreach (var entry in hosts.Entries)
                foreach (var name in entry.Names)
                    if (HostsFile.IsConsequential(name) && !hit.Contains(name)) hit.Add(name);
            return hit;
        }

        private static int CountNames(HostsFile hosts)
        {
            var total = 0;
            foreach (var entry in hosts.Entries) total += entry.Names.Count;
            return total;
        }

        /// <summary>
        /// Une bibliothèque étrangère est insérée dans la pile réseau.
        /// </summary>
        /// <remarks>
        /// Sur une machine d'origine, les quatorze entrées du catalogue désignent toutes
        /// <c>mswsock.dll</c>, mesuré, pas supposé. Une entrée supplémentaire signale un
        /// programme qui voit passer tout le trafic ; et si ce programme a été désinstallé sans
        /// retirer son entrée, la pile appelle une bibliothèque absente et le réseau s'arrête net,
        /// adresse correcte et passerelle joignable au ping.
        /// </remarks>
        private static RuleResult WinsockForeign(RuleContext c)
        {
            var winsock = c.Network.Paths.Winsock;
            if (!winsock.Count.IsReliable)
                return RuleResult.NotEvaluated(
                    winsock.Count.Reason ?? "Le catalogue Winsock n'a pas été lu.");

            var foreign = winsock.Foreign;
            if (foreign.Count == 0) return RuleResult.Clean;

            var described = new List<string>();
            foreach (var provider in foreign) described.Add(provider.LibraryPath);

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Un programme s'est inséré dans la pile réseau",
                foreign.Count + " fournisseur(s) hors du dossier système : " +
                string.Join(" · ", described) + ".",
                "Tout ce que cet ordinateur envoie et reçoit passe par une liste de programmes, et cette " +
                "liste contient autre chose que Windows. Un contrôle parental, un ancien pare-feu ou un " +
                "« accélérateur » de connexion s'y installent volontiers. Tant que le programme est " +
                "présent, la machine fonctionne ; s'il a été désinstallé sans être retiré de la liste, le " +
                "réseau s'arrête complètement alors que tout le reste paraît correct.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fournisseurs étrangers",
                        foreign.Count.ToString(CultureInfo.CurrentCulture), DataSource.Registry),
                    Evidence.Of("Entrées au catalogue",
                        winsock.Count.Value.ToString(CultureInfo.CurrentCulture), DataSource.Registry)),
                recommendations: RuleContext.Rec(Rec.ResetWinsockCatalog)));
        }

        /// <summary>
        /// Un serveur DNS qui n'est ni la box, ni du réseau local, ni un résolveur connu.
        /// </summary>
        /// <remarks>
        /// <b>Trois exclusions avant de dire quoi que ce soit.</b> Mettre 1.1.1.1 ou 8.8.8.8 est le
        /// premier geste de tout technicien devant une box dont le résolveur rame : la machine de
        /// développement en porte deux, et une règle qui les signalerait apprendrait au technicien
        /// à ne plus la lire. Un serveur du même réseau est un filtre maison ou un serveur
        /// d'entreprise. Reste l'adresse publique que personne ne connaît, qui décide de ce que
        /// chaque nom de site veut dire sur cette machine.
        /// </remarks>
        private static RuleResult UnknownDns(RuleContext c)
        {
            var servers = c.Network.Paths.DnsServers;
            if (servers.Count == 0)
                return RuleResult.NotEvaluated("Aucun serveur DNS n'a été relevé.");

            var unknown = new List<string>();
            foreach (var server in servers)
                if (server.Nature == DnsServerNature.Unknown) unknown.Add(server.Address);

            if (unknown.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Un serveur de noms non identifié est utilisé",
                unknown.Count + " serveur(s) DNS hors du réseau local et hors des résolveurs connus : " +
                string.Join(", ", unknown) + ".",
                "C'est ce serveur qui dit à l'ordinateur à quelle machine correspond chaque adresse de " +
                "site. Il n'est ni la box, ni un serveur du réseau, ni l'un des services publics répandus. " +
                "Ce n'est pas une preuve de malveillance (un fournisseur d'accès ou une entreprise peut " +
                "avoir le sien) mais celui qui tient ce rôle voit passer tous les sites consultés et peut " +
                "en renvoyer certains ailleurs. Il vaut d'être identifié.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Serveurs non identifiés",
                        unknown.Count.ToString(CultureInfo.CurrentCulture), DataSource.NativeApi),
                    Evidence.Of("Serveurs configurés",
                        servers.Count.ToString(CultureInfo.CurrentCulture), DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Rec.FixDnsConfiguration)));
        }

        // ============================================================ voisinage

        /// <summary>
        /// Le partage de fichiers de première génération est encore actif.
        /// </summary>
        /// <remarks>
        /// Windows 10 et 11 ne l'installent plus : le trouver signifie que la machine vient d'une
        /// installation plus ancienne, ou que quelqu'un l'a rajouté pour un vieux NAS. Les deux
        /// méritent d'être dits, et la réponse n'est pas la même, d'où le rappel que le retirer
        /// coupe l'accès aux appareils qui n'ont que ce protocole.
        /// </remarks>
        private static RuleResult LegacySharing(RuleContext c)
        {
            var sharing = c.Network.Paths.Sharing;
            if (!sharing.Smb1Enabled.IsReliable)
                return RuleResult.NotEvaluated(
                    sharing.Smb1Enabled.Reason ?? "L'état du partage n'a pas été relevé.");

            if (!sharing.Smb1Enabled.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Un protocole de partage obsolète est actif",
                "Le pilote du partage de première génération est installé et son démarrage n'est pas " +
                "désactivé.",
                "Cette machine sait encore parler la première version du partage de fichiers de Windows, " +
                "conçue en 1983. Elle ne chiffre rien et ne vérifie pas à qui elle parle : c'est par elle " +
                "que se sont propagés les chiffrements de données les plus coûteux de ces dernières " +
                "années. Windows ne l'installe plus depuis longtemps ; sa présence vient d'une " +
                "installation ancienne ou d'un ajout volontaire pour un appareil qui ne sait rien faire " +
                "d'autre. Le retirer coupe l'accès à ces appareils-là : il faut savoir lesquels avant.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Pilote installé", "oui", DataSource.Registry)),
                recommendations: RuleContext.Rec(Rec.RemoveLegacySharing)));
        }

        /// <summary>
        /// Les services de découverte sont arrêtés sur un réseau où ils devraient tourner.
        /// </summary>
        /// <remarks>
        /// <b>Une mesure qui ne veut rien dire sans une seconde.</b> Ces services sont arrêtés sur
        /// la machine de développement, qui n'a aucun problème : sur un réseau public, c'est le
        /// réglage correct, et une règle qui les signalerait partout serait du bruit. Sur un
        /// réseau que l'utilisateur a déclaré privé, en revanche, il attend de voir son NAS et son
        /// imprimante, et leur absence n'a aucune autre explication visible.
        /// </remarks>
        private static RuleResult Discovery(RuleContext c)
        {
            var locations = c.Network.Environment.Locations;
            if (locations.Count == 0)
                return RuleResult.NotEvaluated(
                    "Le classement du réseau n'a pas été relevé : la découverte ne peut pas être jugée.");

            var onPrivate = false;
            foreach (var location in locations)
                if (location.Category == NetworkCategory.Private) onPrivate = true;

            if (!onPrivate)
                return RuleResult.NotEvaluated(
                    "Aucun réseau n'est déclaré privé : la découverte du voisinage y est coupée à juste titre.");

            var stopped = DiscoveryServices.Stopped(c.System.Services);
            if (stopped.Count == 0) return RuleResult.Clean;

            var described = new List<string>();
            foreach (var service in stopped)
                described.Add(service.Name + " (" + DiscoveryServices.Describe(service.Name) + ")");

            return RuleResult.Of(c.Finding(Severity.Info,
                "La machine ne cherche pas ses voisins sur le réseau",
                stopped.Count + " service(s) de découverte à l'arrêt sur un réseau déclaré privé : " +
                string.Join(" · ", described) + ".",
                "Sur un réseau désigné comme un réseau de confiance, Windows cherche normalement les " +
                "autres ordinateurs, les disques réseau et les imprimantes partagées. Ici, les services " +
                "qui font ce travail sont à l'arrêt : la liste du voisinage reste vide, et un NAS ou une " +
                "imprimante branchés au même routeur n'apparaissent nulle part. Rien n'est cassé, la " +
                "machine ne regarde simplement pas.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Services de découverte arrêtés",
                        stopped.Count.ToString(CultureInfo.CurrentCulture), DataSource.Wmi)),
                recommendations: RuleContext.Rec(Rec.StartDiscoveryServices)));
        }

        /// <summary>
        /// NetBIOS a été explicitement coupé partout.
        /// </summary>
        /// <remarks>
        /// C'est un durcissement courant, appliqué par une stratégie de groupe ou par un guide de
        /// sécurité, et parfaitement légitime. Il a une conséquence que personne ne relie : les
        /// partages désignés par un nom court, du genre <c>\\SERVEUR</c>, cessent de répondre alors
        /// que le même partage désigné par son adresse fonctionne.
        /// </remarks>
        private static RuleResult Netbios(RuleContext c)
        {
            var sharing = c.Network.Paths.Sharing;
            if (!sharing.NetbiosDisabled.IsReliable || !sharing.NetbiosInterfaces.IsReliable)
                return RuleResult.NotEvaluated(
                    sharing.NetbiosDisabled.Reason ?? "Le réglage NetBIOS n'a pas été relevé.");

            var total = sharing.NetbiosInterfaces.Value;
            if (total == 0 || sharing.NetbiosDisabled.Value < total) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "La résolution des noms courts est coupée",
                "NetBIOS est explicitement désactivé sur les " + total + " interface(s) relevée(s).",
                "Un ancien mécanisme de désignation des machines par un nom court a été coupé sur toutes " +
                "les cartes réseau. C'est un durcissement volontaire et raisonnable. Il a une conséquence " +
                "qu'on ne relie jamais à ce réglage : un partage ouvert par un nom court, du genre " +
                "« \\\\SERVEUR », ne répond plus, alors que le même partage ouvert par son adresse " +
                "fonctionne parfaitement.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Interfaces concernées",
                        total.ToString(CultureInfo.CurrentCulture), DataSource.Registry))));
        }
    }
}
