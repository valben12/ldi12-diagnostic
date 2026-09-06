using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Qualité de la liaison, au-delà du simple « ça marche ou ça ne marche pas ».
    /// </summary>
    /// <remarks>
    /// Les trois pannes que ces règles couvrent ont un point commun : la connexion fonctionne.
    /// Les pages s'ouvrent, le test de connectivité est au vert, et pourtant la visioconférence
    /// hache ou certains sites restent à moitié chargés. Ce sont les cas où un client se fait
    /// répondre que « tout est normal » : parce que ce qui n'est pas mesuré ne se voit pas.
    /// </remarks>
    internal static class NetworkQualityRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("NET-009", "Pertes sur la liaison Internet", Cat, PathLoss, "network.quality");
            yield return new Rule("NET-010", "Régularité de la liaison", Cat, Jitter, "network.quality");
            yield return new Rule("NET-011", "Taille de paquet", Cat, PathMtu);
        }

        /// <summary>
        /// Pertes mesurées sur une série longue.
        /// </summary>
        /// <remarks>
        /// Distincte de NET-005, qui lit la série courte du test de connectivité : quinze paquets
        /// détectent une perte d'un sur dix que quatre paquets manquent une fois sur deux. Quand
        /// les deux règles se déclenchent, la corrélation les regroupe ; ici, on mesure ce que
        /// l'autre ne peut pas voir.
        /// </remarks>
        private static RuleResult PathLoss(RuleContext c)
        {
            var quality = c.Network.Tests.Quality;
            if (quality == null || !quality.LossPercent.IsReliable)
                return RuleResult.NotEvaluated(
                    quality?.LossPercent.Reason ?? "La qualité de la liaison n'a pas été mesurée.");

            var loss = quality.LossPercent.Value;
            if (loss < c.T.PacketLossWarning) return RuleResult.Clean;

            var sent = quality.Sent.Or(0);
            var received = quality.Received.Or(0);

            // Une perte totale relève de la connectivité, pas de la qualité : NET-003 et NET-004
            // le disent déjà, et mieux. Répéter ici ferait deux constats pour une seule panne.
            if (loss >= 100)
                return RuleResult.NotEvaluated(
                    "Aucun paquet n'est revenu : c'est un défaut de connectivité, traité par les règles dédiées.");

            var severity = loss >= c.T.PacketLossProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem ? c.T.PacketLossProblem : c.T.PacketLossWarning;

            return RuleResult.Of(c.Finding(severity,
                "La liaison Internet perd des paquets",
                Fmt.Percent(loss) + " de perte sur " + sent + " paquets envoyés vers " + quality.Target +
                " (" + received + " revenus, seuil " + Fmt.Percent(threshold) + ").",
                "La connexion fonctionne, mais une partie de ce qui est envoyé se perd en route. " +
                "Les pages web s'en remettent (elles redemandent ce qui manque) mais les appels vidéo, " +
                "la téléphonie et les jeux hachent. C'est le genre de défaut auquel on répond souvent " +
                "à tort que « tout est normal ».",
                evidence: RuleContext.Ev(
                    Evidence.Of("Paquets perdus", (sent - received) + " sur " + sent,
                        quality.LossPercent.Source, "0"),
                    Evidence.Of("Taux de perte", Fmt.Percent(loss), quality.LossPercent.Source,
                        Fmt.Percent(threshold))),
                recommendations: RuleContext.Rec(Rec.CheckNetworkLink)));
        }

        /// <summary>
        /// Gigue : variation d'un temps de réponse au suivant.
        /// </summary>
        /// <remarks>
        /// La mesure qui explique une conversation hachée sur une ligne dont la latence moyenne
        /// est excellente. Une moyenne de 15 ms qui oscille entre 5 et 200 rend la voix
        /// inutilisable ; la moyenne, elle, reste rassurante.
        /// </remarks>
        private static RuleResult Jitter(RuleContext c)
        {
            var quality = c.Network.Tests.Quality;
            if (quality == null || !quality.JitterMs.IsReliable)
                return RuleResult.NotEvaluated(
                    quality?.JitterMs.Reason ?? "La régularité de la liaison n'a pas été mesurée.");

            if (quality.JitterMs.Value < c.T.JitterWarningMs) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "La liaison est irrégulière",
                "Gigue de " + Fmt.Ms(quality.JitterMs.Value) + " pour une latence moyenne de " +
                Fmt.Ms(quality.AverageMs.Or(0)) +
                (quality.MinMs.HasValue && quality.MaxMs.HasValue
                    ? " (de " + Fmt.Ms(quality.MinMs.Value) + " à " + Fmt.Ms(quality.MaxMs.Value) + ")"
                    : string.Empty) +
                ", seuil " + Fmt.Ms(c.T.JitterWarningMs) + ".",
                "Les réponses du réseau arrivent à des rythmes très inégaux. La moyenne reste bonne, ce qui " +
                "donne l'impression d'une ligne saine, mais cette irrégularité suffit à hacher un appel vidéo " +
                "ou une conversation téléphonique par Internet.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Gigue", Fmt.Ms(quality.JitterMs.Value), quality.JitterMs.Source,
                        Fmt.Ms(c.T.JitterWarningMs)),
                    Evidence.Of("Latence moyenne", Fmt.Ms(quality.AverageMs.Or(0)), quality.AverageMs.Source)),
                recommendations: RuleContext.Rec(Rec.CheckNetworkLink)));
        }

        /// <summary>
        /// Taille maximale de paquet transmise sans fragmentation.
        /// </summary>
        /// <remarks>
        /// Le symptôme le plus déroutant du lot : des pages qui se chargent à moitié, des envois
        /// de fichiers qui s'arrêtent, aucun message d'erreur, et un test de connectivité au vert.
        /// La cause est presque toujours un tunnel (VPN, PPPoE) ou une box mal réglée.
        /// </remarks>
        private static RuleResult PathMtu(RuleContext c)
        {
            var mtu = c.Network.Tests.PathMtu;
            if (!mtu.HasValue)
                return RuleResult.NotEvaluated(mtu.Reason ?? "La taille de paquet n'a pas été mesurée.");

            if (mtu.Value >= c.T.PathMtuWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "La taille de paquet est réduite sur cette liaison",
                "MTU du chemin mesurée à " + mtu.Value + " octets, contre " + c.T.PathMtuWarning +
                " attendus sur une liaison Ethernet." +
                (mtu.Availability == Availability.Partial ? " " + mtu.Reason : string.Empty),
                "Les données circulent par paquets plus petits que la normale. La connexion fonctionne, " +
                "mais certains sites se chargent à moitié ou s'arrêtent en cours de route, sans message " +
                "d'erreur. C'est presque toujours le fait d'un VPN, d'une box mal réglée ou d'un abonnement " +
                "dont le raccordement impose cette limite.",
                confidence: mtu.Availability == Availability.Partial ? ConfidenceLevel.Medium : ConfidenceLevel.High,
                evidence: RuleContext.Ev(
                    Evidence.Of("Taille de paquet", mtu.Value + " octets", mtu.Source,
                        c.T.PathMtuWarning + " octets")),
                recommendations: RuleContext.Rec(Rec.RestartRouter)));
        }
    }
}
