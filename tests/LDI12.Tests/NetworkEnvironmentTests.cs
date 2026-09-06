using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce qui commande le réseau par-dessus ce qui est branché, et la liaison elle-même.
    /// </summary>
    public class NetworkEnvironmentTests
    {
        // ============================================================ la liaison

        [Fact]
        public void Une_liaison_qui_perd_des_trames_designe_le_cable()
        {
            // Le seul constat du logiciel qui envoie regarder un câble. Rien d'autre ne le fait :
            // la connexion fonctionne, les tests passent, et la machine rame.
            var finding = Single(Evaluate(b => b.Link(packets: 1_000_000, errorsIn: 5_000)), "NET-016");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("câble", finding.PlainExplanation);
            Assert.Contains("CHECK-NETWORK-CABLING", finding.Recommendations);
        }

        [Fact]
        public void Un_taux_d_erreur_ordinaire_ne_dit_rien()
        {
            // Cent cinquante par million : ce que rend la carte Intel de la machine de
            // développement sur une liaison qui fonctionne parfaitement. Un constat ici enverrait
            // changer un câble sain.
            Assert.DoesNotContain(
                Evaluate(b => b.Link(packets: 1_000_000, errorsIn: 150)),
                finding => finding.RuleId == "NET-016");
        }

        [Fact]
        public void Une_carte_deconnectee_ne_compte_pas()
        {
            // Ses compteurs datent d'avant le débranchement : les lire reviendrait à juger une
            // liaison qui n'existe plus.
            var score = Analyze(b => b.Link(up: false, packets: 1_000_000, errorsIn: 900_000)).Score!;

            Assert.DoesNotContain("NET-016", score.EvaluatedRuleIds);
        }

        [Fact]
        public void Les_trames_ecartees_ne_sont_pas_des_erreurs_et_le_constat_le_dit()
        {
            // Confondre les deux enverrait changer un câble parfaitement sain.
            var finding = Single(
                Evaluate(b => b.Link(packets: 1_000_000, discardsIn: 30_000)), "NET-017");

            Assert.Contains("pas un problème de câble", finding.PlainExplanation);
            Assert.Contains("UPDATE-NETWORK-DRIVER", finding.Recommendations);
        }

        [Fact]
        public void Trop_peu_de_trafic_ne_permet_pas_de_conclure()
        {
            // Un seul paquet écarté sur cent ferait un taux spectaculaire et sans aucun sens.
            var score = Analyze(b => b.Link(packets: 100, discardsIn: 5)).Score!;

            Assert.DoesNotContain("NET-017", score.EvaluatedRuleIds);
        }

        // ============================================================ le proxy

        [Fact]
        public void Un_proxy_sur_un_poste_hors_domaine_est_un_avertissement()
        {
            // C'est la manière la plus simple de détourner la navigation sans rien installer de
            // visible. En entreprise c'est la règle ; chez un particulier, presque jamais voulu.
            var finding = Single(
                Evaluate(b => b.Environment(userProxy: "10.0.0.1:8080")), "NET-018");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("rarement voulu", finding.PlainExplanation);
        }

        [Fact]
        public void Le_meme_proxy_sur_un_poste_de_domaine_est_une_information()
        {
            var finding = Single(
                Evaluate(b => b.Environment(
                    userProxy: "10.0.0.1:8080", machineProxy: "10.0.0.1:8080",
                    domainJoined: true, domainName: "entreprise.local", dnsSuffix: "entreprise.local")),
                "NET-018");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Deux_configurations_de_proxy_qui_divergent_expliquent_les_mises_a_jour_qui_echouent()
        {
            // Windows en tient deux sans lien entre elles : le navigateur suit celle de la
            // session, Windows Update celle de la machine.
            var finding = Single(
                Evaluate(b => b.Environment(userProxy: "10.0.0.1:8080", machineProxy: "10.0.0.9:3128")),
                "NET-019");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("mise à jour", finding.PlainExplanation);
        }

        [Fact]
        public void Deux_configurations_identiques_ne_disent_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.Environment(userProxy: "10.0.0.1:8080", machineProxy: "10.0.0.1:8080")),
                finding => finding.RuleId == "NET-019");
        }

        // ============================================================ le réseau vu par Windows

        [Fact]
        public void Un_reseau_sans_Internet_est_signale_avec_ce_que_Windows_en_dit()
        {
            var finding = Single(Evaluate(b => b.Environment(reach: NetworkReach.LocalOnly)), "NET-020");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("globe", finding.PlainExplanation);
        }

        [Fact]
        public void Un_reseau_sans_aucune_connectivite_est_un_probleme()
        {
            var finding = Single(Evaluate(b => b.Environment(reach: NetworkReach.None)), "NET-020");

            Assert.Equal(Severity.Problem, finding.Severity);
        }

        [Fact]
        public void Un_reseau_public_explique_les_partages_disparus_sans_demander_de_le_changer()
        {
            // Le classement public est la bonne protection dans un lieu inconnu : le constat
            // existe pour expliquer, pas pour faire changer.
            var finding = Single(Evaluate(b => b.Environment(category: NetworkCategory.Public)), "NET-021");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Empty(finding.Recommendations);
        }

        [Fact]
        public void Sans_service_de_localisation_rien_n_est_conclu()
        {
            // Une machine dont le service est arrêté ne doit pas être déclarée saine sur ces
            // deux points : elle est simplement muette.
            var score = Analyze(b => b.Network(true, true, true, true)).Score!;

            Assert.DoesNotContain("NET-020", score.EvaluatedRuleIds);
            Assert.DoesNotContain("NET-021", score.EvaluatedRuleIds);
        }

        // ============================================================ passerelles

        [Fact]
        public void Deux_passerelles_actives_sont_un_avertissement()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Link(name: "Ethernet", gateway: "192.168.1.1");
                b.Link(name: "Wi-Fi", kind: NetworkAdapterKind.WiFi, gateway: "192.168.2.1");
            }), "NET-022");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("un jour et plus le lendemain", finding.PlainExplanation);
        }

        [Fact]
        public void Une_liaison_VPN_explique_la_seconde_passerelle_au_lieu_de_l_accuser()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Link(name: "Ethernet", gateway: "192.168.1.1");
                b.Link(name: "NordLynx", kind: NetworkAdapterKind.Vpn, gateway: "10.5.0.1");
            }), "NET-022");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("tunnel", finding.PlainExplanation);
        }

        // ============================================================ domaine

        [Fact]
        public void Un_poste_de_domaine_qui_interroge_un_DNS_public_est_un_probleme()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Environment(domainJoined: true, domainName: "entreprise.local", dnsSuffix: "entreprise.local");
                b.Link(dns: new[] { "192.168.1.1", "8.8.8.8" });
            }), "NET-023");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("8.8.8.8", finding.TechnicalDetail);
            Assert.Contains("REVIEW-DOMAIN-DNS", finding.Recommendations);
        }

        [Fact]
        public void Le_meme_DNS_public_hors_domaine_est_un_choix_ordinaire()
        {
            // Sur un poste de particulier, c'est un réglage sain et fréquent. La règle ne doit
            // pas se contenter de ne rien trouver : elle ne doit pas s'appliquer du tout.
            var score = Analyze(b =>
            {
                b.Environment(domainJoined: false);
                b.Link(dns: new[] { "1.1.1.1", "8.8.8.8" });
            }).Score!;

            Assert.DoesNotContain("NET-023", score.EvaluatedRuleIds);
        }

        [Fact]
        public void Un_poste_de_domaine_sans_suffixe_DNS_est_signale()
        {
            var finding = Single(
                Evaluate(b => b.Environment(domainJoined: true, domainName: "entreprise.local")), "NET-024");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("noms courts", finding.PlainExplanation);
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_montre_le_reseau_sa_portee_et_le_nombre_de_sorties()
        {
            var facts = Group(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Environment(category: NetworkCategory.Public, reach: NetworkReach.LocalOnly);
                b.Link();
            }), "Réseaux reconnus par Windows");

            Assert.Equal("1", Value(facts, "Sorties par défaut"));
        }

        [Fact]
        public void La_fiche_du_proxy_ne_montre_pas_de_lignes_vides_quand_il_n_y_en_a_pas()
        {
            // « Non mesuré » est réservé à ce qu'on n'a pas su lire, pas à ce qui n'existe pas.
            var facts = Group(Fixtures.Build(b =>
            {
                b.Windows11();
                b.Environment();
                b.Link();
            }), "Serveur mandataire");

            Assert.DoesNotContain(facts, fact => fact.Label == "Serveur");
            Assert.Equal("Aucun", Value(facts, "Proxy de la session"));
        }

        // ============================================================ montage

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => Analyze(configure).Findings;

        private static SystemSnapshot Analyze(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            }));

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static IReadOnlyList<Fact> Group(SystemSnapshot snapshot, string title)
        {
            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == title) return group.Facts;

            throw new InvalidOperationException("Le cadre « " + title + " » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;
    }
}
