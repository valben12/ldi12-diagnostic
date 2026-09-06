using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LDI12.Collectors.Network;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Relevé sans fil détaillé.
    /// </summary>
    /// <remarks>
    /// La machine de développement n'a aucune interface sans fil : rien de ce qui suit n'a pu
    /// être observé sur du matériel réel. Ces tests couvrent donc précisément ce qui reste
    /// vérifiable sans carte Wi-Fi (la disposition mémoire des structures natives, les tables
    /// du 802.11, le calcul du voisinage et les règles) et le chemin dégradé, lui, a été
    /// exécuté sur cette machine : c'est la seule partie du lot dont on ait la preuve.
    /// </remarks>
    public class WirelessTests
    {
        // ============================================================ interop

        /// <summary>
        /// Une structure mal alignée ne casse rien : elle rend des fréquences et des puissances
        /// fantaisistes, silencieusement. C'est le seul garde-fou possible sans matériel.
        /// </summary>
        [Fact]
        public void La_structure_des_points_d_acces_fait_360_octets()
        {
            Assert.Equal(360, Marshal.SizeOf(typeof(WlanNative.WLAN_BSS_ENTRY)));
        }

        [Fact]
        public void La_structure_d_etat_radio_fait_douze_octets()
        {
            Assert.Equal(12, Marshal.SizeOf(typeof(WlanNative.WLAN_PHY_RADIO_STATE)));
        }

        /// <summary>
        /// Un numéro d'erreur affiché tel quel n'apprend rien : le service arrêté d'un poste fixe
        /// est le cas courant, et il se dit en français.
        /// </summary>
        [Fact]
        public void Le_service_sans_fil_arrete_est_explique_sans_code_d_erreur()
        {
            var message = NetworkApi.DescribeOpenFailure(WlanNative.ERROR_SERVICE_NOT_ACTIVE);

            Assert.Contains("Configuration automatique des réseaux sans fil", message);
            Assert.DoesNotContain("1062", message);
        }

        /// <summary>
        /// Le refus de localisation est un état normal depuis Windows 10, pas une panne : la
        /// raison doit dire au technicien où se trouve le réglage.
        /// </summary>
        [Fact]
        public void Le_refus_de_localisation_est_explique_par_sa_cause()
        {
            var message = NetworkApi.DescribeBssFailure(WlanNative.ERROR_ACCESS_DENIED);

            Assert.Contains("localisation", message);
            Assert.DoesNotContain("5", message.Replace("Windows 10", string.Empty));
        }

        // ============================================================ tables 802.11

        [Theory]
        [InlineData(2_412_000, WifiBand.Band24, 1)]
        [InlineData(2_437_000, WifiBand.Band24, 6)]
        [InlineData(2_484_000, WifiBand.Band24, 14)]
        [InlineData(5_180_000, WifiBand.Band5, 36)]
        [InlineData(5_745_000, WifiBand.Band5, 149)]
        [InlineData(5_975_000, WifiBand.Band6, 5)]
        public void La_frequence_donne_la_bande_et_le_canal(int kilohertz, WifiBand band, int channel)
        {
            Assert.Equal(band, WifiChannels.BandFromFrequency(kilohertz));
            Assert.Equal(channel, WifiChannels.ChannelFromFrequency(kilohertz));
        }

        /// <summary>
        /// La bande 6 GHz reprend la numérotation à 1 : un canal 6 peut désigner deux fréquences
        /// très différentes. Sans la fréquence, on ne conclut pas.
        /// </summary>
        [Fact]
        public void Le_canal_seul_ne_tranche_pas_entre_2_4_et_6_GHz()
        {
            Assert.Equal(WifiBand.Band24, WifiChannels.BandFromChannel(6, sixGigahertzCapable: false));
            Assert.Equal(WifiBand.Unknown, WifiChannels.BandFromChannel(6, sixGigahertzCapable: true));
            Assert.Equal(WifiBand.Band5, WifiChannels.BandFromChannel(36, sixGigahertzCapable: true));
        }

        /// <summary>
        /// En 2,4 GHz les canaux sont espacés de 5 MHz pour une largeur de 20 : quatre canaux
        /// d'écart ne suffisent pas à les séparer. En 5 GHz ils ne se recouvrent pas.
        /// </summary>
        [Fact]
        public void Le_recouvrement_ne_vaut_qu_en_2_4_GHz()
        {
            Assert.True(WifiChannels.Overlaps(WifiBand.Band24, 6, 9));
            Assert.False(WifiChannels.Overlaps(WifiBand.Band24, 1, 6));
            Assert.False(WifiChannels.Overlaps(WifiBand.Band5, 36, 40));
            Assert.True(WifiChannels.Overlaps(WifiBand.Band5, 36, 36));
        }

        /// <summary>
        /// « 802.11a » est contenu dans « 802.11ac » comme dans « 802.11ax » : une comparaison par
        /// sous-chaîne annoncerait une carte Wi-Fi 6 comme matériel d'avant 2009.
        /// </summary>
        [Fact]
        public void Une_carte_recente_n_est_pas_prise_pour_une_norme_ancienne()
        {
            Assert.False(WifiChannels.IsLegacyRadio("802.11ac"));
            Assert.False(WifiChannels.IsLegacyRadio("802.11ax"));
            Assert.False(WifiChannels.IsLegacyRadio("802.11n"));
            Assert.True(WifiChannels.IsLegacyRadio("802.11a (OFDM)"));
            Assert.True(WifiChannels.IsLegacyRadio("802.11g"));

            Assert.Equal(433, WifiChannels.SingleStreamRateMbps("802.11ac"));
            Assert.Equal(72, WifiChannels.SingleStreamRateMbps("802.11n"));
            Assert.Equal(0, WifiChannels.SingleStreamRateMbps("Type physique 12"));
        }

        // ============================================================ voisinage

        /// <summary>
        /// Seuls les points d'accès de la même bande gênent : un réseau en 5 GHz n'émet pas dans
        /// les fréquences d'une liaison en 2,4 GHz.
        /// </summary>
        [Fact]
        public void Seuls_les_voisins_de_la_meme_bande_sont_comptes()
        {
            var connection = Connection(channel: 6, frequencyKhz: 2_437_000, points: new[]
            {
                Point("Moi", "AA:BB:CC:DD:EE:FF", 2_437_000, -50),
                Point("Voisin même canal", "00:00:00:00:00:01", 2_437_000, -70),
                Point("Voisin canal 8", "00:00:00:00:00:02", 2_447_000, -75),
                Point("Voisin canal 1", "00:00:00:00:00:03", 2_412_000, -80),
                Point("Voisin en 5 GHz", "00:00:00:00:00:04", 5_180_000, -60),
            });

            var neighbourhood = NetworkAdaptersProbe.BuildNeighbourhood(connection);

            Assert.NotNull(neighbourhood);
            Assert.Equal(5, neighbourhood!.Total.Value);
            Assert.Equal(1, neighbourhood.SameChannel.Value);
            Assert.Equal(1, neighbourhood.OverlappingChannel.Value);
        }

        /// <summary>
        /// Sans bande connue, aucun voisin n'est rapporté à la liaison : compter serait affirmer
        /// un partage qu'on n'a pas mesuré.
        /// </summary>
        [Fact]
        public void Sans_bande_connue_le_voisinage_ne_conclut_pas()
        {
            var connection = Connection(channel: 6, frequencyKhz: null, points: new[]
            {
                Point("Voisin", "00:00:00:00:00:01", 2_437_000, -70),
            });

            var neighbourhood = NetworkAdaptersProbe.BuildNeighbourhood(connection);

            Assert.NotNull(neighbourhood);
            Assert.True(neighbourhood!.Total.IsReliable);
            Assert.False(neighbourhood.SameChannel.IsReliable);
            Assert.NotNull(neighbourhood.SameChannel.Reason);
        }

        /// <summary>
        /// Le refus de localisation laisse le reste du relevé intact : la liaison est mesurée, le
        /// voisinage porte sa raison.
        /// </summary>
        [Fact]
        public void Un_voisinage_refuse_reste_une_mesure_absente_expliquee()
        {
            var connection = Connection(channel: 6, frequencyKhz: 2_437_000, points: new WifiAccessPointInfo[0]);
            connection = new WifiConnectionInfo
            {
                InterfaceId = connection.InterfaceId,
                Connected = true,
                Ssid = connection.Ssid,
                Bssid = connection.Bssid,
                PhyType = connection.PhyType,
                Channel = connection.Channel,
                SignalPercent = 70,
                TxRateMbps = 65,
                State = "Connectée",
                AccessPointsLimitation = "Windows réserve la liste des réseaux voisins…",
            };

            var wifi = NetworkAdaptersProbe.BuildWifi(connection, Measured.Ok<IReadOnlyList<WifiConnectionInfo>>(
                new[] { connection }, DataSource.NativeApi));

            Assert.True(wifi.SignalPercent.IsReliable);
            Assert.NotNull(wifi.Neighbourhood);
            Assert.False(wifi.Neighbourhood!.Total.IsReliable);
        }

        /// <summary>
        /// La bande déduite du seul canal reste partielle : la fréquence, elle, est une mesure.
        /// </summary>
        [Fact]
        public void La_bande_deduite_du_canal_est_marquee_partielle()
        {
            var deduced = NetworkAdaptersProbe.ReadBand(Connection(6, null, new WifiAccessPointInfo[0]));
            Assert.Equal(Availability.Partial, deduced.Availability);
            Assert.Equal(WifiBand.Band24, deduced.Value);

            var measured = NetworkAdaptersProbe.ReadBand(Connection(6, 2_437_000, new WifiAccessPointInfo[0]));
            Assert.Equal(Availability.Available, measured.Availability);
        }

        /// <summary>
        /// Une carte déconnectée reste un relevé utile : c'est l'état de la radio qu'on vient
        /// chercher sur un portable qui ne se connecte plus.
        /// </summary>
        [Fact]
        public void Une_carte_deconnectee_rapporte_quand_meme_sa_radio()
        {
            var connection = new WifiConnectionInfo
            {
                InterfaceId = "{TEST}",
                Connected = false,
                State = "Déconnectée",
                RadioEnabled = false,
            };

            var wifi = NetworkAdaptersProbe.BuildWifi(connection, Measured.Ok<IReadOnlyList<WifiConnectionInfo>>(
                new[] { connection }, DataSource.NativeApi));

            Assert.False(wifi.Ssid.IsReliable);
            Assert.True(wifi.RadioEnabled.IsReliable);
            Assert.False(wifi.RadioEnabled.Value);
            Assert.Equal("Déconnectée", wifi.State.Value);
        }

        // ============================================================ règles

        [Fact]
        public void Un_canal_partage_par_neuf_reseaux_est_signale()
        {
            var findings = Analyze(Fixtures.CrowdedWifi());

            Assert.Contains(findings, f => f.RuleId == "NET-012");
        }

        /// <summary>
        /// Le débit effondré d'une liaison au signal faible est la conséquence attendue du signal :
        /// NET-007 le dit mieux, et deux constats pour une cause feraient croire à deux pannes.
        /// </summary>
        [Fact]
        public void Le_debit_effondre_se_tait_quand_le_signal_est_deja_faible()
        {
            var weak = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(signalPercent: 22, rssiDbm: -84, txMbps: 6);
            });

            var findings = Analyze(weak);

            Assert.Contains(findings, f => f.RuleId == "NET-007");
            Assert.DoesNotContain(findings, f => f.RuleId == "NET-014");
        }

        [Fact]
        public void Un_debit_effondre_a_bon_signal_est_signale()
        {
            var throttled = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(signalPercent: 88, rssiDbm: -52, radio: "802.11ac", txMbps: 24);
            });

            var findings = Analyze(throttled);

            Assert.Contains(findings, f => f.RuleId == "NET-014");
            Assert.DoesNotContain(findings, f => f.RuleId == "NET-007");
        }

        /// <summary>
        /// Une carte à une antenne à son plein régime ne doit pas être signalée : c'est la moitié
        /// des portables d'entrée de gamme.
        /// </summary>
        [Fact]
        public void Une_carte_a_une_antenne_a_plein_regime_n_est_pas_signalee()
        {
            var single = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(signalPercent: 80, rssiDbm: -58, radio: "802.11n", txMbps: 65);
            });

            Assert.DoesNotContain(Analyze(single), f => f.RuleId == "NET-014");
        }

        /// <summary>
        /// Sur un poste raccordé par câble, une radio éteinte est un choix ; sur un portable qui
        /// n'a que ça, c'est la panne elle-même.
        /// </summary>
        [Fact]
        public void La_radio_eteinte_pese_selon_ce_qui_reste_pour_communiquer()
        {
            var wired = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(radioEnabled: false, wired: true);
            });

            var laptop = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(radioEnabled: false, wired: false);
            });

            var onDesktop = Assert.Single(Analyze(wired).Where(f => f.RuleId == "NET-015"));
            var onLaptop = Assert.Single(Analyze(laptop).Where(f => f.RuleId == "NET-015"));

            Assert.Equal(Severity.Info, onDesktop.Severity);
            Assert.Equal(Severity.Warning, onLaptop.Severity);
        }

        [Fact]
        public void Une_norme_d_avant_2009_est_dite_sans_dramatiser()
        {
            var old = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
                b.WifiLink(radio: "802.11g", txMbps: 54, signalPercent: 80);
            });

            var finding = Assert.Single(Analyze(old).Where(f => f.RuleId == "NET-013"));
            Assert.Equal(Severity.Info, finding.Severity);
        }

        /// <summary>
        /// Sans matériel sans fil, aucune règle du domaine ne conclut : elles ne comptent alors
        /// pas dans le nombre de contrôles sur lequel repose le score.
        /// </summary>
        [Fact]
        public void Sans_carte_sans_fil_aucune_regle_ne_conclut()
        {
            var findings = Analyze(Fixtures.Healthy());

            Assert.DoesNotContain(findings, f =>
                f.RuleId == "NET-012" || f.RuleId == "NET-013" ||
                f.RuleId == "NET-014" || f.RuleId == "NET-015");
        }

        // ============================================================ outillage

        private static IReadOnlyList<Finding> Analyze(SystemSnapshot snapshot)
            => new AnalysisEngine().Analyze(snapshot).Findings;

        private static WifiConnectionInfo Connection(
            int channel, int? frequencyKhz, IReadOnlyList<WifiAccessPointInfo> points)
            => new WifiConnectionInfo
            {
                InterfaceId = "{TEST}",
                Connected = true,
                Ssid = "Reseau-Test",
                Bssid = "AA:BB:CC:DD:EE:FF",
                PhyType = "802.11n",
                Channel = channel,
                FrequencyKhz = frequencyKhz,
                SignalPercent = 70,
                TxRateMbps = 65,
                State = "Connectée",
                AccessPoints = points,
            };

        private static WifiAccessPointInfo Point(string ssid, string bssid, int frequencyKhz, int rssi)
            => new WifiAccessPointInfo
            {
                Ssid = ssid,
                Bssid = bssid,
                FrequencyKhz = frequencyKhz,
                RssiDbm = rssi,
                PhyType = "802.11n",
            };
    }
}
