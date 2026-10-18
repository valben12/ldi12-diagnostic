using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.Core.Disks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using Newtonsoft.Json;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les tests de l'écran Disques : journal d'autotest, carte de surface, bilan SMART.</summary>
    public sealed class DiskCheckTests
    {
        private static byte[] SmartData(byte status, byte shortMinutes, byte extendedMinutes)
        {
            var data = new byte[512];
            data[363] = status;
            data[367] = 0x01;
            data[372] = shortMinutes;
            data[373] = extendedMinutes;
            return data;
        }

        [Fact]
        public void Un_autotest_en_cours_donne_sa_part_restante()
        {
            var state = SelfTestState.FromSmartData(SmartData(0xF3, 2, 110), null);

            Assert.True(state.Supported);
            Assert.True(state.Running);
            Assert.Equal(30, state.RemainingPercent);
            Assert.Equal(2, state.ShortMinutes);
            Assert.Equal(110, state.ExtendedMinutes);
        }

        [Fact]
        public void Une_duree_de_test_long_au_dela_de_254_minutes_se_lit_sur_deux_octets()
        {
            var data = SmartData(0x00, 2, 0xFF);
            data[375] = 0x2C;
            data[376] = 0x01;

            var state = SelfTestState.FromSmartData(data, null);

            Assert.False(state.Running);
            Assert.Equal(300, state.ExtendedMinutes);
            Assert.Equal("réussi, aucune erreur", state.LastResult);
        }

        [Fact]
        public void Des_donnees_smart_tronquees_ne_sont_pas_interpretees()
            => Assert.False(SelfTestState.FromSmartData(new byte[100], null).Supported);

        [Fact]
        public void Le_journal_d_autotests_se_lit_du_plus_recent_au_plus_ancien()
        {
            var log = new byte[512];
            void Entry(int index, byte kind, byte status, int hours, uint lba)
            {
                var at = 2 + index * 24;
                log[at] = kind;
                log[at + 1] = status;
                log[at + 2] = (byte)hours;
                log[at + 3] = (byte)(hours >> 8);
                BitConverter.GetBytes(lba).CopyTo(log, at + 5);
            }
            Entry(0, 2, 0x70, 27_904, 1_245_771_264);
            Entry(1, 1, 0x00, 28_410, 0);
            log[508] = 2;

            var entries = SelfTestState.ParseLog(log);

            Assert.Equal(2, entries.Count);
            Assert.Equal("Court", entries[0].Kind);
            Assert.True(entries[0].Passed);
            Assert.Null(entries[0].FailingLba);
            Assert.Equal(28_410, entries[0].LifetimeHours);
            Assert.Equal("Long", entries[1].Kind);
            Assert.Equal("échec de lecture", entries[1].Result);
            Assert.False(entries[1].Passed);
            Assert.Equal(1_245_771_264L, entries[1].FailingLba);
        }

        [Fact]
        public void Un_journal_sans_entree_designee_est_vide()
            => Assert.Empty(SelfTestState.ParseLog(new byte[512]));

        [Fact]
        public void La_carte_de_surface_garde_la_pire_lecture_de_chaque_case()
        {
            var scan = new SurfaceScan(4);
            scan.Add(0.10, 2, false, 0);
            scan.Add(0.12, 700, false, 1);
            scan.Add(0.13, 3, false, 2);
            scan.Add(0.60, 10, true, 3);

            Assert.Equal(6, scan.Cells[0]);
            Assert.Equal(0, scan.Cells[1]);
            Assert.Equal(SurfaceScan.ErrorClass + 1, scan.Cells[2]);
            Assert.Equal(2, scan.Histogram[0]);
            Assert.Equal(1, scan.Histogram[5]);
            Assert.Equal(1, scan.Errors);
            Assert.Equal(new[] { 3L }, scan.ErrorOffsets);
            Assert.Contains("illisible", scan.Verdict());
        }

        [Fact]
        public void Un_test_rapide_remplit_chaque_case_de_la_carte()
        {
            // Le calcul de la lecture rapide sur un disque de 1 To : 4 000 échantillons répartis.
            const long Blocks = 953_869;
            const int Total = 4000;
            var scan = new SurfaceScan(4000);
            for (var step = 0; step < Total; step++)
            {
                var block = step * Blocks / Total;
                scan.Add((step + 0.5) / Total, 4, false, block);
            }

            Assert.DoesNotContain((byte)0, scan.Cells);
        }

        [Fact]
        public void Une_surface_sans_bloc_lent_est_saine()
        {
            var scan = new SurfaceScan(10);
            for (var index = 0; index < 10; index++) scan.Add(index / 10.0, 4, false, index);
            Assert.StartsWith("Surface saine", scan.Verdict());
            Assert.Equal(0, scan.SlowBlocks);
        }

        [Fact]
        public void La_carte_de_surface_survit_au_transport_json()
        {
            var scan = new SurfaceScan(8) { DiskBytes = 1000, Duration = TimeSpan.FromSeconds(3) };
            scan.Add(0.5, 200, false, 500);

            var copy = JsonConvert.DeserializeObject<SurfaceScan>(JsonConvert.SerializeObject(scan))!;

            Assert.Equal(scan.Cells, copy.Cells);
            Assert.Equal(scan.Histogram, copy.Histogram);
            Assert.Equal(TimeSpan.FromSeconds(3), copy.Duration);
        }

        private static PhysicalDiskInfo Disk(SmartData smart) => new PhysicalDiskInfo { Index = 0, Smart = smart };

        [Fact]
        public void Des_secteurs_en_attente_rendent_le_disque_defaillant()
        {
            var health = DiskHealth.Of(Disk(new SmartData
            {
                OverallStatus = Measured.Ok(SmartOverallStatus.Ok, DataSource.NativeApi),
                PendingSectors = Measured.Ok(8L, DataSource.NativeApi),
            }));

            Assert.Equal("Bad", health.Tone);
            Assert.Contains(health.Reasons, r => r.Contains("8 secteur"));
        }

        [Fact]
        public void Quelques_secteurs_realloues_et_un_cable_fatigue_mettent_le_disque_en_surveillance()
        {
            var health = DiskHealth.Of(Disk(new SmartData
            {
                OverallStatus = Measured.Ok(SmartOverallStatus.Ok, DataSource.NativeApi),
                ReallocatedSectors = Measured.Ok(24L, DataSource.NativeApi),
                Attributes = new[] { new SmartAttribute { Id = 199, Current = 200, Worst = 200, Raw = 7 } },
            }));

            Assert.Equal("Warning", health.Tone);
            Assert.Equal(2, health.Reasons.Count);
            Assert.Contains(health.Reasons, r => r.Contains("câble"));
        }

        [Fact]
        public void Un_disque_sans_smart_n_est_pas_declare_sain()
            => Assert.Equal("Unknown", DiskHealth.Of(Disk(new SmartData())).Tone);

        [Fact]
        public void Un_attribut_inconnu_garde_le_nom_du_constructeur()
        {
            Assert.Equal("Secteurs en attente", SmartCatalog.Describe(197, "Current_Pending_Sector").Name);
            Assert.Equal("Vendor_Specific", SmartCatalog.Describe(250, "Vendor_Specific").Name);
            Assert.Equal("Attribut constructeur 250", SmartCatalog.Describe(250, null).Name);
        }
    }

    public partial class NativeRepairTests
    {
        [Fact]
        public async Task L_autotest_court_est_lance_puis_son_etat_relu()
        {
            var control = new FakeControl { SelfTest = new SelfTestState { Running = true, RemainingPercent = 90 } };
            var action = new DiskSelfTestAction();
            var context = Context(control, ("disk", "1"), (DiskSelfTestAction.ModeParameter, "short"));

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal(new[] { "self-test 1 court" }, control.Calls);
            Assert.Contains("10 %", outcome.Summary);
            Assert.True(JsonConvert.DeserializeObject<SelfTestState>(outcome.RawOutput!)!.Running);
        }

        [Fact]
        public async Task Relire_l_autotest_ne_lance_rien()
        {
            var control = new FakeControl { SelfTest = new SelfTestState { LastResult = "réussi, aucune erreur" } };
            var action = new DiskSelfTestAction();
            var context = Context(control, ("disk", "0"), (DiskSelfTestAction.ModeParameter, "status"));

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Empty(control.Calls);
            Assert.Contains("réussi", outcome.Summary);
        }

        [Fact]
        public async Task Un_disque_qui_ne_repond_pas_au_smart_fait_echouer_l_autotest()
        {
            var action = new DiskSelfTestAction();
            var context = Context(new FakeControl(), ("disk", "0"));
            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);
            Assert.Equal(ActionStatus.Failed, outcome.Status);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-1")]
        [InlineData("99")]
        [InlineData("0;1")]
        public async Task Un_disque_mal_designe_est_refuse(string disk)
        {
            var preview = await new DiskSurfaceTestAction().PreviewAsync(Context(new FakeControl(), ("disk", disk)), CancellationToken.None);
            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task Le_test_de_surface_relaie_sa_carte_pendant_la_lecture()
        {
            var control = new FakeControl();
            var action = new DiskSurfaceTestAction();
            var context = Context(control, ("disk", "2"), (DiskSurfaceTestAction.FullParameter, "1"));
            ActionProgress? last = null;

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None),
                new SyncProgress<ActionProgress>(p => last = p), CancellationToken.None);

            Assert.Equal(new[] { "surface 2 complète" }, control.Calls);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.StartsWith("1 bloc(s) très lent", outcome.Summary);
            Assert.NotNull(last);
            Assert.StartsWith(DiskSurfaceTestAction.MapPrefix, last!.Detail);
            var cells = Convert.FromBase64String(last.Detail!.Substring(DiskSurfaceTestAction.MapPrefix.Length));
            Assert.Equal(6, cells[5]);
        }

        [Theory]
        [InlineData("C:\\")]
        [InlineData("C")]
        [InlineData("C: /X")]
        [InlineData("..")]
        public async Task L_optimisation_refuse_ce_qui_n_est_pas_une_lettre_de_volume(string volume)
        {
            var preview = await new DiskOptimizeAction().PreviewAsync(Context(new FakeControl(), (DiskOptimizeAction.VolumeParameter, volume)), CancellationToken.None);
            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task L_optimisation_confie_le_volume_a_l_outil_de_windows()
        {
            var action = new DiskOptimizeAction();
            var context = Context(new FakeControl(), (DiskOptimizeAction.VolumeParameter, "d:"));
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.StartsWith("D:", preview.Summary);

            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal("D: optimisé.", outcome.Summary);
        }

        /// <summary>Un IProgress qui rapporte sur-le-champ, sans contexte de synchronisation.</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public SyncProgress(Action<T> report) => _report = report;
            public void Report(T value) => _report(value);
        }
    }
}
