using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.Assistant;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using Microsoft.Win32;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Les opérations natives : ce qu'elles vérifient avant d'agir, et ce qu'elles demandent à Windows.</summary>
    public partial class NativeRepairTests
    {
        private const string Wifi = @"PCI\VEN_10EC&DEV_C821&SUBSYS_C82110EC\4&2F0E3F0&0&00E1";
        private const string Disk = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\5&1A2B3C&0&000000";

        [Fact]
        public async Task Un_disque_ne_se_desinstalle_jamais_d_ici()
        {
            var control = new FakeControl();
            control.Devices[Disk] = new DeviceState { InstanceId = Disk, Name = "Samsung SSD 980", Class = "DiskDrive" };

            var action = new ReinstallDeviceAction();
            var context = Context(control, ("device", Disk));
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Empty(control.Calls);
        }

        [Fact]
        public async Task Un_peripherique_en_erreur_est_reinstalle_puis_relu()
        {
            var control = new FakeControl();
            control.Devices[Wifi] = new DeviceState { InstanceId = Wifi, Name = "Realtek RTL8821CE", Class = "Net", ProblemCode = 43 };
            control.OnReinstall = id => control.Devices[id] = new DeviceState { InstanceId = id, Name = "Realtek RTL8821CE", Class = "Net" };

            var action = new ReinstallDeviceAction();
            var context = Context(control, ("device", Wifi));
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains("fonctionne", outcome.Summary);
            Assert.Equal(new[] { "reinstall " + Wifi }, control.Calls);
        }

        [Fact]
        public async Task Un_peripherique_inconnu_bloque_la_previsualisation()
        {
            var preview = await new RestartDeviceAction().PreviewAsync(Context(new FakeControl(), ("device", "PCI\\INVENTE")), CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task Reactiver_un_peripherique_qui_marche_ne_fait_rien()
        {
            var control = new FakeControl();
            control.Devices[Wifi] = new DeviceState { InstanceId = Wifi, Name = "Realtek", Class = "Net" };

            var preview = await new EnableDeviceAction().PreviewAsync(Context(control, ("device", Wifi)), CancellationToken.None);

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
        }

        [Fact]
        public async Task Les_pilotes_windows_update_se_filtrent_et_seuls_ceux_montres_s_installent()
        {
            var control = new FakeControl();
            control.Updates.Add(new DriverUpdateInfo { UpdateId = "a", Title = "Realtek - Net - 2024.10.0.1", DriverClass = "Net" });
            control.Updates.Add(new DriverUpdateInfo { UpdateId = "b", Title = "Intel - Display - 31.0.101.5186", DriverClass = "Display" });

            var action = new DriverUpdatesAction();
            var context = Context(control, ("filter", "realtek"));
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Single(preview.WillDo);
            Assert.Equal(new[] { "install a" }, control.Calls);
        }

        [Fact]
        public async Task La_file_d_impression_relance_aussi_les_services_qui_en_dependaient()
        {
            var control = new FakeControl();
            control.Services["Spooler"] = "Running";
            control.Dependents["Spooler"] = new[] { "Fax" };

            var action = new PrintQueueResetAction();
            var context = Context(control);
            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal("stop Spooler", control.Calls[0]);
            Assert.StartsWith("clear ", control.Calls[1]);
            Assert.Contains("start Fax", control.Calls);
            Assert.Contains("start Spooler", control.Calls);
        }

        [Fact]
        public async Task Un_service_qui_refuse_de_s_arreter_empeche_de_vider_son_dossier()
        {
            var control = new FakeControl { StopFails = true };
            control.Services["Spooler"] = "Running";

            var action = new PrintQueueResetAction();
            var context = Context(control);
            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.DoesNotContain(control.Calls, c => c.StartsWith("clear", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Desactiver_au_demarrage_ecrit_ce_qu_ecrit_le_gestionnaire_des_taches()
        {
            var control = new FakeControl();
            var registry = new RunRegistry();
            registry.Values[(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run")] = new[] { "Spotify" };

            var action = new StartupDisableAction();
            var context = Context(control, registry, ("entry", "Spotify"));
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            var write = Assert.Single(control.Binary);
            Assert.Equal(RegistryHive.CurrentUser, write.Hive);
            Assert.EndsWith(@"StartupApproved\Run", write.Key);
            Assert.Equal("Spotify", write.Name);
            Assert.Equal(0x03, write.Data[0]);
            Assert.Equal(12, write.Data.Length);
        }

        [Fact]
        public async Task Un_mode_d_alimentation_absent_n_est_pas_force()
        {
            var preview = await new PowerPlanAction().PreviewAsync(Context(new FakeControl(), ("plan", "performances")), CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task Une_sortie_audio_debranchee_ne_devient_pas_la_sortie_par_defaut()
        {
            var control = new FakeControl();
            control.Audio.Add(new AudioOutputState { Id = "hp", Name = "Haut-parleurs", State = "active" });
            control.Audio.Add(new AudioOutputState { Id = "casque", Name = "Casque", State = "débranchée" });
            control.Audio.Add(new AudioOutputState { Id = "hdmi", Name = "Écran HDMI", State = "active", IsDefault = true, IsDefaultCommunications = true });

            var action = new AudioDefaultOutputAction();
            var unplugged = await action.PreviewAsync(Context(control, ("endpoint", "casque")), CancellationToken.None);
            var speakers = Context(control, ("endpoint", "hp"));
            var preview = await action.PreviewAsync(speakers, CancellationToken.None);
            await action.ExecuteAsync(speakers, preview, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, unplugged.Outcome);
            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Contains("Écran HDMI", preview.Measurements.Single().Value);
            Assert.Equal(new[] { "default hp" }, control.Calls);
        }

        [Fact]
        public async Task Un_son_ni_coupe_ni_a_zero_n_est_pas_retouche()
        {
            var control = new FakeControl();
            control.Audio.Add(new AudioOutputState { Id = "hp", Name = "Haut-parleurs", State = "active", IsDefault = true, VolumePercent = 60, Muted = false });

            var preview = await new AudioRestoreVolumeAction().PreviewAsync(Context(control), CancellationToken.None);

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
        }

        [Fact]
        public void La_fiche_de_la_machine_donne_l_identifiant_des_peripheriques_en_erreur_et_reste_courte()
        {
            var snapshot = new SystemSnapshot
            {
                Windows = new WindowsSnapshot
                {
                    Devices = new[] { new DeviceInfo { Name = "Realtek RTL8821CE", DeviceClass = "Net", ProblemCode = 43, HardwareId = Wifi } },
                    Drivers = Enumerable.Range(0, 300).Select(i => new DriverInfo { DeviceName = "Pilote " + i, DeviceClass = i == 7 ? "Display" : "Keyboard", Version = "1.0" }).ToArray(),
                },
            };

            var brief = MachineBrief.Compose(snapshot, AssistantMode.Drivers);

            Assert.Contains(Wifi, brief);
            Assert.Contains("Pilote 7", brief);
            Assert.DoesNotContain("Pilote 8 ", brief);
            Assert.True(brief.Length < 6000, "Fiche de " + brief.Length + " caractères.");
        }

        [Fact]
        public async Task Une_operation_trop_recente_pour_ce_windows_est_refusee_avec_son_repli()
        {
            // Le démarrage rapide n'existe pas sous Windows 7 : l'action ne le vérifie pas elle-même,
            // le runner s'en charge pour toutes.
            var context = new ActionContext(
                new FakePlatformInfo(build: 7601, elevated: true), new FakeProcessRunner(), new FakeProcessLauncher(), new FakeFileSystemGateway(),
                new RunRegistry(), new FakeSystemRestoreGateway(), NullLogger.Instance, null, null, null, new FakeControl());
            var runner = new ActionRunner(context, new LDI12.Actions.Journal.InterventionJournal(), NullLogger.Instance);
            var action = new FastStartupOffAction();

            var readiness = runner.Readiness(action);
            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
            Assert.Contains("Windows 7", readiness.Workaround);

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            Assert.False(preview.CanExecute);
        }

        private static ActionContext Context(FakeControl control, params (string Key, string Value)[] parameters)
            => Context(control, new RunRegistry(), parameters);

        private static ActionContext Context(FakeControl control, IRegistryGateway registry, params (string Key, string Value)[] parameters)
            => new ActionContext(
                new FakePlatformInfo(elevated: true), new FakeProcessRunner(), new FakeProcessLauncher(), new FakeFileSystemGateway(),
                registry, new FakeSystemRestoreGateway(), NullLogger.Instance, null,
                parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), null, control);

        private sealed class RunRegistry : IRegistryGateway
        {
            public Dictionary<(RegistryHive, string), string[]> Values { get; } = new Dictionary<(RegistryHive, string), string[]>();

            public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => Values.ContainsKey((hive, subKey));
            public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => Array.Empty<string>();

            public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => Values.TryGetValue((hive, subKey), out var names) ? names : Array.Empty<string>();
        }

        private sealed class FakeControl : ISystemControl
        {
            public Dictionary<string, DeviceState> Devices { get; } = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Services { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string[]> Dependents { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            public List<DriverUpdateInfo> Updates { get; } = new List<DriverUpdateInfo>();
            public List<string> Calls { get; } = new List<string>();
            public List<(RegistryHive Hive, string Key, string Name, byte[] Data)> Binary { get; } = new List<(RegistryHive, string, string, byte[])>();
            public Action<string>? OnReinstall { get; set; }
            public bool StopFails { get; set; }

            public ControlResult RescanDevices() { Calls.Add("rescan"); return ControlResult.Ok("ok"); }
            public DeviceState? FindDevice(string instanceId) => Devices.TryGetValue(instanceId, out var device) ? device : null;
            public ControlResult RestartDevice(string instanceId) { Calls.Add("restart " + instanceId); return ControlResult.Ok("ok"); }
            public ControlResult EnableDevice(string instanceId) { Calls.Add("enable " + instanceId); return ControlResult.Ok("ok"); }

            public ControlResult ReinstallDevice(string instanceId)
            {
                Calls.Add("reinstall " + instanceId);
                OnReinstall?.Invoke(instanceId);
                return ControlResult.Ok("ok");
            }

            public string? ServiceStatus(string name) => Services.TryGetValue(name, out var state) ? state : null;

            public ControlResult StopService(string name, TimeSpan timeout, out IReadOnlyList<string> stoppedDependents)
            {
                Calls.Add("stop " + name);
                stoppedDependents = Dependents.TryGetValue(name, out var list) ? list : Array.Empty<string>();
                return StopFails ? ControlResult.Fail("refusé") : ControlResult.Ok("arrêté");
            }

            public ControlResult StartService(string name, TimeSpan timeout) { Calls.Add("start " + name); return ControlResult.Ok("relancé"); }
            public ControlResult ClearFolder(string path) { Calls.Add("clear " + path); return ControlResult.Ok("vidé"); }
            public ControlResult SetRegistryDword(RegistryHive hive, string subKey, string name, int value) { Calls.Add("dword " + name); return ControlResult.Ok("ok"); }

            public ControlResult SetRegistryBinary(RegistryHive hive, string subKey, string name, byte[] value)
            {
                Binary.Add((hive, subKey, name, value));
                return ControlResult.Ok("ok");
            }

            public ControlResult SetRegistryString(RegistryHive hive, string subKey, string name, string value) { Calls.Add("string " + name); return ControlResult.Ok("ok"); }
            public ControlResult DeleteRegistryValue(RegistryHive hive, string subKey, string name) { Calls.Add("delete " + name); return ControlResult.Ok("ok"); }
            public ControlResult SetServiceStart(string name, int start) { Calls.Add("start-type " + name + " " + start); return ControlResult.Ok("ok"); }
            public ControlResult SetTaskEnabled(string path, bool enabled) { Calls.Add("task " + path + " " + enabled); return ControlResult.Ok("ok"); }
            public ControlResult EndProcess(int processId) { Calls.Add("end " + processId); return ControlResult.Ok("ok"); }
            public HashSet<string> PackagesInUse { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public ControlResult DeleteDriverPackage(string publishedName)
            {
                Calls.Add("delete-package " + publishedName);
                return PackagesInUse.Contains(publishedName) ? ControlResult.Fail("un périphérique présent l'utilise") : ControlResult.Ok("ok");
            }

            public List<AudioOutputState> Audio { get; } = new List<AudioOutputState>();
            public IReadOnlyList<AudioOutputState> AudioEndpoints() => Audio;
            public ControlResult SetDefaultAudioEndpoint(string endpointId) { Calls.Add("default " + endpointId); return ControlResult.Ok("ok"); }
            public ControlResult RestoreAudioVolume(string? endpointId) { Calls.Add("volume " + (endpointId ?? "défaut")); return ControlResult.Ok("ok"); }
            public ControlResult PlayTestSound() { Calls.Add("tone"); return ControlResult.Ok("joué"); }

            public Guid? ActivePowerScheme() => null;
            public bool PowerSchemeExists(Guid scheme) => false;
            public ControlResult ActivatePowerScheme(Guid scheme) { Calls.Add("power"); return ControlResult.Ok("ok"); }

            public Task<DriverUpdateSearch> SearchDriverUpdatesAsync(CancellationToken cancellationToken)
                => Task.FromResult(new DriverUpdateSearch { Succeeded = true, Updates = Updates });

            public Task<ControlResult> InstallDriverUpdatesAsync(IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken)
            {
                Calls.Add("install " + string.Join(",", updateIds));
                return Task.FromResult(ControlResult.Ok("installé"));
            }
        }
    }
}
