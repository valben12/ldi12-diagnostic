using System;
using System.IO;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Comparison;
using LDI12.Engine.Profile;
using LDI12.Reports.History;
using LDI12.Reports.Json;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Comparaison avant / après.
    /// </summary>
    /// <remarks>
    /// C'est le document qui répond à « qu'est-ce que votre intervention a changé ? », donc celui
    /// qui a le plus à gagner à mentir : tout ce qui a disparu ressemble à une réussite. Ces
    /// tests portent presque tous sur ce point unique, ce que la comparaison n'a pas le droit
    /// de conclure.
    /// </remarks>
    public class ComparisonTests
    {
        // ============================================================ honnêteté

        /// <summary>
        /// Le piège central. Un second diagnostic qui n'a rien pu mesurer ne guérit personne.
        /// </summary>
        [Fact]
        public void Un_diagnostic_qui_n_a_rien_mesure_ne_resout_aucun_constat()
        {
            var before = Analyze(Fixtures.SlowMachine());
            var after = Analyze(Fixtures.Empty());

            var delta = SnapshotComparer.Compare(before, after);

            Assert.True(before.Findings.Count > 0);
            Assert.Equal(0, delta.Count(FindingChangeKind.Resolved));
            Assert.True(delta.Count(FindingChangeKind.NotRechecked) > 0);
            Assert.False(delta.NothingChanged);
            Assert.False(delta.SawEverything);
        }

        /// <summary>
        /// Un constat non revérifié porte la raison de son statut : sans elle, le technicien ne
        /// sait pas s'il doit relancer une analyse complète ou passer à autre chose.
        /// </summary>
        [Fact]
        public void Un_constat_non_reverifie_dit_pourquoi()
        {
            var delta = SnapshotComparer.Compare(Analyze(Fixtures.SlowMachine()), Analyze(Fixtures.Empty()));

            foreach (var change in delta.Findings.Where(f => f.Kind == FindingChangeKind.NotRechecked))
            {
                Assert.False(string.IsNullOrWhiteSpace(change.Note));
                Assert.Contains(change.Finding.RuleId, change.Note!);
            }
        }

        /// <summary>
        /// Et l'inverse : quand la règle a bien été rejouée, le constat disparu est une réussite,
        /// et elle doit être annoncée comme telle.
        /// </summary>
        [Fact]
        public void Un_constat_dont_la_regle_a_conclu_est_resolu()
        {
            var before = Analyze(Fixtures.Build(b =>
            {
                b.Windows11();
                Common(b);
                b.Volume("C:", totalGb: 500, freeGb: 8, isSystem: true);
            }));

            var after = Analyze(Fixtures.Build(b =>
            {
                b.Windows11();
                Common(b);
                b.Volume("C:", totalGb: 500, freeGb: 260, isSystem: true);
            }));

            var delta = SnapshotComparer.Compare(before, after);

            Assert.Contains(delta.Findings, f =>
                f.Kind == FindingChangeKind.Resolved && f.Finding.Category == DiagnosticCategory.Storage);
            Assert.Equal(0, delta.Count(FindingChangeKind.NotRechecked));

            var space = Assert.Single(delta.Measures.Where(m => m.Label.StartsWith("Espace libre", StringComparison.Ordinal)));
            Assert.Equal(ChangeDirection.Improved, space.Direction);
        }

        /// <summary>
        /// Deux scores calculés avec des barèmes différents mesurent autant le réglage des seuils
        /// que l'état de la machine : ils ne se soustraient pas.
        /// </summary>
        [Fact]
        public void Deux_baremes_differents_interdisent_la_soustraction_des_scores()
        {
            var snapshot = Fixtures.SlowMachine();
            var before = new AnalysisEngine().Analyze(snapshot);
            var after = new AnalysisEngine().Analyze(snapshot, new DiagnosticProfile
            {
                Version = "atelier-2026",
                Limits = new Thresholds { SystemVolumeFreePercentWarning = 60 },
            });

            var delta = SnapshotComparer.Compare(before, after);

            Assert.False(delta.ScoreComparable);
            Assert.Null(delta.ScoreDelta);
            Assert.Contains(delta.Caveats, c => c.Contains("seuils"));

            // Les deux chiffres restent affichables : c'est leur différence qui n'a pas de sens.
            Assert.NotNull(delta.Before.Score);
            Assert.NotNull(delta.After.Score);
        }

        [Fact]
        public void Un_mode_d_analyse_different_est_signale()
        {
            var before = WithMode(Analyze(Fixtures.Healthy()), RunMode.Full);
            var after = WithMode(Analyze(Fixtures.Healthy()), RunMode.Quick);

            var delta = SnapshotComparer.Compare(before, after);

            Assert.Contains(delta.Caveats, c => c.Contains("mode"));
        }

        // ============================================================ identité

        [Fact]
        public void Sans_empreinte_ni_numero_de_serie_l_identite_n_est_pas_affirmee()
        {
            var delta = SnapshotComparer.Compare(Analyze(Fixtures.Healthy()), Analyze(Fixtures.Healthy()));

            // Les cas de référence ne portent ni empreinte ni numéro de série ni nom de poste :
            // la question doit rester sans réponse plutôt que de recevoir un « oui » par défaut.
            Assert.False(delta.SameMachine.IsReliable);
            Assert.False(string.IsNullOrWhiteSpace(delta.SameMachine.Reason));
        }

        [Fact]
        public void Deux_empreintes_identiques_valent_preuve()
        {
            var before = WithMachine(Analyze(Fixtures.Healthy()), "PC-CLIENT", "ABC123");
            var after = WithMachine(Analyze(Fixtures.Healthy()), "PC-CLIENT", "ABC123");

            var delta = SnapshotComparer.Compare(before, after);

            Assert.True(delta.SameMachine.IsReliable);
            Assert.True(delta.SameMachine.Value);
        }

        /// <summary>
        /// Le nom du poste ne prouve rien : deux machines préparées depuis la même image le
        /// partagent. Le rapprochement reste donc partiel, avec sa réserve.
        /// </summary>
        [Fact]
        public void Le_nom_du_poste_seul_ne_vaut_qu_un_rapprochement_partiel()
        {
            var before = WithMachine(Analyze(Fixtures.Healthy()), "PC-BUREAU", fingerprint: null);
            var after = WithMachine(Analyze(Fixtures.Healthy()), "PC-BUREAU", fingerprint: null);

            var delta = SnapshotComparer.Compare(before, after);

            Assert.Equal(Availability.Partial, delta.SameMachine.Availability);
            Assert.True(delta.SameMachine.Value);
            Assert.Contains("image", delta.SameMachine.Reason);
        }

        [Fact]
        public void Deux_empreintes_differentes_disent_non()
        {
            var before = WithMachine(Analyze(Fixtures.Healthy()), "PC-A", "AAA");
            var after = WithMachine(Analyze(Fixtures.Healthy()), "PC-B", "BBB");

            var delta = SnapshotComparer.Compare(before, after);

            Assert.True(delta.SameMachine.IsReliable);
            Assert.False(delta.SameMachine.Value);
        }

        // ============================================================ empreinte

        /// <summary>
        /// L'empreinte doit survivre à ce qui change dans la vie d'une machine (un disque
        /// remplacé, un adaptateur réseau retiré) sinon l'historique se coupe en deux le jour
        /// même où l'on voudrait comparer un avant et un après.
        /// </summary>
        [Fact]
        public void Un_disque_remplace_ne_change_pas_l_empreinte()
        {
            var first = Identity(uuid: "4C4C4544-0037-3010-8043-B4C04F503732", boardSerial: null, diskSerial: "WD-1234");
            var second = Identity(uuid: "4C4C4544-0037-3010-8043-B4C04F503732", boardSerial: null, diskSerial: "SEAGATE-9999");

            Assert.True(first.Fingerprint.IsReliable);
            Assert.Equal(first.Fingerprint.Value, second.Fingerprint.Value);
        }

        [Fact]
        public void Deux_machines_distinctes_ont_deux_empreintes_distinctes()
        {
            var first = Identity(uuid: "4C4C4544-0037-3010-8043-B4C04F503732", boardSerial: null, diskSerial: null);
            var second = Identity(uuid: "4C4C4544-0037-3010-8043-000000000000", boardSerial: null, diskSerial: null);

            Assert.NotEqual(first.Fingerprint.Value, second.Fingerprint.Value);
        }

        /// <summary>
        /// L'empreinte ne porte pas le numéro de série lui-même : le diagnostic archivé permet de
        /// rapprocher deux passages sans reporter une seconde fois l'identifiant de la machine.
        /// </summary>
        [Fact]
        public void L_empreinte_ne_contient_pas_ce_qui_l_a_produite()
        {
            var identity = Identity(uuid: null, boardSerial: "PC0A2B3C4D", diskSerial: null);

            Assert.True(identity.Fingerprint.IsReliable);
            Assert.DoesNotContain("PC0A2B3C4D", identity.Fingerprint.Value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Des cartes entières partagent le même identifiant SMBIOS : le retenir rapprocherait
        /// deux machines différentes sous une seule empreinte.
        /// </summary>
        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
        [InlineData("03000200-0400-0500-0006-000700080009")]
        public void Un_identifiant_partage_par_tout_un_lot_est_refuse(string uuid)
        {
            var identity = Identity(uuid: uuid, boardSerial: null, diskSerial: null);

            Assert.False(identity.Fingerprint.HasValue);
            Assert.False(string.IsNullOrWhiteSpace(identity.Fingerprint.Reason));
        }

        /// <summary>
        /// Sur le seul numéro de série du disque, l'empreinte existe mais reste partielle : elle
        /// ne survivrait pas à un remplacement.
        /// </summary>
        [Fact]
        public void Une_empreinte_tiree_du_seul_disque_reste_partielle()
        {
            var identity = Identity(uuid: null, boardSerial: null, diskSerial: "WD-1234");

            Assert.Equal(Availability.Partial, identity.Fingerprint.Availability);
            Assert.Contains("disque", identity.Fingerprint.Reason);
        }

        [Fact]
        public void Sans_aucune_source_l_empreinte_est_absente_et_motivee()
        {
            var identity = Identity(uuid: null, boardSerial: null, diskSerial: null);

            Assert.False(identity.Fingerprint.HasValue);
            Assert.Contains("nom", identity.Fingerprint.Reason);
        }

        private static MachineIdentity Identity(string? uuid, string? boardSerial, string? diskSerial)
        {
            var hardware = new HardwareSnapshot
            {
                Motherboard = new MotherboardInfo
                {
                    SystemUuid = Text(uuid),
                    SerialNumber = Text(boardSerial),
                    SystemManufacturer = Text("Constructeur de test"),
                    SystemModel = Text("Modèle de test"),
                },
            };

            var storage = new StorageSnapshot
            {
                PhysicalDisks = new[]
                {
                    new PhysicalDiskInfo
                    {
                        Index = 0,
                        IsSystemDisk = Measured.Ok(true, DataSource.Wmi),
                        SerialNumber = Text(diskSerial),
                    },
                },
            };

            return MachineFingerprint.Describe("PC-TEST", @"PC-TEST\essai", hardware, storage);
        }

        private static Measured<string> Text(string? value)
            => value == null ? Measured.Missing<string>("Non renseigné.") : Measured.Ok(value, DataSource.Wmi);

        // ============================================================ mesures

        /// <summary>
        /// Une mesure qu'on ne sait plus lire n'est ni une amélioration ni une dégradation.
        /// </summary>
        [Fact]
        public void Une_mesure_perdue_entre_les_deux_n_est_pas_une_evolution()
        {
            var before = Analyze(Fixtures.Build(b =>
            {
                b.Windows11();
                Common(b);
                b.Volume("C:", totalGb: 500, freeGb: 200, isSystem: true);
                b.Thermal(58);
                b.Performance();
            }));

            var after = Analyze(Fixtures.Build(b =>
            {
                b.Windows11();
                Common(b);
                b.Volume("C:", totalGb: 500, freeGb: 200, isSystem: true);
            }));

            var delta = SnapshotComparer.Compare(before, after);

            foreach (var measure in delta.Measures.Where(m => !m.Before.IsReliable || !m.After.IsReliable))
            {
                Assert.Equal(ChangeDirection.Unknown, measure.Direction);
                Assert.Null(measure.Delta);
                Assert.False(string.IsNullOrWhiteSpace(measure.Note));
            }
        }

        /// <summary>
        /// Un écart en deçà de la marge est du bruit de mesure, pas une évolution : deux relevés
        /// de la même machine ne tombent jamais sur le même chiffre.
        /// </summary>
        [Fact]
        public void Un_ecart_dans_la_marge_ne_devient_pas_un_diagnostic()
        {
            var before = Analyze(Fixtures.Build(b => { b.Windows11(); Common(b); b.Thermal(58); }));
            var after = Analyze(Fixtures.Build(b => { b.Windows11(); Common(b); b.Thermal(60); }));

            var delta = SnapshotComparer.Compare(before, after);
            var temperature = delta.Measures.FirstOrDefault(m => m.Label.StartsWith("Température", StringComparison.Ordinal));

            if (temperature != null) Assert.Equal(ChangeDirection.Unchanged, temperature.Direction);
        }

        /// <summary>
        /// Une liste vide ne vaut pas zéro : sans cette distinction, une sonde qui n'a pas tourné
        /// des deux côtés produirait un rassurant « aucun programme au démarrage, inchangé ».
        /// </summary>
        [Fact]
        public void Une_liste_non_collectee_ne_vaut_pas_zero()
        {
            var delta = SnapshotComparer.Compare(Analyze(Fixtures.Empty()), Analyze(Fixtures.Empty()));

            Assert.DoesNotContain(delta.Measures, m =>
                m.Label.StartsWith("Programmes lancés", StringComparison.Ordinal) && m.IsComparable);
        }

        /// <summary>
        /// Comparer une machine à elle-même : la seule situation où « rien n'a changé » peut être
        /// affirmé, et il faut qu'elle le soit.
        /// </summary>
        [Fact]
        public void Une_machine_comparee_a_elle_meme_ne_montre_aucun_changement()
        {
            var snapshot = Analyze(Fixtures.SlowMachine());

            var delta = SnapshotComparer.Compare(snapshot, snapshot);

            Assert.True(delta.NothingChanged);
            Assert.True(delta.SawEverything);
            Assert.Equal(0, delta.Count(FindingChangeKind.Resolved));
            Assert.Equal(0, delta.Count(FindingChangeKind.Appeared));
            Assert.Equal(0, delta.ScoreDelta);
        }

        [Fact]
        public void Un_constat_nouveau_est_annonce_comme_tel()
        {
            var delta = SnapshotComparer.Compare(Analyze(Fixtures.Healthy()), Analyze(Fixtures.DyingDisk()));

            Assert.Contains(delta.Findings, f =>
                f.Kind == FindingChangeKind.Appeared && f.Finding.Category == DiagnosticCategory.Storage);
        }

        // ============================================================ historique

        [Fact]
        public void Un_diagnostic_archive_se_relit_entierement()
        {
            using var folder = new TempFolder();
            var store = new HistoryStore(folder.Path);
            var snapshot = Analyze(Fixtures.SlowMachine());

            var entry = store.Archive(snapshot);
            var listed = Assert.Single(store.List());

            Assert.True(listed.Readable);
            Assert.Equal(snapshot.Metadata.SnapshotId, listed.SnapshotId);
            Assert.Equal(snapshot.Score!.Global, listed.Score);
            Assert.Equal(snapshot.Findings.Count, listed.FindingCount);
            Assert.True(listed.SizeBytes > 0);

            var reloaded = store.Load(entry);
            Assert.Equal(snapshot.Findings.Count, reloaded.Findings.Count);
            Assert.Equal(snapshot.Score.Global, reloaded.Score!.Global);
        }

        /// <summary>
        /// L'en-tête est lu sans reconstruire le diagnostic entier : ce test vérifie que ce
        /// raccourci rend exactement ce que rend la lecture complète.
        /// </summary>
        [Fact]
        public void L_entete_lu_en_survol_dit_la_meme_chose_que_le_fichier_entier()
        {
            using var folder = new TempFolder();
            var store = new HistoryStore(folder.Path);

            foreach (var fixture in Fixtures.All()) store.Archive(Analyze(fixture.Snapshot));

            foreach (var entry in store.List())
            {
                var full = store.Load(entry);
                Assert.Equal(full.Metadata.SnapshotId, entry.SnapshotId);
                Assert.Equal(full.Findings.Count, entry.FindingCount);
                Assert.Equal(full.Score?.Global, entry.Score);
            }
        }

        /// <summary>
        /// Un fichier illisible reste listé, avec son problème : le masquer ferait croire à un
        /// archivage qui n'a pas eu lieu.
        /// </summary>
        [Fact]
        public void Un_fichier_illisible_est_montre_et_non_masque()
        {
            using var folder = new TempFolder();
            var store = new HistoryStore(folder.Path);
            store.Archive(Analyze(Fixtures.Healthy()));
            File.WriteAllText(Path.Combine(folder.Path, "2026-01-02-030405-PC-abcdef12.json"), "{ ceci n'est pas");

            var entries = store.List();

            Assert.Equal(2, entries.Count);
            var broken = Assert.Single(entries.Where(e => !e.Readable));
            Assert.False(string.IsNullOrWhiteSpace(broken.Problem));
        }

        /// <summary>
        /// Rien n'est supprimé sans demande explicite : la suppression ne touche que le fichier
        /// désigné, et l'historique contient des données personnelles du client.
        /// </summary>
        [Fact]
        public void La_suppression_ne_touche_que_le_diagnostic_designe()
        {
            using var folder = new TempFolder();
            var store = new HistoryStore(folder.Path);
            var first = store.Archive(Analyze(Fixtures.Healthy()));
            store.Archive(Analyze(Fixtures.DyingDisk()));

            store.Delete(first);

            var remaining = Assert.Single(store.List());
            Assert.NotEqual(first.SnapshotId, remaining.SnapshotId);
            Assert.False(File.Exists(first.FilePath));
        }

        /// <summary>
        /// Un nom de poste avec accents, espaces ou apostrophe ne doit pas faire échouer
        /// l'archivage : un diagnostic perdu pour un nom de fichier serait absurde.
        /// </summary>
        [Theory]
        [InlineData("PC de Léa", "PCdeLa")]
        [InlineData("BUREAU-01", "BUREAU-01")]
        [InlineData("", "machine")]
        [InlineData("éàü", "machine")]
        public void Un_nom_de_machine_hostile_ne_bloque_pas_l_archivage(string name, string expected)
        {
            Assert.Equal(expected, HistoryStore.Sanitize(name));
        }

        [Fact]
        public void Deux_diagnostics_de_la_meme_seconde_ne_s_ecrasent_pas()
        {
            using var folder = new TempFolder();
            var store = new HistoryStore(folder.Path);
            var moment = DateTimeOffset.Now;

            store.Archive(WithMoment(Analyze(Fixtures.Healthy()), moment));
            store.Archive(WithMoment(Analyze(Fixtures.DyingDisk()), moment));

            Assert.Equal(2, store.List().Count);
        }

        // ============================================================ outillage

        private static SystemSnapshot Analyze(SystemSnapshot snapshot) => new AnalysisEngine().Analyze(snapshot);

        /// <summary>Décor minimal commun : ce qui doit rester identique entre deux diagnostics comparés.</summary>
        private static void Common(Fixtures.Builder b)
        {
            b.Cpu(cores: 8, usage: 15);
            b.Memory(totalGb: 16, usagePercent: 45, modules: 2, slots: 4);
            b.Motherboard(uefi: true, secureBoot: true, tpm: true, biosAgeMonths: 12, chassis: "Tour");
            b.Disk(0, StorageMediaType.Ssd, SmartOverallStatus.Ok, isSystem: true);
            b.WindowsInstall(activated: true, rebootPending: false, uptimeDays: 2, restoreEnabled: true);
            b.Updates(daysSince: 5, serviceRunning: true);
            b.Events(critical: 0, shutdowns: 0, diskErrors: 0, bsods: 0);
            b.Startup(count: 4, orphans: 0);
            b.Network(connected: true, gatewayReachable: true, dnsOk: true, httpsOk: true);
        }

        private static SystemSnapshot WithMode(SystemSnapshot snapshot, RunMode mode)
            => Rebuild(snapshot, new SnapshotMetadata
            {
                SchemaVersion = snapshot.Metadata.SchemaVersion,
                SnapshotId = snapshot.Metadata.SnapshotId,
                CreatedAt = snapshot.Metadata.CreatedAt,
                ToolVersion = snapshot.Metadata.ToolVersion,
                RunMode = mode,
            }, snapshot.Machine);

        private static SystemSnapshot WithMoment(SystemSnapshot snapshot, DateTimeOffset moment)
            => Rebuild(snapshot, new SnapshotMetadata
            {
                SchemaVersion = snapshot.Metadata.SchemaVersion,
                SnapshotId = snapshot.Metadata.SnapshotId,
                CreatedAt = moment,
                ToolVersion = snapshot.Metadata.ToolVersion,
                RunMode = snapshot.Metadata.RunMode,
            }, snapshot.Machine);

        private static SystemSnapshot WithMachine(SystemSnapshot snapshot, string name, string? fingerprint)
            => Rebuild(snapshot, snapshot.Metadata, new MachineIdentity
            {
                MachineName = name,
                UserName = "test",
                Fingerprint = fingerprint == null
                    ? Measured.Missing<string>("Empreinte non relevée.")
                    : Measured.Ok(fingerprint, DataSource.Inferred),
            });

        /// <summary>
        /// Reconstruction d'un instantané pour les besoins des tests : le modèle est immuable, et
        /// il le reste. Passe par la sérialisation plutôt que par une recopie de sections, qui
        /// perdrait en silence toute section ajoutée plus tard.
        /// </summary>
        private static SystemSnapshot Rebuild(
            SystemSnapshot snapshot, SnapshotMetadata metadata, MachineIdentity machine)
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(SnapshotSerializer.Serialize(snapshot));
            var settings = SnapshotSerializer.CreateSettings();

            json["metadata"] = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(metadata, settings));
            json["machine"] = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(machine, settings));

            return SnapshotSerializer.Deserialize(json.ToString());
        }

        private sealed class TempFolder : IDisposable
        {
            public TempFolder()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ldi12-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                System.IO.Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try { System.IO.Directory.Delete(Path, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
