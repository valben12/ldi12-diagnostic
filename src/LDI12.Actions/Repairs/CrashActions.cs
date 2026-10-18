using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Crashes;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Les vidages lus, mis en texte pour traverser le canal de l'hôte élevé. Les adresses du
    /// noyau dépassent un entier signé : elles voyagent en hexadécimal.
    /// </summary>
    public static class CrashDumpTransfer
    {
        private sealed class ModuleDto
        {
            public string N { get; set; } = string.Empty;
            public string B { get; set; } = "0";
            public uint S { get; set; }
            public uint T { get; set; }
            public bool K { get; set; }
        }

        private sealed class DumpDto
        {
            public string File { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTime Time { get; set; }
            public uint Code { get; set; }
            public List<string> Parameters { get; set; } = new List<string>();
            public bool Is64 { get; set; }
            public string Kind { get; set; } = "mini";
            public string? Address { get; set; }
            public string? CrashModule { get; set; }
            public List<ModuleDto> Modules { get; set; } = new List<ModuleDto>();
            public List<string> Stack { get; set; } = new List<string>();
            public string? Error { get; set; }
        }

        public static string Write(IEnumerable<CrashDump> dumps)
            => JsonConvert.SerializeObject(dumps.Select(d => new DumpDto
            {
                File = d.File,
                Size = d.FileSize,
                Time = d.TimeUtc,
                Code = d.BugCheckCode,
                Parameters = d.Parameters.Select(Hex).ToList(),
                Is64 = d.Is64Bit,
                Kind = d.Kind,
                Address = d.CrashAddress.HasValue ? Hex(d.CrashAddress.Value) : null,
                CrashModule = d.CrashModule,
                Modules = d.Modules.Select(m => new ModuleDto { N = m.Name, B = Hex(m.Base), S = m.Size, T = m.TimeStamp, K = m.InStack }).ToList(),
                Stack = d.StackModules.ToList(),
                Error = d.Error,
            }).ToList());

        public static IReadOnlyList<CrashDump> Read(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<CrashDump>();
            List<DumpDto>? dtos;
            try { dtos = JsonConvert.DeserializeObject<List<DumpDto>>(text!); }
            catch (JsonException) { return Array.Empty<CrashDump>(); }
            if (dtos == null) return Array.Empty<CrashDump>();

            return dtos.Select(d => new CrashDump
            {
                File = d.File,
                FileSize = d.Size,
                TimeUtc = DateTime.SpecifyKind(d.Time, DateTimeKind.Utc),
                BugCheckCode = d.Code,
                Parameters = d.Parameters.Select(Parse).ToList(),
                Is64Bit = d.Is64,
                Kind = d.Kind,
                CrashAddress = d.Address == null ? (ulong?)null : Parse(d.Address),
                CrashModule = d.CrashModule,
                Modules = d.Modules.Select(m => new CrashModule { Name = m.N, Base = Parse(m.B), Size = m.S, TimeStamp = m.T, InStack = m.K }).ToList(),
                StackModules = d.Stack,
                Error = d.Error,
            }).ToList();
        }

        private static string Hex(ulong value) => value.ToString("X", CultureInfo.InvariantCulture);

        private static ulong Parse(string text)
            => ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// Lit les rapports d'écran bleu du dossier Minidump, et l'en-tête du vidage de la mémoire.
    /// </summary>
    /// <remarks>
    /// Windows réserve ce dossier aux administrateurs : la lecture passe par l'hôte élevé, et ne
    /// rapporte que l'analyse, jamais les fichiers. Rien n'est modifié.
    /// </remarks>
    public sealed class CrashDumpsReadAction : IRepairAction
    {
        /// <summary>Au-delà, un minividage n'est plus un minividage : seul l'en-tête est lu.</summary>
        private const int MaxMiniDump = 16 * 1024 * 1024;

        private const int MaxDumps = 40;

        public const string FolderParameter = "folder";

        public const string MemoryParameter = "memory";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.CrashDumpsRead,
            DisplayName = "Lire les rapports d'écran bleu",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.ReadOnly,
            Purpose = "Lit les minividages laissés par les écrans bleus : code d'arrêt, paramètres, pilotes présents.",
            PlainPurpose = "On sait quel pilote a fait planter le PC.",
            TypicalDuration = TimeSpan.FromSeconds(3),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Windows réserve les rapports d'écran bleu aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Les rapports d'écran bleu vont être lus.",
                WillDo = new[] { "Lire les minividages et l'en-tête du vidage de la mémoire." },
                WillNotDo = new[] { "Ne modifie, ne déplace et n'envoie aucun fichier." },
            });

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var folder = context.Parameter(FolderParameter) ?? Path.Combine(windows, "Minidump");
            var memory = context.Parameter(MemoryParameter) ?? Path.Combine(windows, "MEMORY.DMP");

            var dumps = new List<CrashDump>();
            var scan = context.Files.DirectoryExists(folder)
                ? context.Files.Scan(new DirectoryScanRequest(folder) { TopLevelOnly = true, MaxFiles = 2000 }, cancellationToken)
                : (Measured<DirectoryScan>?)null;
            var files = scan?.ValueOrDefault?.Files
                .Where(f => f.Path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteUtc)
                .Take(MaxDumps)
                .ToList() ?? new List<FileEntry>();

            var index = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress("Lecture de " + Path.GetFileName(file.Path) + "…", (double)index++ / Math.Max(1, files.Count)));
                dumps.Add(ReadOne(context, file.Path, file.LastWriteUtc, file.SizeBytes,
                    file.SizeBytes <= MaxMiniDump ? MaxMiniDump : CrashDumpReader.HeaderBytes));
            }

            if (context.Files.FileExists(memory))
            {
                var header = context.Files.ReadBytes(memory, CrashDumpReader.HeaderBytes);
                if (header.ValueOrDefault != null)
                {
                    var dump = CrashDumpReader.Read(memory, header.ValueOrDefault!, DateTime.MinValue, 0);
                    // Un MEMORY.DMP sans date lisible ne dit pas quand : on n'invente pas.
                    if (dump.IsReadable && dump.TimeUtc > DateTime.MinValue && !dumps.Any(d => Math.Abs((d.TimeUtc - dump.TimeUtc).TotalMinutes) < 10)) dumps.Add(dump);
                }
            }

            return Task.FromResult(new ActionOutcome
            {
                Status = ActionStatus.Succeeded,
                Summary = dumps.Count == 0 ? "Aucun rapport d'écran bleu sur ce PC." : dumps.Count + " rapport(s) d'écran bleu lu(s).",
                RawOutput = CrashDumpTransfer.Write(dumps),
                Duration = clock.Elapsed,
            });
        }

        private static CrashDump ReadOne(ActionContext context, string path, DateTime time, long size, int max)
        {
            var data = context.Files.ReadBytes(path, max);
            if (data.ValueOrDefault == null)
                return new CrashDump { File = path, TimeUtc = time, FileSize = size, Error = data.Reason ?? "Fichier illisible." };
            try
            {
                return CrashDumpReader.Read(path, data.ValueOrDefault!, time, size);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException)
            {
                return new CrashDump { File = path, TimeUtc = time, FileSize = size, Error = "Fichier abîmé : " + ex.Message };
            }
        }
    }

    /// <summary>
    /// Règle Windows pour qu'un écran bleu laisse un minividage : sans lui, la cause ne peut pas
    /// être retrouvée après coup.
    /// </summary>
    public sealed class CrashDumpsEnableAction : IRepairAction
    {
        internal const string Key = @"SYSTEM\CurrentControlSet\Control\CrashControl";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.CrashDumpsEnable,
            DisplayName = "Enregistrer les écrans bleus",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Windows,
            Risk = ActionRisk.Low,
            Purpose = "Règle Windows pour qu'un écran bleu laisse un minividage, lisible ensuite par l'écran Plantages.",
            PlainPurpose = "Au prochain écran bleu, on saura quel pilote l'a provoqué.",
            TypicalDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => NativeOutcome.Elevated(context, "Ce réglage de Windows est réservé aux administrateurs");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var mode = context.Registry.ReadInt32(RegistryHive.LocalMachine, Key, "CrashDumpEnabled");
            if (mode.HasValue && mode.Value != 0)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "Windows enregistre déjà les écrans bleus (" + new DumpSettings { Mode = mode }.Describe() + ").",
                });

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Les écrans bleus vont laisser un minividage.",
                WillDo = new[] { "Régler l'enregistrement sur « minividage » : 256 Ko par écran bleu, dans C:\\Windows\\Minidump." },
                WillNotDo = new[] { "Ne touche ni au fichier d'échange ni au redémarrage automatique." },
                Measurements = new[] { new PreviewLine("Réglage actuel", new DumpSettings { Mode = mode }.Describe(), PreviewLineKind.Caution) },
            });
        }

        public Task<ActionOutcome> ExecuteAsync(ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var result = context.Control.SetRegistryDword(RegistryHive.LocalMachine, Key, "CrashDumpEnabled", 3);
            return Task.FromResult(NativeOutcome.From(result, clock.Elapsed, "Les écrans bleus laisseront désormais un minividage."));
        }
    }
}
