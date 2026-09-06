using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Monitoring
{
    /// <summary>
    /// Une chose que la surveillance a vue, et ce qu'elle veut dire.
    /// </summary>
    /// <remarks>
    /// Deux phrases distinctes et non une seule : <see cref="Statement"/> décrit ce qui a été
    /// mesuré, <see cref="Explanation"/> dit ce qu'on en tire. Les mélanger ferait perdre la
    /// frontière entre l'observation et son interprétation, qui est exactement celle que le
    /// client doit pouvoir suivre.
    /// </remarks>
    public sealed class WatchObservation
    {
        public string Id { get; init; } = string.Empty;

        public Severity Severity { get; init; }

        public string Statement { get; init; } = string.Empty;

        public string Explanation { get; init; } = string.Empty;
    }

    /// <summary>
    /// Ce que la surveillance en direct permet (ou ne permet pas) d'affirmer.
    /// </summary>
    /// <remarks>
    /// <b>Une surveillance ne prouve rien sur ce qui ne s'est pas produit pendant qu'elle
    /// regardait.</b> C'est la limite de l'exercice, et le rapport la porte en propre :
    /// <see cref="LongEnough"/> et <see cref="Caveats"/> existent pour qu'un écran vert au bout
    /// de dix secondes ne se lise pas comme un certificat de bonne santé.
    /// </remarks>
    public sealed class WatchReport
    {
        public TimeSpan Duration { get; init; }

        public int SampleCount { get; init; }

        /// <summary>La session a duré assez longtemps pour que les conclusions soient tentées.</summary>
        public bool LongEnough { get; init; }

        /// <summary>Nombre d'échantillons où la machine était réellement sollicitée.</summary>
        public int BusySamples { get; init; }

        public IReadOnlyList<WatchObservation> Observations { get; init; } = Array.Empty<WatchObservation>();

        /// <summary>Ce que la surveillance n'a pas pu voir, et pourquoi.</summary>
        public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();

        /// <summary>Phrase de tête, celle qu'on lit au client.</summary>
        public string Headline { get; init; } = string.Empty;

        public Severity Worst
        {
            get
            {
                var worst = Severity.Info;
                foreach (var observation in Observations)
                    if (observation.Severity > worst) worst = observation.Severity;
                return worst;
            }
        }
    }
}
