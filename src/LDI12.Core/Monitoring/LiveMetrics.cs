using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Monitoring
{
    /// <summary>Consommation d'un processus sur une fenêtre de surveillance.</summary>
    public sealed class LiveProcessLoad
    {
        public string Name { get; init; } = string.Empty;

        public int ProcessId { get; init; }

        public Measured<double> CpuPercent { get; init; }

        public Measured<long> WorkingSetBytes { get; init; }

        /// <summary>
        /// Ce processus est le logiciel de diagnostic lui-même.
        /// </summary>
        /// <remarks>
        /// Un outil qui surveille une machine y consomme du processeur, et le sien apparaît donc
        /// dans la liste comme les autres. Le masquer serait mentir sur ce que la machine fait ;
        /// le laisser sans le nommer laisserait le technicien attribuer au client une charge qui
        /// est la nôtre. Il est donc affiché, marqué, et exclu des conclusions.
        /// </remarks>
        public bool IsSelf { get; init; }
    }

    /// <summary>
    /// Un point de mesure de la surveillance en direct.
    /// </summary>
    /// <remarks>
    /// Chaque champ est mesuré séparément et peut manquer séparément : une machine sans capteur
    /// de température renseigne tout le reste. Un échantillon n'est jamais partiellement rejeté.
    /// </remarks>
    public sealed class LiveSample
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        public Measured<double> CpuPercent { get; init; }

        public Measured<double> MemoryPercent { get; init; }

        /// <summary>Mémoire validée rapportée à la mémoire installée, au-delà de 100 %, la machine pagine.</summary>
        public Measured<double> CommitRatioPercent { get; init; }

        public Measured<double> CpuCelsius { get; init; }

        /// <summary>
        /// Fréquence courante du processeur, telle que Windows la rapporte.
        /// </summary>
        /// <remarks>
        /// Beaucoup de machines renvoient ici la fréquence nominale, figée, quoi que fasse le
        /// processeur. C'est une limite de la source et non une panne : la surveillance le
        /// constate au fil de la session plutôt que de le supposer.
        /// </remarks>
        public Measured<int> CpuMegahertz { get; init; }

        public Measured<int> CpuMaxMegahertz { get; init; }

        /// <summary>Débit réseau cumulé, émission et réception confondues.</summary>
        public Measured<double> NetworkBytesPerSecond { get; init; }

        public IReadOnlyList<LiveProcessLoad> Busiest { get; init; } = Array.Empty<LiveProcessLoad>();
    }

    /// <summary>
    /// Source d'échantillons pour la surveillance en direct.
    /// </summary>
    /// <remarks>
    /// Une mesure et une seule par appel, la fenêtre étant écoulée à l'intérieur : la charge
    /// processeur comme la consommation d'un processus sont des différences entre deux instants,
    /// et une lecture ponctuelle n'en dirait rien.
    /// </remarks>
    public interface ILiveMetricSource
    {
        Task<LiveSample> SampleAsync(TimeSpan window, CancellationToken cancellationToken);
    }
}
