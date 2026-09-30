using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.Actions.Backup
{
    /// <summary>Ce que winget connaît des applications installées.</summary>
    public sealed class WingetListing
    {
        /// <summary>Identifiants winget (« Mozilla.Firefox »), triés.</summary>
        public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();

        /// <summary>Pourquoi la liste est vide, quand elle l'est faute d'avoir pu être lue.</summary>
        public string? Failure { get; init; }
    }

    /// <summary>
    /// Les applications à réinstaller, par winget.
    /// </summary>
    /// <remarks>
    /// <b>On ne copie pas un logiciel installé.</b> Ses fichiers ne sont qu'une partie de lui : il
    /// y a aussi ses clés de registre, ses services, ses composants partagés, sa licence. Copier
    /// « Program Files » donne des programmes qui ne démarrent pas. Ce qu'on peut garder, c'est la
    /// liste de ce qui était installé, sous une forme qu'un outil sait réinstaller seul.
    /// <para>
    /// winget est cet outil : livré avec Windows 10 1809 et suivants par le « Programme
    /// d'installation d'application », il réinstalle chaque logiciel depuis son éditeur, dans sa
    /// dernière version. Seule la source « winget » est retenue : les applications du Microsoft
    /// Store y portent des identifiants illisibles (« 9WZDNCRFJ3PS ») et reviennent de toute
    /// façon avec le compte Microsoft du client. Un logiciel que winget ne connaît pas n'apparaît
    /// pas ici : il reste dans la fiche de réinstallation, à remettre à la main.
    /// </para>
    /// </remarks>
    public static class WingetApplications
    {
        public const string FileName = "applications-winget.json";

        public const string ScriptName = "reinstaller-applications.cmd";

        /// <summary>Source officielle de winget, telle qu'elle figure dans un export.</summary>
        private const string Source = "winget";

        /// <summary>
        /// Un identifiant winget : éditeur et nom séparés par des points.
        /// </summary>
        /// <remarks>
        /// Vérifié avant de passer dans une ligne de commande : un identifiant relu dans une
        /// sauvegarde vient d'un fichier qu'on ne contrôle pas.
        /// </remarks>
        private static readonly Regex Identifier = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.CultureInvariant);

        /// <summary>Code de winget pour un paquet déjà installé : ce n'est pas un échec.</summary>
        internal const int AlreadyInstalled = unchecked((int)0x8A150061);

        public static bool IsIdentifier(string? value) => value != null && Identifier.IsMatch(value);

        /// <summary>
        /// Demande à winget la liste de ce qu'il reconnaît parmi les logiciels installés.
        /// </summary>
        /// <remarks>
        /// Par <c>winget export</c>, dont le fichier JSON ne dépend pas de la langue du système,
        /// contrairement au tableau de <c>winget list</c> qui tronque les noms longs. Compter
        /// une trentaine de secondes : winget met sa source à jour avant de répondre.
        /// </remarks>
        public static async Task<WingetListing> ListAsync(
            IProcessRunner processes, IFileSystemGateway files, string exportPath, CancellationToken cancellationToken)
        {
            var run = await processes.RunAsync(
                new ProcessRequest("winget.exe",
                    "export -o \"" + exportPath + "\" -s " + Source + " --accept-source-agreements")
                {
                    Timeout = TimeSpan.FromMinutes(3),
                },
                cancellationToken).ConfigureAwait(false);

            if (run.LaunchFailed)
                return new WingetListing
                {
                    Failure = "winget n'est pas installé sur cette machine. Il vient avec le « Programme d'installation " +
                              "d'application » du Microsoft Store, sur Windows 10 1809 et suivants.",
                };

            if (run.TimedOut)
                return new WingetListing { Failure = "winget n'a pas répondu en trois minutes." };

            var text = files.ReadText(exportPath);
            if (!text.HasValue)
                return new WingetListing
                {
                    Failure = "winget n'a produit aucune liste (" + DriverBackup.Tail(run) + ").",
                };

            var packages = Parse(text.Value);
            return packages.Count == 0
                ? new WingetListing { Failure = "winget ne reconnaît aucun des logiciels installés." }
                : new WingetListing { Packages = packages };
        }

        /// <summary>Les identifiants de la source winget d'un export, ou d'une sauvegarde.</summary>
        internal static IReadOnlyList<string> Parse(string json)
        {
            var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = JObject.Parse(json);
                if (root["Sources"] is not JArray sources) return new List<string>();

                foreach (var source in sources)
                {
                    var name = source["SourceDetails"]?["Name"]?.Value<string>();
                    if (!string.Equals(name, Source, StringComparison.OrdinalIgnoreCase)) continue;
                    if (source["Packages"] is not JArray packages) continue;

                    foreach (var package in packages)
                    {
                        var id = package["PackageIdentifier"]?.Value<string>();
                        if (IsIdentifier(id)) result.Add(id!);
                    }
                }
            }
            catch (JsonException)
            {
                return new List<string>();
            }

            return new List<string>(result);
        }

        /// <summary>
        /// Le fichier déposé dans la sauvegarde, au format de <c>winget import</c>.
        /// </summary>
        /// <remarks>
        /// Le même format que celui de winget lui-même : la sauvegarde se réinstalle aussi bien par
        /// cet outil que par <c>winget import</c> à la main, ou par le script posé à côté.
        /// </remarks>
        public static string Document(IEnumerable<string> packages)
        {
            var list = new JArray();
            foreach (var id in packages)
                if (IsIdentifier(id)) list.Add(new JObject { ["PackageIdentifier"] = id });

            var document = new JObject
            {
                ["$schema"] = "https://aka.ms/winget-packages.schema.2.0.json",
                ["CreationDate"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                ["Sources"] = new JArray
                {
                    new JObject
                    {
                        ["Packages"] = list,
                        ["SourceDetails"] = new JObject
                        {
                            ["Argument"] = "https://cdn.winget.microsoft.com/cache",
                            ["Identifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                            ["Name"] = Source,
                            ["Type"] = "Microsoft.PreIndexed.Package",
                        },
                    },
                },
            };

            return document.ToString(Formatting.Indented);
        }

        /// <summary>Le script de secours : double-cliquer dessus réinstalle tout, sans LDI12.</summary>
        /// <remarks>
        /// En ASCII seulement : cmd.exe lit un script dans la page de codes OEM, un accent y
        /// deviendrait un caractère parasite. Écrit sans marque d'ordre d'octets, que cmd.exe
        /// prendrait pour le début de la première commande.
        /// </remarks>
        public static string Script()
            => "@echo off\r\n" +
               "rem Reinstalle par winget les applications de cette sauvegarde LDI12. Connexion Internet requise.\r\n" +
               "winget import -i \"%~dp0" + FileName + "\" --accept-package-agreements --accept-source-agreements --ignore-unavailable\r\n" +
               "pause\r\n";

        /// <summary>Une ligne par identifiant coché.</summary>
        public static string Encode(IEnumerable<string> packages)
        {
            var builder = new StringBuilder();
            foreach (var id in packages)
                if (IsIdentifier(id)) builder.Append(id).Append('\n');
            return builder.ToString();
        }

        internal static IReadOnlyList<string> Decode(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text!.Split('\n'))
            {
                var id = line.Trim();
                if (IsIdentifier(id) && seen.Add(id)) result.Add(id);
            }

            return result;
        }
    }

    public sealed class ApplicationRestorePlan
    {
        public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Réinstalle, par winget, les applications listées dans une sauvegarde LDI12.
    /// </summary>
    /// <remarks>
    /// Une application à la fois, avec un résultat pour chacune : <c>winget import</c> ferait tout
    /// d'un coup, mais ne dirait qu'à la fin, et en bloc, ce qui a échoué.
    /// <para>
    /// Dans la session du client, pas en administrateur : winget est une application de l'utilisateur,
    /// et un logiciel qui s'installe pour la machine demande lui-même son élévation. C'est pourquoi
    /// des invites Windows peuvent apparaître pendant l'opération.
    /// </para>
    /// </remarks>
    public sealed class RestoreApplicationsAction : IRepairAction
    {
        public const string SourceParameter = "source";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestoreApplications,
            DisplayName = "Réinstaller les applications de la sauvegarde",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Moderate,
            Purpose = "Réinstalle par winget, dans leur dernière version, les applications listées dans une sauvegarde LDI12.",
            PlainPurpose = "Vos logiciels sont réinstallés automatiquement, dans leur version la plus récente.",
            TypicalDuration = TimeSpan.FromMinutes(20),
            HardTimeout = TimeSpan.FromHours(4),
            Requirements = new ActionRequirements
            {
                MinimumBuild = 17763,
                Workaround = "Réinstaller les logiciels à la main, d'après la fiche de réinstallation de la sauvegarde.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null) return ActionReadiness.No("Aucune sauvegarde n'a été désignée.");

            return context.Files.DirectoryExists(source)
                ? ActionReadiness.Ready
                : ActionReadiness.No("Le dossier « " + source + " » n'existe pas ou n'est pas accessible.");
        }

        public async Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var source = context.Parameter(SourceParameter);
            var backup = source == null ? null : RestoreCatalog.Find(context.Files, source, out _);
            if (backup == null) return Blocked("Aucune sauvegarde LDI12 n'a été trouvée dans « " + source + " ».");

            var document = context.Files.ReadText(Path.Combine(backup, WingetApplications.FileName));
            var packages = document.HasValue ? WingetApplications.Parse(document.Value) : Array.Empty<string>();

            if (packages.Count == 0)
                return new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = "La sauvegarde « " + Path.GetFileName(backup) + " » ne liste aucune application à réinstaller.",
                };

            if (context.Platform.Profile.Build < 17763)
                return Blocked("winget demande Windows 10 1809 ou plus récent. " + Descriptor.Requirements.Workaround);

            var version = await context.Processes.RunAsync(
                new ProcessRequest("winget.exe", "--version") { Timeout = TimeSpan.FromSeconds(30) },
                cancellationToken).ConfigureAwait(false);

            if (!version.Completed || version.ExitCode != 0)
                return Blocked("winget n'est pas disponible sur cette machine. Installer ou mettre à jour le « Programme " +
                               "d'installation d'application » depuis le Microsoft Store, puis préparer de nouveau.");

            var willDo = new List<string>();
            foreach (var id in packages) willDo.Add("Installer " + id);

            return new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = packages.Count + " application(s) seront réinstallées par winget, dans leur dernière version.",
                WillDo = willDo,
                WillNotDo = new[]
                {
                    "Ne désinstalle rien : une application déjà présente est laissée telle quelle.",
                    "Ne reprend ni les licences ni les numéros de série : un logiciel payant peut redemander son activation.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Connexion Internet", "nécessaire : chaque logiciel est téléchargé chez son éditeur",
                        PreviewLineKind.Caution),
                    new PreviewLine("Invites Windows", "un logiciel qui s'installe pour tout l'ordinateur peut demander " +
                                                       "une autorisation : rester devant l'écran", PreviewLineKind.Caution),
                },
                Plan = new ApplicationRestorePlan { Packages = packages },
            };
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not ApplicationRestorePlan plan)
                return ActionOutcome.Simple(ActionStatus.Failed, "La liste des applications n'a pas été établie : rien n'a été installé.");

            var stopwatch = Stopwatch.StartNew();
            var details = new List<string>();
            int installed = 0, present = 0, failed = 0;

            for (var index = 0; index < plan.Packages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = plan.Packages[index];
                progress?.Report(new ActionProgress(
                    "Installation de " + id + " (" + (index + 1) + " sur " + plan.Packages.Count + ")…",
                    (double)index / plan.Packages.Count));

                var run = await context.Processes.RunAsync(
                    new ProcessRequest("winget.exe",
                        "install --id " + id + " -e -s winget --silent --accept-package-agreements --accept-source-agreements")
                    {
                        Timeout = TimeSpan.FromMinutes(30),
                    },
                    cancellationToken).ConfigureAwait(false);

                if (run.Completed && run.ExitCode == 0) { installed++; continue; }
                if (run.Completed && run.ExitCode == WingetApplications.AlreadyInstalled) { present++; continue; }

                failed++;
                details.Add(id + " : non installé, " + DriverBackup.Tail(run) + ".");
            }

            if (present > 0) details.Insert(0, present + " application(s) étaient déjà installées.");
            if (installed > 0) details.Insert(0, installed + " application(s) installées.");

            stopwatch.Stop();
            return new ActionOutcome
            {
                Status = failed == 0 ? ActionStatus.Succeeded
                    : installed + present > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = installed + " application(s) réinstallée(s)" +
                          (present > 0 ? ", " + present + " déjà présente(s)" : string.Empty) +
                          (failed > 0 ? ", " + failed + " en échec." : "."),
                Details = details,
                Duration = stopwatch.Elapsed,
            };
        }

        private static ActionPreview Blocked(string reason)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = reason, Blocker = reason };
    }
}
