using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LibreHardwareMonitor.Hardware;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Capteurs matériels par LibreHardwareMonitor, qui charge un pilote noyau signé.
    /// </summary>
    /// <remarks>
    /// C'est la seule méthode qui existe sous Windows pour lire le capteur DTS d'Intel, le Tctl
    /// d'AMD et les puces Super-I/O des cartes mères. HWMonitor, HWiNFO, NZXT CAM et Core Temp
    /// font tous exactement cela.
    /// <para>
    /// Trois garde-fous, parce que charger un pilote sur la machine d'un client n'est pas anodin :
    /// </para>
    /// <list type="number">
    /// <item>rien ne se charge tant que le technicien n'a pas activé la couche dans les réglages,
    /// et le réglage est éteint à l'installation ;</item>
    /// <item>rien ne se charge sans privilèges administrateur : sans eux, le pilote ne peut pas
    /// s'installer, et l'échec est annoncé plutôt que contourné ;</item>
    /// <item>la couche est refermée après chaque lecture. Le pilote ne reste pas résident entre
    /// deux analyses.</item>
    /// </list>
    /// <para>
    /// Licence MPL-2.0, copyleft par fichier : la bibliothèque est utilisée telle quelle, sans
    /// modification, ce qui n'impose rien au reste du logiciel.
    /// </para>
    /// </remarks>
    public sealed class LibreHardwareSensorApi : IAdvancedSensorApi
    {
        private const string Category = "Platform.Sensors";

        private readonly IScopedLogger _log;
        private readonly IPlatformInfo _platform;
        private readonly object _gate = new object();

        public LibreHardwareSensorApi(IPlatformInfo platform, ILdiLogger logger)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public bool Enabled { get; private set; }

        public void Enable(bool enabled)
        {
            if (Enabled == enabled) return;

            Enabled = enabled;
            _log.Info(enabled
                ? "Capteurs matériels autorisés : le pilote sera chargé à la prochaine analyse."
                : "Capteurs matériels désactivés.");
        }

        public Measured<IReadOnlyList<HardwareSensorReading>> Read()
        {
            if (!Enabled)
                return Measured.Missing<IReadOnlyList<HardwareSensorReading>>(
                    "Les capteurs matériels sont désactivés. Les activer, dans les réglages, charge un " +
                    "pilote noyau signé : la seule méthode qui permette de lire la température du " +
                    "processeur et les sondes de la carte mère.", DataSource.NativeApi);

            if (!_platform.IsElevated)
                return Measured.NeedsElevation<IReadOnlyList<HardwareSensorReading>>(
                    "chargement du pilote de capteurs matériels");

            // Un seul accès à la fois : la bibliothèque n'est pas conçue pour être ouverte deux
            // fois en parallèle, et deux sondes pourraient la demander dans la même vague.
            lock (_gate)
            {
                return ReadCore();
            }
        }

        private Measured<IReadOnlyList<HardwareSensorReading>> ReadCore()
        {
            Computer? computer = null;

            try
            {
                computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                };

                computer.Open();

                var readings = new List<HardwareSensorReading>();
                foreach (var hardware in computer.Hardware)
                {
                    Collect(hardware, readings);
                    foreach (var sub in hardware.SubHardware) Collect(sub, readings, hardware.HardwareType);
                }

                if (readings.Count == 0)
                    return Measured.Missing<IReadOnlyList<HardwareSensorReading>>(
                        "Le pilote de capteurs s'est chargé mais cette machine n'expose aucune sonde " +
                        "reconnue.", DataSource.NativeApi);

                _log.Info(readings.Count + " capteur(s) matériel(s) relevé(s).");
                return Measured.Ok<IReadOnlyList<HardwareSensorReading>>(readings, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
                _log.Warn("Le pilote de capteurs a été refusé.", ex);
                return Measured.Missing<IReadOnlyList<HardwareSensorReading>>(
                    "Le pilote de capteurs matériels a été refusé par Windows. Certaines protections (" +
                    "intégrité de la mémoire, listes de pilotes bloqués), l'interdisent, et c'est " +
                    "un refus légitime.", DataSource.NativeApi);
            }
            catch (Exception ex)
            {
                _log.Error("La lecture des capteurs matériels a échoué.", ex);
                return Measured.Missing<IReadOnlyList<HardwareSensorReading>>(
                    "La lecture des capteurs matériels a échoué : " + ex.Message, DataSource.NativeApi);
            }
            finally
            {
                // Refermé quoi qu'il arrive : le pilote ne reste pas chargé entre deux analyses.
                try { computer?.Close(); }
                catch (Exception ex)
                {
                    _log.Warn("La couche de capteurs ne s'est pas refermée proprement.", ex);
                }
            }
        }

        /// <summary>
        /// Relève les températures et les ventilateurs d'un composant.
        /// </summary>
        /// <remarks>
        /// <c>Update()</c> avant lecture : sans lui, tous les capteurs rendent <c>null</c>. La
        /// bibliothèque ne lit le matériel que sur demande, et c'est heureux, une lecture
        /// permanente maintiendrait le pilote occupé.
        /// </remarks>
        private static void Collect(
            IHardware hardware, ICollection<HardwareSensorReading> readings, HardwareType? parent = null)
        {
            try
            {
                hardware.Update();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
            {
                return;
            }

            var type = parent ?? hardware.HardwareType;

            foreach (var sensor in hardware.Sensors)
            {
                var kind = sensor.SensorType == SensorType.Temperature ? SensorKind.Temperature
                    : sensor.SensorType == SensorType.Fan ? SensorKind.FanRpm
                    : (SensorKind?)null;

                if (kind == null) continue;

                readings.Add(new HardwareSensorReading
                {
                    Component = hardware.Name ?? string.Empty,
                    Name = sensor.Name ?? string.Empty,
                    Kind = kind.Value,
                    IsCpu = type == HardwareType.Cpu,
                    IsGpu = IsGpu(type),
                    IsMotherboard = type == HardwareType.Motherboard,
                    IsStorage = type == HardwareType.Storage,
                    Value = sensor.Value.HasValue
                        ? Measured.Ok(Math.Round(sensor.Value.Value, 1), DataSource.NativeApi)
                        : Measured.Missing<double>(
                            "Ce capteur est déclaré par le matériel mais ne rend aucune valeur.",
                            DataSource.NativeApi),
                });
            }
        }

        private static bool IsGpu(HardwareType type)
            => type == HardwareType.GpuNvidia || type == HardwareType.GpuAmd || type == HardwareType.GpuIntel;
    }
}
