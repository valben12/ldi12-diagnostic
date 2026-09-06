using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Performance
{
    /// <summary>
    /// Durée de démarrage, telle que Windows la mesure lui-même.
    /// </summary>
    /// <remarks>
    /// Le logiciel est lancé bien après la fin du démarrage : il ne peut pas le chronométrer.
    /// Windows le fait pour lui et écrit le résultat dans le journal
    /// <c>Diagnostics-Performance</c> à chaque démarrage : événement 100, avec la durée totale et
    /// celle du chemin principal. C'est la seule mesure qui vaille, et elle a l'avantage de
    /// porter sur plusieurs démarrages : un démarrage lent isolé après une mise à jour n'est pas
    /// un symptôme, dix démarrages lents d'affilée en sont un.
    /// <para>
    /// Le journal n'existe pas sur les éditions serveur ni sur certaines installations allégées,
    /// et il exige des privilèges en lecture. Les deux cas sont déclarés comme tels plutôt que
    /// rendus sous forme de zéro.
    /// </para>
    /// </remarks>
    public sealed class BootPerformanceProbe : IDiagnosticProbe
    {
        private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

        /// <summary>Événement écrit à chaque démarrage réussi, avec ses durées.</summary>
        private const int BootEventId = 100;

        /// <summary>Dix démarrages : assez pour distinguer une tendance d'un accident.</summary>
        private const int MaxSamples = 10;

        /// <summary>
        /// Au-delà, Windows qualifie lui-même le démarrage de dégradé.
        /// </summary>
        /// <remarks>
        /// Seuil de Microsoft, pas le nôtre : 60 secondes. On le reprend tel quel pour compter
        /// les démarrages dégradés, ce qui évite d'inventer un seuil concurrent.
        /// </remarks>
        private static readonly TimeSpan DegradedThreshold = TimeSpan.FromSeconds(60);

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.BootPerformance,
            DisplayName = "Durée de démarrage",
            Category = DiagnosticCategory.Performance,
            EstimatedDuration = TimeSpan.FromMilliseconds(400),
            HardTimeout = TimeSpan.FromSeconds(30),
            Isolation = IsolationMode.SeparateProcess,
            FullScanOnly = true,
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var samples = new List<(TimeSpan Total, TimeSpan MainPath, DateTimeOffset When)>();
            string? failure = null;

            try
            {
                var query = new EventLogQuery(
                    LogName, PathType.LogName,
                    "*[System[(EventID=" + BootEventId.ToString(CultureInfo.InvariantCulture) + ")]]")
                {
                    ReverseDirection = true,
                };

                using var reader = new EventLogReader(query);

                while (samples.Count < MaxSamples)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { break; }
                    if (record == null) break;

                    using (record)
                    {
                        var sample = Read(record);
                        if (sample != null) samples.Add(sample.Value);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                failure = "Le journal de performance de démarrage n'est lisible qu'avec des privilèges administrateur.";
            }
            catch (EventLogNotFoundException)
            {
                failure = "Cette installation de Windows ne tient pas de journal de performance de démarrage.";
            }
            catch (EventLogException ex)
            {
                failure = "Le journal de performance de démarrage n'a pas pu être lu : " + ex.Message;
            }

            if (samples.Count == 0)
            {
                var reason = failure ?? "Aucun démarrage n'a encore été mesuré par Windows sur cette machine.";
                context.Draft.SetBootPerformance(Empty(reason));

                return Task.FromResult(failure == null
                    ? ProbeOutcome.Ok(reason)
                    : ProbeOutcome.Partial(reason));
            }

            var boot = Summarize(samples);
            context.Draft.SetBootPerformance(boot);

            var duration = boot.Duration;
            if (!duration.HasValue) return Task.FromResult(ProbeOutcome.Partial("Durées de démarrage illisibles."));

            var degraded = boot.DegradedBootCount.Or(0);
            return Task.FromResult(ProbeOutcome.Ok(
                "Dernier démarrage en " + Seconds(duration.Value) + " s" +
                (degraded > 0
                    ? " : " + degraded + " démarrage(s) dégradé(s) sur les " + samples.Count + " derniers."
                    : ".")));
        }

        /// <summary>
        /// Le dernier démarrage fait foi ; les précédents servent à dire s'il est représentatif.
        /// </summary>
        /// <remarks>
        /// Une moyenne serait trompeuse : un unique démarrage à trois minutes après une grosse
        /// mise à jour tirerait la moyenne vers le haut et ferait conclure à une machine lente
        /// qui ne l'est pas. On rend donc la dernière valeur, et le nombre de démarrages que
        /// Windows a lui-même qualifiés de dégradés.
        /// </remarks>
        private static BootPerformance Summarize(
            IReadOnlyList<(TimeSpan Total, TimeSpan MainPath, DateTimeOffset When)> samples)
        {
            var degraded = 0;
            foreach (var sample in samples)
                if (sample.Total >= DegradedThreshold) degraded++;

            var last = samples[0];

            return new BootPerformance
            {
                Duration = Measured.Ok(last.Total, DataSource.EventLog),
                MainPathDuration = last.MainPath > TimeSpan.Zero
                    ? Measured.Ok(last.MainPath, DataSource.EventLog)
                    : Measured.Missing<TimeSpan>(
                        "Windows n'a pas renseigné la durée du chemin principal pour ce démarrage.",
                        DataSource.EventLog),
                MeasuredAt = Measured.Ok(last.When, DataSource.EventLog),
                DegradedBootCount = Measured.Ok(degraded, DataSource.EventLog),
                SampleCount = Measured.Ok(samples.Count, DataSource.EventLog),
            };
        }

        private static BootPerformance Empty(string reason) => new BootPerformance
        {
            Duration = Measured.Missing<TimeSpan>(reason, DataSource.EventLog),
            MainPathDuration = Measured.Missing<TimeSpan>(reason, DataSource.EventLog),
            MeasuredAt = Measured.Missing<DateTimeOffset>(reason, DataSource.EventLog),
            DegradedBootCount = Measured.Missing<int>(reason, DataSource.EventLog),
            SampleCount = Measured.Missing<int>(reason, DataSource.EventLog),
        };

        /// <summary>
        /// Lit les durées d'un événement 100.
        /// </summary>
        /// <remarks>
        /// Par position dans les données de l'événement plutôt que par nom : le schéma nomme les
        /// champs, mais <c>EventRecord.Properties</c> ne rend que des valeurs ordonnées. Les
        /// deux premières sont <c>BootTsVersion</c> et <c>BootStartTime</c> ; <c>BootTime</c> et
        /// <c>MainPathBootTime</c> occupent les rangs 5 et 6 depuis Windows 7 et n'ont pas bougé.
        /// Une position hors bornes rend simplement un échantillon nul.
        /// </remarks>
        private static (TimeSpan Total, TimeSpan MainPath, DateTimeOffset When)? Read(EventRecord record)
        {
            try
            {
                var properties = record.Properties;
                if (properties == null || properties.Count < 7) return null;

                var total = ToMilliseconds(properties[5].Value);
                var mainPath = ToMilliseconds(properties[6].Value);
                if (total == null || total.Value <= 0) return null;

                return (
                    TimeSpan.FromMilliseconds(total.Value),
                    mainPath == null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(mainPath.Value),
                    record.TimeCreated ?? DateTime.Now);
            }
            catch (Exception ex) when (ex is EventLogException || ex is InvalidCastException ||
                                       ex is FormatException || ex is OverflowException)
            {
                return null;
            }
        }

        private static double? ToMilliseconds(object? value)
        {
            if (value == null) return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            {
                return null;
            }
        }

        private static string Seconds(TimeSpan value)
            => value.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
    }
}
