using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles réseau.
    /// </summary>
    /// <remarks>
    /// Principe transverse : <b>ne jamais annoncer une panne à un étage inférieur quand un étage
    /// supérieur fonctionne</b>. Une box qui ignore les requêtes ICMP est un réglage courant ;
    /// conclure à une panne de liaison alors que la navigation marche serait un diagnostic faux,
    /// et enverrait le technicien démonter une installation qui fonctionne.
    /// </remarks>
    internal static class NetworkRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Network;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("NET-001", "Présence d'une connexion active", Cat, NoActiveAdapter, "network.connectivity");
            yield return new Rule("NET-002", "Adressage automatique de secours", Cat, ApipaAddress, "network.connectivity");
            yield return new Rule("NET-003", "Joignabilité de la passerelle", Cat, GatewayReachability, "network.connectivity");
            yield return new Rule("NET-004", "Résolution de noms", Cat, DnsResolution, "network.connectivity");
            yield return new Rule("NET-005", "Perte de paquets", Cat, PacketLoss);
            yield return new Rule("NET-006", "Latence", Cat, Latency);
            yield return new Rule("NET-007", "Qualité du signal Wi-Fi", Cat, WifiSignal, WirelessRules.Family);
            yield return new Rule("NET-008", "Chiffrement du réseau sans fil", Cat, WifiSecurity);
        }

        private static RuleResult NoActiveAdapter(RuleContext c)
        {
            if (c.Network.Adapters.Count == 0)
                return RuleResult.NotEvaluated("Les cartes réseau n'ont pas pu être énumérées.");

            foreach (var adapter in c.Network.Adapters)
            {
                if (adapter.Kind == NetworkAdapterKind.Virtual || adapter.Kind == NetworkAdapterKind.Tunnel) continue;
                if (adapter.Status.Or(string.Empty) == "Connectée") return RuleResult.Clean;
            }

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Aucune connexion réseau active",
                c.Network.Adapters.Count + " carte(s) réseau présentes, aucune dans l'état « Connectée ».",
                "La machine n'est raccordée à aucun réseau : ni câble branché, ni Wi-Fi associé. " +
                "C'est la première chose à rétablir avant tout autre diagnostic réseau.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Cartes connectées", "0", DataSource.NativeApi, "au moins 1")),
                recommendations: RuleContext.Rec(Rec.CheckNetworkLink)));
        }

        private static RuleResult ApipaAddress(RuleContext c)
        {
            if (c.Network.Adapters.Count == 0)
                return RuleResult.NotEvaluated("Les cartes réseau n'ont pas pu être énumérées.");

            var findings = new List<Finding>();
            foreach (var adapter in c.Network.Adapters)
            {
                // Une adresse d'auto-configuration n'a de sens que sur une interface physique
                // réellement connectée : ailleurs, c'est l'état normal.
                var isPhysical = adapter.Kind == NetworkAdapterKind.Ethernet || adapter.Kind == NetworkAdapterKind.WiFi;
                if (!isPhysical || adapter.Status.Or(string.Empty) != "Connectée") continue;
                if (!adapter.HasApipaAddress.Or(false)) continue;

                findings.Add(c.Finding(Severity.Problem,
                    "Une carte réseau n'a pas obtenu d'adresse valide",
                    adapter.Name + ", adresse d'auto-configuration 169.254.x.x : aucun bail DHCP obtenu.",
                    "La carte réseau fonctionne et voit le câble ou le Wi-Fi, mais aucun serveur ne lui a " +
                    "attribué d'adresse. C'est la cause typique d'un « je n'ai plus Internet » alors que tout " +
                    "semble branché : la box ne répond pas, ou le raccordement est mauvais.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Adresse IPv4", adapter.IPv4Addresses.Count > 0 ? adapter.IPv4Addresses[0] : "aucune",
                            DataSource.NativeApi),
                        Evidence.Of("DHCP", adapter.DhcpEnabled.Or(false) ? "activé" : "désactivé",
                            adapter.DhcpEnabled.Source)),
                    recommendations: RuleContext.Rec(Rec.CheckNetworkLink, Rec.RestartRouter)));
            }

            return RuleResult.Of(findings);
        }

        private static RuleResult GatewayReachability(RuleContext c)
        {
            var tests = c.Network.Tests;
            if (tests.Gateway == null)
                return RuleResult.NotEvaluated("Aucun test de passerelle n'a été effectué.");

            if (tests.Gateway.Reachable.Or(false)) return RuleResult.Clean;

            // Le silence de la passerelle n'est un problème que si rien ne passe au-dessus.
            // Beaucoup de box refusent l'ICMP tout en routant parfaitement.
            var internetOk = tests.Internet?.Reachable.Or(false) ?? false;
            if (internetOk || AnyDnsResolved(tests))
            {
                return RuleResult.Of(c.Finding(Severity.Info,
                    "La passerelle ne répond pas aux requêtes ICMP",
                    "Passerelle " + tests.Gateway.Target + " : aucune réponse, alors que les étages supérieurs " +
                    "(Internet, résolution de noms) fonctionnent.",
                    "La box ne répond pas aux tests de connectivité, ce qui est un réglage courant et sans " +
                    "conséquence : la connexion fonctionne normalement.",
                    subject: tests.Gateway.Target,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Réponses", "0 sur " + tests.Gateway.Sent.Or(0), DataSource.NativeApi))));
            }

            return RuleResult.Of(c.Finding(Severity.Problem,
                "La passerelle est injoignable",
                "Passerelle " + tests.Gateway.Target + " : aucune réponse, et aucun étage supérieur ne fonctionne.",
                "La machine ne parvient pas à joindre la box. Le problème se situe entre l'ordinateur et le " +
                "routeur : câble, prise, ou point d'accès Wi-Fi.",
                subject: tests.Gateway.Target,
                evidence: RuleContext.Ev(
                    Evidence.Of("Perte de paquets", Fmt.Percent(tests.Gateway.LossPercent.Or(100)), DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Rec.CheckNetworkLink, Rec.RestartRouter)));
        }

        private static RuleResult DnsResolution(RuleContext c)
        {
            var tests = c.Network.Tests;
            if (tests.DnsResolutions.Count == 0)
                return RuleResult.NotEvaluated("Aucun test de résolution de noms n'a été effectué.");

            if (AnyDnsResolved(tests)) return RuleResult.Clean;

            // Si rien ne passe non plus au niveau IP, la cause est en amont : NET-003 la porte.
            var internetOk = tests.Internet?.Reachable.Or(false) ?? false;
            if (!internetOk)
            {
                return RuleResult.Of(c.Finding(Severity.Info,
                    "La résolution de noms échoue, faute de connexion",
                    "Aucun nom résolu, mais Internet est également injoignable : la cause est en amont.",
                    "Les noms de sites ne se traduisent pas en adresses, mais c'est la conséquence de l'absence " +
                    "de connexion, pas un problème de DNS à traiter séparément.",
                    confidence: ConfidenceLevel.High));
            }

            return RuleResult.Of(c.Finding(Severity.Problem,
                "La résolution de noms ne fonctionne pas",
                tests.DnsResolutions.Count + " nom(s) testé(s), aucun résolu, alors que la connexion IP fonctionne.",
                "La connexion Internet fonctionne, mais la machine n'arrive plus à traduire les noms de sites " +
                "en adresses. Tous les sites semblent inaccessibles alors que la ligne est parfaitement bonne : " +
                "c'est un problème de DNS, pas de connexion.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Noms résolus", "0 sur " + tests.DnsResolutions.Count, DataSource.NativeApi)),
                recommendations: RuleContext.Rec(Rec.FixDnsConfiguration)));
        }

        private static RuleResult PacketLoss(RuleContext c)
        {
            var internet = c.Network.Tests.Internet;
            if (internet == null || !internet.LossPercent.IsReliable)
                return RuleResult.NotEvaluated("Aucune mesure de perte de paquets disponible.");

            // La perte totale relève de la joignabilité, pas de la qualité de liaison.
            if (!internet.Reachable.Or(false)) return RuleResult.Clean;
            if (internet.LossPercent.Value < c.T.PacketLossWarning) return RuleResult.Clean;

            var severity = internet.LossPercent.Value >= c.T.PacketLossProblem ? Severity.Problem : Severity.Warning;
            var threshold = severity == Severity.Problem ? c.T.PacketLossProblem : c.T.PacketLossWarning;

            return RuleResult.Of(c.Finding(severity,
                "La connexion perd des paquets",
                internet.Target + " : " + Fmt.Percent(internet.LossPercent.Value) + " de perte sur " +
                internet.Sent.Or(0) + " envois (seuil " + Fmt.Percent(threshold) + ").",
                "Une partie des données envoyées n'arrive pas à destination. Cela se traduit par des coupures " +
                "en visioconférence, des pages qui se chargent mal et des téléchargements qui s'interrompent.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Perte de paquets", Fmt.Percent(internet.LossPercent.Value), internet.LossPercent.Source,
                        Fmt.Percent(threshold))),
                recommendations: RuleContext.Rec(Rec.CheckNetworkLink, Rec.RestartRouter)));
        }

        private static RuleResult Latency(RuleContext c)
        {
            var tests = c.Network.Tests;
            var findings = new List<Finding>();
            var evaluated = false;

            if (tests.Gateway != null && tests.Gateway.AverageMs.IsReliable)
            {
                evaluated = true;
                if (tests.Gateway.AverageMs.Value >= c.T.GatewayLatencyWarningMs)
                {
                    findings.Add(c.Finding(Severity.Warning,
                        "Le temps de réponse de la box est élevé",
                        "Passerelle " + tests.Gateway.Target + " : " + Fmt.Ms(tests.Gateway.AverageMs.Value) +
                        " en moyenne (seuil " + Fmt.Ms(c.T.GatewayLatencyWarningMs) + ").",
                        "La box met anormalement longtemps à répondre alors qu'elle est sur le réseau local. " +
                        "Cela oriente vers un Wi-Fi de mauvaise qualité ou une box saturée.",
                        subject: tests.Gateway.Target,
                        evidence: RuleContext.Ev(
                            Evidence.Of("Latence moyenne", Fmt.Ms(tests.Gateway.AverageMs.Value),
                                tests.Gateway.AverageMs.Source, Fmt.Ms(c.T.GatewayLatencyWarningMs)))));
                }
            }

            if (tests.Internet != null && tests.Internet.AverageMs.IsReliable)
            {
                evaluated = true;
                if (tests.Internet.AverageMs.Value >= c.T.InternetLatencyWarningMs)
                {
                    findings.Add(c.Finding(Severity.Info,
                        "Le temps de réponse vers Internet est élevé",
                        tests.Internet.Target + " : " + Fmt.Ms(tests.Internet.AverageMs.Value) +
                        " en moyenne (seuil " + Fmt.Ms(c.T.InternetLatencyWarningMs) + ").",
                        "Les échanges avec Internet sont lents à s'établir. Sur une ligne satellite ou une " +
                        "connexion mobile, c'est normal ; ailleurs, cela mérite d'être signalé à l'opérateur.",
                        subject: tests.Internet.Target,
                        evidence: RuleContext.Ev(
                            Evidence.Of("Latence moyenne", Fmt.Ms(tests.Internet.AverageMs.Value),
                                tests.Internet.AverageMs.Source, Fmt.Ms(c.T.InternetLatencyWarningMs)))));
                }
            }

            return evaluated ? RuleResult.Of(findings) : RuleResult.NotEvaluated("Aucune mesure de latence disponible.");
        }

        private static RuleResult WifiSignal(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                if (wifi == null || !wifi.SignalPercent.IsReliable) continue;
                evaluated = true;

                if (wifi.SignalPercent.Value > c.T.WifiSignalWarningPercent) continue;

                var severity = wifi.SignalPercent.Value <= c.T.WifiSignalProblemPercent ? Severity.Warning : Severity.Info;
                var threshold = severity == Severity.Warning ? c.T.WifiSignalProblemPercent : c.T.WifiSignalWarningPercent;

                findings.Add(c.Finding(severity,
                    "Le signal Wi-Fi est faible",
                    adapter.Name + " sur « " + wifi.Ssid.Or("réseau inconnu") + " » : signal à " +
                    wifi.SignalPercent.Value + " % (seuil " + threshold + " %), débit négocié " +
                    wifi.RxRateMbps.Or(0) + " Mbit/s.",
                    "La machine capte mal le réseau sans fil. Cela se traduit par des débits réduits et des " +
                    "coupures intermittentes. Rapprocher la machine du point d'accès, ou ajouter un répéteur, " +
                    "règle généralement le problème.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Signal", wifi.SignalPercent.Value + " %", wifi.SignalPercent.Source, threshold + " %"),
                        Evidence.Of("Réseau", wifi.Ssid.Or("inconnu"), wifi.Ssid.Source)),
                    recommendations: RuleContext.Rec(Rec.ImproveWifiSignal)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune liaison sans fil active à évaluer.");
        }

        private static RuleResult WifiSecurity(RuleContext c)
        {
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var adapter in c.Network.Adapters)
            {
                var wifi = adapter.Wifi;
                if (wifi == null || !wifi.Security.IsReliable) continue;
                evaluated = true;

                var security = wifi.Security.Value;
                if (security.IndexOf("ouvert", StringComparison.OrdinalIgnoreCase) < 0) continue;

                findings.Add(c.Finding(Severity.Problem,
                    "Le réseau sans fil utilisé n'est pas chiffré",
                    adapter.Name + " connectée à « " + wifi.Ssid.Or("réseau inconnu") + " » : " + security + ".",
                    "Le réseau Wi-Fi auquel la machine est connectée ne chiffre pas les communications. " +
                    "Tout ce qui y transite peut être lu par n'importe qui à portée du signal.",
                    subject: adapter.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Sécurité", security, wifi.Security.Source, "chiffrement WPA2 ou supérieur")),
                    recommendations: RuleContext.Rec(Rec.SecureWifi)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucune liaison sans fil active à évaluer.");
        }

        private static bool AnyDnsResolved(NetworkTests tests)
        {
            foreach (var resolution in tests.DnsResolutions)
                if (resolution.Resolved.Or(false)) return true;
            return false;
        }
    }
}
