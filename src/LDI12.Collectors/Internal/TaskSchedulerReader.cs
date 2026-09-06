using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using LDI12.Core.Model;
using Microsoft.CSharp.RuntimeBinder;

namespace LDI12.Collectors.Internal
{
    /// <summary>
    /// Lecture du planificateur de tâches de Windows.
    /// </summary>
    /// <remarks>
    /// <b>Par l'interface du planificateur, jamais par le dossier ni par <c>schtasks</c>.</b> Le
    /// dossier <c>%SystemRoot%\System32\Tasks</c> n'est lisible qu'en administrateur ; la
    /// commande <c>schtasks</c> rend des colonnes et des états traduits, qu'il faudrait analyser
    /// pour les comprendre. L'interface, elle, existe depuis Windows Vista, se lit en session
    /// normale, et rend des <b>entiers</b> (types de déclencheurs, types d'actions, codes de
    /// retour) qui ne dépendent d'aucune langue.
    /// <para>
    /// L'appel se fait en liaison tardive : aucun assemblage d'interopérabilité à embarquer dans
    /// l'exécutable unique, et une machine dont le service de planification est cassé rend une
    /// erreur au lieu d'empêcher le chargement du module.
    /// </para>
    /// </remarks>
    internal static class TaskSchedulerReader
    {
        /// <summary>Inclut les tâches masquées : ce sont précisément celles qu'on cherche.</summary>
        private const int IncludeHidden = 1;

        /// <summary>Action lançant un exécutable, par opposition à un composant logiciel.</summary>
        private const int ExecAction = 0;

        private const int MaxTasks = 2000;
        private const int MaxDepth = 8;

        /// <summary>
        /// Le planificateur date les tâches jamais exécutées d'une valeur sentinelle très
        /// ancienne (1899 ou 1999 selon les versions) plutôt que de laisser le champ vide.
        /// </summary>
        private static readonly DateTime NeverRunBefore = new DateTime(2000, 1, 1);

        public sealed class Result
        {
            public List<ScheduledTaskInfo> Tasks { get; } = new List<ScheduledTaskInfo>();
            public int Total { get; set; }
            public int InMicrosoftFolder { get; set; }
            public int Unreadable { get; set; }
            public bool Failed { get; set; }
            public string? Reason { get; set; }
        }

        public static Result Read(CancellationToken cancellationToken)
        {
            var result = new Result();

            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null)
            {
                result.Failed = true;
                result.Reason = "Le planificateur de tâches n'est pas disponible sur cette machine.";
                return result;
            }

            object? service = null;
            try
            {
                service = Activator.CreateInstance(type);
                dynamic scheduler = service!;
                scheduler.Connect();

                Walk(scheduler.GetFolder("\\"), 0, result, cancellationToken);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                result.Failed = true;
                result.Reason = "Le planificateur de tâches n'a pas répondu (" + ex.GetType().Name + ").";
            }
            finally
            {
                if (service != null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service);
            }

            return result;
        }

        private static void Walk(dynamic folder, int depth, Result result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                dynamic tasks = folder.GetTasks(IncludeHidden);
                int count = (int)tasks.Count;

                for (var index = 1; index <= count && result.Total < MaxTasks; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        Add(tasks[index], result);
                    }
                    catch (Exception ex) when (IsComFailure(ex))
                    {
                        // Une session normale ne lit pas tout le planificateur : la tâche refusée
                        // est comptée, et le rapport dira qu'il en reste.
                        result.Unreadable++;
                    }
                }
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                result.Unreadable++;
            }

            if (depth >= MaxDepth) return;

            try
            {
                dynamic folders = folder.GetFolders(0);
                int count = (int)folders.Count;
                for (var index = 1; index <= count; index++)
                    Walk(folders[index], depth + 1, result, cancellationToken);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                result.Unreadable++;
            }
        }

        private static void Add(dynamic task, Result result)
        {
            result.Total++;

            string path = task.Path ?? string.Empty;

            // Windows range l'essentiel de ses tâches sous ce dossier depuis Vista. Elles sont
            // comptées et non listées : à quelques exceptions près, elles ne sont pas le sujet.
            if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
            {
                result.InMicrosoftFolder++;
                return;
            }

            var triggers = new List<TaskTriggerKind>();
            int? delaySeconds = null;
            int? repetitionMinutes = null;
            string? imagePath = null;

            dynamic definition = task.Definition;

            dynamic triggerList = definition.Triggers;
            int triggerCount = (int)triggerList.Count;
            for (var index = 1; index <= triggerCount; index++)
            {
                dynamic trigger = triggerList[index];

                var kind = Classify((int)trigger.Type);
                if (!triggers.Contains(kind)) triggers.Add(kind);

                var delay = Seconds(Read(() => (string?)trigger.Delay));
                if (delay.HasValue && (!delaySeconds.HasValue || delay.Value < delaySeconds.Value))
                    delaySeconds = delay.Value;

                var interval = Seconds(Read(() => (string?)trigger.Repetition.Interval));
                if (interval.HasValue)
                {
                    var minutes = Math.Max(1, interval.Value / 60);
                    if (!repetitionMinutes.HasValue || minutes < repetitionMinutes.Value)
                        repetitionMinutes = minutes;
                }
            }

            dynamic actionList = definition.Actions;
            int actionCount = (int)actionList.Count;
            for (var index = 1; index <= actionCount && imagePath == null; index++)
            {
                dynamic action = actionList[index];
                if ((int)action.Type != ExecAction) continue;

                var candidate = Read(() => (string?)action.Path);
                if (!string.IsNullOrWhiteSpace(candidate)) imagePath = Clean(candidate!);
            }

            var lastRunValue = Read(() => (DateTime?)task.LastRunTime);
            var lastRun = lastRunValue.HasValue && lastRunValue.Value >= NeverRunBefore
                ? (DateTimeOffset?)new DateTimeOffset(lastRunValue.Value)
                : null;

            var lastResult = Read(() => (int?)task.LastTaskResult);

            result.Tasks.Add(new ScheduledTaskInfo
            {
                Path = path,
                Name = Read(() => (string?)task.Name) ?? path,
                Enabled = Read(() => (bool?)task.Enabled) ?? false,
                InMicrosoftFolder = false,
                Triggers = triggers,
                DelaySeconds = delaySeconds,
                RepetitionMinutes = repetitionMinutes,
                ImagePath = imagePath,
                TargetMissing = imagePath != null && !Exists(imagePath),
                LastRun = lastRun,
                LastResult = lastResult,

                // Une tâche jamais lancée n'a pas de verdict : lui en donner un ferait passer
                // « pas encore essayé » pour « a fonctionné ».
                ResultKind = lastRun == null
                    ? TaskResultKind.Unknown
                    : TaskResultCatalog.Classify(lastResult),
            });
        }

        /// <summary>Regroupement des types de déclencheurs du planificateur.</summary>
        /// <remarks>
        /// 8 et 9 sont les deux seuls qui font d'une tâche un programme de démarrage ; 7,
        /// l'enregistrement, ne se produit qu'une fois, à l'installation du logiciel, et le
        /// confondre avec un démarrage gonflerait la liste de programmes qui ne se relancent
        /// jamais.
        /// </remarks>
        private static TaskTriggerKind Classify(int type) => type switch
        {
            0 => TaskTriggerKind.Event,
            1 => TaskTriggerKind.Scheduled,
            2 => TaskTriggerKind.Scheduled,
            3 => TaskTriggerKind.Scheduled,
            4 => TaskTriggerKind.Scheduled,
            5 => TaskTriggerKind.Scheduled,
            6 => TaskTriggerKind.Idle,
            7 => TaskTriggerKind.Registration,
            8 => TaskTriggerKind.Boot,
            9 => TaskTriggerKind.Logon,
            11 => TaskTriggerKind.SessionChange,
            _ => TaskTriggerKind.Unknown,
        };

        /// <summary>Durées ISO 8601 (« PT12M ») telles que le planificateur les écrit.</summary>
        private static int? Seconds(string? duration)
        {
            if (string.IsNullOrWhiteSpace(duration)) return null;

            try
            {
                var span = XmlConvert.ToTimeSpan(duration!);
                return span > TimeSpan.Zero ? (int?)span.TotalSeconds : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>Retire les guillemets dont certains installeurs entourent le chemin.</summary>
        private static string Clean(string path)
        {
            var trimmed = path.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            return Environment.ExpandEnvironmentVariables(trimmed.Trim());
        }

        private static bool Exists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Chemin illisible : on ne peut pas affirmer que le fichier manque.
                return true;
            }
        }

        /// <summary>
        /// Lecture d'une propriété qui n'existe pas sur tous les types de déclencheurs.
        /// </summary>
        /// <remarks>
        /// Un déclencheur quotidien n'a pas de délai, un déclencheur d'ouverture de session si.
        /// En liaison tardive, la différence se constate à l'appel et non à la compilation.
        /// </remarks>
        private static T? Read<T>(Func<T?> read) where T : struct
        {
            try { return read(); }
            catch (Exception ex) when (IsComFailure(ex)) { return null; }
        }

        private static string? Read(Func<string?> read)
        {
            try { return read(); }
            catch (Exception ex) when (IsComFailure(ex)) { return null; }
        }

        private static bool IsComFailure(Exception ex)
            => ex is COMException
               || ex is RuntimeBinderException
               || ex is UnauthorizedAccessException
               || ex is InvalidCastException
               || ex is NullReferenceException
               || ex is MissingMemberException
               || ex is InvalidOperationException;
    }

    /// <summary>
    /// Ce que vaut le code rendu par la dernière exécution d'une tâche.
    /// </summary>
    /// <remarks>
    /// <b>Le champ mélange deux choses.</b> Le planificateur y range ses propres états (la
    /// famille <c>0x00041300</c>, « prête », « en cours », « jamais exécutée ») et, quand la
    /// tâche a réellement tourné, le code de sortie du programme lancé. Un test sur « non nul »
    /// aurait donc annoncé des échecs là où il n'y en avait aucun : sur la machine de
    /// développement, cinq tâches sur quinze portaient un code non nul, toutes informatives.
    /// </remarks>
    internal static class TaskResultCatalog
    {
        /// <summary>Première valeur de la famille d'états du planificateur.</summary>
        private const int SchedulerStateFirst = 0x00041300;

        private const int SchedulerStateLast = 0x000413FF;

        /// <summary>Tâche interrompue par l'arrêt de la machine : ni réussite ni échec.</summary>
        private const int TerminatedByShutdown = 0x40010004;

        public static TaskResultKind Classify(int? result)
        {
            if (!result.HasValue) return TaskResultKind.Unknown;
            if (result.Value == 0) return TaskResultKind.Success;

            if (result.Value == TerminatedByShutdown) return TaskResultKind.Informational;
            if (result.Value >= SchedulerStateFirst && result.Value <= SchedulerStateLast)
                return TaskResultKind.Informational;

            // Le bit de gravité des codes de Windows : la tâche n'a pas pu être lancée.
            if (result.Value < 0) return TaskResultKind.TaskError;

            // Reste le code de sortie du programme lui-même.
            return TaskResultKind.ProgramError;
        }
    }
}
