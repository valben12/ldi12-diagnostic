using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Ce que la machine écoute, et par quel programme.
    /// </summary>
    /// <remarks>
    /// <b>Ce logiciel n'est pas un antivirus, et ces règles ne sont pas un balayage de sécurité.</b>
    /// Elles répondent à la question qu'un technicien se pose sur tout poste qu'il ne connaît
    /// pas : qu'est-ce qui attend des connexions ici, et qui l'a mis là. Rien n'est jugé
    /// dangereux ; ce qui est signalé l'est parce qu'il ne s'y trouve pas par défaut, donc que
    /// quelqu'un l'y a mis, parfois sans le savoir.
    /// </remarks>
    internal static class ListeningPortRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        private const string Family = "network.exposure";

        /// <summary>
        /// Ports que Windows ouvre lui-même sur toute installation.
        /// </summary>
        /// <remarks>
        /// Les signaler reviendrait à signaler Windows : 135 est l'appel de procédure distante,
        /// 139 et 445 le partage de fichiers, et les numéros au-delà de 49152 sont les ports
        /// éphémères que le système attribue à ses propres services. Une règle qui se déclenche
        /// sur toutes les machines ne se déclenche utilement sur aucune.
        /// </remarks>
        private static bool IsWindowsDefault(int port)
            => port == 135 || port == 139 || port == 445 || port >= 49152;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("LSN-001", "Services exposés au réseau", Cat, Exposed, Family);
            yield return new Rule("LSN-002", "Partage de fichiers sur un réseau public", Cat, PublicSharing, Family);
        }

        /// <summary>
        /// Un service d'accès ou de base de données attend des connexions venues du réseau.
        /// </summary>
        /// <remarks>
        /// La distinction qui fait tout : un service qui n'écoute que sur l'adresse de bouclage
        /// ne regarde que lui-même, quel que soit son port. Le même programme, le même numéro, et
        /// deux situations sans rapport : c'est pourquoi la règle ne compte que ce qui est ouvert
        /// sur toutes les interfaces.
        /// </remarks>
        private static RuleResult Exposed(RuleContext c)
        {
            var listening = c.Network.Listening;
            if (!listening.Count.IsReliable)
                return RuleResult.NotEvaluated(
                    listening.Count.Reason ?? "Les ports en écoute n'ont pas pu être relevés.");

            var exposed = new List<string>();

            foreach (var port in listening.Ports)
            {
                if (!port.AllInterfaces) continue;
                if (IsWindowsDefault(port.Port)) continue;
                if (!WellKnown.IsEntryPoint(port.Port)) continue;

                exposed.Add(port.Port + " (" + (port.Service ?? "usage inconnu") + ") : " +
                            port.ProcessName.Or("programme non identifié"));
            }

            if (exposed.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des services attendent des connexions venues du réseau",
                exposed.Count + " service(s) ouvert(s) sur toutes les interfaces : " +
                string.Join(" · ", exposed) + ".",
                "Ces programmes acceptent des connexions depuis le réseau, et non seulement depuis cette " +
                "machine. Aucun n'est ouvert par Windows lui-même : chacun a été installé ou activé par " +
                "quelqu'un, parfois par un logiciel métier, parfois sans que l'utilisateur le sache. Ce " +
                "n'est pas une faille en soi ; c'est une porte d'entrée à connaître, surtout si la machine " +
                "voyage ou se connecte à des réseaux qu'elle ne maîtrise pas.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Services exposés", exposed.Count.ToString(), DataSource.NativeApi),
                    Evidence.Of("Connexions établies",
                        listening.Established.HasValue
                            ? listening.Established.Value.ToString()
                            : "non relevé",
                        DataSource.NativeApi))));
        }

        /// <summary>
        /// Le partage de fichiers est ouvert alors que le réseau est public et le pare-feu éteint.
        /// </summary>
        /// <remarks>
        /// <b>Trois mesures qui ne veulent rien dire séparément.</b> Le partage de fichiers écoute
        /// sur toutes les machines Windows ; un réseau classé public est le bon réglage dans un
        /// lieu inconnu ; un pare-feu éteint est un défaut connu. C'est leur conjonction qui
        /// décrit une machine réellement ouverte à un réseau qu'elle ne connaît pas, et aucune
        /// des trois règles qui les portent séparément ne peut le dire.
        /// </remarks>
        private static RuleResult PublicSharing(RuleContext c)
        {
            var listening = c.Network.Listening;
            if (!listening.Count.IsReliable)
                return RuleResult.NotEvaluated(
                    listening.Count.Reason ?? "Les ports en écoute n'ont pas pu être relevés.");

            var sharing = false;
            foreach (var port in listening.Ports)
                if (port.AllInterfaces && (port.Port == 445 || port.Port == 139)) sharing = true;

            if (!sharing) return RuleResult.Clean;

            var locations = c.Network.Environment.Locations;
            if (locations.Count == 0)
                return RuleResult.NotEvaluated(
                    "Le classement du réseau n'a pas été relevé : l'exposition ne peut pas être jugée.");

            var onPublic = false;
            var name = string.Empty;
            foreach (var location in locations)
                if (location.Category == NetworkCategory.Public) { onPublic = true; name = location.Name; }

            if (!onPublic) return RuleResult.Clean;

            var firewall = PublicProfile(c);
            if (!firewall.HasValue)
                return RuleResult.NotEvaluated("L'état du pare-feu sur le profil public n'a pas pu être lu.");

            if (firewall.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Le partage de fichiers est ouvert sur un réseau public, sans pare-feu",
                "Réseau « " + name + " » classé public, pare-feu du profil public désactivé, " +
                "partage de fichiers en écoute sur toutes les interfaces.",
                "Trois réglages qui, séparément, n'ont rien d'anormal : le partage de fichiers écoute sur " +
                "toutes les machines Windows, un réseau inconnu est classé public à juste titre, et un " +
                "pare-feu peut avoir été coupé pour dépanner autre chose. Ensemble, ils exposent les " +
                "dossiers partagés de cette machine à tout le réseau sur lequel elle se trouve : un hôtel, " +
                "un café, un réseau d'entreprise qui n'est pas le sien. Réactiver le pare-feu sur le profil " +
                "public referme la porte immédiatement.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Classement du réseau", "Public", DataSource.NativeApi),
                    Evidence.Of("Pare-feu du profil public", "Désactivé", DataSource.Wmi, "Activé"),
                    Evidence.Of("Partage de fichiers", "En écoute sur toutes les interfaces",
                        DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Recommendations.Rec.EnableFirewall)));
        }

        private static bool? PublicProfile(RuleContext c)
        {
            foreach (var profile in c.Snapshot.Security.Firewall)
            {
                if (profile.Profile.IndexOf("ublic", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!profile.Enabled.IsReliable) return null;
                return profile.Enabled.Value;
            }

            return null;
        }
    }
}
