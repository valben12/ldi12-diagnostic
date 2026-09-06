using System;
using LDI12.Engine.Profile;

namespace LDI12.Engine.Monitoring
{
    /// <summary>
    /// Seuils de la surveillance en direct.
    /// </summary>
    /// <remarks>
    /// Ils reprennent ceux du barème là où la question est la même (un processeur chaud est
    /// chaud à la même température qu'il soit vu en direct ou à l'analyse) et n'ajoutent que ce
    /// qui n'a de sens que dans la durée : à partir de quand la machine est dite sollicitée, et
    /// combien de temps il faut avoir regardé pour avoir le droit de conclure.
    /// <para>
    /// Ces derniers ne rejoignent pas <see cref="Thresholds"/> volontairement : le barème pèse
    /// sur la note du diagnostic, et rien de ce qui est décidé ici n'y touche. Y ajouter des
    /// seuils sans effet sur le score rendrait l'écran des réglages illisible.
    /// </para>
    /// </remarks>
    public sealed class WatchLimits
    {
        /// <summary>En deçà, aucune conclusion n'est tentée. Une minute de regard n'est pas un diagnostic.</summary>
        public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Arrêt automatique.
        /// </summary>
        /// <remarks>
        /// Une surveillance oubliée continuerait de consommer du processeur sur la machine d'un
        /// client, et de le faire pendant que personne ne regarde l'écran. Elle s'arrête donc
        /// seule, en le disant.
        /// </remarks>
        public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromHours(2);

        /// <summary>Au-delà, la machine est dite sollicitée : c'est là que les responsabilités se lisent.</summary>
        public double BusyCpuPercent { get; init; } = 40;

        public double SustainedCpuPercent { get; init; } = 85;

        /// <summary>Part du temps au-dessus du seuil à partir de laquelle la charge est dite soutenue.</summary>
        public double SustainedShare { get; init; } = 0.6;

        /// <summary>Part des échantillons sollicités qu'un même programme doit dominer pour être nommé.</summary>
        public double LeaderShare { get; init; } = 0.7;

        public double HotCelsius { get; init; } = 88;

        public double VeryHotCelsius { get; init; } = 95;

        /// <summary>
        /// Rapport entre la fréquence tenue à chaud et celle tenue à froid en dessous duquel on
        /// parle de ralentissement thermique.
        /// </summary>
        public double ThrottleRatio { get; init; } = 0.8;

        /// <summary>Échantillons minimaux de chaque côté du seuil de température pour comparer.</summary>
        public int ThrottleSamples { get; init; } = 10;

        public double CommitRatioPercent { get; init; } = 100;

        /// <summary>Reprend du barème du technicien tout ce qui y est déjà réglable.</summary>
        public static WatchLimits From(Thresholds thresholds)
        {
            if (thresholds == null) return new WatchLimits();

            return new WatchLimits
            {
                SustainedCpuPercent = thresholds.CpuUsageWarning,
                HotCelsius = thresholds.CpuTemperatureWarning,
                VeryHotCelsius = thresholds.CpuTemperatureProblem,
                CommitRatioPercent = thresholds.CommitRatioWarning,
            };
        }
    }
}
