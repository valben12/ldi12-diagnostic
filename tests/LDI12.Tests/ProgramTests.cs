using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.Collectors.Programs;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Programs;
using Microsoft.Win32;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>L'écran Programmes : chemins lancés, inventaire du démarrage, bascules réversibles, fin de processus.</summary>
    public partial class NativeRepairTests
    {
        // ============================================================ chemin lancé par une commande

        [Theory]
        [InlineData("\"C:\\Program Files\\Spotify\\Spotify.exe\" --autostart", @"C:\Program Files\Spotify\Spotify.exe")]
        [InlineData(@"C:\Program Files\Realtek\Audio\HDA\RtkNGUI64.exe -s", @"C:\Program Files\Realtek\Audio\HDA\RtkNGUI64.exe")]
        [InlineData(@"\SystemRoot\System32\drivers\nvlddmkm.sys", @"C:\Windows\System32\drivers\nvlddmkm.sys")]
        [InlineData(@"System32\DRIVERS\igdkmd64.sys", @"C:\Windows\System32\DRIVERS\igdkmd64.sys")]
        [InlineData(@"\??\C:\Program Files\Vendor\filter.sys", @"C:\Program Files\Vendor\filter.sys")]
        [InlineData("rundll32.exe \"C:\\Program Files\\Vendor\\helper.dll\",Start", @"C:\Program Files\Vendor\helper.dll")]
        public void La_commande_designe_le_bon_fichier(string command, string expected)
            => Assert.Equal(expected, ImagePaths.Extract(command, @"C:\Windows", _ => false));

        [Fact]
        public void Un_nom_seul_se_cherche_dans_system32_avec_son_extension()
            => Assert.Equal(@"C:\Windows\System32\ctfmon.exe",
                ImagePaths.Extract("ctfmon", @"C:\Windows", path => path == @"C:\Windows\System32\ctfmon.exe"));

        // ============================================================ inventaire

        [Fact]
        public void L_inventaire_lit_l_etat_du_gestionnaire_des_taches_et_les_services_tiers()
        {
            var registry = new TableRegistry();
            registry.Set(RegistryHive.CurrentUser, AutorunsCollector.RunKey, "Spotify", "\"C:\\Users\\M\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart");
            registry.Set(RegistryHive.CurrentUser, AutorunsCollector.RunKey, "OneDrive", "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background");
            registry.Binary(RegistryHive.CurrentUser, AutorunsCollector.Approved + "Run", "Spotify", new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 });
            registry.Service("AdobeARMservice", type: 0x10, start: 2, image: "\"C:\\Program Files (x86)\\Common Files\\Adobe\\ARM\\1.0\\armsvc.exe\"");
            registry.Service("wuauserv", type: 0x20, start: 3, image: @"C:\Windows\system32\svchost.exe -k netsvcs");
            registry.Service("nvlddmkm", type: 0x1, start: 3, image: @"\SystemRoot\System32\drivers\nvlddmkm.sys");
            registry.Service("Vendorflt", type: 0x2, start: 0, image: @"System32\drivers\vendorflt.sys");
            registry.Service("Oldsvc", type: 0x10, start: 4, image: @"C:\Old\old.exe", previous: 2);
            registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe", "Debugger", @"C:\Windows\System32\cmd.exe");

            var entries = new AutorunsCollector(registry, 19045).Collect(CancellationToken.None);

            var spotify = Assert.Single(entries, e => e.Name == "Spotify");
            Assert.False(spotify.Enabled);
            Assert.Equal(@"C:\Users\M\AppData\Roaming\Spotify\Spotify.exe", spotify.ImagePath);
            Assert.True(Assert.Single(entries, e => e.Name == "OneDrive").Enabled);

            Assert.Contains(entries, e => e.Kind == AutorunKind.Service && e.Key == "AdobeARMservice" && e.Enabled);
            Assert.DoesNotContain(entries, e => e.Key == "wuauserv");              // démarrage manuel : ne démarre pas tout seul
            Assert.DoesNotContain(entries, e => e.Key == "nvlddmkm");              // pilote à la demande
            Assert.Contains(entries, e => e.Kind == AutorunKind.Driver && e.Key == "Vendorflt" && e.Detail == "démarrage du noyau");
            Assert.Contains(entries, e => e.Key == "Oldsvc" && !e.Enabled);        // désactivé par ce logiciel : il sait le rétablir

            var hijack = Assert.Single(entries, e => e.Kind == AutorunKind.ImageHijack);
            Assert.Equal(AutorunCategory.Sensitive, hijack.Category);
            Assert.Equal(@"C:\Windows\System32\cmd.exe", hijack.ImagePath);
        }

        // ============================================================ bascules

        [Fact]
        public async Task Desactiver_une_entree_run_ecrit_l_etat_du_gestionnaire_des_taches()
        {
            var control = new FakeControl();
            var context = Context(control, new TableRegistry(),
                (AutorunToggleAction.KindParameter, "RunKey"), (AutorunToggleAction.KeyParameter, AutorunToggleAction.RunKey + "|Spotify"),
                (AutorunToggleAction.HiveParameter, "HKCU"), (AutorunToggleAction.EnableParameter, "0"));
            var action = new AutorunToggleAction(elevated: false);

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
            var written = Assert.Single(control.Binary);
            Assert.Equal(RegistryHive.CurrentUser, written.Hive);
            Assert.Equal(AutorunToggleAction.Approved + "Run", written.Key);
            Assert.Equal(3, written.Data[0]);
        }

        [Fact]
        public async Task Desactiver_un_service_note_son_demarrage_d_origine_pour_le_rendre()
        {
            var control = new FakeControl();
            var registry = new TableRegistry();
            registry.Service("AdobeARMservice", type: 0x10, start: 2, image: @"C:\x\armsvc.exe");
            var action = new AutorunToggleAction(elevated: true);

            var off = Context(control, registry, (AutorunToggleAction.KindParameter, "Service"), (AutorunToggleAction.KeyParameter, "AdobeARMservice"), (AutorunToggleAction.EnableParameter, "0"));
            await action.ExecuteAsync(off, await action.PreviewAsync(off, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(new[] { "dword " + AutorunToggleAction.PreviousStart, "start-type AdobeARMservice 4" }, control.Calls);
        }

        [Fact]
        public async Task Un_pilote_charge_avec_le_noyau_ne_se_desactive_pas()
        {
            var registry = new TableRegistry();
            registry.Service("Vendorflt", type: 0x2, start: 0, image: @"System32\drivers\vendorflt.sys");
            var context = Context(new FakeControl(), registry, (AutorunToggleAction.KindParameter, "Driver"), (AutorunToggleAction.KeyParameter, "Vendorflt"), (AutorunToggleAction.EnableParameter, "0"));

            var preview = await new AutorunToggleAction(elevated: true).PreviewAsync(context, CancellationToken.None);

            Assert.False(preview.CanExecute);
            Assert.Contains("noyau", preview.Summary);
        }

        // ============================================================ fin de processus

        [Fact]
        public async Task Un_processus_vital_n_est_jamais_termine()
        {
            var context = Context(new FakeControl(), (ProcessEndAction.IdsParameter, "620"), (ProcessEndAction.NamesParameter, "csrss.exe"));
            var preview = await new ProcessEndAction(elevated: false).PreviewAsync(context, CancellationToken.None);

            Assert.False(preview.CanExecute);
        }

        [Fact]
        public async Task Une_arborescence_se_termine_des_enfants_vers_le_parent()
        {
            var control = new FakeControl();
            var context = Context(control, (ProcessEndAction.IdsParameter, "7200,7340,7344"), (ProcessEndAction.NamesParameter, "chrome.exe|chrome.exe|chrome.exe"));
            var action = new ProcessEndAction(elevated: false);

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal(new[] { "end 7344", "end 7340", "end 7200" }, control.Calls);
        }

        [Fact]
        public void Les_operations_de_l_ecran_programmes_n_encombrent_pas_les_reparations()
        {
            foreach (var id in new[] { ActionIds.AutorunToggle, ActionIds.AutorunToggleAdmin, ActionIds.ProcessEnd, ActionIds.ProcessEndAdmin })
            {
                Assert.NotNull(ActionCatalog.Find(id));
                Assert.Contains(id, ActionIds.OwnedByScreens);
            }
        }

        /// <summary>Un registre en mémoire, rempli par le test.</summary>
        private sealed class TableRegistry : IRegistryGateway
        {
            // Clés comparées sans tenir compte de la casse, comme dans Windows, mais rendues avec leur nom d'origine.
            private readonly Dictionary<string, (RegistryHive Hive, string Path, Dictionary<string, object> Values)> _keys =
                new Dictionary<string, (RegistryHive, string, Dictionary<string, object>)>(StringComparer.OrdinalIgnoreCase);

            private Dictionary<string, object> Key(RegistryHive hive, string key)
            {
                var id = hive + "|" + key;
                if (!_keys.TryGetValue(id, out var entry)) _keys[id] = entry = (hive, key, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
                return entry.Values;
            }

            public void Set(RegistryHive hive, string key, string name, object value) => Key(hive, key)[name] = value;

            public void Binary(RegistryHive hive, string key, string name, byte[] value) => Key(hive, key)[name] = value;

            public void Service(string name, int type, int start, string image, int? previous = null)
            {
                var key = AutorunsCollector.ServicesKey + "\\" + name;
                Set(RegistryHive.LocalMachine, key, "Type", type);
                Set(RegistryHive.LocalMachine, key, "Start", start);
                Set(RegistryHive.LocalMachine, key, "ImagePath", image);
                if (previous.HasValue) Set(RegistryHive.LocalMachine, key, AutorunsCollector.PreviousStartValue, previous.Value);
            }

            private object? Value(RegistryHive hive, string key, string name)
                => _keys.TryGetValue(hive + "|" + key, out var entry) && entry.Values.TryGetValue(name, out var value) ? value : null;

            public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => Value(hive, subKey, valueName) as string;
            public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => Value(hive, subKey, valueName) as int?;
            public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => Value(hive, subKey, valueName) as byte[];
            public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => _keys.ContainsKey(hive + "|" + subKey);

            public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
            {
                var prefix = subKey + "\\";
                return _keys.Values.Where(k => k.Hive == hive && k.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(k => k.Path.Substring(prefix.Length).Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => _keys.TryGetValue(hive + "|" + subKey, out var entry) ? entry.Values.Keys.ToList() : (IReadOnlyList<string>)Array.Empty<string>();
        }
    }
}
