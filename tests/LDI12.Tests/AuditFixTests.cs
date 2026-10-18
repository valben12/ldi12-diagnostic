using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Crashes;
using Microsoft.Win32;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les défauts relevés par l'audit, chacun fixé par un test.</summary>
    public partial class NativeRepairTests
    {
        [Fact]
        public async Task Un_numero_de_processus_ecarte_n_emporte_pas_le_nom_du_suivant()
        {
            var control = new FakeControl();
            var context = Context(control, (ProcessEndAction.IdsParameter, "abc,7200"), (ProcessEndAction.NamesParameter, "faux.exe|notepad.exe"));
            var action = new ProcessEndAction(elevated: false);

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal("« notepad.exe » va être terminé.", preview.Summary);
            Assert.Equal(new[] { "end 7200" }, control.Calls);
            Assert.Equal("« notepad.exe » terminé.", outcome.Summary);
        }

        [Fact]
        public async Task Un_paquet_ecarte_n_emporte_pas_le_libelle_du_suivant()
        {
            var control = new FakeControl();
            var context = Context(control,
                (DriverPackagesDeleteAction.PackagesParameter, "nvlddmkm.inf|oem12.inf|OEM12.INF"),
                (DriverPackagesDeleteAction.LabelsParameter, "Pilote de Windows|Realtek Audio|doublon"));
            var action = new DriverPackagesDeleteAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);

            Assert.Equal(new[] { "Retirer Realtek Audio" }, preview.WillDo);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);
            Assert.Equal(new[] { "delete-package oem12.inf" }, control.Calls);
        }

        [Fact]
        public async Task Une_entree_rangee_a_part_reprend_sa_place_avant_d_etre_reactivee()
        {
            var control = new FakeControl();
            var registry = new TableRegistry();
            registry.Set(RegistryHive.CurrentUser, AutorunToggleAction.RunKey + "\\" + AutorunToggleAction.DisabledSubKey, "Spotify", @"C:\Spotify.exe");
            var context = Context(control, registry,
                (AutorunToggleAction.KindParameter, "RunKey"), (AutorunToggleAction.KeyParameter, AutorunToggleAction.RunKey + "|Spotify"),
                (AutorunToggleAction.HiveParameter, "HKCU"), (AutorunToggleAction.EnableParameter, "1"));
            var action = new AutorunToggleAction(elevated: false);

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal(new[] { "string Spotify", "delete Spotify" }, control.Calls);
            Assert.Equal(2, Assert.Single(control.Binary).Data[0]);
        }

        [Fact]
        public async Task Seules_les_cles_run_se_basculent()
        {
            var control = new FakeControl();
            var context = Context(control, new TableRegistry(),
                (AutorunToggleAction.KindParameter, "RunKey"), (AutorunToggleAction.KeyParameter, @"SYSTEM\CurrentControlSet\Control|Valeur"),
                (AutorunToggleAction.HiveParameter, "HKLM"), (AutorunToggleAction.EnableParameter, "0"));
            var action = new AutorunToggleAction(elevated: true);

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Empty(control.Calls);
            Assert.Empty(control.Binary);
        }
    }

    public sealed class CrashReaderRobustnessTests
    {
        [Fact]
        public void Un_minividage_aux_decalages_extremes_est_lu_sans_exception()
        {
            var data = new byte[0x3000];
            BitConverter.GetBytes(0x45474150u).CopyTo(data, 0);      // PAGE
            BitConverter.GetBytes(0x34365544u).CopyTo(data, 4);      // DU64
            BitConverter.GetBytes(0x116u).CopyTo(data, 0x38);
            BitConverter.GetBytes(4u).CopyTo(data, 0xF98);           // minividage
            // Une liste d'un pilote dont le nom est annoncé à 2^31 - 2 : « décalage + 4 » déborde en 32 bits.
            BitConverter.GetBytes(0x2100u).CopyTo(data, 0x2030);      // liste des pilotes
            BitConverter.GetBytes(1u).CopyTo(data, 0x2034);           // un pilote
            BitConverter.GetBytes(0x7FFFFFFEu).CopyTo(data, 0x2038);  // réserve de noms
            BitConverter.GetBytes(0x7FFFFFFEu).CopyTo(data, 0x2100);  // décalage du nom
            BitConverter.GetBytes(0x7FFFFF10u).CopyTo(data, 0x200C);  // contexte

            var dump = CrashDumpReader.Read("x.dmp", data, DateTime.UtcNow, data.Length);

            Assert.True(dump.IsReadable);
            Assert.Equal(0x116u, dump.BugCheckCode);
            Assert.Empty(dump.Modules);
        }
    }
}
