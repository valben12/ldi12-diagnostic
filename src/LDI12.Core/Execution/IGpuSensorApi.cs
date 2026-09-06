using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Relevé d'une carte graphique par la bibliothèque de gestion de son constructeur.</summary>
    public sealed class GpuSensorReading
    {
        /// <summary>Rang de la carte tel que la bibliothèque l'énumère.</summary>
        public int Index { get; init; }

        /// <summary>Nom rendu par le pilote, sert à rapprocher ce relevé de la carte listée par Windows.</summary>
        public string Name { get; init; } = string.Empty;

        public Measured<double> TemperatureCelsius { get; init; }

        public Measured<double> UtilizationPercent { get; init; }

        public Measured<double> MemoryUtilizationPercent { get; init; }

        /// <summary>Vitesse du ventilateur en pourcentage de son maximum. Absente sur une carte passive.</summary>
        public Measured<double> FanPercent { get; init; }
    }

    /// <summary>
    /// Capteurs des cartes graphiques, par la bibliothèque du constructeur.
    /// </summary>
    /// <remarks>
    /// À la différence du processeur, dont la température exige un pilote noyau, celle du GPU est
    /// exposée par une bibliothèque que le pilote graphique installe lui-même :
    /// <c>nvml.dll</c> chez NVIDIA, présente dans System32 dès que le pilote l'est. On se
    /// contente donc de l'appeler : aucune installation, aucun privilège, et la mesure est celle
    /// que donne le constructeur.
    /// <para>
    /// L'absence de la bibliothèque est un cas normal (machine sans carte NVIDIA, pilote
    /// générique de Windows) et non une erreur : elle rend une mesure absente avec sa raison.
    /// </para>
    /// </remarks>
    public interface IGpuSensorApi
    {
        Measured<IReadOnlyList<GpuSensorReading>> Read();
    }
}
