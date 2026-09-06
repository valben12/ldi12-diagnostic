using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Ce qui se lance sans qu'on le demande : registre, dossiers de démarrage et tâches
    /// planifiées.
    /// </summary>
    /// <remarks>
    /// Le registre et les dossiers se lisent sans WMI : Win32_StartupCommand est lent et,
    /// surtout, oublie les entrées désactivées ainsi que la vue 32 bits d'un système 64 bits.
    /// Or c'est précisément une entrée oubliée qui explique le démarrage interminable dont se
    /// plaint le client.
    /// <para>
    /// <b>Et l'entrée la plus oubliée n'est pas dans le registre.</b> Le gestionnaire des tâches
    /// de Windows, où le technicien va regarder, n'affiche pas les tâches planifiées : un
    /// logiciel qui se relance à chaque ouverture de session par une tâche est invisible dans
    /// l'onglet « Démarrage » comme il l'était dans ce module. Les tâches déclenchées à
    /// l'ouverture de session ou au démarrage rejoignent donc la liste ; les autres sont
    /// inventoriées à part, parce qu'elles répondent à une autre question.
    /// </para>
    /// </remarks>
    public sealed class StartupProbe : IDiagnosticProbe
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string RunKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Startup,
            DisplayName = "Démarrage et tâches planifiées",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(1.5),
            HardTimeout = TimeSpan.FromSeconds(45),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var items = new List<StartupItem>();

            CollectRegistry(context, RegistryHive.LocalMachine, RunKey, StartupLocation.RegistryRunMachine, items);
            CollectRegistry(context, RegistryHive.LocalMachine, RunKeyWow, StartupLocation.RegistryRunMachine, items);
            CollectRegistry(context, RegistryHive.LocalMachine, RunOnceKey, StartupLocation.RegistryRunOnce, items);
            CollectRegistry(context, RegistryHive.CurrentUser, RunKey, StartupLocation.RegistryRunUser, items);
            CollectRegistry(context, RegistryHive.CurrentUser, RunOnceKey, StartupLocation.RegistryRunOnce, items);

            CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                StartupLocation.StartupFolderMachine, items);
            CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                StartupLocation.StartupFolderUser, items);

            var tasks = TaskSchedulerReader.Read(cancellationToken);
            foreach (var task in tasks.Tasks) AppendIfStartup(task, items);

            context.Draft.SetStartup(items, Inventory(tasks));

            var orphans = 0;
            foreach (var item in items) if (item.TargetMissing) orphans++;

            var summary = items.Count + " programme(s) au démarrage";
            if (orphans > 0) summary += ", dont " + orphans + " pointant vers un fichier absent";
            summary += tasks.Failed
                ? ". Les tâches planifiées n'ont pas pu être lues."
                : " · " + tasks.Tasks.Count + " tâche(s) planifiée(s) ajoutée(s) sur " + tasks.Total + ".";

            return Task.FromResult(tasks.Failed
                ? ProbeOutcome.Partial(summary)
                : ProbeOutcome.Ok(summary));
        }

        /// <summary>
        /// Une tâche déclenchée à l'ouverture de session ou au démarrage est un programme de
        /// démarrage, et se compte avec les autres.
        /// </summary>
        /// <remarks>
        /// Les tâches désactivées ne sont pas retenues : elles ne se lancent pas, et les compter
        /// gonflerait la liste que le technicien va chercher à réduire. Elles restent visibles
        /// dans l'inventaire des tâches, qui, lui, dit leur état.
        /// </remarks>
        private static void AppendIfStartup(ScheduledTaskInfo task, ICollection<StartupItem> items)
        {
            if (!task.RunsAtStartup) return;

            var trigger = TaskTriggerKind.Logon;
            foreach (var kind in task.Triggers)
                if (kind == TaskTriggerKind.Boot) { trigger = kind; break; }

            items.Add(new StartupItem
            {
                Name = task.Name,
                Command = task.ImagePath ?? task.Path,
                Location = StartupLocation.ScheduledTask,
                ImagePath = task.ImagePath,
                TargetMissing = task.TargetMissing,
                Trigger = trigger,
                DelaySeconds = task.DelaySeconds,
            });
        }

        private static ScheduledTaskInventory Inventory(TaskSchedulerReader.Result tasks)
        {
            if (tasks.Failed)
            {
                var reason = tasks.Reason ?? "Le planificateur de tâches n'a pas pu être lu.";
                return new ScheduledTaskInventory
                {
                    TotalCount = Measured.Missing<int>(reason),
                    MicrosoftFolderCount = Measured.Missing<int>(reason),
                    Unreadable = Measured.Missing<int>(reason),
                };
            }

            return new ScheduledTaskInventory
            {
                TotalCount = Measured.Ok(tasks.Total, DataSource.NativeApi),
                MicrosoftFolderCount = Measured.Ok(tasks.InMicrosoftFolder, DataSource.NativeApi),
                Tasks = tasks.Tasks,
                Unreadable = Measured.Ok(tasks.Unreadable, DataSource.NativeApi),
            };
        }

        private static void CollectRegistry(
            ProbeContext context, RegistryHive hive, string key, StartupLocation location, List<StartupItem> items)
        {
            foreach (var name in context.Registry.GetValueNames(hive, key))
            {
                var command = context.Registry.ReadString(hive, key, name);
                if (string.IsNullOrWhiteSpace(command)) continue;

                var imagePath = ExtractImagePath(command!);
                items.Add(new StartupItem
                {
                    Name = name,
                    Command = command!,
                    Location = location,
                    ImagePath = imagePath,
                    TargetMissing = imagePath != null && !FileExists(imagePath),
                });
            }
        }

        private static void CollectFolder(string folder, StartupLocation location, List<StartupItem> items)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(name, "desktop", StringComparison.OrdinalIgnoreCase)) continue;

                items.Add(new StartupItem
                {
                    Name = name,
                    Command = file,
                    Location = location,
                    ImagePath = file,
                    TargetMissing = false,
                });
            }
        }

        /// <summary>
        /// Isole l'exécutable d'une ligne de commande : guillemets, ou premier segment se
        /// terminant par « .exe ». Une heuristique suffit : elle ne sert qu'à vérifier que la
        /// cible existe encore.
        /// </summary>
        private static string? ExtractImagePath(string command)
        {
            var trimmed = command.Trim();
            if (trimmed.Length == 0) return null;

            if (trimmed[0] == '"')
            {
                var end = trimmed.IndexOf('"', 1);
                return end > 1 ? trimmed.Substring(1, end - 1) : null;
            }

            var exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) return trimmed.Substring(0, exe + 4);

            var space = trimmed.IndexOf(' ');
            return space > 0 ? trimmed.Substring(0, space) : trimmed;
        }

        private static bool FileExists(string path)
        {
            try { return File.Exists(Environment.ExpandEnvironmentVariables(path)); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return true;   // chemin illisible : ne pas accuser à tort
            }
        }
    }
}
