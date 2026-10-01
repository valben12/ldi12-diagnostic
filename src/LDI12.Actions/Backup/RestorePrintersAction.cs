using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    public sealed class PrinterRestorePlan
    {
        /// <summary>Les imprimantes à recréer : partagées et réseau.</summary>
        public IReadOnlyList<PrinterEntry> Printers { get; init; } = Array.Empty<PrinterEntry>();

        /// <summary>L'imprimante à remettre par défaut ; nulle si aucune ne l'était.</summary>
        public string? Default { get; init; }
    }

    /// <summary>
    /// Réinstalle les imprimantes relevées avec une sauvegarde LDI12.
    /// </summary>
    /// <remarks>
    /// <b>Après les pilotes.</b> Une imprimante réseau se recrée avec son port (son adresse IP) et
    /// son pilote : celui-ci est pris dans le magasin de pilotes, où la réinstallation des pilotes
    /// de la sauvegarde vient de le remettre. Une imprimante partagée par un autre poste se
    /// reconnecte, et Windows récupère son pilote auprès de lui. Une imprimante USB n'a rien à
    /// recréer : son pilote suffit, Windows la retrouve au branchement.
    /// <para>
    /// Rien n'est supprimé, et une imprimante qui existe déjà sous ce nom est laissée telle quelle.
    /// </para>
    /// </remarks>
    public sealed class RestorePrintersAction : IRepairAction
    {
        public const string SourceParameter = "source";

        /// <summary>Le compte de la session du client, pour savoir si l'invite a pris un autre compte.</summary>
        public const string SessionUserParameter = "session-user";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestorePrinters,
            DisplayName = "Réinstaller les imprimantes de la sauvegarde",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Storage,
            Risk = ActionRisk.Low,
            Purpose = "Recrée les imprimantes réseau (port et pilote) et reconnecte les imprimantes partagées relevées " +
                      "dans une sauvegarde LDI12, puis remet l'imprimante par défaut.",
            PlainPurpose = "Vos imprimantes sont remises en place, et celle que vous utilisiez par défaut le redevient.",
            TypicalDuration = TimeSpan.FromMinutes(2),
            HardTimeout = TimeSpan.FromMinutes(20),
            Requirements = new ActionRequirements
            {
                RequiresElevation = true,
                MinimumBuild = 9200,
                Workaround = "Ajouter les imprimantes à la main d'après la fiche de réinstallation de la sauvegarde.",
            },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            var source = context.Parameter(SourceParameter);
            if (source == null) return ActionReadiness.No("Aucune sauvegarde n'a été désignée.");
            if (!context.Files.DirectoryExists(source))
                return ActionReadiness.No("Le dossier « " + source + " » n'existe pas ou n'est pas accessible.");
            if (context.Platform.Profile.Build < 9200)
                return ActionReadiness.No("La réinstallation des imprimantes demande Windows 8 ou plus récent.",
                    Descriptor.Requirements.Workaround);

            return context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("le port d'une imprimante réseau ne se crée qu'en administrateur");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var source = context.Parameter(SourceParameter);
            var backup = source == null ? null : RestoreCatalog.Find(context.Files, source, out _);
            if (backup == null)
                return Task.FromResult(Blocked("Aucune sauvegarde LDI12 n'a été trouvée dans « " + source + " »."));

            var document = context.Files.ReadText(Path.Combine(backup, MachineSettings.FileName));
            var printers = MachineSettings.Read(document.HasValue ? document.Value : null).Printers;

            var recreate = new List<PrinterEntry>();
            var plugged = new List<string>();
            string? fallbackDefault = null;
            foreach (var printer in printers)
            {
                if (printer.Kind == PrinterKind.Local) plugged.Add(printer.Name);
                else recreate.Add(printer);
                if (printer.IsDefault) fallbackDefault = printer.Name;
            }

            if (recreate.Count == 0 && fallbackDefault == null)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.NothingToDo,
                    Summary = printers.Count == 0
                        ? "La sauvegarde ne relève aucune imprimante."
                        : "Les imprimantes de la sauvegarde sont branchées en USB : leur pilote suffit, Windows les retrouve au branchement.",
                });

            var willDo = new List<string>();
            foreach (var printer in recreate)
                willDo.Add(printer.Kind == PrinterKind.Connection
                    ? "Reconnecter l'imprimante partagée " + printer.Name
                    : "Recréer l'imprimante réseau « " + printer.Name + " » à l'adresse " + printer.Host + ", pilote « " + printer.Driver + " »");
            if (fallbackDefault != null) willDo.Add("Remettre « " + fallbackDefault + " » comme imprimante par défaut");

            // L'invite administrateur a pu demander un autre compte que celui du client : les
            // imprimantes partagées et l'imprimante par défaut sont propres à chaque compte.
            var measurements = new List<PreviewLine>
            {
                new PreviewLine("Pilotes", "une imprimante réseau demande son pilote : réinstaller d'abord les pilotes de la sauvegarde"),
            };
            var sessionUser = context.Parameter(SessionUserParameter);
            if (sessionUser != null && !string.Equals(sessionUser, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                measurements.Add(new PreviewLine("Autre compte",
                    "l'invite administrateur a ouvert le compte « " + Environment.UserName + " », pas celui du client (« " + sessionUser +
                    " ») : les imprimantes partagées et l'imprimante par défaut iront sur ce compte. Les ajouter depuis la " +
                    "session du client, d'après la fiche", PreviewLineKind.Caution));

            var willNotDo = new List<string>
            {
                "Ne supprime ni ne modifie aucune imprimante déjà installée : une imprimante qui existe sous ce nom est laissée telle quelle.",
            };
            if (plugged.Count > 0)
                willNotDo.Add("Ne recrée pas " + string.Join(", ", plugged) + " : branchée en USB, elle revient au branchement, " +
                              "avec le pilote de la sauvegarde.");

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = recreate.Count + " imprimante(s) seront remises en place.",
                WillDo = willDo,
                WillNotDo = willNotDo,
                Measurements = measurements,
                Plan = new PrinterRestorePlan { Printers = recreate, Default = fallbackDefault },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview?.Plan is not PrinterRestorePlan plan)
                return ActionOutcome.Simple(ActionStatus.Failed, "La liste des imprimantes n'a pas été établie : rien n'a été fait.");

            var stopwatch = Stopwatch.StartNew();
            var details = new List<string>();
            int done = 0, failed = 0;

            for (var index = 0; index < plan.Printers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var printer = plan.Printers[index];
                progress?.Report(new ActionProgress("Imprimante " + printer.Name + "…", (double)index / Math.Max(1, plan.Printers.Count)));

                var run = await context.Processes.RunAsync(
                    MachineSettings.PowerShell(Script(printer), TimeSpan.FromMinutes(3)), cancellationToken).ConfigureAwait(false);
                var answer = Answer(run);

                if (answer == "OK") { done++; details.Add(printer.Name + " : remise en place."); }
                else if (answer == "EXISTE") { done++; details.Add(printer.Name + " : déjà présente, laissée telle quelle."); }
                else
                {
                    failed++;
                    details.Add(printer.Name + " : non remise, " + answer +
                                (printer.Kind == PrinterKind.Network ? ". Vérifier que son pilote est installé." : "."));
                }
            }

            if (plan.Default != null)
            {
                var run = await context.Processes.RunAsync(
                    MachineSettings.PowerShell(DefaultScript(plan.Default), TimeSpan.FromMinutes(1)), cancellationToken).ConfigureAwait(false);
                details.Add(Answer(run) == "OK"
                    ? "« " + plan.Default + " » est de nouveau l'imprimante par défaut."
                    : "L'imprimante par défaut n'a pas pu être remise : la choisir dans Paramètres, Imprimantes.");
            }

            stopwatch.Stop();
            return new ActionOutcome
            {
                Status = failed == 0 ? ActionStatus.Succeeded : done > 0 ? ActionStatus.PartiallySucceeded : ActionStatus.Failed,
                Summary = done + " imprimante(s) remise(s) en place" + (failed > 0 ? ", " + failed + " en échec." : "."),
                Details = details,
                Duration = stopwatch.Elapsed,
            };
        }

        /// <summary>Le script d'une imprimante : il répond OK, EXISTE ou ERREUR suivi de la raison.</summary>
        internal static string Script(PrinterEntry printer)
        {
            var name = MachineSettings.Quote(printer.Name);
            var body = printer.Kind == PrinterKind.Connection
                ? "if (Get-Printer -Name " + name + " -ErrorAction SilentlyContinue) { 'EXISTE'; return }\n" +
                  "Add-Printer -ConnectionName " + name + " -ErrorAction Stop\n" +
                  "'OK'\n"
                : "if (Get-Printer -Name " + name + " -ErrorAction SilentlyContinue) { 'EXISTE'; return }\n" +
                  "$port = " + MachineSettings.Quote(printer.Port) + "\n" +
                  "if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $port -PrinterHostAddress " +
                  MachineSettings.Quote(printer.Host ?? string.Empty) + " -ErrorAction Stop }\n" +
                  "$driver = " + MachineSettings.Quote(printer.Driver) + "\n" +
                  "if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $driver -ErrorAction Stop }\n" +
                  "Add-Printer -Name " + name + " -DriverName $driver -PortName $port -ErrorAction Stop\n" +
                  "'OK'\n";

            return "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
                   "try {\n" + body + "} catch { 'ERREUR ' + $_.Exception.Message }\n";
        }

        /// <summary>
        /// Remet l'imprimante par défaut, et empêche Windows 10 de la changer de lui-même pour la
        /// dernière utilisée.
        /// </summary>
        internal static string DefaultScript(string name)
            => "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
               "try {\n" +
               "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Windows' -Name LegacyDefaultPrinterMode -Value 1 -Type DWord -ErrorAction SilentlyContinue\n" +
               "(New-Object -ComObject WScript.Network).SetDefaultPrinter(" + MachineSettings.Quote(name) + ")\n" +
               "'OK'\n" +
               "} catch { 'ERREUR ' + $_.Exception.Message }\n";

        private static string Answer(ProcessResult run)
        {
            if (!run.Completed) return DriverBackup.Tail(run);

            string? last = null;
            foreach (var line in run.StandardOutput.Split('\r', '\n'))
                if (line.Trim().Length > 0) last = line.Trim();

            if (last == null) return "Windows n'a rien répondu";
            return last.StartsWith("ERREUR ", StringComparison.Ordinal) ? last.Substring(7) : last;
        }

        private static ActionPreview Blocked(string reason)
            => new ActionPreview { Outcome = PreviewOutcome.Blocked, Summary = reason, Blocker = reason };
    }
}
