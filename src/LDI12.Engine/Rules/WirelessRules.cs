using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Ce qui explique un Wi-Fi lent alors que le signal est bon.
    /// </summary>
    /// <remarks>
    /// La qualité du signal, seule, ne suffit pas à répondre à « ça rame le soir ». Trois causes
    /// courantes laissent la barre de signal pleine : un canal partagé avec six voisins, une
    /// liaison négociée à une fraction de ce que la carte sait faire, et une norme d'avant 2009
    /// dont le débit se partage entre tous les appareils du logement. Un client qui entend
    /// « votre signal est excellent » sans autre explication en conclut que le problème est
    /// ailleurs, souvent dans une machine qu'il envisage alors de remplacer.
    /// </remarks>
    internal static class WirelessRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        /// <summary>
        /// Famille partagée avec NET-007 : signal faible, canal encombré et débit effondré
        /// décrivent la même mauvaise liaison sans fil vue sous trois angles.
        /// </summary>
        internal const string Family = "network.wireless";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("NET-012", "Encombrement du canal sans fil", Cat, ChannelCongestion, Family);
            yield return new Rule("NET-013", "Norme de la liaison sans fil", Cat, RadioStandard, Family);
            yield return new Rule("NET-014", "Débit négocié en sans fil", Cat, NegotiatedRate, Family);
            yield return new Rule("NET-015", "Radio sans fil désactivée", Cat, RadioDisabled);
        }

        /// <summary>
        /// Voisins sur le canal de la liaison.
        /// </summary>
        /// <remarks>
        /// En 2,4 GHz, trois canaux seulement ne se recouvrent pas : dans un immeuble, une
        /// vingtaine de box se les partagent. Le temps de parole se divise entre tous les
        /// émetteurs de la fréquence, y compris ceux du voisin, d'où un débit qui s'effondre le
        /// soir alors que la barre de signal reste pleine, et qu'aucun autre relevé n'explique.
        /// </remarks>
        private static RuleResult ChannelCongestion(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;
            var reason = "Aucune liaison sans fil active à évaluer.";

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                var neighbourhood = wifi?.Neighbourhood;
                if (wifi == null || neighbourhood == null) continue;

                if (!neighbourhood.SameChannel.IsReliable)
                {
                    reason = neighbourhood.SameChannel.Reason ?? reason;
                    continue;
                }

                evaluated = true;

                var same = neighbourhood.SameChannel.Value;
                var overlapping = neighbourhood.OverlappingChannel.Or(0);
                var competing = same + overlapping;
                if (competing < c.T.WifiSameChannelWarning) continue;

                var severity = competing >= c.T.WifiSameChannelProblem ? Severity.Warning : Severity.Info;
                var threshold = severity == Severity.Warning
                    ? c.T.WifiSameChannelProblem
                    : c.T.WifiSameChannelWarning;

                var band = wifi.Band.HasValue ? WifiChannels.Describe(wifi.Band.Value) : "la bande utilisée";
                var channel = wifi.Channel.Or(0);

                findings.Add(c.Finding(severity,
                    "Le canal Wi-Fi utilisé est partagé avec plusieurs autres réseaux",
                    "Canal " + channel + " en " + band + " : " + same + " autre(s) réseau(x) sur le même " +
                    "canal et " + overlapping + " sur un canal qui le recouvre, soit " + competing +
                    " au total (seuil " + threshold + ").",
                    "Tous les appareils qui émettent sur une même fréquence attendent leur tour, y " +
                    "compris ceux des voisins. Le signal reste excellent et le débit s'effondre quand " +
                    "même, surtout le soir. Changer de canal sur la box, ou passer les appareils qui " +
                    "le permettent en 5 GHz, règle le plus souvent la situation.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Réseaux concurrents", competing.ToString(),
                            neighbourhood.SameChannel.Source, threshold.ToString()),
                        Evidence.Of("Canal", channel.ToString(), wifi.Channel.Source),
                        Evidence.Of("Réseaux entendus", neighbourhood.Total.Or(0).ToString(),
                            neighbourhood.Total.Source)),
                    recommendations: RuleContext.Rec(Rec.ChangeWifiChannel)));
            }

            return evaluated ? RuleResult.Of(findings) : RuleResult.NotEvaluated(reason);
        }

        /// <summary>
        /// Liaison établie sur une norme d'avant 2009.
        /// </summary>
        /// <remarks>
        /// Constat informatif et non avertissement : une liaison 802.11g fonctionne. Elle plafonne
        /// simplement à quelques mégabits une fois le débit partagé, ce qui devient visible le jour
        /// où la fibre arrive et où le client constate qu'il n'en voit rien.
        /// </remarks>
        private static RuleResult RadioStandard(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                if (wifi == null || !wifi.RadioType.IsReliable) continue;
                evaluated = true;

                var radio = wifi.RadioType.Value;
                if (!WifiChannels.IsLegacyRadio(radio)) continue;

                findings.Add(c.Finding(Severity.Info,
                    "La liaison sans fil utilise une norme ancienne",
                    adapter.Name + " sur « " + wifi.Ssid.Or("réseau inconnu") + " » : liaison en " + radio + ".",
                    "Cette norme date d'avant 2009 et plafonne à 54 Mbit/s partagés entre tous les " +
                    "appareils du logement, en pratique bien moins. Elle vient soit de la carte de la " +
                    "machine, soit d'une box configurée pour rester compatible avec un ancien appareil. " +
                    "C'est ce qui fait qu'un abonnement fibre ne se voit pas sur cette machine.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Norme", radio, wifi.RadioType.Source, "802.11n ou supérieur"),
                        Evidence.Of("Débit négocié", wifi.TxRateMbps.Or(0) + " Mbit/s", wifi.TxRateMbps.Source)),
                    recommendations: RuleContext.Rec(Rec.ImproveWifiSignal)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune liaison sans fil active à évaluer.");
        }

        /// <summary>
        /// Débit négocié très en deçà de ce que la norme annoncée permet.
        /// </summary>
        /// <remarks>
        /// La comparaison se fait avec une carte à <b>une seule antenne</b>, la borne basse du
        /// matériel courant. Comparer au maximum théorique de la norme ferait signaler comme
        /// défaillante la moitié des portables d'entrée de gamme, qui fonctionnent pourtant à
        /// leur plein régime.
        ///
        /// La règle se tait quand le signal est déjà faible : un débit effondré est alors la
        /// conséquence attendue, et NET-007 le dit mieux. Deux constats pour une seule cause
        /// feraient croire à deux pannes.
        /// </remarks>
        private static RuleResult NegotiatedRate(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                if (wifi == null || !wifi.RadioType.IsReliable || !wifi.TxRateMbps.IsReliable) continue;

                var reference = WifiChannels.SingleStreamRateMbps(wifi.RadioType.Value);
                if (reference <= 0) continue;

                evaluated = true;

                if (wifi.SignalPercent.IsReliable && wifi.SignalPercent.Value <= c.T.WifiSignalWarningPercent)
                    continue;

                var rate = wifi.TxRateMbps.Value;
                if (rate <= 0) continue;

                var share = rate * 100 / reference;
                if (share >= c.T.WifiRateShareWarningPercent) continue;

                var severity = share < c.T.WifiRateShareProblemPercent ? Severity.Warning : Severity.Info;
                var threshold = severity == Severity.Warning
                    ? c.T.WifiRateShareProblemPercent
                    : c.T.WifiRateShareWarningPercent;

                findings.Add(c.Finding(severity,
                    "Le débit négocié en Wi-Fi est très inférieur à la norme utilisée",
                    adapter.Name + " : " + rate + " Mbit/s négociés en " + wifi.RadioType.Value +
                    ", soit " + share + " % de ce qu'atteint une carte à une antenne sur cette norme " +
                    "(seuil " + threshold + " %), alors que le signal est à " +
                    wifi.SignalPercent.Or(0) + " %.",
                    "La machine capte bien mais négocie un débit très bas. Cela vient en général de " +
                    "perturbations sur la fréquence (un four à micro-ondes, un téléphone sans fil, un " +
                    "voisin qui émet fort) ou d'une box qui limite le débit pour rester compatible " +
                    "avec un ancien appareil. Le symptôme est un Wi-Fi lent que rien n'explique.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Débit négocié", rate + " Mbit/s", wifi.TxRateMbps.Source,
                            reference * threshold / 100 + " Mbit/s"),
                        Evidence.Of("Norme", wifi.RadioType.Value, wifi.RadioType.Source),
                        Evidence.Of("Signal", wifi.SignalPercent.Or(0) + " %", wifi.SignalPercent.Source)),
                    recommendations: RuleContext.Rec(Rec.ChangeWifiChannel)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune liaison sans fil active à évaluer.");
        }

        /// <summary>
        /// Radio coupée par un commutateur.
        /// </summary>
        /// <remarks>
        /// Première cause d'un « le Wi-Fi a disparu » sur un portable : le commutateur physique du
        /// châssis, la combinaison de touches, ou le mode avion. Le constat ne vaut que si la
        /// machine n'a pas d'autre lien actif : sur un poste raccordé en Ethernet, une radio
        /// éteinte est un choix, pas une panne.
        /// </remarks>
        private static RuleResult RadioDisabled(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                if (wifi == null || !wifi.RadioEnabled.IsReliable) continue;
                evaluated = true;

                if (wifi.RadioEnabled.Value) continue;

                var wired = HasWiredLink(c.Network);
                var severity = wired ? Severity.Info : Severity.Warning;

                findings.Add(c.Finding(severity,
                    "La radio Wi-Fi de la machine est éteinte",
                    adapter.Name + " : la carte est présente et son émetteur est coupé" +
                    (wired ? ", la machine étant raccordée par câble." : "."),
                    "Un commutateur du châssis, une combinaison de touches ou le mode avion coupe " +
                    "l'émetteur sans désactiver la carte : Windows affiche alors une machine sans " +
                    "aucun réseau à portée, ce qui ressemble à une panne de carte." +
                    (wired
                        ? " Ici la machine est raccordée par câble, donc rien n'est perdu, mais elle ne " +
                          "verra aucun réseau sans fil si on la débranche."
                        : " C'est la première chose à vérifier avant de suspecter le matériel."),
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Émetteur", "éteint", wifi.RadioEnabled.Source, "allumé"),
                        Evidence.Of("État de la carte", wifi.State.Or("inconnu"), wifi.State.Source)),
                    recommendations: RuleContext.Rec(Rec.EnableWifiRadio)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune carte sans fil ne rapporte l'état de sa radio.");
        }

        private static bool HasWiredLink(NetworkSnapshot network)
        {
            foreach (var adapter in network.Adapters)
            {
                if (adapter.Kind != NetworkAdapterKind.Ethernet) continue;
                if (adapter.IsPrimary) return true;
            }
            return false;
        }
    }
}
