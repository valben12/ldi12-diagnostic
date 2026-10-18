using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.Core.Crashes;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;

namespace LDI12.App.ViewModels
{
    /// <summary>Un écran bleu, tel que l'écran le montre.</summary>
    public sealed class BlueScreenItem : ObservableObject
    {
        private bool _isExpanded;

        public BlueScreenItem(DateTime timeUtc, uint? code, CrashDump? dump, IModuleFileSource files)
        {
            var info = code.HasValue ? BugCheckCatalog.Describe(code.Value) : null;
            When = timeUtc.ToLocalTime().ToString("dddd d MMMM yyyy 'à' HH:mm", Formats.French);
            CodeName = info?.Name ?? "Code d'arrêt inconnu";
            CodeHex = code.HasValue ? "0x" + code.Value.ToString("X8", CultureInfo.InvariantCulture) : string.Empty;
            Meaning = info?.Meaning ?? "Le journal a noté un écran bleu sans en garder le code.";
            CauseLabel = info == null ? string.Empty : "Piste : " + BugCheckCatalog.Explain(info.Cause);

            if (dump == null)
            {
                SuspectTitle = "Pas de rapport lisible";
                SuspectDetail = "Windows n'a pas laissé de vidage pour cet écran bleu, ou il n'a pas pu être lu.";
                return;
            }

            HasDump = true;
            FileText = Path.GetFileName(dump.File) + (dump.FileSize > 0 ? " · " + ValueFormat.Bytes(dump.FileSize) : string.Empty) + " · vidage " + dump.Kind;
            Parameters = string.Join("   ", dump.Parameters.Select(p => "0x" + p.ToString(dump.Is64Bit ? "X16" : "X8", CultureInfo.InvariantCulture)));
            AddressText = dump.CrashAddress.HasValue
                ? "0x" + dump.CrashAddress.Value.ToString(dump.Is64Bit ? "X16" : "X8", CultureInfo.InvariantCulture) + (dump.CrashModule != null ? " dans " + dump.CrashModule : string.Empty)
                : "non relevée";

            if (dump.Suspect != null)
            {
                var file = files.Find(dump.Suspect);
                SuspectTitle = dump.Suspect;
                SuspectDetail = CrashAnalyzer.Describe(dump.Suspect, file) == dump.Suspect
                    ? Capitalize(dump.SuspectReason ?? string.Empty) + "."
                    : string.Join(" · ", new[] { file?.Description, file?.Company, file?.Version }.Where(t => !string.IsNullOrWhiteSpace(t)))
                      + "\n" + Capitalize(dump.SuspectReason ?? string.Empty) + ".";
                IsKernel = CrashAnalyzer.IsKernel(dump.Suspect);
                IsThirdParty = file != null && !file.IsMicrosoft;
            }
            else
            {
                SuspectTitle = dump.Modules.Count == 0 ? "Pilote non désigné" : "Aucun pilote désigné";
                SuspectDetail = dump.Modules.Count == 0
                    ? "Ce type de vidage ne porte pas la liste des pilotes : seul un minividage la contient."
                    : "Aucune adresse de la pile ne tombe dans un pilote.";
            }

            Stack = dump.Modules.Where(m => m.InStack)
                .OrderBy(m => dump.StackModules.ToList().IndexOf(m.Name))
                .Select(m => new CrashModuleItem(m, files.Find(m.Name), string.Equals(m.Name, dump.Suspect, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ModulesText = dump.Modules.Count == 0 ? string.Empty : dump.Modules.Count + " pilotes chargés au moment de l'arrêt, " + Stack.Count + " présents dans la pile.";
            ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        }

        public string When { get; }
        public string CodeName { get; }
        public string CodeHex { get; }
        public string Meaning { get; }
        public string CauseLabel { get; }
        public string SuspectTitle { get; } = string.Empty;
        public string SuspectDetail { get; } = string.Empty;
        public bool IsKernel { get; }
        public bool IsThirdParty { get; }
        public bool HasDump { get; }
        public string FileText { get; } = string.Empty;
        public string Parameters { get; } = string.Empty;
        public string AddressText { get; } = string.Empty;
        public string ModulesText { get; } = string.Empty;
        public IReadOnlyList<CrashModuleItem> Stack { get; } = Array.Empty<CrashModuleItem>();
        public bool HasStack => Stack.Count > 0;
        public ICommand ToggleCommand { get; } = new RelayCommand(() => { });

        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Formats.French) + text.Substring(1);
    }

    /// <summary>Un pilote présent dans la pile d'un écran bleu.</summary>
    public sealed class CrashModuleItem
    {
        public CrashModuleItem(CrashModule module, ModuleFile? file, bool isSuspect)
        {
            Name = module.Name;
            IsSuspect = isSuspect;
            Detail = string.Join(" · ", new[]
            {
                file?.Company,
                file?.Version,
                module.BuiltUtc.HasValue ? "compilé le " + module.BuiltUtc.Value.ToString("d MMM yyyy", Formats.French) : null,
            }.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        public string Name { get; }
        public string Detail { get; }
        public bool IsSuspect { get; }
    }

    /// <summary>Un programme qui plante ou se fige, regroupé.</summary>
    public sealed class AppCrashItem
    {
        public AppCrashItem(AppCrash crash)
        {
            Title = crash.Application + (crash.Version != null ? " " + crash.Version : string.Empty);
            Tag = crash.IsHang ? "Se fige" : "Plante";
            IsHang = crash.IsHang;
            Detail = crash.IsHang
                ? "Le programme ne répondait plus et a été fermé."
                : "Erreur dans " + (crash.Module ?? "un module inconnu") + (crash.ExceptionCode != null ? " (" + crash.ExceptionCode + ")" : string.Empty)
                  + (crash.Module != null && !string.Equals(crash.Module, crash.Application, StringComparison.OrdinalIgnoreCase) ? " : un composant chargé par le programme." : ".");
            CountText = crash.Count + " fois · dernière le " + crash.LastUtc.ToLocalTime().ToString("d MMM 'à' HH:mm", Formats.French);
        }

        public string Title { get; }
        public string Tag { get; }
        public bool IsHang { get; }
        public string Detail { get; }
        public string CountText { get; }
    }

    /// <summary>
    /// L'écran Plantages : les écrans bleus lus dans leurs vidages, le pilote en cause, et une
    /// conclusion qui dit quoi faire ; puis les programmes qui plantent.
    /// </summary>
    public sealed class CrashesViewModel : ObservableObject, IScreenActivation
    {
        private const string Category = "Plantages";
        private const int Days = 60;

        private readonly RepairsViewModel _repairs;
        private readonly ILdiLogger _logger;
        private readonly ModuleFileSource _files = new ModuleFileSource();
        private CrashHistory? _history;
        private bool _loaded, _isLoading, _needsElevation, _demo;
        private string _status = string.Empty;
        private string _verdictTitle = string.Empty, _verdictDetail = string.Empty, _verdictTone = "Good";
        private string _summary = "Écrans bleus, arrêts brutaux et programmes qui plantent.";
        private string _settingsText = string.Empty;
        private ActionLink? _verdictLink;
        private int _blueScreens, _shutdowns, _appCrashes;

        public CrashesViewModel(RepairsViewModel repairs, ILdiLogger logger)
        {
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // « Relire » ne relance l'invite d'administration que si les rapports ont déjà été lus
            // une fois : sinon c'est le bouton « Lire les rapports » qui la propose, explicitement.
            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(readDumps: !NeedsElevation), () => !IsLoading);
            ReadDumpsCommand = new AsyncRelayCommand(() => LoadAsync(readDumps: true), () => !IsLoading);
            EnableDumpsLink = new ActionLink(ActionIds.CrashDumpsEnable);
            SolveCommand = new RelayCommand(() => ActionNavigator.Solve?.Invoke("Écrans bleus : " + VerdictTitle, VerdictDetail + " " + string.Join(" ", Advice)));
        }

        public ObservableCollection<BlueScreenItem> BlueScreens { get; } = new ObservableCollection<BlueScreenItem>();
        public ObservableCollection<AppCrashItem> Apps { get; } = new ObservableCollection<AppCrashItem>();
        public ObservableCollection<string> Advice { get; } = new ObservableCollection<string>();

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand ReadDumpsCommand { get; }
        public ICommand SolveCommand { get; }
        public ActionLink EnableDumpsLink { get; }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string VerdictTitle { get => _verdictTitle; private set => Set(ref _verdictTitle, value); }
        public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }

        /// <summary>« Good », « Warning » ou « Bad » : la teinte de la carte de conclusion.</summary>
        public string VerdictTone { get => _verdictTone; private set => Set(ref _verdictTone, value); }

        public ActionLink? VerdictLink { get => _verdictLink; private set { if (Set(ref _verdictLink, value)) Raise(nameof(HasVerdictLink)); } }
        public bool HasVerdictLink => _verdictLink != null;
        public string? VerdictLinkLabel { get; private set; }

        public string SettingsText { get => _settingsText; private set => Set(ref _settingsText, value); }
        public bool DumpsDisabled => _history?.Settings.Disabled == true;

        public int BlueScreenCount { get => _blueScreens; private set => Set(ref _blueScreens, value); }
        public int ShutdownCount { get => _shutdowns; private set => Set(ref _shutdowns, value); }
        public int AppCrashCount { get => _appCrashes; private set => Set(ref _appCrashes, value); }

        public bool HasBlueScreens => BlueScreens.Count > 0;
        public bool HasApps => Apps.Count > 0;
        public bool HasVerdict => _history != null;

        /// <summary>Les vidages n'ont pas été lus : l'application n'est pas administrateur.</summary>
        public bool NeedsElevation { get => _needsElevation; private set => Set(ref _needsElevation, value); }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (!Set(ref _isLoading, value)) return;
                RefreshCommand.RaiseCanExecuteChanged();
                ReadDumpsCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// À la première ouverture : les journaux tout de suite, les vidages aussi si aucune invite
        /// n'est nécessaire. Sinon, un bouton les propose, l'invite UAC venant de ce clic.
        /// </summary>
        public void Activated() => _ = EnsureLoadedAsync();

        public void Deactivated()
        {
        }

        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            _loaded = true;
            var elevated = await _repairs.IsElevatedAsync().ConfigureAwait(true);
            await LoadAsync(readDumps: elevated).ConfigureAwait(true);
        }

        public async Task LoadAsync(bool readDumps)
        {
            if (IsLoading) return;
            IsLoading = true;
            Status = "Lecture des journaux de Windows…";
            try
            {
                var events = await Task.Run(() => CrashEventsReader.Read(Days)).ConfigureAwait(true);
                var settings = await Task.Run(CrashEventsReader.Settings).ConfigureAwait(true);

                IReadOnlyList<CrashDump> dumps = _history?.Dumps ?? Array.Empty<CrashDump>();
                var needsElevation = !readDumps && dumps.Count == 0;
                if (readDumps)
                {
                    Status = "Lecture des rapports d'écran bleu…";
                    var outcome = await _repairs.RunQuietAsync(ActionIds.CrashDumpsRead, new Dictionary<string, string>
                    {
                        [CrashDumpsReadAction.FolderParameter] = settings.MinidumpFolder,
                        [CrashDumpsReadAction.MemoryParameter] = settings.MemoryDump,
                    }, null, CancellationToken.None).ConfigureAwait(true);

                    if (outcome.Succeeded)
                    {
                        dumps = CrashDumpTransfer.Read(outcome.RawOutput)
                            .Where(d => d.TimeUtc >= DateTime.UtcNow.AddDays(-Days) || !d.IsReadable).ToList();
                        needsElevation = false;
                    }
                    else
                    {
                        needsElevation = true;
                        _logger.Warn(Category, "Rapports d'écran bleu non lus : " + outcome.Summary);
                    }
                }

                var history = new CrashHistory
                {
                    Dumps = dumps,
                    BugChecks = events.BugChecks,
                    AppCrashes = events.Apps,
                    UnexpectedShutdowns = events.Shutdowns,
                    WindowDays = Days,
                    Settings = settings,
                    DumpsNeedElevation = needsElevation,
                };
                var verdict = await Task.Run(() => CrashAnalyzer.Conclude(history, _files)).ConfigureAwait(true);
                if (_demo) return; // les captures montrent l'exemple, pas ce PC
                Show(history, verdict);
                NeedsElevation = needsElevation;
                Status = string.Empty;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.Error(Category, "Lecture des plantages impossible.", ex);
                Status = "Lecture impossible : " + ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void Show(CrashHistory history, CrashVerdict verdict)
        {
            _history = history;
            BlueScreens.Clear();
            var readable = history.Dumps.Where(d => d.IsReadable).ToList();
            var items = new List<(DateTime Time, BlueScreenItem Item)>();
            foreach (var dump in readable)
                items.Add((dump.TimeUtc, new BlueScreenItem(dump.TimeUtc, dump.BugCheckCode, dump, _files)));
            foreach (var e in history.BugChecks.Where(e => !readable.Any(d => Math.Abs((d.TimeUtc - e.TimeUtc).TotalMinutes) < 10)))
                items.Add((e.TimeUtc, new BlueScreenItem(e.TimeUtc, e.BugCheckCode, null, _files)));
            foreach (var item in items.OrderByDescending(i => i.Time)) BlueScreens.Add(item.Item);
            if (BlueScreens.Count > 0 && BlueScreens[0].HasDump) BlueScreens[0].IsExpanded = true;

            Apps.Clear();
            foreach (var crash in history.AppCrashes.Take(30)) Apps.Add(new AppCrashItem(crash));

            BlueScreenCount = CrashAnalyzer.CountBlueScreens(history);
            ShutdownCount = history.UnexpectedShutdowns;
            AppCrashCount = history.AppCrashes.Sum(a => a.Count);

            VerdictTitle = verdict.Title;
            VerdictDetail = verdict.Detail;
            VerdictTone = verdict.Tone;
            Advice.Clear();
            foreach (var line in verdict.Advice) Advice.Add(line);
            VerdictLinkLabel = verdict.ActionLabel;
            Raise(nameof(VerdictLinkLabel));
            VerdictLink = ActionLink.For(verdict.ActionId);

            SettingsText = "Enregistrement des écrans bleus : " + history.Settings.Describe() + ". Dossier des minividages : " + history.Settings.MinidumpFolder + ".";
            Summary = BlueScreenCount == 0 && ShutdownCount == 0
                ? "Rien d'anormal sur " + Days + " jours."
                : BlueScreenCount + " écran(s) bleu(s), " + ShutdownCount + " arrêt(s) brutal(aux) sur " + Days + " jours.";

            foreach (var name in new[] { nameof(HasBlueScreens), nameof(HasApps), nameof(HasVerdict), nameof(DumpsDisabled) }) Raise(name);
            _logger.Info(Category, Summary + " " + verdict.Title);
        }

        /// <summary>Un historique d'exemple, pour les captures d'écran seulement.</summary>
        public void ShowDemo()
        {
            var nv = new CrashModule { Name = "nvlddmkm.sys", Base = 0xFFFFF80612340000, Size = 0x2000000, TimeStamp = 1_600_000_000, InStack = true };
            var nt = new CrashModule { Name = "ntoskrnl.exe", Base = 0xFFFFF80600000000, Size = 0x1000000, TimeStamp = 1_700_000_000, InStack = true };
            var dx = new CrashModule { Name = "dxgkrnl.sys", Base = 0xFFFFF80614340000, Size = 0x400000, TimeStamp = 1_700_000_000, InStack = true };
            CrashDump Dump(int daysAgo, uint code, params ulong[] parameters) => new CrashDump
            {
                File = @"C:\Windows\Minidump\" + DateTime.Now.AddDays(-daysAgo).ToString("MMddyy", CultureInfo.InvariantCulture) + "-11234-01.dmp",
                FileSize = 1_245_184,
                TimeUtc = DateTime.UtcNow.AddDays(-daysAgo).AddHours(-3),
                BugCheckCode = code,
                Parameters = parameters,
                Is64Bit = true,
                CrashAddress = 0xFFFFF80612345678,
                CrashModule = "nvlddmkm.sys",
                Modules = new[] { nt, nv, dx },
                StackModules = new[] { "ntoskrnl.exe", "nvlddmkm.sys", "dxgkrnl.sys" },
            };
            var history = new CrashHistory
            {
                Dumps = new[]
                {
                    Dump(2, 0x116, 0xFFFFA5011E2C4010, 0xFFFFF80612345678, 0, 0x2),
                    Dump(9, 0x3B, 0xC0000005, 0xFFFFF80612349ABC, 0xFFFF9A8F1C7E6920, 0),
                    Dump(23, 0x116, 0xFFFFA5011E2C4010, 0xFFFFF80612345678, 0, 0x2),
                },
                AppCrashes = new[]
                {
                    new AppCrash { Application = "OUTLOOK.EXE", Version = "16.0.17928.20114", Module = "mso20win32client.dll", ExceptionCode = "0xc0000005", Count = 4, LastUtc = DateTime.UtcNow.AddDays(-1) },
                    new AppCrash { Application = "explorer.exe", Version = "10.0.22621.4317", Module = "ShellExtension.dll", ExceptionCode = "0xc0000005", Count = 2, LastUtc = DateTime.UtcNow.AddDays(-6) },
                    new AppCrash { Application = "chrome.exe", Version = "130.0.6723.70", IsHang = true, Count = 1, LastUtc = DateTime.UtcNow.AddDays(-12) },
                },
                UnexpectedShutdowns = 1,
                WindowDays = Days,
                Settings = new DumpSettings { Mode = 7 },
            };
            _loaded = _demo = true;
            IsLoading = false;
            Status = string.Empty;
            NeedsElevation = false;
            Show(history, CrashAnalyzer.Conclude(history, _files));
        }
    }
}
