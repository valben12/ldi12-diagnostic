using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using LDI12.Reports.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LDI12.Tests
{
    public class SerializationTests
    {
        private static SystemSnapshot Sample() => new SystemSnapshot
        {
            Metadata = new SnapshotMetadata { ToolVersion = "0.1.0", RunMode = RunMode.Full, DurationMs = 1234 },
            Machine = new MachineIdentity
            {
                MachineName = "POSTE-CLIENT",
                UserName = "POSTE-CLIENT\\marie",
                Manufacturer = Measured.Ok("Dell Inc.", DataSource.Wmi),
                SerialNumber = Measured.NeedsElevation<string>("lecture du numéro de série du châssis"),
                Model = Measured.Missing<string>("Information absente du BIOS de cette machine."),
            },
            Platform = new PlatformSnapshot
            {
                Windows = new WindowsProfile
                {
                    Family = WindowsFamily.Windows7,
                    Version = new Version(6, 1, 7601),
                    Build = 7601,
                    ServicePackMajor = 1,
                    EditionId = "Professional",
                    Level = CompatibilityLevel.Minimal,
                    LevelReason = "Windows 7 SP1.",
                    NativeArchitecture = ProcessorArchitecture.X64,
                },
                Elevation = ElevationState.NotElevated,
                Features = new[]
                {
                    new FeatureState
                    {
                        Id = FeatureId.SmartNvme,
                        DisplayName = "Lecture SMART des disques NVMe",
                        Availability = Availability.Unavailable,
                        Reason = "Requiert Windows 10 1607 (build 14393). Build actuel : 7601.",
                    },
                },
            },
            ModuleReports = new[]
            {
                new ModuleReport
                {
                    ProbeId = "STO-DISKS",
                    DisplayName = "Disques physiques",
                    Category = DiagnosticCategory.Storage,
                    Status = ProbeStatus.Partial,
                    DurationMs = 412,
                    Message = "2 disques sur 3 ont répondu.",
                },
            },
        };

        [Fact]
        public void Le_diagnostic_survit_a_un_aller_retour_json()
        {
            var original = Sample();

            var restored = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(original));

            Assert.Equal(SystemSnapshot.CurrentSchemaVersion, restored.Metadata.SchemaVersion);
            Assert.Equal("POSTE-CLIENT", restored.Machine.MachineName);
            Assert.Equal(WindowsFamily.Windows7, restored.Platform.Windows.Family);
            Assert.Equal(7601, restored.Platform.Windows.Build);
            Assert.Equal(new Version(6, 1, 7601), restored.Platform.Windows.Version);
            Assert.Single(restored.ModuleReports);
            Assert.Equal(ProbeStatus.Partial, restored.ModuleReports[0].Status);
        }

        [Fact]
        public void L_etat_et_la_raison_d_une_mesure_absente_survivent_a_l_aller_retour()
        {
            // C'est la propriété qui donne sa valeur au rapport archivé : six mois plus tard, on
            // doit encore savoir POURQUOI une information manquait.
            var restored = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(Sample()));

            Assert.Equal(Availability.Unavailable, restored.Machine.Model.Availability);
            Assert.Contains("BIOS", restored.Machine.Model.Reason!);

            Assert.Equal(Availability.RequiresElevation, restored.Machine.SerialNumber.Availability);
            Assert.Contains("administrateur", restored.Machine.SerialNumber.Reason!);

            Assert.True(restored.Machine.Manufacturer.IsReliable);
            Assert.Equal("Dell Inc.", restored.Machine.Manufacturer.Value);
        }

        [Fact]
        public void Une_mesure_disponible_s_ecrit_sous_sa_forme_courte()
        {
            var json = SnapshotSerializer.Serialize(Sample());

            // Lisibilité du rapport : la valeur nue quand tout va bien, l'objet justifié sinon.
            Assert.Contains("\"manufacturer\": \"Dell Inc.\"", json);
            Assert.Contains("\"availability\": \"Unavailable\"", json);
            Assert.Contains("\"availability\": \"RequiresElevation\"", json);
        }

        [Fact]
        public void Les_enumerations_sont_ecrites_en_clair()
        {
            var json = SnapshotSerializer.Serialize(Sample());

            // Un rapport doit rester lisible sans l'application qui l'a produit.
            Assert.Contains("\"family\": \"Windows7\"", json);
            Assert.Contains("\"status\": \"Partial\"", json);
            Assert.DoesNotContain("\"family\": 2", json);
        }

        /// <summary>
        /// Un diagnostic archivé par une version plus ancienne du logiciel se relit.
        /// </summary>
        /// <remarks>
        /// C'est la contrepartie du refus d'un schéma plus récent, et le cas se produit à chaque
        /// livraison : l'inventaire logiciel est arrivé en phase 9, et les diagnostics archivés
        /// avant elle n'en portent pas la moindre trace. Le comparatif avant / après n'a d'intérêt
        /// que si l'archive de la semaine dernière se relit avec le logiciel d'aujourd'hui : un
        /// champ ajouté depuis doit donc ressortir vide, et non faire échouer la lecture.
        /// </remarks>
        [Fact]
        public void Un_diagnostic_archive_avant_l_ajout_d_un_champ_se_relit_encore()
        {
            var root = JObject.Parse(SnapshotSerializer.Serialize(Sample()));
            var windows = (JObject)root["windows"]!;

            // Ce que ne contenait aucun diagnostic antérieur à la phase 9.
            windows.Remove("software");
            Assert.Null(windows["software"]);

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
            System.IO.File.WriteAllText(path, root.ToString());

            try
            {
                var reloaded = SnapshotSerializer.Load(path);

                Assert.NotNull(reloaded.Windows.Software);
                Assert.Empty(reloaded.Windows.Software.Programs);

                // Et l'analyse de cette archive ne conclut rien sur ce qu'elle n'a pas vu.
                var findings = new LDI12.Engine.AnalysisEngine().Analyze(reloaded).Findings;
                Assert.DoesNotContain(findings, finding => finding.RuleId.StartsWith("SFT-", StringComparison.Ordinal));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void Une_version_de_schema_plus_recente_est_refusee_avec_une_explication()
        {
            var json = SnapshotSerializer.Serialize(Sample())
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
            System.IO.File.WriteAllText(path, json);

            try
            {
                var ex = Assert.Throws<System.IO.InvalidDataException>(() => SnapshotSerializer.Load(path));
                Assert.Contains("version plus récente", ex.Message);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
