using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Execution
{
    /// <summary>Nature de ce qu'un capteur matériel mesure.</summary>
    public enum SensorKind
    {
        Temperature = 0,
        FanRpm = 1,
    }

    /// <summary>Un capteur exposé par la couche de capteurs matériels.</summary>
    public sealed class HardwareSensorReading
    {
        /// <summary>Composant porteur : « AMD Ryzen 7 5700G », « ITE IT8686E ».</summary>
        public string Component { get; init; } = string.Empty;

        /// <summary>Nom du capteur tel que le matériel le déclare : « Core (Tctl/Tdie) ».</summary>
        public string Name { get; init; } = string.Empty;

        public SensorKind Kind { get; init; }

        /// <summary>Le capteur appartient au processeur, sert à en tirer la température de paquet.</summary>
        public bool IsCpu { get; init; }

        public bool IsGpu { get; init; }

        public bool IsMotherboard { get; init; }

        public bool IsStorage { get; init; }

        public Measured<double> Value { get; init; }
    }

    /// <summary>
    /// Capteurs matériels : température du processeur, sondes de la carte mère, ventilateurs.
    /// </summary>
    /// <remarks>
    /// <b>Cette couche installe et charge un pilote noyau.</b> C'est la seule méthode qui existe
    /// sous Windows pour lire le capteur DTS d'Intel ou le Tctl d'AMD, et c'est ce que font
    /// HWMonitor, HWiNFO, NZXT CAM et Core Temp : aucun d'eux n'y parvient autrement.
    /// <para>
    /// Conséquence assumée : elle est <b>désactivée par défaut</b> et ne s'active que sur décision
    /// explicite du technicien, dans l'écran de réglages, en session administrateur. Un outil de
    /// diagnostic qui installerait un pilote chez un client sans le dire ne mériterait pas la
    /// confiance que le reste du logiciel cherche à établir.
    /// </para>
    /// </remarks>
    public interface IAdvancedSensorApi
    {
        /// <summary>Vrai quand le technicien a autorisé le chargement du pilote de capteurs.</summary>
        bool Enabled { get; }

        /// <summary>
        /// Autorise ou interdit la couche de capteurs. Le pilote n'est chargé qu'à la première
        /// lecture qui suit, jamais à l'activation elle-même.
        /// </summary>
        void Enable(bool enabled);

        Measured<IReadOnlyList<HardwareSensorReading>> Read();
    }
}
