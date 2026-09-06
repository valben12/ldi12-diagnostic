using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Cartes graphiques, pilotes et modes d'affichage.
    /// </summary>
    /// <remarks>
    /// Le point utile en intervention n'est pas la puissance de la carte : c'est de savoir si
    /// Windows utilise le pilote du constructeur ou le pilote d'affichage générique Microsoft.
    /// Une carte tombée sur le pilote générique explique à elle seule « l'écran rame » et
    /// « je ne peux plus régler la résolution ».
    /// </remarks>
    public sealed class GpuProbe : IDiagnosticProbe
    {
        /// <summary>Classe d'installation « Cartes graphiques » du Gestionnaire de périphériques.</summary>
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        /// <summary>AdapterRAM est un entier 32 bits : il plafonne à 4 Gio et devient trompeur au-delà.</summary>
        private const long AdapterRamCeiling = 4L * 1024 * 1024 * 1024;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Gpu,
            DisplayName = "Carte graphique",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var query = await context.Wmi.QueryAsync(
                WmiNamespaces.CimV2,
                "SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate, " +
                "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID " +
                "FROM Win32_VideoController",
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            if (!query.Succeeded)
            {
                context.Draft.SetGpus(Array.Empty<GpuInfo>());
                return ProbeOutcome.Failed(
                    "Les cartes graphiques n'ont pas pu être énumérées : " + query.Reason);
            }

            // Capteurs du constructeur lus une seule fois : la bibliothèque s'initialise et se
            // referme à chaque appel, et rien ne justifie de le faire par carte.
            var sensors = context.GpuSensors == null
                ? Measured.NotCollected<IReadOnlyList<GpuSensorReading>>()
                : context.GpuSensors.Read();

            var gpus = new List<GpuInfo>(query.Records.Count);
            var genericDriverCount = 0;

            for (var index = 0; index < query.Records.Count; index++)
            {
                var gpu = ToGpu(query.Records[index], index, context, sensors);
                if (gpu.UsesGenericMicrosoftDriver.Or(false)) genericDriverCount++;
                gpus.Add(gpu);
            }

            context.Draft.SetGpus(gpus);

            if (gpus.Count == 0)
                return ProbeOutcome.Partial("Aucune carte graphique n'a été rapportée par le système.");

            var summary = gpus.Count + " carte(s)";
            if (genericDriverCount > 0)
            {
                return ProbeOutcome.Partial(
                    summary + " : " + genericDriverCount +
                    " fonctionne(nt) avec le pilote d'affichage générique Microsoft.");
            }

            return ProbeOutcome.Ok(summary);
        }

        private static GpuInfo ToGpu(
            WmiRecord record, int index, ProbeContext context,
            Measured<IReadOnlyList<GpuSensorReading>> sensors)
        {
            var name = record.GetString("Name");
            var vendor = record.GetString("AdapterCompatibility");
            var horizontal = record.GetInt32("CurrentHorizontalResolution");
            var vertical = record.GetInt32("CurrentVerticalResolution");
            var sensor = Match(sensors, name);

            return new GpuInfo
            {
                Manufacturer = Measure.Text(record, "AdapterCompatibility", "Le fabricant de la carte"),
                Model = Measure.Text(record, "Name", "Le modèle de la carte"),
                VideoMemoryBytes = ReadVideoMemory(record, index, context),
                DriverVersion = Measure.Text(record, "DriverVersion", "La version du pilote"),
                DriverDate = Measure.DmtfDate(record, "DriverDate", "La date du pilote"),
                DriverProvider = Measure.Text(record, "AdapterCompatibility", "L'éditeur du pilote"),
                CurrentResolution = horizontal.HasValue && vertical.HasValue && horizontal.Value > 0
                    ? Measured.Ok(
                        horizontal.Value.ToString(CultureInfo.InvariantCulture) + " × " +
                        vertical!.Value.ToString(CultureInfo.InvariantCulture), DataSource.Wmi)
                    : Measured.Missing<string>("Aucun mode d'affichage actif sur cette sortie."),
                RefreshHz = Measure.Int32(record, "CurrentRefreshRate", "La fréquence de rafraîchissement"),
                UsagePercent = sensor?.UtilizationPercent ?? UsageUnavailable(context),
                TemperatureCelsius = sensor?.TemperatureCelsius ?? TemperatureUnavailable(sensors),
                FanPercent = sensor?.FanPercent ?? Measured.NotCollected<double>(),
                UsesGenericMicrosoftDriver = DetectGenericDriver(name, vendor),
            };
        }

        /// <summary>
        /// Rapproche un relevé de capteur de la carte listée par Windows.
        /// </summary>
        /// <remarks>
        /// Par le nom, et non par le rang : Windows énumère aussi les adaptateurs virtuels
        /// (bureau à distance, machines virtuelles) que la bibliothèque du constructeur ignore.
        /// Se fier au rang associerait la température de la vraie carte à un adaptateur fantôme.
        /// Sans correspondance de nom, aucun relevé n'est attribué : mieux vaut pas de mesure
        /// qu'une mesure sur la mauvaise carte.
        /// </remarks>
        internal static GpuSensorReading? Match(
            Measured<IReadOnlyList<GpuSensorReading>> sensors, string? adapterName)
        {
            if (!sensors.HasValue || string.IsNullOrWhiteSpace(adapterName)) return null;

            foreach (var sensor in sensors.Value)
            {
                if (string.IsNullOrWhiteSpace(sensor.Name)) continue;

                if (string.Equals(sensor.Name, adapterName, StringComparison.OrdinalIgnoreCase) ||
                    adapterName!.IndexOf(sensor.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sensor.Name.IndexOf(adapterName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sensor;
            }

            return null;
        }

        /// <summary>Pourquoi la température de cette carte manque, dans les mots du cas réel.</summary>
        private static Measured<double> TemperatureUnavailable(
            Measured<IReadOnlyList<GpuSensorReading>> sensors)
            => Measured.Missing<double>(
                sensors.Reason ??
                "La température du GPU n'est lisible que par la bibliothèque du constructeur de la carte.");

        /// <summary>
        /// AdapterRAM est un <c>uint32</c> et devient faux au-delà de 4 Gio. La vraie valeur est
        /// dans la clé de classe d'affichage du pilote, sur 64 bits.
        /// </summary>
        private static Measured<long> ReadVideoMemory(WmiRecord record, int index, ProbeContext context)
        {
            var subKey = DisplayClassKey + "\\" + index.ToString("D4", CultureInfo.InvariantCulture);
            var precise = context.Registry.ReadInt64(RegistryHive.LocalMachine, subKey, "HardwareInformation.qwMemorySize");
            if (precise.HasValue && precise.Value > 0)
                return Measured.Ok(precise.Value, DataSource.Registry);

            var adapterRam = record.GetUInt64("AdapterRAM");
            if (adapterRam == null || adapterRam.Value == 0)
                return Measured.Missing<long>("La quantité de mémoire vidéo n'est pas renseignée par ce pilote.");

            var bytes = (long)adapterRam.Value;
            return bytes >= AdapterRamCeiling
                ? Measured.Partial(bytes, DataSource.Wmi,
                    "Valeur plafonnée à 4 Gio par la limite du champ système : la carte en possède probablement davantage.")
                : Measured.Ok(bytes, DataSource.Wmi);
        }

        private static Measured<bool> DetectGenericDriver(string? name, string? vendor)
        {
            if (name == null && vendor == null)
                return Measured.Missing<bool>("Le pilote d'affichage n'a pas pu être identifié.");

            var isGeneric =
                (name?.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (name?.IndexOf("Carte graphique de base", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (vendor?.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false);

            return Measured.Ok(isGeneric, DataSource.Wmi);
        }

        private static Measured<double> UsageUnavailable(ProbeContext context)
        {
            var feature = context.Platform.Features.Get(Core.Platform.FeatureId.GpuEngineCounters);
            return Measured.Missing<double>(
                feature.Availability == Availability.Available
                    ? "La mesure d'utilisation du GPU est prévue dans le module Performances."
                    : feature.Reason);
        }
    }
}
