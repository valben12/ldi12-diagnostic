using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Assistant
{
    /// <summary>
    /// La fiche de la machine, envoyée d'emblée avec la demande : de quoi conclure sans rien
    /// relire dans la plupart des cas.
    /// </summary>
    /// <remarks>
    /// <b>Ce qui coûte, c'est ce qu'on renvoie à chaque tour.</b> Chaque appel à l'API renvoie
    /// toute la conversation : un relevé lu en JSON au premier tour est repayé à tous les
    /// suivants. Une fiche courte, en lignes de texte, qui dit d'emblée l'essentiel (matériel,
    /// périphériques en erreur, constats, pilotes qui comptent, écrans bleus) évite la plupart
    /// des lectures, et celles qui restent sont ciblées.
    /// </remarks>
    public static class MachineBrief
    {
        /// <summary>Les classes de pilotes qui expliquent l'essentiel des pannes.</summary>
        private static readonly string[] KeyDriverClasses =
        {
            "DISPLAY", "NET", "MEDIA", "AUDIOENDPOINT", "SYSTEM", "HDC", "SCSIADAPTER", "BLUETOOTH", "CAMERA", "IMAGE", "USB",
        };

        public static string Compose(SystemSnapshot snapshot, AssistantMode mode)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var text = new StringBuilder(6000);
            var windows = snapshot.Platform.Windows;

            text.AppendLine("## Machine");
            Line(text, "PC", Join(snapshot.Machine.Manufacturer.Or(string.Empty), snapshot.Machine.Model.Or(string.Empty)));
            Line(text, "Windows", Join(windows.ProductName ?? string.Empty, windows.DisplayVersion ?? string.Empty, "build " + windows.Build));
            Line(text, "Processeur", snapshot.Hardware.Cpu.Model.Or(string.Empty));
            Line(text, "Mémoire", snapshot.Hardware.Memory.TotalBytes.HasValue ? ValueFormat.Bytes(snapshot.Hardware.Memory.TotalBytes.Value) : string.Empty);
            foreach (var gpu in snapshot.Hardware.Gpus)
                Line(text, "Carte graphique", Join(gpu.Model.Or("?"), "pilote " + gpu.DriverVersion.Or("?"),
                    gpu.DriverDate.HasValue ? gpu.DriverDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
                    gpu.UsesGenericMicrosoftDriver.Or(false) ? "PILOTE GÉNÉRIQUE MICROSOFT" : string.Empty));
            foreach (var disk in snapshot.Storage.PhysicalDisks)
                Line(text, "Disque", Join(disk.Model.Or("?"), disk.CapacityBytes.HasValue ? ValueFormat.Bytes(disk.CapacityBytes.Value) : string.Empty,
                    disk.MediaType.HasValue ? disk.MediaType.Value.ToString() : string.Empty, disk.IsSystemDisk.Or(false) ? "système" : string.Empty));
            if (snapshot.Score != null) Line(text, "État général", snapshot.Score.Global + "/100");

            var faulty = snapshot.Windows.Devices.Where(d => d.ProblemCode != 0 || d.IsUnknownDevice).ToList();
            text.AppendLine().AppendLine("## Périphériques en erreur (" + faulty.Count + ")");
            if (faulty.Count == 0) text.AppendLine("aucun");
            foreach (var device in faulty.Take(25))
                text.Append("- ").Append(device.Name).Append(" | classe ").Append(device.DeviceClass ?? "?")
                    .Append(" | code ").Append(device.ProblemCode).Append(device.ProblemLabel != null ? " (" + device.ProblemLabel + ")" : string.Empty)
                    .Append(" | id ").AppendLine(device.HardwareId ?? "?");

            var findings = snapshot.Findings.Where(f => f.Severity >= Severity.Warning).OrderByDescending(f => f.Severity).ToList();
            text.AppendLine().AppendLine("## Constats du diagnostic (" + findings.Count + ")");
            if (findings.Count == 0) text.AppendLine("aucun problème notable");
            foreach (var finding in findings.Take(20))
                text.Append("- [").Append(finding.Severity).Append("] ").Append(finding.Title)
                    .Append(" : ").AppendLine(Short(finding.TechnicalDetail, 160));

            var events = snapshot.Windows.Events;
            text.AppendLine().AppendLine("## Journaux");
            Line(text, "Écrans bleus", events.Bsods.Count == 0 ? "aucun" : string.Join(" ; ", events.Bsods.Take(5).Select(b =>
                b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + (b.BugCheckCode ?? "?"))));
            Line(text, "Arrêts brutaux", events.UnexpectedShutdowns.HasValue ? events.UnexpectedShutdowns.Value.ToString(CultureInfo.InvariantCulture) : "?");
            foreach (var error in events.CriticalErrors.Concat(events.RecurringErrors).OrderByDescending(e => e.Count).Take(6))
                text.Append("- ").Append(error.Source).Append(' ').Append(error.EventId).Append(" ×").Append(error.Count)
                    .Append(" : ").AppendLine(Short(error.SampleMessage, 120));

            if (mode == AssistantMode.Drivers)
            {
                var drivers = snapshot.Windows.Drivers
                    .Where(d => d.IsMicrosoftGeneric || d.IsSigned == false
                                || KeyDriverClasses.Contains((d.DeviceClass ?? string.Empty).ToUpperInvariant()))
                    .ToList();
                text.AppendLine().AppendLine("## Pilotes importants (" + drivers.Count + " sur " + snapshot.Windows.Drivers.Count + ")");
                foreach (var driver in drivers.Take(60)) text.AppendLine("- " + MachineData.DriverLine(driver));
            }
            else
            {
                var startup = snapshot.Windows.Startup.Where(s => s.Location != StartupLocation.ScheduledTask).ToList();
                text.AppendLine().AppendLine("## Au démarrage (" + startup.Count + ")");
                foreach (var item in startup.Take(25)) text.AppendLine("- " + MachineData.StartupLine(item));

                var services = snapshot.Windows.Services.Where(s => s.Deviation != null).ToList();
                if (services.Count > 0)
                {
                    text.AppendLine().AppendLine("## Services anormaux");
                    foreach (var service in services.Take(10)) text.AppendLine("- " + MachineData.ServiceLine(service));
                }

                var top = snapshot.Performance.TopByCpu.Concat(snapshot.Performance.TopByMemory)
                    .Where(p => !p.IsSystem).GroupBy(p => p.Name).Select(g => g.First()).Take(6).ToList();
                if (top.Count > 0)
                {
                    text.AppendLine().AppendLine("## Processus gourmands");
                    foreach (var process in top)
                        text.Append("- ").Append(process.Name).Append(" : ")
                            .Append(process.CpuPercent.HasValue ? Math.Round(process.CpuPercent.Value) + " % CPU, " : string.Empty)
                            .AppendLine(process.WorkingSetBytes.HasValue ? ValueFormat.Bytes(process.WorkingSetBytes.Value) : string.Empty);
                }
            }

            return text.ToString();
        }

        private static void Line(StringBuilder text, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.Append(label).Append(" : ").AppendLine(value.Trim());
        }

        private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        internal static string Short(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var flat = text!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }
    }
}
