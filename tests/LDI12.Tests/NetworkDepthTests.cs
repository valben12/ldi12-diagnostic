using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Actions;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le réseau jusqu'au bout : ports série, ports en écoute, et les actions qui réparent.
    /// </summary>
    public class NetworkDepthTests
    {
        // ============================================================ ports série

        [Fact]
        public void Un_convertisseur_sans_pilote_est_reconnu_par_sa_puce()
        {
            // Sans pilote, Windows ne lui donne ni classe ni nom : il se perd parmi les
            // périphériques inconnus, et c'est le cas qui amène le client à l'atelier.
            var finding = Single(Evaluate(b =>
                b.SerialDevice("Périphérique inconnu", deviceClass: null!,
                    hardwareId: @"USB\VID_0403&PID_6001", problemCode: 28)), "SER-001");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("INSTALL-MISSING-DRIVERS", finding.Recommendations);
        }

        [Fact]
        public void Un_port_sans_numero_attribue_explique_le_port_introuvable()
        {
            var finding = Single(Evaluate(b =>
                b.SerialDevice("USB Serial Converter", hardwareId: @"USB\VID_0403&PID_6001")), "SER-001");

            Assert.Contains("port introuvable", finding.PlainExplanation);
        }

        [Fact]
        public void Un_port_serie_qui_fonctionne_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.SerialDevice("USB Serial Port (COM3)")),
                finding => finding.RuleId == "SER-001");
        }

        [Fact]
        public void Des_numeros_retenus_sans_appareil_expliquent_la_derive_vers_COM13()
        {
            // Windows ne rend jamais un numéro attribué : après quelques rebranchements
            // l'appareil sort de la plage COM1 à COM9 que les logiciels anciens acceptent.
            var finding = Single(Evaluate(b =>
            {
                b.SerialDevice("USB Serial Port (COM12)");
                b.ReservedSerialPorts(3, 4, 5, 6, 7, 12);
            }), "SER-002");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("COM1 à COM9", finding.PlainExplanation);
            Assert.Contains("COM12", finding.TechnicalDetail);
        }

        [Fact]
        public void Le_numero_d_un_appareil_present_n_est_pas_compte_comme_perdu()
        {
            // Cinq numéros retenus dont un utilisé : quatre orphelins, soit le seuil exactement.
            var findings = Evaluate(b =>
            {
                b.SerialDevice("USB Serial Port (COM3)");
                b.ReservedSerialPorts(3, 4, 5);
            });

            Assert.DoesNotContain(findings, finding => finding.RuleId == "SER-002");
        }

        // ============================================================ ports en écoute

        [Fact]
        public void Un_service_ouvert_sur_le_reseau_est_nomme_avec_son_programme()
        {
            var finding = Single(Evaluate(b => b.Listening(5900, process: "winvnc")), "LSN-001");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("winvnc", finding.TechnicalDetail);
            Assert.Contains("5900", finding.TechnicalDetail);
        }

        [Fact]
        public void Le_meme_service_sur_la_machine_seule_n_est_pas_une_porte_d_entree()
        {
            // Un service qui n'écoute que sur l'adresse de bouclage ne regarde que lui-même,
            // quel que soit son port.
            Assert.DoesNotContain(
                Evaluate(b => b.Listening(5432, allInterfaces: false, process: "postgres")),
                finding => finding.RuleId == "LSN-001");
        }

        [Fact]
        public void Les_ports_que_Windows_ouvre_lui_meme_ne_sont_jamais_signales()
        {
            // Une règle qui se déclenche sur toutes les machines ne se déclenche utilement sur
            // aucune : 135, 139, 445 et les ports éphémères sont ceux de Windows.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Listening(135);
                    b.Listening(445, processId: 4, process: "System");
                    b.Listening(49664);
                }),
                finding => finding.RuleId == "LSN-001");
        }

        [Fact]
        public void Le_partage_expose_sur_un_reseau_public_sans_pare_feu_est_un_probleme()
        {
            // Trois mesures qui ne veulent rien dire séparément, et qui ensemble décrivent une
            // machine ouverte à un réseau qu'elle ne connaît pas.
            var finding = Single(Evaluate(b =>
            {
                b.Listening(445, processId: 4, process: "System");
                b.Environment(category: NetworkCategory.Public);
                b.FirewallProfile("Public", enabled: false);
            }), "LSN-002");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("ENABLE-FIREWALL", finding.Recommendations);
        }

        [Fact]
        public void Le_meme_partage_avec_le_pare_feu_actif_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Listening(445, processId: 4, process: "System");
                    b.Environment(category: NetworkCategory.Public);
                    b.FirewallProfile("Public", enabled: true);
                }),
                finding => finding.RuleId == "LSN-002");
        }

        [Fact]
        public void Le_meme_partage_sur_un_reseau_prive_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Listening(445, processId: 4, process: "System");
                    b.Environment(category: NetworkCategory.Private);
                    b.FirewallProfile("Public", enabled: false);
                }),
                finding => finding.RuleId == "LSN-002");
        }

        // ============================================================ les actions

        [Fact]
        public void Les_cinq_actions_reseau_sont_au_catalogue()
        {
            var ids = new HashSet<string>();
            foreach (var action in ActionCatalog.CreateAll()) ids.Add(action.Descriptor.Id);

            foreach (var id in new[]
                     {
                         "REPAIR-NETWORK-RENEW", "REPAIR-NETWORK-PROXY", "REPAIR-NETWORK-ARP",
                         "REPAIR-NETWORK-ADAPTER", "REPAIR-NETWORK-FIREWALL",
                     })
                Assert.Contains(id, ids);
        }

        [Fact]
        public void Toute_action_qui_coupe_la_liaison_avertit_de_l_intervention_a_distance()
        {
            // Un technicien connecté à distance doit pouvoir refuser en connaissance de cause :
            // l'oubli de cet avertissement lui couperait le tapis sous les pieds.
            foreach (var id in new[] { "REPAIR-NETWORK-RENEW", "REPAIR-NETWORK-ADAPTER" })
            {
                var preview = PreviewOf(id, id == "REPAIR-NETWORK-ADAPTER"
                    ? new Dictionary<string, string> { { "adapter", "Ethernet" } }
                    : null,
                    Fixtures.Build(b => { b.Windows11(); b.Link(name: "Ethernet"); }));

                Assert.Contains(preview.Measurements,
                    line => line.Label.IndexOf("distance", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        [Fact]
        public void Le_redemarrage_de_carte_refuse_de_s_executer_sans_carte_choisie()
        {
            // Le typage impose une prévisualisation ; celle-ci refuse plutôt que d'agir au hasard.
            var preview = PreviewOf("REPAIR-NETWORK-ADAPTER", null);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public void La_reinitialisation_du_pare_feu_dit_ce_qui_sera_perdu()
        {
            var preview = PreviewOf("REPAIR-NETWORK-FIREWALL", null);

            Assert.Contains(preview.Measurements,
                line => line.Label.IndexOf("perdu", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(preview.Measurements,
                line => line.Label.IndexOf("avant", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ============================================================ les fiches

        [Fact]
        public void La_fiche_des_ports_distingue_ce_qui_est_visible_du_reseau()
        {
            var facts = Group(b =>
            {
                b.Listening(3389, process: "svchost");
                b.Listening(5432, allInterfaces: false, process: "postgres");
            }, "Ports en écoute");

            Assert.Equal("2", Value(facts, "Ports en écoute"));
            Assert.Equal("1", Value(facts, "Ouverts sur le réseau"));
        }

        [Fact]
        public void La_fiche_des_ports_serie_compte_les_numeros_perdus()
        {
            var facts = Group(b =>
            {
                b.SerialDevice("USB Serial Port (COM3)");
                b.ReservedSerialPorts(3, 4, 5, 6);
            }, "Ports série");

            Assert.Equal("1", Value(facts, "Ports présents"));
            Assert.Equal("3", Value(facts, "Retenus sans appareil"));
        }

        // ============================================================ montage

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            })).Findings;

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static ActionPreview PreviewOf(
            string id, IReadOnlyDictionary<string, string>? parameters, SystemSnapshot? snapshot = null)
        {
            foreach (var action in ActionCatalog.CreateAll())
            {
                if (!string.Equals(action.Descriptor.Id, id, StringComparison.Ordinal)) continue;

                var context = ActionFakes.Context(snapshot: snapshot, parameters: parameters);
                return action.PreviewAsync(context, default).GetAwaiter().GetResult();
            }

            throw new InvalidOperationException("Action absente du catalogue : " + id);
        }

        private static IReadOnlyList<Fact> Group(Action<Fixtures.Builder> configure, string title)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == title) return group.Facts;

            throw new InvalidOperationException("Le cadre « " + title + " » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;
    }
}
