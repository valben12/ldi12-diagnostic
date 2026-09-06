using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le chemin d'un paquet, le sens d'un nom, et le voisinage qu'on ne voit plus.
    /// </summary>
    public class NetworkPathTests
    {
        // ============================================================ routage

        [Fact]
        public void Deux_sorties_aux_priorites_proches_decrivent_une_machine_instable()
        {
            // Le tunnel qui laisse sa route en place : le trafic part par l'une ou par l'autre,
            // et le choix peut changer au redémarrage.
            var finding = Single(Evaluate(b =>
            {
                b.DefaultRoute("192.168.1.254", metric: 25);
                b.DefaultRoute("10.8.0.1", metric: 30, interfaceName: "NordLynx");
            }), "NET-025");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("NordLynx", finding.TechnicalDetail);
        }

        [Fact]
        public void Deux_sorties_nettement_hierarchisees_decrivent_une_liaison_de_secours()
        {
            // Une hiérarchie franche est un choix, pas une panne : le constat le dit, sans
            // pénaliser et sans conseiller quoi que ce soit.
            var finding = Single(Evaluate(b =>
            {
                b.DefaultRoute("192.168.1.254", metric: 25);
                b.DefaultRoute("192.168.9.1", metric: 400, interfaceName: "Clé 4G");
            }), "NET-025");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Empty(finding.Recommendations);
        }

        [Fact]
        public void Une_seule_sortie_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.DefaultRoute()),
                finding => finding.RuleId == "NET-025");
        }

        [Fact]
        public void Une_route_posee_a_la_main_est_nommee_avec_son_passage()
        {
            var finding = Single(Evaluate(b =>
            {
                b.DefaultRoute();
                b.Route("10.0.0.0", "255.0.0.0", "192.168.1.9", RouteOrigin.Manual, prefixLength: 8);
            }), "NET-026");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("10.0.0.0/8", finding.TechnicalDetail);
            Assert.Contains("192.168.1.9", finding.TechnicalDetail);
        }

        [Fact]
        public void Les_routes_que_Windows_pose_lui_meme_ne_sont_jamais_signalees()
        {
            // Une règle qui se déclenche sur toutes les machines ne se déclenche utilement sur
            // aucune : la table d'un poste sain ne contient que ces routes-là.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.DefaultRoute();
                    b.Route("192.168.1.0", "255.255.255.0", "0.0.0.0", RouteOrigin.System, prefixLength: 24);
                    b.Route("127.0.0.0", "255.0.0.0", "0.0.0.0", RouteOrigin.System, prefixLength: 8);
                }),
                finding => finding.RuleId == "NET-026");
        }

        // ============================================================ fichier hosts

        [Fact]
        public void Une_redirection_dans_le_fichier_hosts_est_signalee_avec_le_nom_vise()
        {
            var finding = Single(Evaluate(b => b.Hosts("0.0.0.0 pub.exemple.fr")), "NET-027");

            Assert.Contains("pub.exemple.fr", finding.TechnicalDetail);
            Assert.Contains("CLEAN-HOSTS-FILE", finding.Recommendations);
        }

        [Fact]
        public void Un_domaine_de_mise_a_jour_detourne_est_un_probleme_et_non_un_simple_constat()
        {
            // « Windows ne se met plus à jour » envoie le technicien partout sauf vers un fichier
            // texte de quatre lignes : c'est le seul constat de cette famille qui vaille un
            // problème.
            var finding = Single(Evaluate(b =>
                b.Hosts("0.0.0.0 windowsupdate.microsoft.com", "0.0.0.0 activation.exemple.fr")), "NET-028");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("windowsupdate.microsoft.com", finding.TechnicalDetail);
        }

        [Fact]
        public void Le_constat_general_se_tait_quand_le_constat_grave_parle()
        {
            // Deux constats sur les mêmes lignes diraient deux fois la même chose au technicien.
            var findings = Evaluate(b => b.Hosts("0.0.0.0 update.microsoft.com"));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "NET-027");
            Assert.Contains(findings, finding => finding.RuleId == "NET-028");
        }

        [Fact]
        public void Un_fichier_hosts_lu_et_vide_de_redirections_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.HostsUntouched()),
                finding => finding.RuleId == "NET-027" || finding.RuleId == "NET-028");
        }

        // ============================================================ pile réseau

        [Fact]
        public void Un_catalogue_Winsock_d_origine_ne_dit_rien()
        {
            // Mesuré sur la machine de développement : quatorze entrées, toutes vers mswsock.dll.
            Assert.DoesNotContain(
                Evaluate(b => b.Winsock(@"%systemroot%\system32\mswsock.dll")),
                finding => finding.RuleId == "NET-029");
        }

        [Fact]
        public void Une_bibliotheque_hors_du_dossier_systeme_est_nommee_avec_son_chemin()
        {
            var finding = Single(Evaluate(b => b.Winsock(
                @"%systemroot%\system32\mswsock.dll",
                @"C:\Program Files\Filtrage\lsp.dll")), "NET-029");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains(@"C:\Program Files\Filtrage\lsp.dll", finding.TechnicalDetail);
            Assert.Contains("RESET-WINSOCK-CATALOG", finding.Recommendations);
        }

        // ============================================================ serveurs de noms

        [Fact]
        public void Les_resolveurs_publics_repandus_sont_reconnus_et_non_signales()
        {
            // Mettre 1.1.1.1 est le premier geste de tout technicien devant une box qui rame : une
            // règle qui le signalerait apprendrait à ne plus la lire. Les deux sont configurés sur
            // la machine de développement.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.DnsServer("1.1.1.1", DnsServerNature.KnownPublic, "Cloudflare");
                    b.DnsServer("8.8.8.8", DnsServerNature.KnownPublic, "Google");
                }),
                finding => finding.RuleId == "NET-030");
        }

        [Fact]
        public void Un_serveur_du_meme_reseau_est_un_serveur_d_entreprise_et_non_une_anomalie()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.DnsServer("192.168.1.20", DnsServerNature.LocalNetwork)),
                finding => finding.RuleId == "NET-030");
        }

        [Fact]
        public void Une_adresse_publique_inconnue_est_signalee_sans_etre_accusee()
        {
            var finding = Single(Evaluate(b =>
            {
                b.DnsServer("192.168.1.254", DnsServerNature.Gateway);
                b.DnsServer("203.0.113.9", DnsServerNature.Unknown);
            }), "NET-030");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("203.0.113.9", finding.TechnicalDetail);
            Assert.Contains("preuve de malveillance", finding.PlainExplanation);
        }

        // ============================================================ voisinage

        [Fact]
        public void Un_partage_de_premiere_generation_actif_est_signale()
        {
            var finding = Single(Evaluate(b => b.Sharing(smb1Installed: true, smb1Enabled: true)), "NET-031");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("REMOVE-LEGACY-SHARING", finding.Recommendations);
        }

        [Fact]
        public void Un_Windows_moderne_sans_ce_pilote_ne_dit_rien()
        {
            // Mesuré : le service mrxsmb10 est absent de la machine de développement.
            Assert.DoesNotContain(
                Evaluate(b => b.Sharing()),
                finding => finding.RuleId == "NET-031");
        }

        [Fact]
        public void La_decouverte_arretee_sur_un_reseau_prive_explique_le_NAS_disparu()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Environment(category: NetworkCategory.Private);
                b.Service("FDResPub", "Stopped");
                b.Service("fdPHost", "Stopped");
            }), "NET-032");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("START-DISCOVERY-SERVICES", finding.Recommendations);
        }

        [Fact]
        public void La_meme_decouverte_arretee_sur_un_reseau_public_est_le_bon_reglage()
        {
            // Les services sont arrêtés sur la machine de développement, qui n'a aucun problème :
            // sans le classement du réseau, la règle serait du bruit partout.
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Environment(category: NetworkCategory.Public);
                    b.Service("FDResPub", "Stopped");
                }),
                finding => finding.RuleId == "NET-032");
        }

        [Fact]
        public void NetBIOS_coupe_partout_explique_les_partages_ouverts_par_leur_nom_court()
        {
            var finding = Single(Evaluate(b =>
                b.Sharing(netbiosDisabled: 3, netbiosInterfaces: 3)), "NET-033");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("SERVEUR", finding.PlainExplanation);
        }

        [Fact]
        public void Le_reglage_par_defaut_de_NetBIOS_ne_dit_rien()
        {
            // Mesuré : sept interfaces, aucune coupée, sur la machine de développement.
            Assert.DoesNotContain(
                Evaluate(b => b.Sharing(netbiosDisabled: 0, netbiosInterfaces: 7)),
                finding => finding.RuleId == "NET-033");
        }

        // ============================================================ les opérations

        [Fact]
        public void Les_trois_operations_de_chemin_sont_au_catalogue()
        {
            var ids = new HashSet<string>();
            foreach (var action in ActionCatalog.CreateAll()) ids.Add(action.Descriptor.Id);

            foreach (var id in new[]
                     { "REPAIR-NETWORK-HOSTS", "REPAIR-NETWORK-DISCOVERY", "REPAIR-NETWORK-ROUTE" })
                Assert.Contains(id, ids);
        }

        [Fact]
        public void L_apercu_du_fichier_hosts_montre_chaque_ligne_telle_qu_elle_est_ecrite()
        {
            // « Toujours afficher ce qui va être supprimé avant suppression » : ici, la
            // prévisualisation n'est pas un résumé de l'opération, elle en est le cœur.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Hosts("0.0.0.0   pub.exemple.fr", "127.0.0.1 licence.editeur.com");
            });

            var preview = PreviewOf("REPAIR-NETWORK-HOSTS", null, snapshot);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Contains(preview.Measurements, line => line.Value.Contains("pub.exemple.fr"));
            Assert.Contains(preview.Measurements, line => line.Value.Contains("licence.editeur.com"));
            Assert.Contains(preview.Measurements, line => line.Label == "Sauvegarde");
        }

        [Fact]
        public void Une_ligne_qui_coupe_les_mises_a_jour_est_signalee_dans_l_apercu()
        {
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Hosts("0.0.0.0 windowsupdate.microsoft.com");
            });

            var preview = PreviewOf("REPAIR-NETWORK-HOSTS", null, snapshot);

            Assert.Contains(preview.Measurements,
                line => line.Kind == PreviewLineKind.Caution &&
                        line.Value.Contains("windowsupdate.microsoft.com"));
        }

        [Fact]
        public void Le_fichier_hosts_modifie_entre_l_apercu_et_l_execution_arrete_l_operation()
        {
            // Retirer une ligne que le technicien n'a pas vue est exactement ce que ce logiciel
            // s'interdit : quelques secondes séparent l'aperçu de l'exécution, et rien ne garantit
            // que le fichier n'ait pas bougé.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Hosts("0.0.0.0 pub.exemple.fr");
            });

            var path = snapshot.Network.Paths.Hosts.Path!;
            var files = new FakeFileSystemGateway();
            files.Texts[path] = "# commentaire\r\n0.0.0.0 pub.exemple.fr\r\n0.0.0.0 ajoute-entre-temps.fr\r\n";

            var action = Find("REPAIR-NETWORK-HOSTS");
            var context = ActionFakes.Context(snapshot: snapshot, files: files);
            var preview = action.PreviewAsync(context, default).GetAwaiter().GetResult();
            var outcome = action.ExecuteAsync(context, preview, null, default).GetAwaiter().GetResult();

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("changé", outcome.Summary);
            Assert.Empty(files.Written);
        }

        [Fact]
        public void Le_contenu_retire_est_sauvegarde_avant_d_etre_remplace()
        {
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Hosts("0.0.0.0 pub.exemple.fr");
            });

            var path = snapshot.Network.Paths.Hosts.Path!;
            var files = new FakeFileSystemGateway();
            files.Texts[path] = "# commentaire\r\n0.0.0.0 pub.exemple.fr\r\n";

            var action = Find("REPAIR-NETWORK-HOSTS");
            var context = ActionFakes.Context(snapshot: snapshot, files: files);
            var preview = action.PreviewAsync(context, default).GetAwaiter().GetResult();
            var outcome = action.ExecuteAsync(context, preview, null, default).GetAwaiter().GetResult();

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);

            var backup = files.Written.Keys.Single(key => key.StartsWith(path + ".", StringComparison.Ordinal));
            Assert.Contains("pub.exemple.fr", files.Written[backup]);
            Assert.DoesNotContain("pub.exemple.fr", files.Texts[path]);
        }

        [Fact]
        public void Le_retrait_d_une_route_refuse_de_s_executer_sans_route_choisie()
        {
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Route("10.0.0.0", "255.0.0.0", "192.168.1.9", RouteOrigin.Manual, prefixLength: 8);
            });

            var preview = PreviewOf("REPAIR-NETWORK-ROUTE", null, snapshot);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Contains(preview.Measurements, line => line.Label == "10.0.0.0/8");
        }

        [Fact]
        public void L_apercu_d_une_route_donne_de_quoi_la_remettre()
        {
            // Le pire résultat possible est un technicien qui retire une route volontaire sans
            // avoir noté de quoi la reposer.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Route("10.0.0.0", "255.0.0.0", "192.168.1.9", RouteOrigin.Manual, prefixLength: 8);
            });

            var preview = PreviewOf("REPAIR-NETWORK-ROUTE",
                new Dictionary<string, string> { { "route", "10.0.0.0/8" } }, snapshot);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Contains(preview.Measurements, line => line.Label == "Masque" && line.Value == "255.0.0.0");
            Assert.Contains(preview.Measurements, line => line.Label == "Passage" && line.Value == "192.168.1.9");
            Assert.Contains(preview.Measurements, line => line.Kind == PreviewLineKind.Caution);
        }

        // ============================================================ les fiches

        [Fact]
        public void La_fiche_de_routage_compte_les_sorties_et_les_routes_posees_a_la_main()
        {
            var facts = Group(b =>
            {
                b.DefaultRoute();
                b.Route("10.0.0.0", "255.0.0.0", "192.168.1.9", RouteOrigin.Manual, prefixLength: 8);
            }, "Table de routage");

            Assert.Equal("2", Value(facts, "Routes"));
            Assert.Equal("1", Value(facts, "Chemins de sortie"));
            Assert.Equal("1", Value(facts, "Posées à la main"));
        }

        [Fact]
        public void La_fiche_du_fichier_hosts_distingue_les_domaines_consequents()
        {
            var facts = Group(b => b.Hosts(
                "0.0.0.0 pub.exemple.fr", "0.0.0.0 windowsupdate.microsoft.com"), "Fichier hosts");

            Assert.Equal("2", Value(facts, "Lignes actives"));
            Assert.Equal("1", Value(facts, "Domaines de mise à jour visés"));
        }

        [Fact]
        public void La_fiche_de_la_pile_reseau_compte_les_bibliotheques_etrangeres()
        {
            var facts = Group(b => b.Winsock(
                @"%systemroot%\system32\mswsock.dll", @"C:\Outil\lsp.dll"), "Pile réseau");

            Assert.Equal("2", Value(facts, "Entrées au catalogue"));
            Assert.Equal("1", Value(facts, "Bibliothèques étrangères"));
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

        private static IRepairAction Find(string id)
        {
            foreach (var action in ActionCatalog.CreateAll())
                if (string.Equals(action.Descriptor.Id, id, StringComparison.Ordinal)) return action;

            throw new InvalidOperationException("Action absente du catalogue : " + id);
        }

        private static ActionPreview PreviewOf(
            string id, IReadOnlyDictionary<string, string>? parameters, SystemSnapshot? snapshot = null)
        {
            var context = ActionFakes.Context(snapshot: snapshot, parameters: parameters);
            return Find(id).PreviewAsync(context, default).GetAwaiter().GetResult();
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
