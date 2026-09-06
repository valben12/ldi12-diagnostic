using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Consommation d'un processus au moment de l'analyse.
    /// </summary>
    /// <remarks>
    /// Instantané, et rien de plus : un processus vu à 40 % de processeur pendant deux secondes
    /// n'est pas nécessairement un problème. Les règles qui s'appuient dessus le déclarent en
    /// confiance moyenne, et le rapport dit toujours que la mesure est ponctuelle.
    /// </remarks>
    public sealed class ProcessUsage
    {
        public string Name { get; init; } = string.Empty;

        public int ProcessId { get; init; }

        /// <summary>Mémoire de travail, ce que le gestionnaire des tâches appelle « Mémoire ».</summary>
        public Measured<long> WorkingSetBytes { get; init; }

        /// <summary>Part de processeur consommée pendant la fenêtre d'échantillonnage.</summary>
        public Measured<double> CpuPercent { get; init; }

        /// <summary>Processus livré avec Windows : à ne pas présenter au client comme un intrus.</summary>
        public bool IsSystem { get; init; }
    }

    /// <summary>
    /// Durée de démarrage, telle que Windows la mesure lui-même.
    /// </summary>
    /// <remarks>
    /// Lue dans le journal <c>Diagnostics-Performance</c> plutôt que chronométrée : Windows y
    /// écrit à chaque démarrage la durée totale et celle du chemin principal. C'est la seule
    /// mesure qui vaille, puisque le logiciel est lancé bien après la fin du démarrage.
    /// </remarks>
    public sealed class BootPerformance
    {
        public Measured<TimeSpan> Duration { get; init; }

        /// <summary>Temps avant que le bureau soit utilisable, hors services différés.</summary>
        public Measured<TimeSpan> MainPathDuration { get; init; }

        public Measured<DateTimeOffset> MeasuredAt { get; init; }

        /// <summary>Démarrages signalés comme dégradés par Windows sur les relevés retenus.</summary>
        public Measured<int> DegradedBootCount { get; init; }

        /// <summary>Nombre de démarrages effectivement relevés, sans lui, une moyenne ne veut rien dire.</summary>
        public Measured<int> SampleCount { get; init; }
    }

    public sealed class ResponsivenessInfo
    {
        public Measured<double> CpuUsagePercent { get; init; }
        public Measured<double> MemoryUsagePercent { get; init; }

        /// <summary>Mémoire validée : mémoire vive et fichier d'échange confondus.</summary>
        public Measured<long> CommittedBytes { get; init; }

        public Measured<long> CommitLimitBytes { get; init; }

        /// <summary>
        /// Rapport entre mémoire validée et mémoire vive installée.
        /// </summary>
        /// <remarks>
        /// Au-dessus de 100 %, la machine s'appuie sur le fichier d'échange pour tenir sa charge
        /// courante : c'est la mesure qui explique le mieux « mon PC rame » sur une machine dont
        /// le processeur n'est pourtant pas saturé.
        /// </remarks>
        public Measured<double> CommitRatioPercent { get; init; }
    }

    /// <summary>
    /// Dimension Performances de l'instantané.
    /// </summary>
    /// <remarks>
    /// Tout y est mesuré à un instant donné, sur une machine où le technicien vient d'ouvrir un
    /// logiciel de diagnostic. Aucune de ces valeurs ne peut donc fonder à elle seule un constat
    /// de lenteur : elles servent à expliquer une lenteur constatée par ailleurs, ou à écarter
    /// une cause. La durée de démarrage fait exception, Windows l'a mesurée sans nous.
    /// </remarks>
    public sealed class PerformanceSnapshot
    {
        public ResponsivenessInfo Responsiveness { get; init; } = new ResponsivenessInfo();

        public BootPerformance Boot { get; init; } = new BootPerformance();

        public IReadOnlyList<ProcessUsage> TopByMemory { get; init; } = Array.Empty<ProcessUsage>();

        public IReadOnlyList<ProcessUsage> TopByCpu { get; init; } = Array.Empty<ProcessUsage>();

        public Measured<int> ProcessCount { get; init; }

        public Measured<TimeSpan> Uptime { get; init; }

        /// <summary>Les réglages qui brident la machine sans rien casser.</summary>
        public PowerConfiguration Power { get; init; } = new PowerConfiguration();
    }
}
