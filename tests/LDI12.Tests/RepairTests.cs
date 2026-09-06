using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les réparations réseau, exécutées.
    /// </summary>
    /// <remarks>
    /// <b>Sept de ces opérations n'avaient jamais été exécutées par un test.</b> Leurs
    /// prévisualisations l'étaient ; ce qui touche à la machine, non. Le relevé qui l'a montré est
    /// simple : aucun fichier de test ne nommait <c>ResetFirewallAction</c>,
    /// <c>RestartAdapterAction</c>, <c>RenewAddressAction</c> ni leurs voisines.
    /// <para>
    /// Deux défauts en sont sortis, de la même famille : <b>le logiciel annonçait une réparation
    /// qui n'avait pas eu lieu</b>, parce qu'il croyait le code de sortie d'un outil de Windows.
    /// Les deux ont été mesurés sur cette machine avant d'être corrigés, et les mesures sont
    /// citées dans le code.
    /// </para>
    /// </remarks>
    public class RepairTests
    {
        /// <summary>Ce que netsh écrit et rend quand il refuse. Relevé sur Windows 11.</summary>
        private const string Refus = "L'opération demandée requiert une élévation " +
                                     "(Exécuter en tant qu'administrateur).";

        // ============================================================ le code de sortie de netsh

        [Theory]
        [InlineData(ActionIds.FlushArpCache)]
        [InlineData(ActionIds.ResetFirewall)]
        [InlineData(ActionIds.ResetMachineProxy)]
        public async Task Un_netsh_qui_refuse_n_est_pas_une_reussite(string id)
        {
            // netsh rend 1 quand il refuse, et l'ancien code acceptait 0 ou 1. Un pare-feu qu'on
            // n'avait pas le droit de réinitialiser était donc annoncé « revenu à ses règles
            // d'origine ». C'est le mensonge que ce logiciel s'interdit.
            var outcome = await Executer(id, ActionFakes.Result(1, Refus));

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.False(outcome.Succeeded);
        }

        [Theory]
        [InlineData(ActionIds.FlushArpCache, "interface ip delete arpcache")]
        [InlineData(ActionIds.ResetFirewall, "advfirewall reset")]
        [InlineData(ActionIds.ResetMachineProxy, "winhttp reset proxy")]
        public async Task Un_netsh_qui_aboutit_lance_bien_ce_qui_etait_annonce(string id, string attendu)
        {
            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result());
            var outcome = await Executer(id, runner);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);

            var commande = Assert.Single(runner.Requests);
            Assert.Equal("netsh.exe", commande.FileName);
            Assert.Equal(attendu, commande.Arguments);
        }

        [Fact]
        public async Task Un_outil_qui_ne_s_est_pas_lance_n_est_pas_une_reussite()
        {
            var outcome = await Executer(ActionIds.ResetFirewall, ActionFakes.Result(launchFailed: true));

            Assert.Equal(ActionStatus.Failed, outcome.Status);
        }

        // ============================================================ la carte réseau

        [Fact]
        public async Task Une_carte_qu_on_n_a_pas_pu_eteindre_n_est_pas_touchee()
        {
            // Le pire résultat serait d'enchaîner sur le rallumage d'une carte qu'on n'a pas
            // éteinte : la seconde commande partirait pour rien, et le compte rendu parlerait
            // d'un redémarrage qui n'a pas eu lieu.
            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result(1, Refus));
            var outcome = await Executer(
                ActionIds.RestartAdapter, runner, Machine(), Choix(RestartAdapterAction.AdapterParameter, "Ethernet"));

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("reste en service", outcome.Summary, StringComparison.Ordinal);
            Assert.Single(runner.Requests);
        }

        [Fact]
        public async Task Une_carte_eteinte_et_non_rallumee_le_dit_sans_detour()
        {
            var appels = 0;
            var runner = new ScriptedProcessRunner(_ => ++appels == 1
                ? ActionFakes.Result()
                : ActionFakes.Result(1, Refus));

            var outcome = await Executer(
                ActionIds.RestartAdapter, runner, Machine(), Choix(RestartAdapterAction.AdapterParameter, "Ethernet"));

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("n'a pas pu être rallumée", outcome.Summary, StringComparison.Ordinal);
            Assert.Contains(outcome.Details, d => d.Contains("rallumer sans attendre"));
            Assert.Equal(2, runner.Requests.Count);
            Assert.Contains("admin=disable", runner.Requests[0].Arguments, StringComparison.Ordinal);
            Assert.Contains("admin=enable", runner.Requests[1].Arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void Une_carte_absente_de_la_machine_ne_se_redemarre_pas()
        {
            var action = Trouver(ActionIds.RestartAdapter);
            var context = ActionFakes.Context(
                snapshot: Machine(), parameters: Choix(RestartAdapterAction.AdapterParameter, "Wi-Fi du voisin"));

            var readiness = action.CheckReadiness(context);

            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
            Assert.Contains("Wi-Fi du voisin", readiness.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void Sans_carte_choisie_l_operation_ne_part_pas()
        {
            var action = Trouver(ActionIds.RestartAdapter);
            var readiness = action.CheckReadiness(ActionFakes.Context(snapshot: Machine()));

            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
        }

        // ============================================================ le renouvellement d'adresse

        [Fact]
        public async Task Un_renouvellement_qui_echoue_avec_le_code_zero_n_est_pas_une_reussite()
        {
            // Mesuré : « ipconfig /renew CarteQuiNExistePas » écrit « L'opération a échoué » et
            // rend 0. Juger sur le code de sortie revenait à annoncer une adresse renouvelée à
            // chaque fois que la carte n'était pas en état d'en obtenir une.
            var outcome = await Executer(
                ActionIds.RenewAddress,
                ActionFakes.Result(0, "Configuration IP de Windows\r\n\r\nL'opération a échoué, car aucun " +
                                      "adaptateur n'est dans l'état permettant cette opération."),
                Dhcp());

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("aucune adresse", outcome.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Une_adresse_que_la_machine_s_est_donnee_a_elle_meme_n_est_pas_un_bail()
        {
            // 169.254, c'est précisément la panne qu'on venait réparer.
            var outcome = await Executer(
                ActionIds.RenewAddress,
                ActionFakes.Result(0, "Carte Ethernet Ethernet :\r\n" +
                                      "   Adresse IPv4. . . . . . . . . . . . . .: 169.254.31.7\r\n" +
                                      "   Masque de sous-reseau. . . . . . . . . : 255.255.0.0\r\n"),
                Dhcp());

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("s'est attribué une adresse", outcome.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Une_adresse_obtenue_est_une_reussite_et_elle_est_nommee()
        {
            var outcome = await Executer(
                ActionIds.RenewAddress,
                ActionFakes.Result(0, "Carte Ethernet Ethernet :\r\n" +
                                      "   Adresse IPv4. . . . . . . . . . . . . .: 192.168.1.23\r\n" +
                                      "   Masque de sous-reseau. . . . . . . . . : 255.255.255.0\r\n" +
                                      "   Passerelle par defaut. . . . . . . . . : 192.168.1.1\r\n"),
                Dhcp());

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(outcome.Details, d => d.Contains("192.168.1.23"));
        }

        [Theory]
        [InlineData("255.255.255.0", false)]
        [InlineData("127.0.0.1", false)]
        [InlineData("0.0.0.0", false)]
        [InlineData("169.254.31.7", false)]
        [InlineData("999.1.1.1", false)]
        [InlineData("192.168.1.23", true)]
        [InlineData("10.0.0.4", true)]
        public void Ce_qui_compte_comme_adresse_obtenue(string texte, bool retenue)
        {
            // Les chiffres sont ce qui reste quand la langue de Windows change : les masques, la
            // boucle locale et la route par défaut ne prouvent pas qu'un serveur a répondu.
            var trouvees = NetworkRepairHelp.LeasedAddresses("   quelque chose : " + texte + "\r\n");

            Assert.Equal(retenue, trouvees.Count == 1);
        }

        [Fact]
        public void Une_machine_a_adresses_fixes_ne_renouvelle_rien()
        {
            var action = Trouver(ActionIds.RenewAddress);
            var readiness = action.CheckReadiness(ActionFakes.Context(snapshot: Machine()));

            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
            Assert.Contains("DHCP", readiness.Reason!, StringComparison.Ordinal);
        }

        // ============================================================ le voisinage réseau

        [Fact]
        public async Task Un_service_de_decouverte_qui_refuse_de_demarrer_est_nomme()
        {
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Service("FDResPub", state: "Stopped", startMode: "Disabled");
                b.Service("SSDPSRV", state: "Stopped", startMode: "Manual");
            });

            var appels = 0;
            var runner = new ScriptedProcessRunner(_ => ++appels == 1
                ? ActionFakes.Result(2, "Le service n'a pas pu être démarré.")
                : ActionFakes.Result());

            var outcome = await Executer(ActionIds.RestartDiscovery, runner, snapshot);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Contains(outcome.Details, d => d.StartsWith("Refusés : FDResPub", StringComparison.Ordinal));
            Assert.Contains(outcome.Details, d => d.StartsWith("Démarrés : SSDPSRV", StringComparison.Ordinal));
        }

        // ============================================================ le chemin ajouté à la main

        [Fact]
        public async Task La_route_retiree_est_celle_qui_a_ete_montree()
        {
            // L'exécution relit la route dans sa propre prévisualisation, et non dans le
            // paramètre : ce qui est supprimé est exactement ce qui a été affiché.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Route("10.8.0.0", "255.255.255.0", "192.168.1.254", RouteOrigin.Manual, prefixLength: 24);
            });

            var runner = new ScriptedProcessRunner(_ => ActionFakes.Result());
            var outcome = await Executer(
                ActionIds.RemoveRoute, runner, snapshot, Choix(RemoveRouteAction.Parameter, "10.8.0.0/24"));

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);

            var commande = Assert.Single(runner.Requests);
            Assert.Equal("route.exe", commande.FileName);
            Assert.Equal("delete 10.8.0.0 mask 255.255.255.0 192.168.1.254", commande.Arguments);
            Assert.Contains(outcome.Details, d => d.Contains("route -p add"));
        }

        // ============================================================ le fichier hosts

        [Fact]
        public void Une_ligne_signalee_dans_l_apercu_reste_reconnue_a_l_execution()
        {
            // L'aperçu ajoute une annotation aux lignes qui coupent un service de mise à jour, et
            // l'exécution la retire avant de comparer. Si les deux cessaient de s'accorder, le
            // logiciel refuserait d'agir en disant que le fichier a changé, sans qu'il ait bougé.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Hosts("0.0.0.0 windowsupdate.microsoft.com");
            });

            var path = snapshot.Network.Paths.Hosts.Path!;
            var files = new FakeFileSystemGateway();
            files.Texts[path] = "# fichier hosts\r\n0.0.0.0 windowsupdate.microsoft.com\r\n";

            var action = Trouver(ActionIds.RestoreHostsFile);
            var context = ActionFakes.Context(snapshot: snapshot, files: files);
            var preview = action.PreviewAsync(context, default).GetAwaiter().GetResult();

            Assert.Contains(preview.Measurements, m => m.Value.Contains("coupe un service de mise à jour"));

            var outcome = action.ExecuteAsync(context, preview, null, default).GetAwaiter().GetResult();

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.DoesNotContain("windowsupdate", files.Texts[path], StringComparison.Ordinal);
        }

        // ============================================================ mise en place

        private static IRepairAction Trouver(string id)
        {
            foreach (var action in ActionCatalog.CreateAll())
                if (string.Equals(action.Descriptor.Id, id, StringComparison.Ordinal)) return action;

            throw new InvalidOperationException("Action absente du catalogue : " + id);
        }

        private static Task<ActionOutcome> Executer(
            string id, ProcessResult reponse, SystemSnapshot? snapshot = null,
            IReadOnlyDictionary<string, string>? parameters = null)
            => Executer(id, new ScriptedProcessRunner(_ => reponse), snapshot, parameters);

        private static async Task<ActionOutcome> Executer(
            string id, ScriptedProcessRunner runner, SystemSnapshot? snapshot = null,
            IReadOnlyDictionary<string, string>? parameters = null)
        {
            var action = Trouver(id);
            var context = ActionFakes.Context(processes: runner, snapshot: snapshot, parameters: parameters);
            var preview = await action.PreviewAsync(context, CancellationToken.None);

            return await action.ExecuteAsync(context, preview, null, CancellationToken.None);
        }

        private static Dictionary<string, string> Choix(string cle, string valeur)
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [cle] = valeur };

        /// <summary>Une machine ordinaire : une carte branchée, une adresse fixée à la main.</summary>
        private static SystemSnapshot Machine()
            => Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
            });

        /// <summary>La même, mais son adresse vient d'un serveur.</summary>
        private static SystemSnapshot Dhcp()
        {
            var machine = Machine();
            var adapters = new List<NetworkAdapterInfo>();

            foreach (var adapter in machine.Network.Adapters)
                adapters.Add(new NetworkAdapterInfo
                {
                    Name = adapter.Name,
                    Description = adapter.Description,
                    Kind = adapter.Kind,
                    Status = adapter.Status,
                    IsUp = adapter.IsUp,
                    HasApipaAddress = adapter.HasApipaAddress,
                    IPv4Addresses = adapter.IPv4Addresses,
                    Gateways = adapter.Gateways,
                    DnsServers = adapter.DnsServers,
                    IsPrimary = adapter.IsPrimary,
                    DhcpEnabled = Measured.Ok(true, DataSource.NativeApi),
                });

            return new SystemSnapshot
            {
                Windows = machine.Windows,
                Network = new NetworkSnapshot
                {
                    Adapters = adapters,
                    Tests = machine.Network.Tests,
                    Environment = machine.Network.Environment,
                    Paths = machine.Network.Paths,
                },
            };
        }
    }
}
