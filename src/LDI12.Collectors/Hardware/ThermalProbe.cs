using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Températures relevables sans installer de pilote.
    /// </summary>
    /// <remarks>
    /// Trois sources existent sous Windows, et une seule répond en session utilisateur :
    /// <list type="number">
    /// <item><c>Win32_PerfFormattedData_Counters_ThermalZoneInformation</c> : zones ACPI, lisible
    /// sans privilège, disponible depuis Windows 8 ;</item>
    /// <item><c>MSAcpi_ThermalZoneTemperature</c> dans <c>root\WMI</c> : mêmes zones, plus le
    /// détail du refroidissement actif, mais <b>refusé sans élévation</b> ;</item>
    /// <item><c>Win32_TemperatureProbe</c> : sonde SMBIOS, presque jamais renseignée par les
    /// constructeurs.</item>
    /// </list>
    /// <para>
    /// Aucune ne donne la température du processeur. Le DTS d'Intel et le Tctl d'AMD ne sont
    /// accessibles que par un pilote noyau ; c'est ce qu'installent HWMonitor, HWiNFO et Core
    /// Temp. LDI12 ne le fait pas, et le dit plutôt que de laisser une ligne vide.
    /// </para>
    /// <para>
    /// Ce que le firmware expose sous le nom de « zone thermique » n'est pas un composant :
    /// selon la carte mère, une zone suit le processeur, le chipset, l'air d'admission, ou rien
    /// de reconnaissable. Les zones sont donc rendues sous leur nom ACPI brut, et celles dont la
    /// valeur sort d'une plage crédible sont signalées comme telles, jamais supprimées.
    /// </para>
    /// </remarks>
    public sealed class ThermalProbe : IDiagnosticProbe
    {
        /// <summary>
        /// Plage dans laquelle une zone interne de machine en marche a un sens.
        /// </summary>
        /// <remarks>
        /// Bornes larges à dessein : il ne s'agit pas de juger la machine mais de repérer une
        /// zone qui ne mesure manifestement pas ce qu'on croit. Une carte mère qui déclare une
        /// zone à 17 °C alors que la machine tourne depuis huit heures ne mesure pas un
        /// composant.
        /// </remarks>
        private const double PlausibleMinimum = 20;
        private const double PlausibleMaximum = 110;

        /// <summary>Le zéro absolu en dixièmes de kelvin : borne de validité de l'encodage ACPI.</summary>
        private const double KelvinOffset = 2731.5;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Thermal,
            DisplayName = "Températures",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromMilliseconds(600),
            HardTimeout = TimeSpan.FromSeconds(25),
            Isolation = IsolationMode.SeparateProcess,
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            // Capteurs matériels d'abord : quand ils répondent, ils mesurent des composants
            // nommés, là où une zone ACPI ne dit jamais ce qu'elle suit.
            var sensors = context.Sensors == null
                ? Measured.NotCollected<IReadOnlyList<HardwareSensorReading>>()
                : context.Sensors.Read();

            var zones = new List<ThermalZoneReading>();
            var fans = new List<FanReading>();
            var cpuPackage = Measured.Missing<double>(Limitation(context.Platform.IsElevated, false));

            if (sensors.HasValue)
            {
                cpuPackage = Harvest(sensors.Value, zones, fans);
            }

            zones.AddRange(await ReadPerformanceCountersAsync(context, cancellationToken).ConfigureAwait(false));

            // La source privilégiée n'ajoute que le détail du refroidissement actif : inutile de
            // provoquer une élévation pour elle, mais autant la lire si on l'a déjà.
            if (context.Platform.IsElevated)
                Merge(zones, await ReadAcpiAsync(context, cancellationToken).ConfigureAwait(false));

            zones.AddRange(await ReadSmbiosAsync(context, cancellationToken).ConfigureAwait(false));

            context.Draft.SetThermal(new ThermalSnapshot
            {
                Zones = zones,
                Fans = fans,
                CpuPackageCelsius = cpuPackage,
                AdvancedSensorsUsed = sensors.HasValue,
                Limitation = sensors.HasValue
                    ? null
                    : Limitation(context.Platform.IsElevated, sensors.Availability == Availability.RequiresElevation),
            });

            return Conclude(zones, cpuPackage, sensors);
        }

        /// <summary>
        /// Transforme les capteurs matériels en zones nommées, et isole la température du paquet
        /// processeur.
        /// </summary>
        /// <remarks>
        /// Le capteur retenu pour le paquet est le plus chaud des capteurs de processeur : sur un
        /// processeur multi-cœurs, la bibliothèque expose un capteur par cœur en plus du Tctl, et
        /// c'est bien le maximum qui décide du bridage thermique, pas la moyenne.
        /// </remarks>
        internal static Measured<double> Harvest(
            IReadOnlyList<HardwareSensorReading> sensors,
            ICollection<ThermalZoneReading> zones,
            ICollection<FanReading> fans)
        {
            Measured<double> cpuPackage = Measured.Missing<double>(
                "Le pilote de capteurs n'a exposé aucune sonde de processeur sur cette machine.",
                DataSource.NativeApi);

            var hottestCpu = double.MinValue;

            foreach (var sensor in sensors)
            {
                if (sensor.Kind == SensorKind.FanRpm)
                {
                    fans.Add(new FanReading
                    {
                        // Même raison que pour les sondes : deux puces numérotent leurs
                        // connecteurs à partir de un, et « Fan #1 » deux fois n'apprend rien.
                        Name = sensor.IsGpu
                            ? "de la carte graphique"
                            : Chip(sensor.Component) + " " + sensor.Name,
                        Component = sensor.Component,
                        Rpm = sensor.Value,
                    });
                    continue;
                }

                if (!sensor.Value.HasValue) continue;

                var celsius = sensor.Value.Value;
                if (sensor.IsCpu && celsius > hottestCpu)
                {
                    hottestCpu = celsius;
                    cpuPackage = Measured.Ok(celsius, DataSource.NativeApi);
                }

                zones.Add(new ThermalZoneReading
                {
                    Name = Describe(sensor),
                    Component = sensor.Component,
                    Source = ThermalSource.HardwareDriver,
                    IsProcessor = sensor.IsCpu,
                    Celsius = sensor.Value,

                    // Un capteur physique mesure un composant nommé : la question de savoir s'il
                    // mesure « quelque chose » ne se pose plus comme pour une zone ACPI.
                    Plausible = celsius >= PlausibleMinimum && celsius <= PlausibleMaximum,
                    ActivelyCooled = Measured.NotCollected<bool>(),
                });
            }

            return cpuPackage;
        }

        /// <summary>
        /// Nomme un capteur pour l'affichage.
        /// </summary>
        /// <remarks>
        /// Les cartes mères portent souvent deux puces Super-I/O, chacune numérotant ses sondes à
        /// partir de un : sans le nom de la puce, l'écran affichait deux fois
        /// « Carte mère, Temperature #1 » avec des valeurs différentes.
        /// </remarks>
        private static string Describe(HardwareSensorReading sensor)
        {
            if (sensor.IsCpu) return "Processeur : " + sensor.Name;
            if (sensor.IsGpu) return "Carte graphique : " + sensor.Name;
            if (sensor.IsStorage) return "Disque " + sensor.Component;
            if (sensor.IsMotherboard) return "Carte mère " + Chip(sensor.Component) + " : " + sensor.Name;
            return sensor.Component + " : " + sensor.Name;
        }

        /// <summary>Réduit « ITE IT8686E » à « IT8686E » : le fabricant n'apprend rien de plus.</summary>
        internal static string Chip(string? component)
        {
            var name = component ?? string.Empty;
            var space = name.IndexOf(' ');
            return space > 0 && space < name.Length - 1 ? name.Substring(space + 1) : name;
        }

        private static ProbeOutcome Conclude(
            IReadOnlyList<ThermalZoneReading> zones, Measured<double> cpuPackage,
            Measured<IReadOnlyList<HardwareSensorReading>> sensors)
        {
            if (cpuPackage.HasValue)
                return ProbeOutcome.Ok(
                    "Processeur à " + ValueFormat.Celsius((int)Math.Round(cpuPackage.Value)) +
                    ", " + zones.Count + " capteur(s) relevé(s).");

            if (sensors.Availability == Availability.RequiresElevation)
                return ProbeOutcome.ElevationRequired(
                    "les capteurs matériels, qui seuls donnent la température du processeur");

            if (zones.Count == 0)
                return ProbeOutcome.Partial(
                    "Cette machine n'expose aucune zone thermique, et les capteurs matériels sont " +
                    "désactivés : aucune température lisible en dehors de celle des disques.");

            var plausible = 0;
            foreach (var zone in zones)
                if (zone.Plausible) plausible++;

            if (plausible == 0)
                return ProbeOutcome.Partial(
                    zones.Count + " zone(s) thermique(s) déclarée(s), mais aucune ne rend une valeur " +
                    "crédible pour une machine en marche : le firmware les expose sans les alimenter.");

            return ProbeOutcome.Ok(
                plausible + " zone(s) thermique(s) exploitable(s) sur " + zones.Count + " déclarée(s). " +
                "La température du processeur demande d'activer les capteurs matériels.");
        }

        /// <summary>
        /// Ce que la machine n'a pas su rendre, et pourquoi.
        /// </summary>
        /// <remarks>
        /// Le texte est repris tel quel dans la fiche et les rapports. Il nomme les outils qui y
        /// parviennent : un technicien à qui l'on dit « impossible » alors qu'un autre logiciel
        /// affiche le chiffre cesse de faire confiance à celui qui dit non.
        /// </remarks>
        internal static string Limitation(bool elevated, bool elevationBlocked)
        {
            const string Why =
                "La température du processeur n'est lisible que par un pilote noyau : ni le capteur DTS " +
                "d'Intel ni le Tctl d'AMD ne sont exposés à l'espace utilisateur. HWMonitor, HWiNFO, " +
                "NZXT CAM et Core Temp l'affichent tous en chargeant un tel pilote : il n'existe pas " +
                "d'autre méthode. ";

            if (elevationBlocked)
                return Why + "Les capteurs matériels sont activés, mais leur chargement exige une " +
                       "session administrateur. Relancer LDI12 en tant qu'administrateur.";

            return Why + "LDI12 peut le faire : les capteurs matériels s'activent dans les réglages, " +
                   "en session administrateur." +
                   (elevated
                       ? string.Empty
                       : " Cette session n'est pas administrateur : il faudra aussi relancer l'outil élevé.");
        }

        /// <summary>
        /// Zones ACPI par compteur de performance, la seule source qui réponde sans privilège.
        /// </summary>
        /// <remarks>
        /// <c>HighPrecisionTemperature</c> est en dixièmes de kelvin, <c>Temperature</c> en
        /// kelvins entiers. On préfère la première quand elle existe : un degré d'écart n'a pas
        /// d'importance ici, mais un arrondi qui fait passer une zone sous le seuil de
        /// plausibilité en aurait.
        /// </remarks>
        private static async Task<IReadOnlyList<ThermalZoneReading>> ReadPerformanceCountersAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var result = await context.Wmi.QueryAsync(
                    WmiNamespaces.CimV2,
                    "SELECT Name, Temperature, HighPrecisionTemperature FROM " +
                    "Win32_PerfFormattedData_Counters_ThermalZoneInformation",
                    TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded) return Array.Empty<ThermalZoneReading>();

            var zones = new List<ThermalZoneReading>(result.Records.Count);
            foreach (var record in result.Records)
            {
                var name = record.GetString("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var precise = record.GetUInt32("HighPrecisionTemperature");
                var coarse = record.GetUInt32("Temperature");

                var celsius = precise != null && precise.Value > 0
                    ? FromDeciKelvin(precise.Value)
                    : coarse != null && coarse.Value > 0
                        ? FromDeciKelvin(coarse.Value * 10u)
                        : (double?)null;

                // Meme normalisation que la source WMI : les deux sources nomment la meme
                // zone differemment, et sans cela une session administrateur l'affichait
                // deux fois avec la meme temperature.
                zones.Add(Build(Shorten(name!), ThermalSource.AcpiZone, celsius, DataSource.PerformanceCounter));
            }

            return zones;
        }

        /// <summary>
        /// Zones ACPI par <c>root\WMI</c>. Refusé sans élévation, y compris en lecture.
        /// </summary>
        private static async Task<IReadOnlyList<ThermalZoneReading>> ReadAcpiAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var result = await context.Wmi.QueryAsync(
                    WmiNamespaces.Wmi, "SELECT InstanceName, CurrentTemperature, Active FROM MSAcpi_ThermalZoneTemperature",
                    TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded) return Array.Empty<ThermalZoneReading>();

            var zones = new List<ThermalZoneReading>(result.Records.Count);
            foreach (var record in result.Records)
            {
                var name = record.GetString("InstanceName");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var raw = record.GetUInt32("CurrentTemperature");
                var celsius = raw != null && raw.Value > 0 ? FromDeciKelvin(raw.Value) : (double?)null;
                var active = record.GetBoolean("Active");

                var zone = Build(Shorten(name!), ThermalSource.AcpiZone, celsius, DataSource.Wmi);
                zones.Add(new ThermalZoneReading
                {
                    Name = zone.Name,
                    Source = zone.Source,
                    Celsius = zone.Celsius,
                    Plausible = zone.Plausible,
                    ActivelyCooled = active == null
                        ? Measured.Missing<bool>("Le firmware ne déclare pas le refroidissement de cette zone.", DataSource.Wmi)
                        : Measured.Ok(active.Value, DataSource.Wmi),
                });
            }

            return zones;
        }

        /// <summary>
        /// Sonde SMBIOS. Déclarée par la spécification, renseignée par presque personne.
        /// </summary>
        /// <remarks>
        /// <c>CurrentReading</c> est en dixièmes de degré Celsius, contrairement aux zones ACPI :
        /// deux encodages voisins dans deux classes voisines, et l'occasion classique d'afficher
        /// une température de 2 700 °C.
        /// </remarks>
        private static async Task<IReadOnlyList<ThermalZoneReading>> ReadSmbiosAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var result = await context.Wmi.QueryAsync(
                    WmiNamespaces.CimV2, "SELECT Name, CurrentReading FROM Win32_TemperatureProbe",
                    TimeSpan.FromSeconds(6), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded) return Array.Empty<ThermalZoneReading>();

            var probes = new List<ThermalZoneReading>(result.Records.Count);
            foreach (var record in result.Records)
            {
                var reading = record.GetInt32("CurrentReading");
                if (reading == null) continue;

                var name = record.GetString("Name");
                probes.Add(Build(
                    string.IsNullOrWhiteSpace(name) ? "Sonde SMBIOS" : name!,
                    ThermalSource.Smbios, reading.Value / 10d, DataSource.Wmi));
            }

            return probes;
        }

        /// <summary>Complète les zones déjà relevées, sans en dupliquer aucune.</summary>
        private static void Merge(List<ThermalZoneReading> zones, IReadOnlyList<ThermalZoneReading> extra)
        {
            foreach (var zone in extra)
            {
                var index = -1;
                for (var i = 0; i < zones.Count; i++)
                    if (string.Equals(zones[i].Name, zone.Name, StringComparison.OrdinalIgnoreCase)) index = i;

                if (index < 0) zones.Add(zone);
                else zones[index] = zone;
            }
        }

        internal static ThermalZoneReading Build(
            string name, ThermalSource source, double? celsius, DataSource origin)
        {
            if (celsius == null)
                return new ThermalZoneReading
                {
                    Name = name,
                    Source = source,
                    Celsius = Measured.Missing<double>(
                        "La zone « " + name + " » est déclarée mais ne rend aucune valeur.", origin),
                    Plausible = false,
                    ActivelyCooled = Measured.NotCollected<bool>(),
                };

            var value = Math.Round(celsius.Value, 1);
            var plausible = value >= PlausibleMinimum && value <= PlausibleMaximum;

            return new ThermalZoneReading
            {
                Name = name,
                Source = source,
                // Une valeur hors plage reste une mesure (elle a bien été lue) mais partielle :
                // elle ne dit pas ce qu'on croit qu'elle dit, et la raison le précise.
                Celsius = plausible
                    ? Measured.Ok(value, origin)
                    : Measured.Partial(value, origin,
                        "Valeur hors de la plage attendue pour une zone interne (" +
                        PlausibleMinimum.ToString(CultureInfo.CurrentCulture) + " à " +
                        PlausibleMaximum.ToString(CultureInfo.CurrentCulture) +
                        " °C) : cette zone ne mesure probablement pas un composant."),
                Plausible = plausible,
                ActivelyCooled = Measured.NotCollected<bool>(),
            };
        }

        /// <summary>Dixièmes de kelvin vers degrés Celsius, l'encodage des zones ACPI.</summary>
        internal static double FromDeciKelvin(uint deciKelvin) => (deciKelvin - KelvinOffset) / 10d;

        /// <summary>
        /// Réduit un nom de zone à ce qui l'identifie : <c>ACPI\ThermalZone\UAD0_0</c> et
        /// <c>\_TZ.UAD0</c> donnent tous deux <c>UAD0</c>.
        /// </summary>
        /// <remarks>
        /// Les deux sources nomment la même zone différemment : chemin de périphérique côté WMI,
        /// chemin ACPI côté compteur de performance. Sans normalisation commune, une session
        /// administrateur affichait <b>deux fois la même zone</b>, sous deux noms, avec la même
        /// température : défaut constaté au premier lancement élevé.
        /// <para>
        /// Le chemin complet n'apprend rien et déborde de la colonne ; le nom court, lui, est ce
        /// que le technicien retrouvera dans la documentation de sa carte mère.
        /// </para>
        /// </remarks>
        internal static string Shorten(string instanceName)
        {
            var tail = instanceName;

            var slash = tail.LastIndexOf('\\');
            if (slash >= 0) tail = tail.Substring(slash + 1);

            var dot = tail.LastIndexOf('.');
            if (dot >= 0) tail = tail.Substring(dot + 1);

            // Suffixe d'instance « _0 » ajouté par le gestionnaire de périphériques, pas par ACPI.
            var underscore = tail.LastIndexOf('_');
            if (underscore > 0 && underscore == tail.Length - 2) tail = tail.Substring(0, underscore);

            return tail;
        }
    }
}
