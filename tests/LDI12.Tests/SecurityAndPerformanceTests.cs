using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LDI12.Collectors.Hardware;
using LDI12.Collectors.Network;
using LDI12.Collectors.Security;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que les dimensions Sécurité et Performances doivent garantir.
    /// </summary>
    /// <remarks>
    /// Deux familles de tests. Les premiers portent sur la discipline du modèle : un réglage
    /// qu'on n'a pas lu ne vaut jamais « désactivé », une section collectée ne se perd pas en
    /// chemin. Les seconds portent sur la formulation : un audit de sécurité qui crie au loup
    /// sur une configuration normale finit ignoré, et ne protège alors plus personne.
    /// </remarks>
    public class SecurityAndPerformanceTests
    {
        private static SystemSnapshot Analyze(SystemSnapshot snapshot) => new AnalysisEngine().Analyze(snapshot);

        private static IReadOnlyList<Finding> FindingsOf(SystemSnapshot snapshot, string ruleId)
        {
            var found = new List<Finding>();
            foreach (var finding in Analyze(snapshot).Findings)
                if (finding.RuleId == ruleId) found.Add(finding);
            return found;
        }

        // ---------- Le piège qui a coûté une section entière ----------

        /// <summary>
        /// Analyser un instantané n'en perd aucune section.
        /// </summary>
        /// <remarks>
        /// Le moteur reconstruisait l'instantané en énumérant ses sections : une section ajoutée
        /// plus tard était collectée correctement puis perdue au passage suivant, sans erreur,
        /// sans avertissement, avec pour seul symptôme un rapport vide. Ce test parcourt les
        /// propriétés par réflexion : il attrapera donc aussi la prochaine section ajoutée, que
        /// personne n'aura pensé à recopier.
        /// </remarks>
        [Fact]
        public void Analyser_un_instantane_n_en_perd_aucune_section()
        {
            var collected = Fixtures.ExposedMachine();
            var analyzed = Analyze(collected);

            // Tout sauf ce que l'analyse produit justement : findings, corrélations, actions, score.
            var produced = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(SystemSnapshot.Findings),
                nameof(SystemSnapshot.Correlations),
                nameof(SystemSnapshot.Recommendations),
                nameof(SystemSnapshot.Score),
            };

            var checkedCount = 0;
            foreach (var property in typeof(SystemSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (produced.Contains(property.Name)) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                Assert.True(
                    ReferenceEquals(property.GetValue(collected), property.GetValue(analyzed)),
                    "La section « " + property.Name + " » a été perdue pendant l'analyse. " +
                    "Elle doit être recopiée par SystemSnapshot.WithAnalysis.");
                checkedCount++;
            }

            Assert.True(checkedCount >= 8, "Le test ne parcourt pas toutes les sections attendues.");
        }

        [Fact]
        public void Les_fiches_de_securite_et_de_performances_sont_produites()
        {
            // Le manque explicitement laissé par la phase 4 : les deux écrans n'affichaient que
            // constats et modules, faute de section dans l'instantané.
            var sheets = FactSheetBuilder.Build(Fixtures.Healthy());

            Assert.Contains(sheets, sheet => sheet.Category == DiagnosticCategory.Security);
            Assert.Contains(sheets, sheet => sheet.Category == DiagnosticCategory.Performance);
        }

        // ---------- Décodage de l'état des produits de sécurité ----------

        [Theory]
        [InlineData(0x061100, ProtectionState.Enabled, true)]
        [InlineData(0x061000, ProtectionState.Enabled, true)]
        [InlineData(0x060000, ProtectionState.Disabled, true)]
        [InlineData(0x061010, ProtectionState.Enabled, false)]
        [InlineData(0x060100, ProtectionState.Expired, true)]
        public void L_etat_d_un_produit_de_securite_se_decode(int state, ProtectionState expected, bool upToDate)
        {
            var decoded = SecurityProductsProbe.Decode(state);

            Assert.True(decoded.State.IsReliable);
            Assert.Equal(expected, decoded.State.Value);
            Assert.Equal(upToDate, decoded.UpToDate.Value);
        }

        [Fact]
        public void Un_etat_de_produit_inconnu_n_est_pas_traduit_en_desactive()
        {
            // productState n'est pas documenté par Microsoft. Un motif qu'on ne sait pas lire
            // doit rendre une mesure absente, et non « désactivé », qui ferait conclure à une
            // machine sans protection.
            var decoded = SecurityProductsProbe.Decode(0x067700);

            Assert.False(decoded.State.HasValue);
            Assert.Equal(Availability.Unavailable, decoded.State.Availability);
            Assert.False(string.IsNullOrWhiteSpace(decoded.State.Reason));
        }

        [Fact]
        public void Un_produit_qui_ne_declare_aucun_etat_ne_devient_pas_actif()
        {
            var decoded = SecurityProductsProbe.Decode(null);

            Assert.False(decoded.State.HasValue);
            Assert.False(decoded.UpToDate.HasValue);
        }

        // ---------- Règles de sécurité ----------

        [Fact]
        public void Un_antivirus_expire_est_un_probleme_et_non_une_simple_information()
        {
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-004");

            Assert.Single(findings);
            Assert.Equal(Severity.Problem, findings[0].Severity);
        }

        [Fact]
        public void Un_pare_feu_coupe_sur_le_profil_public_est_un_probleme()
        {
            // Une machine protégée chez elle et exposée dans un lieu public : la configuration
            // qu'on ne soupçonne pas, parce que tout fonctionne à la maison.
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-006");

            Assert.Single(findings);
            Assert.Equal(Severity.Problem, findings[0].Severity);
            Assert.Contains("Public", findings[0].TechnicalDetail, StringComparison.Ordinal);
        }

        [Fact]
        public void Un_controle_de_compte_actif_mais_muet_est_signale()
        {
            // EnableLUA vaut 1, ConsentPromptBehaviorAdmin vaut 0 : la protection est comptée
            // comme active et ne s'affiche jamais. Ne lire que le premier réglage ferait
            // conclure à une machine protégée.
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-007");

            Assert.Single(findings);
            Assert.Equal(Severity.Warning, findings[0].Severity);
            Assert.Contains("jamais", findings[0].Title, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Un_compte_administrateur_sans_mot_de_passe_est_un_probleme()
        {
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-009");

            Assert.Single(findings);
            Assert.Equal(Severity.Problem, findings[0].Severity);
        }

        [Fact]
        public void Un_compte_integre_desactive_ne_compte_pas_comme_expose()
        {
            // Invité, DefaultAccount et WDAGUtilityAccount portent l'indicateur « aucun mot de
            // passe requis » sur toutes les machines Windows, en étant désactivés. Les signaler
            // ferait crier au loup partout, et le garde-fou finirait ignoré.
            Assert.Empty(FindingsOf(Fixtures.Healthy(), "SEC-009"));
        }

        [Fact]
        public void Un_reglage_absent_du_registre_ne_vaut_jamais_desactive()
        {
            // Aucune donnée de sécurité collectée : aucune règle ne doit conclure. Une absence
            // de mesure n'est pas une absence de protection.
            var analyzed = Analyze(Fixtures.Empty());

            foreach (var finding in analyzed.Findings)
                Assert.False(
                    finding.RuleId == "SEC-006" || finding.RuleId == "SEC-007" || finding.RuleId == "SEC-009",
                    finding.RuleId + " conclut sur une machine dont aucun réglage n'a été lu.");
        }

        [Fact]
        public void Plusieurs_comptes_administrateurs_restent_une_information()
        {
            // Sur un poste familial, c'est la règle plus que l'exception : le signaler ouvre la
            // conversation, il n'a pas à coûter de point.
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-010");

            Assert.Single(findings);
            Assert.Equal(Severity.Info, findings[0].Severity);
        }

        [Fact]
        public void Le_bureau_a_distance_active_n_est_pas_presente_comme_un_defaut()
        {
            var findings = FindingsOf(Fixtures.ExposedMachine(), "SEC-008");

            Assert.Single(findings);
            Assert.Equal(Severity.Info, findings[0].Severity);
        }

        // ---------- Règles de performance ----------

        [Fact]
        public void Un_demarrage_lent_isole_n_est_pas_traite_comme_une_tendance()
        {
            // Trois minutes après une grosse mise à jour de Windows : c'est normal, et le dire
            // autrement ferait conclure à une machine lente qui ne l'est pas.
            var snapshot = Fixtures.Healthy();
            var isolated = WithBoot(snapshot, seconds: 150, degraded: 1, samples: 10);

            var findings = FindingsOf(isolated, "PRF-005");

            Assert.Single(findings);
            Assert.Equal(Severity.Info, findings[0].Severity);
            Assert.Contains("isolé", findings[0].Title, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Un_demarrage_lent_repete_devient_un_avertissement()
        {
            var findings = FindingsOf(Fixtures.SlowMachine(), "PRF-005");

            Assert.Single(findings);
            Assert.Equal(Severity.Warning, findings[0].Severity);
        }

        [Fact]
        public void La_memoire_reclamee_au_dela_de_la_memoire_installee_est_signalee()
        {
            var findings = FindingsOf(Fixtures.SlowMachine(), "PRF-006");

            Assert.Single(findings);
            Assert.Equal(ConfidenceLevel.Medium, findings[0].Confidence);
        }

        [Fact]
        public void Un_processus_gourmand_est_nomme_sans_etre_juge()
        {
            // Un navigateur à 2 Go est banal : le constat sert à pouvoir le montrer au client,
            // pas à le désigner comme une anomalie.
            var findings = FindingsOf(Fixtures.SlowMachine(), "PRF-007");

            Assert.Single(findings);
            Assert.Equal(Severity.Info, findings[0].Severity);
            Assert.Equal("navigateur", findings[0].Subject);
        }

        // ---------- Qualité réseau ----------

        [Fact]
        public void Une_perte_de_paquets_mesuree_sur_serie_longue_est_signalee()
        {
            var findings = FindingsOf(Fixtures.ExposedMachine(), "NET-009");

            Assert.Single(findings);
            Assert.Contains("13", findings[0].TechnicalDetail, StringComparison.Ordinal);
        }

        [Fact]
        public void Une_liaison_irreguliere_est_signalee_meme_avec_une_bonne_latence_moyenne()
        {
            // 48 ms de gigue pour 18 ms de moyenne : la moyenne rassure, la conversation hache.
            var findings = FindingsOf(Fixtures.ExposedMachine(), "NET-010");

            Assert.Single(findings);
            Assert.Equal(Severity.Warning, findings[0].Severity);
        }

        [Fact]
        public void Une_taille_de_paquet_reduite_est_signalee()
        {
            var findings = FindingsOf(Fixtures.ExposedMachine(), "NET-011");

            Assert.Single(findings);
            Assert.Contains("1420", findings[0].TechnicalDetail, StringComparison.Ordinal);
        }

        [Fact]
        public void Une_perte_totale_est_laissee_aux_regles_de_connectivite()
        {
            // Sinon la même panne produirait deux constats : « la liaison perd des paquets » et
            // « Internet est injoignable ». Le second est le bon.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Network(connected: true, gatewayReachable: false, dnsOk: false, httpsOk: false);
                b.NetworkQuality(lossPercent: 100);
            });

            Assert.Empty(FindingsOf(snapshot, "NET-009"));
        }

        [Fact]
        public void Une_liaison_saine_ne_produit_aucun_constat_de_qualite()
        {
            foreach (var ruleId in new[] { "NET-009", "NET-010", "NET-011" })
                Assert.Empty(FindingsOf(Fixtures.Healthy(), ruleId));
        }

        // ---------- Mesures du chemin réseau ----------

        [Fact]
        public void La_gigue_exige_au_moins_deux_mesures()
        {
            // Avec une seule réponse, il n'y a aucun écart à calculer : rendre zéro affirmerait
            // une régularité parfaite qu'on n'a pas mesurée.
            Assert.False(NetworkPathProbe.Jitter(new[] { 12d }).HasValue);
            Assert.False(NetworkPathProbe.Jitter(Array.Empty<double>()).HasValue);

            var jitter = NetworkPathProbe.Jitter(new[] { 10d, 20d, 15d });
            Assert.True(jitter.IsReliable);
            Assert.Equal(7.5, jitter.Value, 1);
        }

        [Theory]
        [InlineData("192.168.1.1", true)]
        [InlineData("10.4.2.9", true)]
        [InlineData("172.16.0.1", true)]
        [InlineData("172.32.0.1", false)]
        [InlineData("169.254.3.4", true)]
        [InlineData("1.1.1.1", false)]
        [InlineData("pas une adresse", false)]
        public void La_frontiere_du_reseau_local_est_reconnue(string address, bool expected)
        {
            // C'est elle qui sépare « chez le client », où l'on peut agir, de « chez
            // l'opérateur », où l'on ne peut que constater.
            Assert.Equal(expected, NetworkPathProbe.IsPrivate(address));
        }

        // ---------- Températures ----------

        [Theory]
        [InlineData(3131u, 39.95)]  // 313,1 K
        [InlineData(2900u, 16.85)]  // la zone réelle de la machine de développement
        [InlineData(3731u, 99.95)]
        public void L_encodage_ACPI_des_temperatures_se_convertit(uint deciKelvin, double expected)
        {
            // Dixièmes de kelvin pour les zones ACPI, dixièmes de degré Celsius pour la sonde
            // SMBIOS : deux encodages voisins dans deux classes voisines, et l'occasion classique
            // d'afficher une température de 2 700 °C.
            Assert.Equal(expected, ThermalProbe.FromDeciKelvin(deciKelvin), 2);
        }

        [Theory]
        [InlineData(@"ACPI\ThermalZone\TZ00_0", "TZ00")]
        [InlineData(@"ACPI\ThermalZone\THRM_0", "THRM")]
        [InlineData("TZ01", "TZ01")]
        public void Le_nom_d_une_zone_est_reduit_a_ce_qui_sert(string instance, string expected)
            => Assert.Equal(expected, ThermalProbe.Shorten(instance));

        [Fact]
        public void Une_zone_hors_plage_reste_lue_mais_cesse_d_etre_exploitable()
        {
            // Cas réel : la carte mère de développement déclare une zone à 16,8 °C sur une
            // machine allumée depuis huit heures. La valeur existe, elle a bien été mesurée,
            // mais elle ne mesure pas ce qu'on croit. La supprimer serait pire, le technicien
            // ne saurait pas qu'elle existe.
            var zone = ThermalProbe.Build("UAD0", ThermalSource.AcpiZone, 16.8, DataSource.PerformanceCounter);

            Assert.False(zone.Plausible);
            Assert.True(zone.Celsius.HasValue);
            Assert.Equal(Availability.Partial, zone.Celsius.Availability);
            Assert.Equal(16.8, zone.Celsius.Value);
            Assert.False(string.IsNullOrWhiteSpace(zone.Celsius.Reason));
        }

        [Fact]
        public void Une_zone_declaree_sans_valeur_ne_devient_pas_zero()
        {
            var zone = ThermalProbe.Build("TZ00", ThermalSource.AcpiZone, null, DataSource.Wmi);

            Assert.False(zone.Celsius.HasValue);
            Assert.False(zone.Plausible);
        }

        [Fact]
        public void Aucune_regle_ne_conclut_sur_une_zone_invraisemblable()
        {
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Thermal(16.8, 17.2);
            });

            Assert.Empty(FindingsOf(snapshot, "THM-001"));
        }

        [Fact]
        public void Une_zone_chaude_et_credible_est_signalee_sans_nommer_de_composant()
        {
            // Le firmware ne dit pas ce que chaque zone suit : conclure « le processeur chauffe »
            // à partir de TZ00 serait une déduction que rien n'appuie.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Thermal(45, 92);
            });

            var findings = FindingsOf(snapshot, "THM-001");

            Assert.Single(findings);
            Assert.Equal(Severity.Warning, findings[0].Severity);
            Assert.Equal(ConfidenceLevel.Medium, findings[0].Confidence);
            Assert.DoesNotContain("processeur", findings[0].TechnicalDetail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void La_fiche_explique_pourquoi_la_temperature_du_processeur_manque()
        {
            // « Pourquoi HWMonitor l'affiche et pas vous ? » est une question qu'un technicien se
            // pose devant un écran muet. Le groupe est donc produit même sans aucune zone lisible.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Thermal();
            });

            var sheet = FactSheetBuilder.For(snapshot, DiagnosticCategory.Hardware);
            Assert.NotNull(sheet);

            var group = sheet!.Groups.SingleOrDefault(g => g.Title == "Températures");
            Assert.NotNull(group);

            var fact = group!.Visible.Single(f => f.Label == "Température du processeur");
            Assert.Equal(FactState.Missing, fact.State);
            Assert.Equal("Non mesuré", fact.Value);
            Assert.Contains("pilote noyau", fact.Note!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HWMonitor", fact.Note!, StringComparison.Ordinal);
        }

        [Fact]
        public void Le_releve_du_GPU_est_rattache_par_le_nom_et_non_par_le_rang()
        {
            // Windows énumère aussi les adaptateurs virtuels (bureau à distance, machines
            // virtuelles) que la bibliothèque du constructeur ignore. Se fier au rang
            // attribuerait la température de la vraie carte à un adaptateur fantôme.
            var sensors = Measured.Ok<IReadOnlyList<GpuSensorReading>>(
                new[]
                {
                    new GpuSensorReading
                    {
                        Index = 0,
                        Name = "NVIDIA GeForce GTX 1080",
                        TemperatureCelsius = Measured.Ok(47d, DataSource.NativeApi),
                    },
                },
                DataSource.NativeApi);

            Assert.Null(GpuProbe.Match(sensors, "Adaptateur d'affichage de Bureau à distance Microsoft"));

            var matched = GpuProbe.Match(sensors, "NVIDIA GeForce GTX 1080");
            Assert.NotNull(matched);
            Assert.Equal(47d, matched!.TemperatureCelsius.Value);
        }

        [Fact]
        public void Sans_bibliotheque_constructeur_aucune_temperature_de_GPU_n_est_inventee()
        {
            var absent = Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                "Aucune bibliothèque de gestion NVIDIA sur cette machine.", DataSource.NativeApi);

            Assert.Null(GpuProbe.Match(absent, "NVIDIA GeForce GTX 1080"));
        }

        // ---------- Capteurs matériels ----------

        [Fact]
        public void Le_paquet_processeur_retient_le_capteur_le_plus_chaud()
        {
            // Sur un processeur multi-cœurs, la bibliothèque expose un capteur par cœur en plus
            // du Tctl : c'est le maximum qui décide du bridage thermique, pas la moyenne.
            var zones = new List<ThermalZoneReading>();
            var fans = new List<FanReading>();

            var cpu = ThermalProbe.Harvest(
                new[]
                {
                    Sensor("AMD Ryzen 7 5700G", "Core #1", 58, isCpu: true),
                    Sensor("AMD Ryzen 7 5700G", "Core (Tctl/Tdie)", 71, isCpu: true),
                    Sensor("AMD Ryzen 7 5700G", "Core #2", 62, isCpu: true),
                    Sensor("ITE IT8686E", "Temperature #3", 44, isMotherboard: true),
                },
                zones, fans);

            Assert.True(cpu.IsReliable);
            Assert.Equal(71, cpu.Value);
            Assert.Equal(4, zones.Count);
        }

        [Fact]
        public void Deux_puces_de_carte_mere_ne_produisent_pas_de_capteurs_homonymes()
        {
            // Les cartes mères portent souvent deux puces Super-I/O, chacune numérotant ses
            // sondes à partir de un : l'écran affichait deux fois « Temperature #1 » avec des
            // valeurs différentes.
            var zones = new List<ThermalZoneReading>();
            var fans = new List<FanReading>();

            ThermalProbe.Harvest(
                new[]
                {
                    Sensor("ITE IT8686E", "Temperature #1", 35, isMotherboard: true),
                    Sensor("ITE IT8792E", "Temperature #1", 29, isMotherboard: true),
                },
                zones, fans);

            Assert.Equal(2, zones.Count);
            Assert.NotEqual(zones[0].Name, zones[1].Name);
            Assert.Contains("IT8686E", zones[0].Name, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(@"ACPI\ThermalZone\UAD0_0", "UAD0")]
        [InlineData(@"\_TZ.UAD0", "UAD0")]
        [InlineData(@"\_TZ.TZ00", "TZ00")]
        public void Les_deux_sources_de_zones_ACPI_nomment_pareil(string raw, string expected)
        {
            // Sans normalisation commune, une session administrateur affichait deux fois la même
            // zone (une par source) avec la même température. Défaut constaté au premier
            // lancement élevé.
            Assert.Equal(expected, ThermalProbe.Shorten(raw));
        }

        [Fact]
        public void Sans_capteurs_actifs_la_regle_du_processeur_ne_conclut_pas()
        {
            // Dire qu'un processeur ne chauffe pas quand on n'a pas su prendre sa température
            // serait exactement le mensonge que le reste du logiciel évite.
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Cpu(cores: 8, usage: 20);
                b.Thermal(45);
            });

            Assert.Empty(FindingsOf(snapshot, "CPU-003"));
        }

        [Fact]
        public void Une_seule_surchauffe_ne_produit_pas_deux_constats()
        {
            // Le capteur du processeur est laissé à CPU-003 : sans cela, THM-001 le compterait
            // une seconde fois sous le nom de « zone thermique ».
            var snapshot = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Cpu(cores: 8, usage: 95);
                b.ThermalSensors(cpuCelsius: 97, motherboardCelsius: 52);
            });

            var cpu = FindingsOf(snapshot, "CPU-003");
            Assert.Single(cpu);
            Assert.Equal(Severity.Problem, cpu[0].Severity);
            Assert.Equal(ConfidenceLevel.Medium, cpu[0].Confidence);

            Assert.Empty(FindingsOf(snapshot, "THM-001"));
        }

        private static HardwareSensorReading Sensor(
            string component, string name, double celsius, bool isCpu = false, bool isMotherboard = false)
            => new HardwareSensorReading
            {
                Component = component,
                Name = name,
                Kind = SensorKind.Temperature,
                IsCpu = isCpu,
                IsMotherboard = isMotherboard,
                Value = Measured.Ok(celsius, DataSource.NativeApi),
            };

        // ---------- Score ----------

        [Fact]
        public void Une_machine_saine_mais_exposee_perd_des_points_en_securite_seulement()
        {
            var healthy = Analyze(Fixtures.Healthy()).Score!;
            var exposed = Analyze(Fixtures.ExposedMachine()).Score!;

            var healthySecurity = healthy.Dimensions.Single(d => d.Dimension == DiagnosticCategory.Security);
            var exposedSecurity = exposed.Dimensions.Single(d => d.Dimension == DiagnosticCategory.Security);

            Assert.True(exposedSecurity.Score < healthySecurity.Score,
                "Le poste exposé devrait perdre des points de sécurité.");

            var healthyStorage = healthy.Dimensions.Single(d => d.Dimension == DiagnosticCategory.Storage);
            var exposedStorage = exposed.Dimensions.Single(d => d.Dimension == DiagnosticCategory.Storage);

            Assert.Equal(healthyStorage.Score, exposedStorage.Score);
        }

        private static SystemSnapshot WithBoot(SystemSnapshot snapshot, int seconds, int degraded, int samples)
            => new SystemSnapshot
            {
                Metadata = snapshot.Metadata,
                Machine = snapshot.Machine,
                Platform = snapshot.Platform,
                Hardware = snapshot.Hardware,
                Storage = snapshot.Storage,
                Windows = snapshot.Windows,
                Network = snapshot.Network,
                Security = snapshot.Security,
                ModuleReports = snapshot.ModuleReports,
                Performance = new PerformanceSnapshot
                {
                    Responsiveness = snapshot.Performance.Responsiveness,
                    TopByMemory = snapshot.Performance.TopByMemory,
                    TopByCpu = snapshot.Performance.TopByCpu,
                    ProcessCount = snapshot.Performance.ProcessCount,
                    Uptime = snapshot.Performance.Uptime,
                    Boot = new BootPerformance
                    {
                        Duration = Measured.Ok(TimeSpan.FromSeconds(seconds), DataSource.EventLog),
                        MainPathDuration = Measured.Ok(TimeSpan.FromSeconds(seconds * 0.6), DataSource.EventLog),
                        MeasuredAt = Measured.Ok(DateTimeOffset.Now, DataSource.EventLog),
                        DegradedBootCount = Measured.Ok(degraded, DataSource.EventLog),
                        SampleCount = Measured.Ok(samples, DataSource.EventLog),
                    },
                },
            };
    }
}
