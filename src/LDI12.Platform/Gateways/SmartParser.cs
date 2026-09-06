using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Interprétation des données SMART brutes.
    /// </summary>
    /// <remarks>
    /// Les attributs SMART sont normalisés par le constructeur : seule la comparaison au seuil
    /// qu'il déclare a une valeur absolue. Les valeurs brutes, elles, sont interprétées
    /// différemment d'un fabricant à l'autre, d'où la prudence sur l'usure des SSD, rapportée
    /// comme approximative plutôt que comme une mesure fiable.
    /// </remarks>
    internal static class SmartParser
    {
        private const int IdReallocatedSectors = 5;
        private const int IdPowerOnHours = 9;
        private const int IdPowerCycles = 12;
        private const int IdWearLevelingCount = 177;
        private const int IdReportedUncorrectable = 187;
        private const int IdCommandTimeout = 188;
        private const int IdAirflowTemperature = 190;
        private const int IdTemperature = 194;
        private const int IdReallocationEvents = 196;
        private const int IdPendingSectors = 197;
        private const int IdOfflineUncorrectable = 198;
        private const int IdUdmaCrcErrors = 199;
        private const int IdLifetimeRemaining = 202;
        private const int IdSsdLifeLeft = 231;
        private const int IdMediaWearout = 233;
        private const int IdTotalLbaWritten = 241;

        /// <summary>
        /// Attributs dont la dégradation annonce une panne mécanique ou une perte de données.
        /// Les autres se dégradent en fonctionnement normal et ne doivent pas alarmer.
        /// </summary>
        private static readonly HashSet<int> CriticalIds = new HashSet<int>
        {
            IdReallocatedSectors, IdReportedUncorrectable, IdReallocationEvents,
            IdPendingSectors, IdOfflineUncorrectable,
        };

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { 1, "Taux d'erreurs de lecture brutes" },
            { 3, "Temps de démarrage des plateaux" },
            { 4, "Nombre de démarrages/arrêts" },
            { IdReallocatedSectors, "Secteurs réalloués" },
            { 7, "Taux d'erreurs de positionnement" },
            { IdPowerOnHours, "Heures de fonctionnement" },
            { 10, "Nouvelles tentatives de démarrage" },
            { IdPowerCycles, "Cycles d'allumage" },
            { IdWearLevelingCount, "Nivellement d'usure" },
            { 179, "Blocs de réserve utilisés" },
            { 181, "Échecs de programmation" },
            { 182, "Échecs d'effacement" },
            { IdReportedUncorrectable, "Erreurs non corrigeables signalées" },
            { IdCommandTimeout, "Délais de commande dépassés" },
            { IdAirflowTemperature, "Température du flux d'air" },
            { IdTemperature, "Température" },
            { IdReallocationEvents, "Événements de réallocation" },
            { IdPendingSectors, "Secteurs instables en attente" },
            { IdOfflineUncorrectable, "Secteurs non corrigeables hors ligne" },
            { IdUdmaCrcErrors, "Erreurs CRC de la liaison" },
            { IdLifetimeRemaining, "Durée de vie restante" },
            { IdSsdLifeLeft, "Durée de vie restante du SSD" },
            { IdMediaWearout, "Indicateur d'usure du support" },
            { IdTotalLbaWritten, "Total de secteurs écrits" },
        };

        /// <summary>
        /// Table d'attributs ATA, avec ou sans les seuils constructeur.
        /// </summary>
        /// <param name="predictedFailure">
        /// Verdict rendu par le disque lui-même, quand la lecture l'a fourni.
        /// </param>
        /// <remarks>
        /// Sans les seuils, la comparaison « la valeur normalisée est-elle retombée au seuil »
        /// ne peut pas se faire ici. Elle n'est pas perdue pour autant : le chemin non privilégié
        /// rend le résultat de cette même comparaison, faite par le disque. Un verdict emprunté
        /// vaut mieux qu'un verdict calculé sur des seuils inconnus.
        /// </remarks>
        public static SmartData Parse(
            byte[] attributeSector, byte[]? thresholdSector, bool? predictedFailure = null)
        {
            var thresholds = ReadThresholds(thresholdSector);
            var attributes = new List<SmartAttribute>();

            for (var i = 0; i < StorageNative.SmartAttributeCount; i++)
            {
                var offset = StorageNative.SmartAttributeTableOffset + (i * StorageNative.SmartAttributeSize);
                if (offset + StorageNative.SmartAttributeSize > attributeSector.Length) break;

                var id = attributeSector[offset];
                if (id == 0) continue;   // emplacement inutilisé

                var raw = 0L;
                for (var b = 0; b < 6; b++) raw |= (long)attributeSector[offset + 5 + b] << (8 * b);

                attributes.Add(new SmartAttribute
                {
                    Id = id,
                    Name = Names.TryGetValue(id, out var name) ? name : "Attribut " + id,
                    Current = attributeSector[offset + 3],
                    Worst = attributeSector[offset + 4],
                    Threshold = thresholds.TryGetValue(id, out var threshold) ? threshold : 0,
                    Raw = raw,
                    IsCritical = CriticalIds.Contains(id),
                });
            }

            return Build(attributes, predictedFailure);
        }

        private static Dictionary<int, int> ReadThresholds(byte[]? sector)
        {
            var thresholds = new Dictionary<int, int>();
            if (sector == null) return thresholds;

            for (var i = 0; i < StorageNative.SmartAttributeCount; i++)
            {
                var offset = StorageNative.SmartAttributeTableOffset + (i * StorageNative.SmartAttributeSize);
                if (offset + 2 > sector.Length) break;
                var id = sector[offset];
                if (id == 0) continue;
                thresholds[id] = sector[offset + 1];
            }
            return thresholds;
        }

        private static SmartData Build(List<SmartAttribute> attributes, bool? predictedFailure = null)
        {
            var byId = new Dictionary<int, SmartAttribute>();
            foreach (var attribute in attributes) byId[attribute.Id] = attribute;

            var reallocated = RawOf(byId, IdReallocatedSectors, "Les secteurs réalloués");
            var pending = RawOf(byId, IdPendingSectors, "Les secteurs instables");
            var uncorrectable = RawOf(byId, IdOfflineUncorrectable, "Les erreurs non corrigeables");

            return new SmartData
            {
                OverallStatus = predictedFailure.HasValue
                    ? Measured.Partial(DetermineStatus(attributes, predictedFailure), DataSource.NativeApi,
                        "Verdict rendu par le disque lui-même : les seuils constructeur, qui " +
                        "exigent une commande privilégiée, n'ont pas été lus.")
                    : Measured.Ok(DetermineStatus(attributes, predictedFailure), DataSource.Inferred),
                PowerOnHours = RawOf(byId, IdPowerOnHours, "Les heures de fonctionnement"),
                PowerCycles = RawOf(byId, IdPowerCycles, "Les cycles d'allumage"),
                TemperatureCelsius = ReadTemperature(byId),
                ReallocatedSectors = reallocated,
                PendingSectors = pending,
                UncorrectableErrors = uncorrectable,
                WearPercent = ReadWear(byId),
                TotalBytesWritten = byId.TryGetValue(IdTotalLbaWritten, out var written) && written.Raw > 0
                    ? Measured.Partial(written.Raw * 512, DataSource.NativeApi,
                        "Calculé à partir du compteur de secteurs écrits ; l'unité varie selon le constructeur.")
                    : Measured.Missing<long>("Le volume total écrit n'est pas exposé par ce disque."),
                Attributes = attributes,
            };
        }

        private static SmartOverallStatus DetermineStatus(
            List<SmartAttribute> attributes, bool? predictedFailure)
        {
            // Le verdict du disque prime quand on l'a : c'est lui qui a comparé ses valeurs à ses
            // propres seuils, y compris ceux que la lecture non privilégiée ne rend pas.
            if (predictedFailure == true) return SmartOverallStatus.Failing;

            var warning = false;
            foreach (var attribute in attributes)
            {
                // Le disque lui-même déclare la panne quand la valeur normalisée atteint le seuil.
                if (attribute.IsCritical && attribute.ThresholdExceeded) return SmartOverallStatus.Failing;
                if (attribute.IsCritical && attribute.Raw > 0) warning = true;
            }
            return warning ? SmartOverallStatus.Warning : SmartOverallStatus.Ok;
        }

        private static Measured<long> RawOf(Dictionary<int, SmartAttribute> byId, int id, string label)
            => byId.TryGetValue(id, out var attribute)
                ? Measured.Ok(attribute.Raw, DataSource.NativeApi)
                : Measured.Missing<long>(label + " ne font pas partie des attributs exposés par ce disque.");

        private static Measured<int> ReadTemperature(Dictionary<int, SmartAttribute> byId)
        {
            // Les octets de poids fort portent souvent les minima/maxima : seul l'octet bas est
            // la température courante.
            if (byId.TryGetValue(IdTemperature, out var temperature))
                return Measured.Ok((int)(temperature.Raw & 0xFF), DataSource.NativeApi);
            if (byId.TryGetValue(IdAirflowTemperature, out var airflow))
                return Measured.Ok((int)(airflow.Raw & 0xFF), DataSource.NativeApi);

            return Measured.Missing<int>("Ce disque n'expose pas de capteur de température.");
        }

        private static Measured<double> ReadWear(Dictionary<int, SmartAttribute> byId)
        {
            // Ces attributs portent la durée de vie *restante* en valeur normalisée :
            // l'usure en est le complément. L'identifiant retenu varie selon le constructeur.
            foreach (var id in new[] { IdSsdLifeLeft, IdMediaWearout, IdLifetimeRemaining, IdWearLevelingCount })
            {
                if (!byId.TryGetValue(id, out var attribute) || attribute.Current <= 0) continue;
                var wear = Math.Max(0d, Math.Min(100d, 100d - attribute.Current));
                return Measured.Partial(wear, DataSource.NativeApi,
                    "Estimée à partir de l'attribut « " + attribute.Name + " » ; l'échelle exacte dépend du constructeur.");
            }

            return Measured.Missing<double>(
                "L'usure n'est exposée que par les disques à mémoire flash, et pas par tous.");
        }

        /// <summary>
        /// Journal de santé NVMe, page 0x02. Contrairement à l'ATA, le format est normalisé :
        /// les valeurs sont directement exploitables, sans interprétation constructeur.
        /// </summary>
        public static SmartData ParseNvmeHealthLog(byte[] log)
        {
            if (log.Length < 512)
            {
                return new SmartData
                {
                    OverallStatus = Measured.Missing<SmartOverallStatus>("Journal de santé NVMe incomplet."),
                };
            }

            var criticalWarning = log[0];
            var temperatureKelvin = BitConverter.ToUInt16(log, 1);
            var availableSpare = log[3];
            var availableSpareThreshold = log[4];
            var percentageUsed = log[5];

            // Décalages fixés par la spécification NVMe (journal de santé, page 0x02) :
            // 32 Data Units Read · 48 Data Units Written · 64 Host Reads · 80 Host Writes
            // 96 Controller Busy · 112 Power Cycles · 128 Power On Hours · 144 Unsafe Shutdowns
            // 160 Media and Data Integrity Errors · 176 Error Log Entries
            var dataUnitsWritten = ReadUInt128AsInt64(log, 48);
            var powerCycles = ReadUInt128AsInt64(log, 112);
            var powerOnHours = ReadUInt128AsInt64(log, 128);
            var unsafeShutdowns = ReadUInt128AsInt64(log, 144);
            var mediaErrors = ReadUInt128AsInt64(log, 160);

            var status = criticalWarning != 0
                ? SmartOverallStatus.Failing
                : (percentageUsed >= 90 || (availableSpareThreshold > 0 && availableSpare <= availableSpareThreshold)
                    ? SmartOverallStatus.Warning
                    : SmartOverallStatus.Ok);

            var attributes = new List<SmartAttribute>
            {
                Synthetic(1, "Avertissement critique", criticalWarning),
                Synthetic(2, "Réserve disponible (%)", availableSpare),
                Synthetic(3, "Seuil de réserve (%)", availableSpareThreshold),
                Synthetic(4, "Durée de vie consommée (%)", percentageUsed, isCritical: true),
                Synthetic(5, "Erreurs de support", mediaErrors, isCritical: true),
                Synthetic(6, "Arrêts inattendus", unsafeShutdowns),
            };

            return new SmartData
            {
                OverallStatus = Measured.Ok(status, DataSource.NativeApi),
                PowerOnHours = Measured.Ok(powerOnHours, DataSource.NativeApi),
                PowerCycles = Measured.Ok(powerCycles, DataSource.NativeApi),

                // La température composite est en kelvins dans le journal NVMe.
                TemperatureCelsius = temperatureKelvin > 0
                    ? Measured.Ok(temperatureKelvin - 273, DataSource.NativeApi)
                    : Measured.Missing<int>("Ce disque NVMe n'expose pas de température composite."),

                // Le NVMe ne connaît pas la notion de secteur réalloué : ne pas inventer un zéro
                // qui se lirait comme « aucun problème ».
                ReallocatedSectors = NotApplicableToNvme("Les secteurs réalloués"),
                PendingSectors = NotApplicableToNvme("Les secteurs instables"),
                UncorrectableErrors = Measured.Ok(mediaErrors, DataSource.NativeApi),
                WearPercent = Measured.Ok((double)percentageUsed, DataSource.NativeApi),

                // Une unité de données NVMe vaut 1000 blocs de 512 octets, par spécification.
                TotalBytesWritten = dataUnitsWritten > 0
                    ? Measured.Ok(dataUnitsWritten * 1000L * 512L, DataSource.NativeApi)
                    : Measured.Missing<long>("Le volume écrit n'est pas renseigné."),
                Attributes = attributes,
            };
        }

        private static Measured<long> NotApplicableToNvme(string label)
            => Measured.Missing<long>(
                label + " sont une notion propre aux disques ATA : le NVMe rapporte à la place " +
                "sa réserve disponible et sa durée de vie consommée.");

        private static SmartAttribute Synthetic(int id, string name, long raw, bool isCritical = false)
            => new SmartAttribute { Id = id, Name = name, Raw = raw, IsCritical = isCritical };

        /// <summary>
        /// Les compteurs NVMe sont sur 128 bits. Les 64 bits de poids faible suffisent très
        /// largement : le débordement demanderait des millions d'années de fonctionnement.
        /// </summary>
        private static long ReadUInt128AsInt64(byte[] buffer, int offset)
        {
            if (offset + 8 > buffer.Length) return 0;
            var value = BitConverter.ToUInt64(buffer, offset);
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }
    }
}
