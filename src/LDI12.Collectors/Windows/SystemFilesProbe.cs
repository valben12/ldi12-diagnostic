using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Intégrité des fichiers système, en lecture seule, sans jamais rien réparer.
    /// </summary>
    /// <remarks>
    /// Le verdict SFC ne vient pas de l'exécution de <c>sfc /scannow</c>, qui modifierait le
    /// système et prendrait plusieurs minutes, mais de la relecture du journal CBS. Ses lignes
    /// <c>[SR]</c> sont écrites en anglais quelle que soit la langue de Windows : elles sont donc
    /// analysables sans risque, contrairement à la sortie de l'outil.
    /// <para>
    /// Le contrôle DISM, lui, exige l'élévation. Sans privilèges, la sonde le dit plutôt que de
    /// laisser croire que l'image est saine.
    /// </para>
    /// </remarks>
    public sealed class SystemFilesProbe : IDiagnosticProbe
    {
        /// <summary>CBS.log dépasse couramment 50 Mo : seule la fin porte le dernier passage.</summary>
        private const int TailBytes = 2 * 1024 * 1024;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SystemFiles,
            DisplayName = "Intégrité des fichiers système",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(90),
            Isolation = IsolationMode.SeparateProcess,
            FullScanOnly = true,
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var sfc = ReadSfcVerdict(context);
            var componentStore = await ReadComponentStoreAsync(context, cancellationToken).ConfigureAwait(false);

            context.Draft.SetSystemFiles(new SystemFilesInfo
            {
                Sfc = sfc.Status,
                ComponentStore = componentStore.State,
                LastCheck = sfc.LastCheck,
                Evidence = sfc.Evidence,
            });

            var verdict = sfc.Status.Or(SfcStatus.Unknown);
            if (verdict == SfcStatus.CorruptionFound || verdict == SfcStatus.RepairFailed)
                return ProbeOutcome.Ok("Le dernier contrôle d'intégrité a détecté des fichiers système endommagés.");

            if (componentStore.State.Or(ComponentStoreState.Unknown) == ComponentStoreState.Repairable)
                return ProbeOutcome.Ok("Le magasin de composants est endommagé mais réparable.");

            if (!sfc.Status.HasValue && !componentStore.State.HasValue)
                return ProbeOutcome.Partial(sfc.Status.Reason ?? "Aucun contrôle d'intégrité exploitable.");

            return ProbeOutcome.Ok(verdict == SfcStatus.Clean
                ? "Dernier contrôle d'intégrité : aucun fichier endommagé."
                : "Aucune corruption connue des fichiers système.");
        }

        /// <summary>
        /// Les marqueurs [SR] de CBS.log ne sont jamais traduits : c'est ce qui rend cette
        /// analyse fiable sur un Windows en n'importe quelle langue.
        /// </summary>
        private static (Measured<SfcStatus> Status, Measured<DateTimeOffset> LastCheck, Measured<string> Evidence)
            ReadSfcVerdict(ProbeContext context)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

            if (!File.Exists(path))
            {
                var reason = "Aucun contrôle d'intégrité n'a jamais été exécuté sur cette machine (journal CBS absent).";
                return (Measured.Ok(SfcStatus.NotRun, DataSource.FileSystem),
                        Measured.Missing<DateTimeOffset>(reason),
                        Measured.Missing<string>(reason));
            }

            string tail;
            try
            {
                tail = ReadTail(path, TailBytes);
            }
            catch (UnauthorizedAccessException)
            {
                var reason = "Le journal CBS n'est lisible qu'avec des privilèges administrateur.";
                return (Measured.NeedsElevation<SfcStatus>("lecture du journal d'intégrité CBS"),
                        Measured.NeedsElevation<DateTimeOffset>("lecture du journal d'intégrité CBS"),
                        Measured.Missing<string>(reason));
            }
            catch (IOException ex)
            {
                var reason = "Le journal CBS n'a pas pu être lu (" + ex.GetType().Name + ").";
                return (Measured.Missing<SfcStatus>(reason),
                        Measured.Missing<DateTimeOffset>(reason),
                        Measured.Missing<string>(reason));
            }

            var lines = new List<string>();
            foreach (var line in tail.Split('\n'))
                if (line.IndexOf("[SR]", StringComparison.Ordinal) >= 0) lines.Add(line.Trim());

            if (lines.Count == 0)
            {
                var reason = "Le journal CBS ne contient aucun contrôle d'intégrité récent.";
                return (Measured.Ok(SfcStatus.NotRun, DataSource.FileSystem),
                        Measured.Missing<DateTimeOffset>(reason),
                        Measured.Missing<string>(reason));
            }

            var status = SfcStatus.Clean;
            var evidence = lines[lines.Count - 1];

            foreach (var line in lines)
            {
                if (line.IndexOf("Cannot repair member file", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    status = SfcStatus.RepairFailed;
                    evidence = line;
                    break;
                }
                if (line.IndexOf("Repairing corrupted file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Repaired file", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    status = SfcStatus.RepairedSuccessfully;
                    evidence = line;
                }
                else if (line.IndexOf("is not repairable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("Could not reproject corrupted file", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    status = SfcStatus.CorruptionFound;
                    evidence = line;
                }
            }

            return (Measured.Ok(status, DataSource.FileSystem),
                    ParseTimestamp(lines[lines.Count - 1]),
                    Measured.Ok(evidence, DataSource.FileSystem));
        }

        /// <summary>
        /// DISM /CheckHealth se contente de lire l'état enregistré du magasin de composants :
        /// c'est une opération de diagnostic, sans modification. La réparation, elle, relève de
        /// LDI12.Actions et demandera une confirmation explicite.
        /// </summary>
        private static async Task<(Measured<ComponentStoreState> State, string? Detail)> ReadComponentStoreAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var feature = context.Platform.Features.Get(FeatureId.DismRestoreHealth);
            if (feature.Availability != Availability.Available)
                return (Measured.Missing<ComponentStoreState>(feature.Reason), null);

            if (!context.Platform.IsElevated)
            {
                return (Measured.NeedsElevation<ComponentStoreState>(
                    "contrôle de l'état du magasin de composants (DISM)"), null);
            }

            var result = await context.Processes.RunAsync(new ProcessRequest(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
                "/Online /Cleanup-Image /CheckHealth")
            {
                Timeout = TimeSpan.FromSeconds(60),
                OutputEncoding = ConsoleOutputEncoding.ConsoleDefault,
            }, cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
                return (Measured.Missing<ComponentStoreState>("Le contrôle DISM n'a pas répondu dans le délai imparti."), null);

            if (result.LaunchFailed)
                return (Measured.Missing<ComponentStoreState>("DISM n'a pas pu être lancé sur cette machine."), null);

            // On s'appuie sur le code de sortie, pas sur le texte : la sortie de DISM est traduite.
            // 0 = aucune corruption détectée. Les autres codes sont documentés et stables.
            return result.ExitCode switch
            {
                0 => (Measured.Ok(ComponentStoreState.Healthy, DataSource.Cli), null),
                unchecked((int)0x800F081F) => (Measured.Ok(ComponentStoreState.NonRepairable, DataSource.Cli),
                    "Les fichiers sources de réparation sont introuvables."),
                _ => (Measured.Partial(ComponentStoreState.Repairable, DataSource.Cli,
                    "DISM a renvoyé le code " + result.ExitCode + " : le magasin de composants est probablement réparable."), null),
            };
        }

        /// <summary>Lit la fin d'un fichier sans le charger entièrement en mémoire.</summary>
        private static string ReadTail(string path, int bytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > bytes) stream.Seek(-bytes, SeekOrigin.End);

            var buffer = new byte[Math.Min(bytes, (int)Math.Min(stream.Length, int.MaxValue))];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        /// <summary>Les lignes CBS commencent par « aaaa-MM-jj hh:mm:ss », format invariant.</summary>
        private static Measured<DateTimeOffset> ParseTimestamp(string line)
        {
            if (line.Length >= 19 &&
                DateTime.TryParse(line.Substring(0, 19), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return Measured.Ok(new DateTimeOffset(parsed, TimeSpan.Zero), DataSource.FileSystem);
            }

            return Measured.Missing<DateTimeOffset>("La date du dernier contrôle n'a pas pu être lue.");
        }
    }
}
