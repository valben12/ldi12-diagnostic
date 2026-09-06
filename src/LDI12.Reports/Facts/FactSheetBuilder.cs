using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Reports.Facts
{
    /// <summary>
    /// Construit les fiches de caractéristiques à partir d'un instantané.
    /// </summary>
    /// <remarks>
    /// Point unique où une mesure devient du texte affichable. Tout passe par <see cref="Of"/>,
    /// qui traduit l'état de disponibilité en <see cref="FactState"/> et reporte la raison :
    /// il n'existe aucun chemin par lequel une valeur absente pourrait apparaître comme « 0 »,
    /// « Inconnu » ou une chaîne vide. C'est la contrepartie, côté affichage, de la garantie que
    /// <c>Measured&lt;T&gt;</c> apporte côté collecte.
    ///
    /// Un groupe sans aucune ligne visible et sans ligne de tableau n'est pas produit : mieux
    /// vaut ne rien montrer que montrer un cadre vide dont on ne sait pas s'il est vide parce
    /// qu'il n'y a rien, ou parce que la mesure a échoué.
    /// </remarks>
    public static class FactSheetBuilder
    {
        public static IReadOnlyList<FactSheet> Build(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var sheets = new List<FactSheet>();
            AddSheet(sheets, DiagnosticCategory.Hardware, "Matériel", BuildHardware(snapshot));
            AddSheet(sheets, DiagnosticCategory.Storage, "Stockage", BuildStorage(snapshot.Storage));
            AddSheet(sheets, DiagnosticCategory.Windows, "Windows", BuildWindows(snapshot));
            AddSheet(sheets, DiagnosticCategory.Network, "Réseau", BuildNetwork(snapshot.Network));
            AddSheet(sheets, DiagnosticCategory.Security, "Sécurité", BuildSecurity(snapshot.Security));
            AddSheet(sheets, DiagnosticCategory.Performance, "Performances", BuildPerformance(snapshot.Performance));
            return sheets;
        }

        /// <summary>Fiche d'un domaine, ou <c>null</c> si ce domaine n'a produit aucune caractéristique.</summary>
        public static FactSheet? For(SystemSnapshot snapshot, DiagnosticCategory category)
        {
            foreach (var sheet in Build(snapshot))
                if (sheet.Category == category)
                    return sheet;
            return null;
        }

        private static void AddSheet(
            ICollection<FactSheet> sheets, DiagnosticCategory category, string title, IReadOnlyList<FactGroup> groups)
        {
            if (groups.Count > 0) sheets.Add(new FactSheet(category, title, groups));
        }

        // ================================================================= Matériel

        private static IReadOnlyList<FactGroup> BuildHardware(SystemSnapshot snapshot)
        {
            var hardware = snapshot.Hardware;
            var groups = new List<FactGroup>();

            var cpu = hardware.Cpu;
            Add(groups, "Processeur", null, new[]
            {
                Str("Modèle", cpu.Model),
                Str("Fabricant", cpu.Manufacturer),
                Str("Architecture", cpu.Architecture),
                Str("Socket", cpu.Socket),
                Of("Cœurs physiques", cpu.PhysicalCores, v => v.ToString(CultureInfo.CurrentCulture)),
                Of("Cœurs logiques", cpu.LogicalCores, v => v.ToString(CultureInfo.CurrentCulture)),
                Of("Fréquence de base", cpu.BaseClockMhz, ValueFormat.Mhz),
                Of("Fréquence maximale", cpu.MaxClockMhz, ValueFormat.Mhz),
                Of("Fréquence courante", cpu.CurrentClockMhz, ValueFormat.Mhz),
                Of("Cache L2", cpu.L2CacheKb, v => ValueFormat.Bytes(v * 1024L)),
                Of("Cache L3", cpu.L3CacheKb, v => ValueFormat.Bytes(v * 1024L)),
                Flag("Virtualisation", cpu.VirtualizationEnabled, "Activée", "Désactivée", FactTone.Neutral, FactTone.Neutral),
                Of("Charge", cpu.UsagePercent, ValueFormat.Percent),
                // La couche de capteurs, quand elle est active, donne la vraie température du
                // paquet ; sinon la mesure absente porte l'explication de son absence.
                Of("Température", hardware.Thermal.CpuPackageCelsius.HasValue
                        ? hardware.Thermal.CpuPackageCelsius
                        : cpu.TemperatureCelsius,
                    v => ValueFormat.Celsius((int)Math.Round(v)),
                    v => v >= 90 ? FactTone.Bad : v >= 80 ? FactTone.Warning : FactTone.Good),
                Flag("Bridage de fréquence", cpu.ThrottlingDetected, "Détecté", "Non détecté", FactTone.Bad, FactTone.Good),
            });

            var memory = hardware.Memory;
            Add(groups, "Mémoire vive", null, new[]
            {
                Of("Capacité totale", memory.TotalBytes, ValueFormat.Bytes),
                Of("Disponible", memory.AvailableBytes, ValueFormat.Bytes),
                Of("Occupation", memory.UsagePercent, ValueFormat.Percent, v => v >= 90 ? FactTone.Bad : v >= 80 ? FactTone.Warning : FactTone.Good),
                Of("Mémoire validée", memory.CommittedBytes, ValueFormat.Bytes),
                Of("Limite de validation", memory.CommitLimitBytes, ValueFormat.Bytes),
                Of("Emplacements occupés", memory.SlotsUsed, v => v.ToString(CultureInfo.CurrentCulture)),
                Of("Emplacements totaux", memory.SlotsTotal, v => v.ToString(CultureInfo.CurrentCulture)),
            }, MemoryModules(memory));

            foreach (var gpu in hardware.Gpus)
            {
                Add(groups, "Carte graphique" + Suffix(gpu.Model), null, new[]
                {
                    Str("Modèle", gpu.Model),
                    Str("Fabricant", gpu.Manufacturer),
                    Of("Mémoire vidéo", gpu.VideoMemoryBytes, ValueFormat.Bytes),
                    Str("Version du pilote", gpu.DriverVersion),
                    Of("Date du pilote", gpu.DriverDate, ValueFormat.Date),
                    Str("Fournisseur du pilote", gpu.DriverProvider),
                    Str("Résolution", gpu.CurrentResolution),
                    Of("Fréquence d'affichage", gpu.RefreshHz, v => v + " Hz"),
                    Of("Charge", gpu.UsagePercent, ValueFormat.Percent),
                    Of("Température", gpu.TemperatureCelsius, v => ValueFormat.Celsius((int)Math.Round(v)),
                        v => v >= 85 ? FactTone.Bad : v >= 75 ? FactTone.Warning : FactTone.Good),
                    Of("Ventilateur", gpu.FanPercent, ValueFormat.Percent),
                    Flag("Pilote générique Microsoft", gpu.UsesGenericMicrosoftDriver,
                        "Oui : la carte fonctionne sans accélération", "Non : pilote constructeur en place",
                        FactTone.Bad, FactTone.Good),
                });
            }

            AddDisplays(groups, hardware.Displays);
            AddSerialPorts(groups, snapshot);
            AddThermal(groups, snapshot);

            var board = hardware.Motherboard;
            Add(groups, "Carte mère et machine", null, new[]
            {
                Str("Fabricant de la carte", board.Manufacturer),
                Str("Modèle de la carte", board.Model),
                Str("Révision", board.Version),
                Str("Numéro de série de la carte", board.SerialNumber),
                Str("Fabricant de la machine", board.SystemManufacturer),
                Str("Modèle de la machine", board.SystemModel),
                Str("Référence constructeur", board.SystemSku),
                Str("Type de châssis", board.ChassisType),
                Str("Identifiant système", board.SystemUuid),
            });

            var bios = board.Bios;
            Add(groups, "Firmware", null, new[]
            {
                Str("Éditeur", bios.Vendor),
                Str("Version", bios.Version),
                Of("Date", bios.ReleaseDate, ValueFormat.Date),
                Of("Mode", bios.Mode, DescribeFirmware, v => v == FirmwareMode.LegacyBios ? FactTone.Warning : FactTone.Neutral),
                Flag("Démarrage sécurisé", board.SecureBootEnabled, "Activé", "Désactivé", FactTone.Good, FactTone.Warning),
            });

            var tpm = board.Tpm;
            Add(groups, "Module de sécurité (TPM)", null, new[]
            {
                Flag("Présent", tpm.Present, "Oui", "Non", FactTone.Good, FactTone.Neutral),
                Flag("Activé", tpm.Enabled, "Oui", "Non", FactTone.Good, FactTone.Warning),
                Flag("Prêt à l'emploi", tpm.Ready, "Oui", "Non", FactTone.Good, FactTone.Warning),
                Str("Version de spécification", tpm.SpecVersion),
                Str("Fabricant", tpm.Manufacturer),
            });

            foreach (var battery in hardware.Batteries)
            {
                Add(groups, "Batterie" + Suffix(battery.Name), null, new[]
                {
                    Str("Modèle", battery.Name),
                    Str("Chimie", battery.Chemistry),
                    Str("État", battery.Status),
                    Of("Charge", battery.ChargePercent, v => v + " %"),
                    Of("Capacité d'origine", battery.DesignCapacityMwh, v => ValueFormat.Number(v) + " mWh"),
                    Of("Capacité à pleine charge", battery.FullChargeCapacityMwh, v => ValueFormat.Number(v) + " mWh"),
                    Of("Usure", battery.WearPercent, ValueFormat.Percent,
                        v => v >= 40 ? FactTone.Bad : v >= 20 ? FactTone.Warning : FactTone.Good),
                    Of("Cycles de charge", battery.CycleCount, v => ValueFormat.Number(v)),
                });
            }

            AddHardwareErrors(groups, hardware.Errors);

            return groups;
        }

        /// <summary>
        /// Ce que le matériel a signalé de lui-même, avant l'arrivée du technicien.
        /// </summary>
        /// <remarks>
        /// Le libellé écrit par Windows figure dans le tableau parce qu'il nomme le composant
        /// concerné. Il est reproduit, jamais interprété : la qualification de chaque ligne vient
        /// de l'identifiant d'événement.
        /// </remarks>
        private static void AddHardwareErrors(ICollection<FactGroup> groups, HardwareErrorsInfo errors)
        {
            var rows = new List<FactRow>();
            foreach (var group in errors.Errors)
                rows.Add(new FactRow(new[]
                {
                    DescribeErrorKind(group.Kind),
                    group.EventId.ToString(CultureInfo.CurrentCulture),
                    ValueFormat.Number(group.Count),
                    ValueFormat.DateTime(group.FirstSeen),
                    ValueFormat.DateTime(group.LastSeen),
                    Shorten(group.Sample),
                }, group.Kind == HardwareErrorKind.Uncorrected ? FactTone.Bad
                    : group.Kind == HardwareErrorKind.Corrected ? FactTone.Warning
                    : FactTone.Neutral));

            Add(groups, "Erreurs matérielles signalées",
                "Enregistrées par Windows lui-même, hors de toute analyse de ce logiciel.", new[]
                {
                    Of("Fenêtre analysée", errors.WindowDays, v => v + " derniers jours"),
                    Of("Irrécupérables", errors.UncorrectedCount, v => ValueFormat.Number(v),
                        v => v > 0 ? FactTone.Bad : FactTone.Good),
                    Of("Corrigées", errors.CorrectedCount, v => ValueFormat.Number(v),
                        v => v >= 25 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good),
                }, new FactTable(
                    new[] { "Nature", "Événement", "Occurrences", "Première", "Dernière", "Libellé de Windows" },
                    rows,
                    "Aucune erreur matérielle signalée sur la fenêtre analysée."));

            // « Jamais exécuté » est un résultat, pas une lacune : il tient dans le verdict, et
            // les lignes de date et de libellé n'apparaissent que s'il y a eu un test à décrire.
            var test = errors.LastMemoryTest;
            var memory = new List<Fact> { Of("Verdict", errors.MemoryTest, DescribeMemoryTest, MemoryTestTone) };
            if (test != null)
            {
                memory.Add(Known("Date du test", ValueFormat.Date(test.Date)));
                if (test.Detail != null) memory.Add(Known("Libellé de Windows", Shorten(test.Detail)));
            }

            Add(groups, "Test mémoire de Windows", null, memory);

            var dumps = new List<FactRow>();
            foreach (var dump in errors.CrashDumps)
                dumps.Add(new FactRow(new[]
                {
                    ValueFormat.DateTime(dump.Date),
                    dump.IsFullDump ? "Vidage complet" : "Mini-vidage",
                    ValueFormat.Bytes(dump.SizeBytes),
                    dump.Path,
                }, FactTone.Neutral));

            Add(groups, "Rapports de plantage",
                "Seuls le chemin, la date et la taille sont relevés : le contenu n'est jamais ouvert.", new[]
                {
                    Flag("Enregistrement", errors.CrashDumpsEnabled, "Activé", "Désactivé",
                        FactTone.Good, FactTone.Warning),
                    Str("Réglage", errors.CrashDumpMode),
                    Known("Rapports présents", ValueFormat.Number(errors.CrashDumps.Count)),
                }, new FactTable(
                    new[] { "Date", "Type", "Taille", "Fichier" },
                    dumps,
                    "Aucun rapport de plantage sur le disque."));
        }

        private static string DescribeErrorKind(HardwareErrorKind kind) => kind switch
        {
            HardwareErrorKind.Uncorrected => "Irrécupérable",
            HardwareErrorKind.Corrected => "Corrigée",
            HardwareErrorKind.Informational => "Information",
            _ => "Indéterminée",
        };

        private static string DescribeMemoryTest(MemoryTestOutcome outcome) => outcome switch
        {
            MemoryTestOutcome.NeverRun => "Jamais exécuté sur cette machine",
            MemoryTestOutcome.NoErrors => "Aucune erreur détectée",
            MemoryTestOutcome.ErrorsFound => "Des erreurs matérielles ont été détectées",
            MemoryTestOutcome.Interrupted => "Interrompu, sans conclusion",
            _ => "Indéterminé",
        };

        private static FactTone MemoryTestTone(MemoryTestOutcome outcome) => outcome switch
        {
            MemoryTestOutcome.NoErrors => FactTone.Good,
            MemoryTestOutcome.ErrorsFound => FactTone.Bad,
            MemoryTestOutcome.Interrupted => FactTone.Warning,
            _ => FactTone.Neutral,
        };

        private static FactTable MemoryModules(MemoryInfo memory)
        {
            var rows = new List<FactRow>();
            foreach (var module in memory.Modules)
            {
                // Une barrette sous-cadencée par la carte mère est une cause classique de
                // performances décevantes : l'écart mérite d'être visible dans le tableau.
                var underclocked = module.SpeedMhz.IsReliable && module.ConfiguredSpeedMhz.IsReliable &&
                                   module.ConfiguredSpeedMhz.Value < module.SpeedMhz.Value;

                rows.Add(new FactRow(new[]
                {
                    Cell(module.DeviceLocator, module.BankLabel),
                    Cell(module.CapacityBytes, ValueFormat.Bytes),
                    Cell(module.MemoryType),
                    Cell(module.FormFactor),
                    Cell(module.SpeedMhz, v => v + " MHz"),
                    Cell(module.ConfiguredSpeedMhz, v => v + " MHz"),
                    Cell(module.Manufacturer),
                    Cell(module.PartNumber),
                }, underclocked ? FactTone.Warning : FactTone.Neutral));
            }

            return new FactTable(
                new[] { "Emplacement", "Capacité", "Type", "Format", "Fréquence nominale", "Fréquence appliquée", "Fabricant", "Référence" },
                rows,
                "Le détail des barrettes n'a pas pu être lu.");
        }

        // ================================================================= Stockage

        private static IReadOnlyList<FactGroup> BuildStorage(StorageSnapshot storage)
        {
            var groups = new List<FactGroup>();

            Add(groups, "Entretien du stockage", null, new[]
            {
                Flag("Notification de suppression", storage.Maintenance.TrimEnabled,
                    "Activée", "Désactivée", FactTone.Good, FactTone.Warning),
            });

            foreach (var disk in storage.PhysicalDisks)
            {
                var title = "Disque " + disk.Index.ToString(CultureInfo.CurrentCulture) + Suffix(disk.Model);
                var subtitle = disk.IsSystemDisk.Or(false) ? "Disque système" : null;
                var smart = disk.Smart;

                Add(groups, title, subtitle, new[]
                {
                    Str("Modèle", disk.Model),
                    Str("Fabricant", disk.Manufacturer),
                    Str("Micrologiciel", disk.Firmware),
                    Str("Numéro de série", disk.SerialNumber),
                    Of("Interface", disk.BusType, DescribeBus),
                    Of("Type de support", disk.MediaType, DescribeMedia),
                    Of("Capacité", disk.CapacityBytes, ValueFormat.Bytes),

                    // Les trois lignes que seule une session administrateur peut renseigner.
                    // Sur un compte standard elles portent « privilèges requis », ce qui se lit
                    // autrement qu'un tiret : la donnée existe, c'est l'accès qui manque.
                    Of("Rotation", disk.Identity.RotationRpm, DescribeRpm),
                    Of("Liaison négociée", disk.Identity.LinkGigabitsPerSecond, ValueFormat.LinkGigabits,
                        _ => LinkTone(disk.Identity)),
                    Of("Liaison maximale du disque", disk.Identity.MaximumGigabitsPerSecond,
                        ValueFormat.LinkGigabits),
                    Of("État SMART", smart.OverallStatus, DescribeSmart, SmartTone),
                    Of("Heures de fonctionnement", smart.PowerOnHours, ValueFormat.OperatingHours),
                    Of("Cycles d'allumage", smart.PowerCycles, ValueFormat.Number),
                    Of("Température", smart.TemperatureCelsius, ValueFormat.Celsius,
                        v => v >= 60 ? FactTone.Bad : v >= 50 ? FactTone.Warning : FactTone.Good),
                    Of("Secteurs réalloués", smart.ReallocatedSectors, ValueFormat.Number,
                        v => v > 0 ? FactTone.Bad : FactTone.Good),
                    Of("Secteurs en attente", smart.PendingSectors, ValueFormat.Number,
                        v => v > 0 ? FactTone.Bad : FactTone.Good),
                    Of("Erreurs non corrigeables", smart.UncorrectableErrors, ValueFormat.Number,
                        v => v > 0 ? FactTone.Bad : FactTone.Good),
                    Of("Usure", smart.WearPercent, ValueFormat.Percent,
                        v => v >= 90 ? FactTone.Bad : v >= 70 ? FactTone.Warning : FactTone.Good),
                    Of("Total écrit", smart.TotalBytesWritten, ValueFormat.Bytes),
                }, SmartAttributes(smart));
            }

            Add(groups, "Volumes", null, Array.Empty<Fact>(), Volumes(storage));
            AddSystemSpace(groups, storage);
            return groups;
        }

        private static FactTable SmartAttributes(SmartData smart)
        {
            var rows = new List<FactRow>();
            foreach (var attribute in smart.Attributes)
            {
                var tone = attribute.ThresholdExceeded
                    ? FactTone.Bad
                    : attribute.IsCritical && attribute.Raw > 0 ? FactTone.Warning : FactTone.Neutral;

                rows.Add(new FactRow(new[]
                {
                    attribute.Id.ToString("X2", CultureInfo.InvariantCulture),
                    attribute.Name,
                    attribute.Current.ToString(CultureInfo.CurrentCulture),
                    attribute.Worst.ToString(CultureInfo.CurrentCulture),
                    attribute.Threshold > 0 ? attribute.Threshold.ToString(CultureInfo.CurrentCulture) : "-",
                    ValueFormat.Number(attribute.Raw),
                }, tone));
            }

            return new FactTable(
                new[] { "Id", "Attribut", "Actuel", "Pire", "Seuil", "Valeur brute" },
                rows,
                "Ce disque n'a pas fourni de table d'attributs SMART.");
        }

        private static FactTable Volumes(StorageSnapshot storage)
        {
            var rows = new List<FactRow>();
            foreach (var volume in storage.Volumes)
            {
                var usage = volume.UsagePercent;
                var tone = usage.IsReliable
                    ? usage.Value >= 95 ? FactTone.Bad : usage.Value >= 90 ? FactTone.Warning : FactTone.Good
                    : FactTone.Neutral;

                rows.Add(new FactRow(new[]
                {
                    Cell(volume.DriveLetter),
                    Cell(volume.Label),
                    Cell(volume.FileSystem),
                    Cell(volume.TotalBytes, ValueFormat.Bytes),
                    Cell(volume.FreeBytes, ValueFormat.Bytes),
                    Cell(volume.UsagePercent, ValueFormat.Percent),
                    Cell(volume.BitLockerStatus),
                    Cell(volume.DiskIndex, v => "Disque " + v),
                }, tone));
            }

            return new FactTable(
                new[] { "Lettre", "Nom", "Système de fichiers", "Capacité", "Libre", "Occupation", "Chiffrement", "Disque" },
                rows,
                "Aucun volume n'a pu être énuméré.");
        }

        // ================================================================= Windows

        /// <summary>
        /// Ce qui occupe le disque système et que rien d'autre ne mesure.
        /// </summary>
        /// <remarks>
        /// Chaque ligne porte ce que l'élément est et ce qu'il en coûte de récupérer sa place.
        /// Une taille seule invite à supprimer ; une taille accompagnée de sa contrepartie
        /// permet de décider, et c'est le technicien qui décide, pas le tableau.
        /// </remarks>
        private static void AddSystemSpace(ICollection<FactGroup> groups, StorageSnapshot storage)
        {
            var space = storage.SystemSpace;
            if (space.Consumers.Count == 0) return;

            var rows = new List<FactRow>();

            foreach (var consumer in space.Consumers)
                rows.Add(new FactRow(new[]
                {
                    consumer.Name,
                    Cell(consumer.Bytes, ValueFormat.Bytes),
                    consumer.Purpose,
                    DescribeReclaim(consumer.Reclaim),
                    consumer.Cost ?? "-",
                }, consumer.Kind == SpaceKind.PreviousWindows ? FactTone.Warning : FactTone.Neutral));

            var facts = new List<Fact>
            {
                Known("Relevé sur", space.Volume),
                Known("Total relevé", ValueFormat.Bytes(space.TotalBytes)),
            };

            var volume = SystemVolumeOf(storage);
            if (volume != null && volume.TotalBytes.IsReliable && volume.TotalBytes.Value > 0)
                facts.Add(Known("Part du volume",
                    Math.Round(100d * space.TotalBytes / volume.TotalBytes.Value) + " %"));

            Add(groups, "Occupation du disque système",
                "Ces fichiers sont cachés et protégés : aucun explorateur ne les montre, et un client " +
                "qui cherche ce qui remplit son disque ne les trouvera jamais. Les fichiers temporaires " +
                "et la corbeille sont ailleurs, dans le nettoyage.",
                facts,
                new FactTable(
                    new[] { "Élément", "Taille", "À quoi il sert", "Récupérable", "Ce qu'il en coûte" },
                    rows,
                    "Aucun occupant notable en dehors de ce qui est relevé ailleurs."));
        }

        private static VolumeInfo? SystemVolumeOf(StorageSnapshot storage)
        {
            foreach (var volume in storage.Volumes)
                if (volume.IsSystemVolume.Or(false)) return volume;
            return null;
        }

        private static string DescribeReclaim(SpaceReclaim reclaim) => reclaim switch
        {
            SpaceReclaim.None => "Non : Windows en a besoin",
            SpaceReclaim.Setting => "Par un réglage",
            SpaceReclaim.Removable => "Oui, sans rien perdre",
            SpaceReclaim.Automatic => "Windows s'en charge",
            _ => "À voir avec le client",
        };

        private static IReadOnlyList<FactGroup> BuildWindows(SystemSnapshot snapshot)
        {
            var windows = snapshot.Windows;
            var groups = new List<FactGroup>();

            var install = windows.Install;
            Add(groups, "Installation", null, new[]
            {
                Of("Date d'installation", install.InstallDate, ValueFormat.Date),
                Of("Dernier démarrage", install.LastBootTime, ValueFormat.DateTime),
                Of("Temps de fonctionnement", install.Uptime, ValueFormat.Duration),
                Str("Activation", install.ActivationStatus),
                Str("Langue", install.Locale),
                Str("Fuseau horaire", install.TimeZone),
                Flag("Redémarrage en attente", install.RebootPending, "Oui", "Non", FactTone.Warning, FactTone.Good),
                Flag("Démarrage rapide", install.FastStartupEnabled,
                    "Activé : un « redémarrage » ne recharge pas toujours le système", "Désactivé",
                    FactTone.Warning, FactTone.Neutral),
                Flag("Restauration système", install.SystemRestoreEnabled, "Activée", "Désactivée",
                    FactTone.Good, FactTone.Warning),
                Of("Fichier d'échange", install.PageFileSizeBytes, ValueFormat.Bytes),
            });

            var files = windows.SystemFiles;
            Add(groups, "Intégrité des fichiers système", null, new[]
            {
                Of("Vérificateur de fichiers", files.Sfc, DescribeSfc, SfcTone),
                Of("Magasin de composants", files.ComponentStore, DescribeStore, StoreTone),
                Of("Dernière vérification", files.LastCheck, ValueFormat.DateTime),
                Str("Extrait de journal", files.Evidence),
            });

            var updates = windows.Updates;
            Add(groups, "Mises à jour", null, new[]
            {
                Of("Dernière installation", updates.LastInstalled, ValueFormat.Date),
                Of("Ancienneté", updates.DaysSinceLastUpdate, v => v + " jour" + (v > 1 ? "s" : ""),
                    v => v >= 120 ? FactTone.Bad : v >= 60 ? FactTone.Warning : FactTone.Good),
                Str("État du service", updates.ServiceState),
                Of("Mises à jour installées", updates.InstalledCount, v => ValueFormat.Number(v)),
            }, FailedUpdates(updates));

            AddSupportAndUpgrade(groups, snapshot);
            AddDevices(groups, windows);
            AddDrivers(groups, windows);
            AddStartup(groups, windows);
            AddTasks(groups, windows);
            AddSoftware(groups, windows);
            AddServices(groups, windows);
            AddEvents(groups, windows);
            AddStability(groups, snapshot);
            AddClock(groups, windows);
            AddProfiles(groups, windows);
            AddSafetyNet(groups, windows);
            AddPrinting(groups, windows);
            AddAudio(groups, windows);

            return groups;
        }

        private static FactTable FailedUpdates(UpdatesInfo updates)
        {
            var rows = new List<FactRow>();
            foreach (var failure in updates.Failures)
                rows.Add(new FactRow(new[]
                {
                    failure.Title,
                    failure.KbNumber ?? "-",
                    failure.Date.HasValue ? ValueFormat.Date(failure.Date.Value) : "-",
                    failure.ResultCode ?? "-",
                }, FactTone.Bad));

            return new FactTable(
                new[] { "Mise à jour", "KB", "Date", "Code" },
                rows,
                "Aucun échec d'installation relevé.");
        }

        private static void AddDevices(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            if (windows.Devices.Count == 0) return;

            int faulty = 0, unknown = 0, disabled = 0;
            var rows = new List<FactRow>();

            foreach (var device in windows.Devices)
            {
                if (device.IsUnknownDevice) unknown++;
                if (device.IsDisabled) disabled++;
                if (device.ProblemCode == 0 && !device.IsUnknownDevice && !device.IsDisabled) continue;

                faulty++;
                rows.Add(new FactRow(new[]
                {
                    device.Name,
                    device.DeviceClass ?? "-",
                    device.Manufacturer ?? "-",
                    device.ProblemCode == 0 ? "-" : device.ProblemCode.ToString(CultureInfo.CurrentCulture),
                    device.ProblemLabel ?? (device.IsDisabled ? "Périphérique désactivé" : "Pilote absent"),
                }, device.IsDisabled ? FactTone.Warning : FactTone.Bad));
            }

            Add(groups, "Périphériques", null, new[]
            {
                Known("Périphériques énumérés", ValueFormat.Number(windows.Devices.Count)),
                Known("En défaut", ValueFormat.Number(faulty), faulty > 0 ? FactTone.Bad : FactTone.Good),
                Known("Sans pilote", ValueFormat.Number(unknown), unknown > 0 ? FactTone.Bad : FactTone.Good),
                Known("Désactivés", ValueFormat.Number(disabled), disabled > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Périphérique", "Classe", "Fabricant", "Code", "Problème" },
                rows,
                "Aucun périphérique en défaut."));
        }

        private static void AddDrivers(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            if (windows.Drivers.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var driver in windows.Drivers)
            {
                var suspect = driver.IsMicrosoftGeneric || driver.IsSigned == false;
                if (!suspect) continue;

                rows.Add(new FactRow(new[]
                {
                    driver.DeviceName,
                    driver.DeviceClass ?? "-",
                    driver.Manufacturer ?? "-",
                    driver.Version ?? "-",
                    driver.Date.HasValue ? ValueFormat.Date(driver.Date.Value) : "-",
                    driver.IsMicrosoftGeneric ? "Pilote générique Microsoft" : "Pilote non signé",
                }, driver.IsSigned == false ? FactTone.Bad : FactTone.Warning));
            }

            Add(groups, "Pilotes", null, new[]
            {
                Known("Pilotes recensés", ValueFormat.Number(windows.Drivers.Count)),
                Known("À vérifier", ValueFormat.Number(rows.Count), rows.Count > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Périphérique", "Classe", "Fabricant", "Version", "Date", "Observation" },
                rows,
                "Aucun pilote générique ni non signé."));
        }

        private static void AddStartup(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            if (windows.Startup.Count == 0) return;

            var rows = new List<FactRow>();
            var orphans = 0;

            foreach (var item in windows.Startup)
            {
                if (item.TargetMissing) orphans++;
                rows.Add(new FactRow(new[]
                {
                    item.Name,
                    DescribeStartupLocation(item.Location) + DescribeDelay(item.DelaySeconds),
                    item.Publisher ?? "-",
                    item.Enabled ? "Actif" : "Désactivé",
                    item.TargetMissing ? "Cible introuvable" : item.ImagePath ?? item.Command,
                }, item.TargetMissing ? FactTone.Warning : FactTone.Neutral));
            }

            Add(groups, "Démarrage automatique", null, new[]
            {
                Known("Entrées", ValueFormat.Number(windows.Startup.Count),
                    windows.Startup.Count >= 15 ? FactTone.Warning : FactTone.Neutral),
                Known("Entrées orphelines", ValueFormat.Number(orphans),
                    orphans > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Programme", "Emplacement", "Éditeur", "État", "Cible" },
                rows,
                "Aucun programme au démarrage."));
        }

        /// <summary>
        /// Un délai change le sens d'une entrée de démarrage.
        /// </summary>
        /// <remarks>
        /// Une tâche lancée dix minutes après l'ouverture de session ne pèse pas sur le démarrage
        /// comme un programme lancé immédiatement : l'écrire évite de faire supprimer la mauvaise.
        /// </remarks>
        private static string DescribeDelay(int? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return string.Empty;

            return seconds.Value >= 60
                ? " (après " + (seconds.Value / 60) + " min)"
                : " (après " + seconds.Value + " s)";
        }

        /// <summary>
        /// Les tâches planifiées ajoutées à la machine.
        /// </summary>
        /// <remarks>
        /// Celles que Windows range sous son propre dossier sont comptées et non listées. Le
        /// total et le nombre de tâches non lues figurent à côté, sans quoi la liste passerait
        /// pour l'inventaire complet du planificateur alors qu'elle en est la petite part.
        /// </remarks>
        private static void AddTasks(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var inventory = windows.Tasks;

            var rows = new List<FactRow>();
            foreach (var task in inventory.Tasks)
                rows.Add(new FactRow(new[]
                {
                    task.Path,
                    DescribeTriggers(task.Triggers) + DescribeDelay(task.DelaySeconds) +
                        (task.RepetitionMinutes.HasValue
                            ? ", toutes les " + task.RepetitionMinutes.Value + " min"
                            : string.Empty),
                    task.Enabled ? "Active" : "Désactivée",
                    task.LastRun.HasValue ? ValueFormat.DateTime(task.LastRun.Value) : "Jamais",
                    DescribeResult(task),
                    task.TargetMissing ? "Programme introuvable" : task.ImagePath ?? "-",
                }, task.TargetMissing || task.ResultKind == TaskResultKind.TaskError
                    ? FactTone.Warning
                    : FactTone.Neutral));

            Add(groups, "Tâches planifiées",
                "Celles que Windows range dans son propre dossier sont comptées, pas listées.", new[]
                {
                    Of("Tâches enregistrées", inventory.TotalCount, v => ValueFormat.Number(v)),
                    Of("Dont rangées par Windows", inventory.MicrosoftFolderCount, v => ValueFormat.Number(v)),
                    Of("Non lues faute de droits", inventory.Unreadable, v => ValueFormat.Number(v),
                        v => v > 0 ? FactTone.Warning : FactTone.Good),
                }, new FactTable(
                    new[] { "Tâche", "Déclenchement", "État", "Dernière exécution", "Résultat", "Programme" },
                    rows,
                    "Aucune tâche planifiée hors de celles de Windows."));
        }

        private static string DescribeTriggers(IReadOnlyList<TaskTriggerKind> triggers)
        {
            if (triggers.Count == 0) return "Aucun déclencheur";

            var parts = new List<string>();
            foreach (var trigger in triggers)
            {
                var label = DescribeTrigger(trigger);
                if (!parts.Contains(label)) parts.Add(label);
            }

            return string.Join(", ", parts.ToArray());
        }

        private static string DescribeTrigger(TaskTriggerKind trigger) => trigger switch
        {
            TaskTriggerKind.Logon => "À l'ouverture de session",
            TaskTriggerKind.Boot => "Au démarrage de Windows",
            TaskTriggerKind.Scheduled => "À heure fixe",
            TaskTriggerKind.Event => "Sur événement",
            TaskTriggerKind.Idle => "Machine inactive",
            TaskTriggerKind.Registration => "À l'installation",
            TaskTriggerKind.SessionChange => "Verrouillage ou session distante",
            _ => "Déclencheur inconnu",
        };

        /// <summary>
        /// Le code de la dernière exécution, dit pour ce qu'il est.
        /// </summary>
        /// <remarks>
        /// Le planificateur range ses propres états dans le même champ que le code de sortie du
        /// programme : afficher le nombre brut ferait passer « en cours d'exécution » pour une
        /// erreur, ce qu'un technicien n'a aucune raison de deviner.
        /// </remarks>
        private static string DescribeResult(ScheduledTaskInfo task) => task.ResultKind switch
        {
            TaskResultKind.Success => "Réussite",
            TaskResultKind.Informational => "État du planificateur",
            TaskResultKind.ProgramError => "Le programme a rendu le code " +
                                           (task.LastResult ?? 0).ToString(CultureInfo.CurrentCulture),
            TaskResultKind.TaskError => "Windows n'a pas pu l'exécuter",
            _ => "-",
        };

        /// <summary>
        /// Ce qui est installé.
        /// </summary>
        /// <remarks>
        /// La liste complète, et non les seules entrées signalées : c'est le document qu'un
        /// technicien parcourt avec le client pour retrouver ce qu'il a installé lui-même et ce
        /// qui est arrivé sans lui. Les entrées écartées (mises à jour et composants système)
        /// sont comptées à côté du total, sans quoi le chiffre ne se rapprocherait pas de ce
        /// qu'affiche « Programmes et fonctionnalités ».
        /// </remarks>
        private static void AddSoftware(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var inventory = windows.Software;
            if (inventory.Programs.Count == 0) return;

            var rows = new List<FactRow>();
            var flagged = 0;
            var user = 0;

            foreach (var program in inventory.Programs)
            {
                var note = SoftwareCatalog.Describe(program.Name);
                if (note != null) flagged++;
                if (program.Scope == SoftwareScope.User) user++;

                rows.Add(new FactRow(new[]
                {
                    program.Name,
                    program.Version ?? "-",
                    program.Publisher ?? "-",
                    program.InstalledOn.HasValue ? ValueFormat.Date(program.InstalledOn.Value) : "-",
                    DescribeScope(program.Scope),
                    note == null ? "-" : DescribeConcern(note),
                }, note == null ? FactTone.Neutral
                    : note.Concern == SoftwareConcern.EndOfSupport ? FactTone.Warning : FactTone.Neutral));
            }

            var facts = new List<Fact>
            {
                Known("Programmes installés", ValueFormat.Number(inventory.Programs.Count)),
                Known("Installés pour cet utilisateur seul", ValueFormat.Number(user)),
                Of("Mises à jour et composants écartés", inventory.HiddenEntries,
                    count => ValueFormat.Number(count)),
            };

            if (flagged > 0)
                facts.Add(Known("Signalés", ValueFormat.Number(flagged), FactTone.Warning));

            Add(groups, "Logiciels installés", inventory.Limitation, facts, new FactTable(
                new[] { "Programme", "Version", "Éditeur", "Installé le", "Portée", "Remarque" },
                rows,
                "Aucun logiciel n'a été relevé."));
        }

        /// <summary>
        /// Portée d'une installation, en aussi peu de mots que possible.
        /// </summary>
        /// <remarks>
        /// Le nom du programme est la colonne qui compte, et c'est elle qui se fait tronquer
        /// quand les autres s'allongent : « Toute la machine (32 bits) » coûtait à lui seul le
        /// quart de la largeur du tableau.
        /// </remarks>
        private static string DescribeScope(SoftwareScope scope) => scope switch
        {
            SoftwareScope.Machine32 => "Machine (32 bits)",
            SoftwareScope.User => "Cet utilisateur",
            _ => "Machine",
        };

        private static string DescribeConcern(SoftwareNote note)
            => note.Concern == SoftwareConcern.EndOfSupport
                ? "Plus corrigé" + (note.SupportEndedYear.HasValue
                      ? " depuis " + note.SupportEndedYear.Value.ToString(CultureInfo.CurrentCulture)
                      : string.Empty)
                : "Utilitaire d'optimisation";

        private static void AddServices(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            if (windows.Services.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var service in windows.Services)
            {
                if (service.Deviation == null) continue;
                rows.Add(new FactRow(new[]
                {
                    service.DisplayName,
                    service.Name,
                    service.State,
                    service.StartMode,
                    service.Deviation,
                }, service.IsEssential ? FactTone.Bad : FactTone.Warning));
            }

            Add(groups, "Services", null, new[]
            {
                Known("Services surveillés", ValueFormat.Number(windows.Services.Count)),
                Known("Écarts constatés", ValueFormat.Number(rows.Count),
                    rows.Count > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Service", "Nom interne", "État", "Démarrage", "Écart" },
                rows,
                "Tous les services surveillés sont dans l'état attendu."));
        }

        private static void AddEvents(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var events = windows.Events;

            var errors = new List<FactRow>();
            AppendEvents(errors, events.CriticalErrors, FactTone.Bad);
            AppendEvents(errors, events.RecurringErrors, FactTone.Warning);

            Add(groups, "Journaux d'événements", null, new[]
            {
                Of("Fenêtre analysée", events.WindowDays, v => v + " derniers jours"),
                Of("Arrêts inattendus", events.UnexpectedShutdowns, v => ValueFormat.Number(v),
                    v => v >= 3 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Journal", "Source", "Id", "Occurrences", "Dernière", "Extrait" },
                errors,
                "Aucune erreur critique ni récurrente sur la fenêtre analysée."));

            if (events.DiskErrors.Count > 0)
            {
                var disk = new List<FactRow>();
                AppendEvents(disk, events.DiskErrors, FactTone.Bad);
                Add(groups, "Erreurs disque", "Signalées par Windows lui-même, indépendamment du SMART.",
                    Array.Empty<Fact>(),
                    new FactTable(
                        new[] { "Journal", "Source", "Id", "Occurrences", "Dernière", "Extrait" },
                        disk,
                        "Aucune erreur disque."));
            }

            if (events.Bsods.Count > 0)
            {
                var rows = new List<FactRow>();
                foreach (var bsod in events.Bsods)
                    rows.Add(new FactRow(new[]
                    {
                        ValueFormat.DateTime(bsod.Date),
                        bsod.BugCheckCode ?? "-",
                        bsod.Parameters ?? "-",
                        bsod.DumpPath ?? "-",
                    }, FactTone.Bad));

                Add(groups, "Écrans bleus", null, Array.Empty<Fact>(),
                    new FactTable(
                        new[] { "Date", "Code d'arrêt", "Paramètres", "Fichier de vidage" },
                        rows,
                        "Aucun écran bleu enregistré."));
            }
        }

        /// <summary>
        /// La stabilité dans le temps : qui échoue, et depuis quand.
        /// </summary>
        /// <remarks>
        /// Deux cadres et non un seul, parce que ce sont deux lectures. Le premier répond à
        /// « qu'est-ce qui plante ? » et se lit par programme ; le second répond à « depuis
        /// quand ? » et se lit par date, incidents et changements mêlés. Ce mélange est
        /// exactement ce qu'on vient chercher : voir une mise à jour ou une installation juste
        /// avant le début d'une série de plantages. Il n'établit aucune cause, et la colonne qui
        /// les distingue reste sous les yeux du technicien pour cette raison.
        /// </remarks>
        private static void AddStability(ICollection<FactGroup> groups, SystemSnapshot snapshot)
        {
            var stability = snapshot.Windows.Stability;
            var collected = stability.WindowDays.IsReliable;

            var failures = new List<FactRow>();
            foreach (var program in stability.Programs)
                failures.Add(new FactRow(new[]
                {
                    program.Name,
                    DescribeIncident(program.Kind),
                    ValueFormat.Number(program.Count),
                    ValueFormat.Date(program.FirstSeen),
                    ValueFormat.Date(program.LastSeen),
                    program.Module ?? "-",
                }, program.Count >= 10 ? FactTone.Bad : FactTone.Warning));

            Add(groups, "Stabilité dans le temps",
                "Ce qui plante sur cette machine, regroupé par programme.", new[]
                {
                    Of("Fenêtre analysée", stability.WindowDays, v => v + " derniers jours"),

                    // La période réellement couverte, distincte de la fenêtre demandée : sans
                    // elle, un journal purgé se lirait comme une période calme.
                    Of("Le plus ancien incident lu", stability.OldestEntry, ValueFormat.Date),

                    Of("Programmes fermés brutalement", stability.ProgramCrashes, v => ValueFormat.Number(v),
                        v => v >= 20 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good),
                    Of("Programmes qui ont cessé de répondre", stability.ProgramHangs, v => ValueFormat.Number(v),
                        v => v >= 20 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good),
                    Of("Services arrêtés seuls", stability.ServiceCrashes, v => ValueFormat.Number(v),
                        v => v >= 10 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good),
                }, new FactTable(
                    new[] { "Programme ou service", "Nature", "Occurrences", "Première", "Dernière", "Composant mis en cause" },
                    failures,
                    collected
                        ? "Aucun plantage enregistré sur la fenêtre analysée."
                        : "Les journaux n'ont pas été lus : réservés au diagnostic complet."));

            // Sans les incidents, la chronologie ne montrerait que les changements, et se
            // lirait comme une machine que rien n'a perturbée depuis trois mois. Une analyse
            // rapide ne lit pas les journaux : elle n'a donc pas de chronologie à montrer.
            if (!collected) return;

            var timeline = TimelineBuilder.Build(snapshot);
            if (timeline.Entries.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var entry in timeline.Entries)
                rows.Add(new FactRow(new[]
                {
                    ValueFormat.Date(entry.Date),
                    entry.IsIncident ? "Incident" : "Changement",
                    DescribeTimeline(entry.Kind),
                    entry.Subject,
                }, entry.IsIncident ? FactTone.Warning : FactTone.Neutral));

            Add(groups, "Chronologie",
                "Ce que la machine a subi et ce qu'on lui a fait, mêlés : une coïncidence de dates " +
                "n'est pas une cause, mais c'est par là qu'on commence à chercher.",
                Array.Empty<Fact>(),
                new FactTable(
                    new[] { "Date", "Genre", "Nature", "Sujet" },
                    rows,
                    "Rien d'enregistré sur la période."));
        }

        /// <summary>
        /// Le filet de sécurité : ce qui permettrait de revenir en arrière.
        /// </summary>
        /// <remarks>
        /// L'état déclaré de la protection et son activité réelle sont affichés côte à côte,
        /// parce que c'est leur écart qui compte : « activée » et « aucun point créé depuis
        /// quatre-vingt-dix jours » sur la même ligne se voit tout de suite, alors que la
        /// première valeur seule rassurerait.
        /// </remarks>
        /// <summary>
        /// Jusqu'à quand cette installation est suivie, et ce que la machine pourra recevoir ensuite.
        /// </summary>
        /// <remarks>
        /// Les deux tiennent dans un seul cadre parce qu'en atelier elles tiennent dans une seule
        /// phrase : « votre Windows n'est plus mis à jour » n'a d'intérêt que suivi de ce qu'on
        /// peut y faire. Les séparer obligerait à lire deux écrans pour tenir une conversation.
        /// </remarks>
        private static void AddSupportAndUpgrade(ICollection<FactGroup> groups, SystemSnapshot snapshot)
        {
            var support = WindowsLifecycle.For(snapshot.Platform.Windows, snapshot.Metadata.CreatedAt);
            var assessment = UpgradeAdvisor.Assess(snapshot);

            var facts = new List<Fact>
            {
                Known("Version installée", support.VersionLabel),
                SupportEnd(support),
                new Fact("Passage à Windows 11", DescribeVerdict(assessment.Verdict), FactState.Known,
                    assessment.Note, VerdictTone(assessment.Verdict)),
            };

            foreach (var requirement in assessment.Requirements)
                facts.Add(RequirementFact(requirement));

            Add(groups, "Support et passage à Windows 11",
                "Le calendrier de fin de support est embarqué, pour répondre sans réseau : une version " +
                "qu'il ne connaît pas est signalée comme telle plutôt que devinée.",
                facts);
        }

        /// <summary>
        /// La date de fin de support, et le temps qui en sépare.
        /// </summary>
        /// <remarks>
        /// La teinte ne distingue que « terminé » de « en cours ». L'approche de l'échéance est
        /// affaire de seuil, et le seuil appartient au barème, que le technicien ajuste : le
        /// recopier ici en ferait une seconde vérité, capable de contredire le constat.
        /// </remarks>
        private static Fact SupportEnd(SupportStatus support)
        {
            if (support.State == WindowsSupportState.Unknown)
                return new Fact("Fin de support", "Non connue", FactState.Missing, support.Reason);

            var value = support.EndOfSupport.HasValue
                ? ValueFormat.Date(support.EndOfSupport.Value)
                : "Depuis plusieurs années";

            var days = support.DaysRemaining;
            var note = support.Reason;

            if (note == null && days.HasValue)
                note = days.Value >= 0
                    ? "Encore " + days.Value + " jour" + (days.Value > 1 ? "s" : "") + "."
                    : "Dépassée de " + (-days.Value) + " jour" + (days.Value < -1 ? "s" : "") + ".";

            return new Fact("Fin de support", value, FactState.Known, note,
                support.State == WindowsSupportState.Ended ? FactTone.Bad : FactTone.Good);
        }

        private static Fact RequirementFact(UpgradeRequirement requirement)
        {
            if (requirement.Outcome == RequirementOutcome.Unknown)
                return new Fact(requirement.Label, "Non mesuré", FactState.Missing, requirement.Observation);

            return new Fact(requirement.Label, requirement.Observation, FactState.Known,
                requirement.Note, RequirementTone(requirement.Outcome));
        }

        private static FactTone RequirementTone(RequirementOutcome outcome)
        {
            switch (outcome)
            {
                case RequirementOutcome.Met: return FactTone.Good;
                case RequirementOutcome.Fixable: return FactTone.Warning;
                case RequirementOutcome.NotMet: return FactTone.Bad;
                default: return FactTone.Neutral;
            }
        }

        private static string DescribeVerdict(UpgradeVerdict verdict)
        {
            switch (verdict)
            {
                case UpgradeVerdict.NotApplicable: return "Sans objet";
                case UpgradeVerdict.AlreadyThere: return "Déjà installé";
                case UpgradeVerdict.Eligible: return "Possible";
                case UpgradeVerdict.EligibleAfterSetting: return "Possible après réglage du micrologiciel";
                case UpgradeVerdict.Ineligible: return "Impossible sans changer de matériel";
                case UpgradeVerdict.Undetermined: return "Indéterminé : une exigence n'a pas pu être mesurée";
                default: return "Indéterminé";
            }
        }

        private static FactTone VerdictTone(UpgradeVerdict verdict)
        {
            switch (verdict)
            {
                case UpgradeVerdict.AlreadyThere:
                case UpgradeVerdict.Eligible: return FactTone.Good;
                case UpgradeVerdict.EligibleAfterSetting: return FactTone.Warning;
                case UpgradeVerdict.Ineligible: return FactTone.Bad;
                default: return FactTone.Neutral;
            }
        }

        /// <summary>
        /// L'heure, et la seule référence qui permette de la juger sans réseau.
        /// </summary>
        /// <remarks>
        /// La date des fichiers du noyau figure à côté de l'heure de la machine, systématiquement.
        /// Seule, une heure n'apprend rien : elle a toujours l'air juste. C'est sa comparaison
        /// avec une date venue de l'extérieur qui la met en cause ou la met hors de cause.
        /// </remarks>
        private static void AddClock(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var time = windows.Time;
            if (!time.SystemTime.HasValue) return;

            var behind = time.SystemTime.HasValue && time.NewestSystemFile.HasValue &&
                         time.NewestSystemFile.Value > time.SystemTime.Value;

            Add(groups, "Heure et synchronisation",
                "Une horloge fausse fait échouer toutes les connexions sécurisées à la fois, sans " +
                "jamais se désigner. La date des fichiers de Windows vient de chez Microsoft : c'est " +
                "la seule référence qui ne dépende pas de cette machine.",
                new[]
                {
                    Of("Heure de la machine", time.SystemTime, ValueFormat.DateTime,
                        _ => behind ? FactTone.Bad : FactTone.Good),
                    Of("Fichiers du noyau datés du", time.NewestSystemFile, ValueFormat.Date),
                    Str("Fuseau horaire", time.TimeZone),
                    Of("Décalage sur l'heure universelle", time.UtcOffset,
                        v => (v < TimeSpan.Zero ? "−" : "+") +
                             v.Duration().Hours.ToString("00", CultureInfo.InvariantCulture) + " h " +
                             v.Duration().Minutes.ToString("00", CultureInfo.InvariantCulture)),
                    Flag("Passage à l'heure d'été", time.DaylightAdjustment,
                        "Automatique", "Désactivé", FactTone.Good, FactTone.Warning),
                    Flag("Heure d'été en cours", time.InDaylightSaving, "Oui", "Non",
                        FactTone.Neutral, FactTone.Neutral),
                    Of("Dernière remise à l'heure", time.Sync.LastSynchronised, ValueFormat.DateTime),
                    Str("Serveur de temps", time.Sync.Server),
                    Of("Source", time.Sync.Source, DescribeTimeSource),
                    Flag("Service de temps", time.Sync.ServiceDisabled,
                        "Désactivé", "Actif au besoin", FactTone.Warning, FactTone.Good),
                });
        }

        private static string DescribeTimeSource(TimeSource source) => source switch
        {
            TimeSource.Ntp => "Serveur de temps",
            TimeSource.Domain => "Hiérarchie du domaine",
            TimeSource.None => "Aucune synchronisation",
            _ => "Inconnue",
        };

        /// <summary>
        /// Les comptes qui ont ouvert une session ici, et l'état de leurs profils.
        /// </summary>
        /// <remarks>
        /// La table donne le chemin réel de chaque profil, et pas seulement le nom du compte :
        /// c'est le chemin qui distingue un profil sain d'un profil provisoire, et c'est lui
        /// qu'on va chercher pour récupérer ce qu'un utilisateur croit avoir perdu.
        /// </remarks>
        private static void AddProfiles(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var inventory = windows.Profiles;
            if (inventory.Profiles.Count == 0) return;

            int normal = 0, setAside = 0, orphaned = 0, temporary = 0;
            var rows = new List<FactRow>();

            foreach (var profile in inventory.Profiles)
            {
                switch (profile.State)
                {
                    case ProfileState.SetAside: setAside++; break;
                    case ProfileState.Orphaned: orphaned++; break;
                    case ProfileState.Temporary: temporary++; break;
                    default: normal++; break;
                }

                rows.Add(new FactRow(new[]
                {
                    profile.AccountName.Or("compte inconnu"),
                    profile.Path.Length == 0 ? "-" : profile.Path,
                    DescribeProfile(profile.State),
                    profile.IsCurrent ? "Session en cours" : profile.Loaded ? "Session ouverte" : "Fermée",
                    profile.LastUsed.HasValue ? ValueFormat.Date(profile.LastUsed.Value) : "-",
                }, ProfileTone(profile.State)));
            }

            var facts = new List<Fact>
            {
                Known("Profils déclarés", ValueFormat.Number(inventory.Profiles.Count)),
                Of("Sessions ouvertes", inventory.LoadedCount, v => ValueFormat.Number(v),
                    v => v > 1 ? FactTone.Warning : FactTone.Neutral),
                Known("Profils normaux", ValueFormat.Number(normal),
                    normal > 0 ? FactTone.Good : FactTone.Neutral),
            };

            // Les trois états anormaux ne s'affichent que lorsqu'ils existent : une ligne
            // « profils provisoires : 0 » sur les machines saines apprendrait à ne plus la lire,
            // et c'est exactement celle qu'il ne faut pas manquer le jour où elle change.
            if (temporary > 0)
                facts.Add(Known("Profils provisoires", ValueFormat.Number(temporary), FactTone.Bad));
            if (setAside > 0)
                facts.Add(Known("Profils mis de côté", ValueFormat.Number(setAside), FactTone.Warning));
            if (orphaned > 0)
                facts.Add(Known("Profils sans dossier", ValueFormat.Number(orphaned), FactTone.Neutral));

            Add(groups, "Comptes et profils",
                "Une session verrouillée reste une session ouverte, et un profil mis de côté laisse " +
                "son dossier intact à côté du nouveau. Ces deux faits expliquent la plupart des " +
                "« j'ai tout perdu » et des « la machine rame sans raison ».",
                facts,
                new FactTable(
                    new[] { "Compte", "Dossier du profil", "État", "Session", "Dernière utilisation" },
                    rows,
                    "Aucun profil d'utilisateur n'a été relevé."));
        }

        private static string DescribeProfile(ProfileState state) => state switch
        {
            ProfileState.Normal => "Normal",
            ProfileState.SetAside => "Mis de côté par Windows",
            ProfileState.Temporary => "Provisoire : effacé à la fermeture",
            ProfileState.Orphaned => "Dossier absent",
            _ => "État inconnu",
        };

        private static FactTone ProfileTone(ProfileState state) => state switch
        {
            ProfileState.Temporary => FactTone.Bad,
            ProfileState.SetAside => FactTone.Warning,
            ProfileState.Orphaned => FactTone.Neutral,
            _ => FactTone.Neutral,
        };

        private static void AddSafetyNet(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var net = windows.SafetyNet;
            var restore = net.Restore;

            var facts = new List<Fact>
            {
                Flag("Protection du système", restore.Enabled, "Activée", "Désactivée",
                    FactTone.Good, FactTone.Warning),
                Of("Dernier point de restauration créé", restore.LastPointCreated, ValueFormat.Date),
                Of("Points créés sur la période", restore.PointsCreated, v => ValueFormat.Number(v),
                    v => v > 0 ? FactTone.Good : FactTone.Warning),
                Of("Créations en échec", restore.Failures, v => ValueFormat.Number(v),
                    v => v > 0 ? FactTone.Bad : FactTone.Good),
                Of("Part du disque réservée", restore.ReservedPercent, v => v + " %"),
                Of("Environnement de récupération", net.Recovery.State, DescribeRecovery, RecoveryTone),
                Flag("Réparation automatique au démarrage", net.Recovery.AutomaticRepair,
                    "Armée", "Désarmée", FactTone.Good, FactTone.Warning),
            };

            facts.Add(net.Backups.Count == 0
                ? Known("Moyens de sauvegarde repérés", "Aucun", FactTone.Warning)
                : List("Moyens de sauvegarde repérés", Labels(net.Backups), "Aucun"));

            var rows = new List<FactRow>();
            foreach (var folder in net.Folders)
                rows.Add(new FactRow(new[]
                {
                    DescribeFolder(folder.Kind),
                    folder.Path,
                    folder.OnSystemVolume ? "Disque système" : "Autre disque",
                    folder.Exists
                        ? folder.InCloud ? "Synchronisé dans le nuage" : "Accessible"
                        : "Introuvable",
                }, folder.Exists ? FactTone.Neutral : FactTone.Bad));

            Add(groups, "Filet de sécurité",
                "Ce qui permettrait de revenir en arrière, à lire avant d'intervenir. " +
                "La liste des points de restauration encore présents demande les privilèges administrateur : " +
                "elle n'est pas relevée.",
                facts,
                new FactTable(
                    new[] { "Dossier", "Emplacement réel", "Disque", "État" },
                    rows,
                    "L'emplacement des dossiers personnels n'a pas pu être lu."));
        }

        /// <summary>
        /// L'impression, du service jusqu'à l'appareil.
        /// </summary>
        /// <remarks>
        /// Le spouleur figure en premier parce qu'il commande tout le reste : une imprimante
        /// « prête » derrière un service arrêté n'imprimera rien, et lire les deux dans cet ordre
        /// évite de chercher la panne du mauvais côté.
        /// </remarks>
        private static void AddPrinting(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var printing = windows.Printing;

            var rows = new List<FactRow>();
            foreach (var printer in printing.Printers)
                rows.Add(new FactRow(new[]
                {
                    printer.Name,
                    printer.IsDefault ? "Par défaut" : "-",
                    DescribePrinter(printer),
                    printer.IsNetwork ? "Réseau" : "Locale",
                    printer.Driver ?? "-",
                    printer.Port ?? "-",
                }, printer.Availability == PrinterAvailability.Ready ? FactTone.Neutral : FactTone.Warning));

            Add(groups, "Impression", null, new[]
            {
                Str("Service d'impression", printing.SpoolerState),
                Of("Imprimantes installées", Measured.Ok(printing.Printers.Count, DataSource.Wmi),
                    v => ValueFormat.Number(v)),
                Of("Documents en attente", printing.PendingJobs, v => ValueFormat.Number(v),
                    v => v > 0 ? FactTone.Warning : FactTone.Good),

                // Le dossier de spoule est fermé à l'utilisateur courant : la ligne dit qu'elle
                // n'a pas été lue plutôt que d'afficher zéro.
                Of("Fichiers bloqués dans le spouleur", printing.SpoolFiles, v => ValueFormat.Number(v),
                    v => v > 0 ? FactTone.Warning : FactTone.Good),
                Of("Erreurs d'impression enregistrées", printing.RecentErrors,
                    v => ValueFormat.Number(v) + " sur " + printing.ErrorWindowDays.Or(30) + " jours",
                    v => v > 0 ? FactTone.Warning : FactTone.Good),
            }, new FactTable(
                new[] { "Imprimante", "Rôle", "État", "Raccordement", "Pilote", "Port" },
                rows,
                "Aucune imprimante installée sur cette machine."));
        }

        private static string DescribePrinter(PrinterInfo printer)
        {
            var state = printer.Availability switch
            {
                PrinterAvailability.Ready => "Prête",
                PrinterAvailability.Offline => "Hors connexion",
                PrinterAvailability.Error => "Bourrage, capot ouvert ou bac plein",
                PrinterAvailability.PaperOut => "Plus de papier",
                PrinterAvailability.InkOut => "Plus d'encre",
                PrinterAvailability.Paused => "Impression en pause",
                _ => "État indéterminé",
            };

            // « Hors connexion » subi et « hors connexion » demandé se règlent différemment : le
            // second est une case cochée, et la colonne le dit plutôt que de les confondre.
            return printer.OfflineByChoice && printer.Availability == PrinterAvailability.Offline
                ? state + " (demandé)"
                : state;
        }

        /// <summary>
        /// Le son : ce qui peut jouer, ce qui peut enregistrer.
        /// </summary>
        /// <remarks>
        /// Ce cadre dit aussi ce qu'il ne dit pas. Le volume et la sortie choisie par défaut ne
        /// se lisent pas sans deviner : les taire sans le signaler laisserait croire que « tout
        /// est vert » alors que la moitié des causes de silence n'a pas été regardée.
        /// </remarks>
        private static void AddAudio(ICollection<FactGroup> groups, WindowsSnapshot windows)
        {
            var audio = windows.Audio;

            var rows = new List<FactRow>();
            foreach (var endpoint in audio.Endpoints)
                rows.Add(new FactRow(new[]
                {
                    endpoint.Name,
                    endpoint.Direction == AudioDirection.Output ? "Sortie" : "Entrée",
                    DescribeEndpoint(endpoint.State),
                    // Seul un périphérique désactivé est signalé : une prise où rien n'est
                    // branché est l'état normal d'une prise inutilisée, et la teinter ferait
                    // paraître en défaut une machine dont le son fonctionne très bien.
                }, endpoint.State == AudioEndpointState.Disabled ? FactTone.Warning : FactTone.Neutral));

            Add(groups, "Son",
                "Le volume et la sortie choisie par défaut ne sont pas relevés : Windows ne les range " +
                "nulle part où on puisse les lire sans deviner.", new[]
                {
                    Flag("Service audio", audio.ServiceRunning, "Démarré", "Arrêté",
                        FactTone.Good, FactTone.Bad),
                    Flag("Construction des périphériques", audio.EndpointBuilderRunning, "Démarrée", "Arrêtée",
                        FactTone.Good, FactTone.Bad),
                    Of("Sorties actives", audio.ActiveOutputs, v => ValueFormat.Number(v),
                        v => v > 0 ? FactTone.Good : FactTone.Bad),
                    Of("Entrées actives", audio.ActiveInputs, v => ValueFormat.Number(v)),

                    // Comptés et non listés : Windows garde une entrée par appareil branché un
                    // jour, et les afficher noierait les quelques périphériques réels.
                    Of("Périphériques oubliés", audio.ForgottenEndpoints,
                        v => ValueFormat.Number(v) + " entrée(s) d'appareils absents"),
                }, new FactTable(
                    new[] { "Périphérique", "Sens", "État" },
                    rows,
                    "Aucun périphérique audio présent."));
        }

        private static string DescribeEndpoint(AudioEndpointState state) => state switch
        {
            AudioEndpointState.Active => "Disponible",
            AudioEndpointState.Disabled => "Désactivé dans Windows",
            AudioEndpointState.Unplugged => "Rien n'est branché",
            AudioEndpointState.NotPresent => "Matériel absent",
            _ => "État indéterminé",
        };

        private static IReadOnlyList<string> Labels(IReadOnlyList<BackupTool> backups)
        {
            var labels = new List<string>(backups.Count);
            foreach (var backup in backups) labels.Add(backup.Label);
            return labels;
        }

        private static string DescribeRecovery(RecoveryEnvironmentState state) => state switch
        {
            RecoveryEnvironmentState.Installed => "Installé et actif",
            RecoveryEnvironmentState.Disabled => "Présent mais désactivé",
            RecoveryEnvironmentState.Missing => "Absent de cette machine",
            _ => "Indéterminé",
        };

        private static FactTone RecoveryTone(RecoveryEnvironmentState state)
            => state == RecoveryEnvironmentState.Installed ? FactTone.Good : FactTone.Warning;

        private static string DescribeFolder(PersonalFolderKind kind) => kind switch
        {
            PersonalFolderKind.Desktop => "Bureau",
            PersonalFolderKind.Documents => "Documents",
            PersonalFolderKind.Pictures => "Images",
            PersonalFolderKind.Music => "Musique",
            PersonalFolderKind.Videos => "Vidéos",
            PersonalFolderKind.Downloads => "Téléchargements",
            _ => "Dossier personnel",
        };

        private static string DescribeIncident(IncidentKind kind) => kind switch
        {
            IncidentKind.ProgramCrash => "Fermeture brutale",
            IncidentKind.ProgramHang => "Ne répond plus",
            IncidentKind.ServiceCrash => "Service arrêté seul",
            IncidentKind.BlueScreen => "Écran bleu",
            IncidentKind.HardwareError => "Erreur matérielle",
            _ => "-",
        };

        private static string DescribeTimeline(TimelineKind kind) => kind switch
        {
            TimelineKind.ProgramCrash => "Fermeture brutale",
            TimelineKind.ProgramHang => "Ne répond plus",
            TimelineKind.ServiceCrash => "Service arrêté seul",
            TimelineKind.BlueScreen => "Écran bleu",
            TimelineKind.HardwareError => "Erreur matérielle",
            TimelineKind.WindowsInstalled => "Installation de Windows",
            TimelineKind.UpdateInstalled => "Correctif Windows",
            TimelineKind.SoftwareInstalled => "Logiciel installé ou mis à jour",
            _ => "-",
        };

        private static void AppendEvents(ICollection<FactRow> rows, IReadOnlyList<EventSummary> source, FactTone tone)
        {
            foreach (var summary in source)
                rows.Add(new FactRow(new[]
                {
                    summary.LogName,
                    summary.Source,
                    summary.EventId.ToString(CultureInfo.CurrentCulture),
                    ValueFormat.Number(summary.Count),
                    ValueFormat.DateTime(summary.LastSeen),
                    Shorten(summary.SampleMessage),
                }, tone));
        }

        // ================================================================= Réseau

        private static IReadOnlyList<FactGroup> BuildNetwork(NetworkSnapshot network)
        {
            var groups = new List<FactGroup>();

            foreach (var adapter in network.Adapters)
            {
                var facts = new List<Fact>
                {
                    Known("Type", DescribeAdapter(adapter.Kind)),
                    Str("Statut", adapter.Status),
                    Str("Adresse MAC", adapter.MacAddress),
                    Of("Débit du lien", adapter.LinkSpeedBps, ValueFormat.LinkSpeed),
                    Flag("DHCP", adapter.DhcpEnabled, "Activé", "Adressage manuel", FactTone.Neutral, FactTone.Neutral),
                    Str("Serveur DHCP", adapter.DhcpServer),
                    List("Adresses IPv4", adapter.IPv4Addresses, "Aucune adresse IPv4"),
                    List("Adresses IPv6", adapter.IPv6Addresses, "Aucune adresse IPv6"),
                    List("Passerelle", adapter.Gateways, "Aucune passerelle"),
                    List("Serveurs DNS", adapter.DnsServers, "Aucun serveur DNS"),
                    Flag("Auto-configuration 169.254", adapter.HasApipaAddress,
                        "Oui : aucun bail DHCP obtenu", "Non", FactTone.Bad, FactTone.Good),
                };

                AddCounters(facts, adapter.Counters);

                if (adapter.Wifi != null)
                {
                    var wifi = adapter.Wifi;
                    facts.Add(Str("État de la liaison", wifi.State));
                    facts.Add(Flag("Émetteur", wifi.RadioEnabled,
                        "Allumé", "Éteint", FactTone.Good, FactTone.Bad));
                    facts.Add(Str("Réseau sans fil", wifi.Ssid));
                    facts.Add(Str("Point d'accès", wifi.Bssid));
                    facts.Add(Str("Sécurité", wifi.Security));
                    facts.Add(Str("Norme", wifi.RadioType));
                    facts.Add(Of("Bande", wifi.Band, WifiChannels.Describe));
                    facts.Add(Of("Canal", wifi.Channel, v => v.ToString(CultureInfo.CurrentCulture)));
                    facts.Add(Of("Qualité du signal", wifi.SignalPercent, v => v + " %",
                        v => v < 40 ? FactTone.Bad : v < 60 ? FactTone.Warning : FactTone.Good));

                    // Les repères du métier se lisent en dBm, pas en pourcentage : −67 dBm est la
                    // limite basse pour de la vidéo, −75 celle où la liaison devient instable.
                    facts.Add(Of("Puissance reçue", wifi.RssiDbm, v => Dbm(v),
                        v => v <= -75 ? FactTone.Bad : v <= -67 ? FactTone.Warning : FactTone.Good));
                    facts.Add(Of("Débit d'émission", wifi.TxRateMbps, v => v + " Mbit/s"));
                    facts.Add(Of("Débit de réception", wifi.RxRateMbps, v => v + " Mbit/s"));

                    AddNeighbourhood(facts, wifi.Neighbourhood);
                }

                Add(groups, adapter.Name, adapter.IsPrimary
                    ? "Interface portant la route par défaut : " + adapter.Description
                    : adapter.Description, facts);
            }

            AddEnvironment(groups, network);
            AddListening(groups, network.Listening);
            AddPaths(groups, network.Paths);
            AddPings(groups, network.Tests);
            AddQuality(groups, network.Tests);
            AddDns(groups, network.Tests);
            AddHttp(groups, network.Tests);
            AddRoute(groups, network.Tests);

            return groups;
        }

        /// <summary>
        /// Les compteurs de la liaison, quand elle en a.
        /// </summary>
        /// <remarks>
        /// Seul le taux est teinté. Les compteurs bruts sont donnés parce qu'un technicien veut
        /// voir l'ordre de grandeur (cent erreurs et cent millions de trames ne se lisent pas
        /// comme cent erreurs et mille trames) mais c'est le rapport des deux qui décide, et
        /// c'est lui qui porte la couleur.
        /// </remarks>
        private static void AddCounters(ICollection<Fact> facts, LinkCounters counters)
        {
            if (!counters.HasValue) return;

            facts.Add(Of("Trames reçues", counters.PacketsReceived, v => ValueFormat.Number(v)));
            facts.Add(Of("Trames émises", counters.PacketsSent, v => ValueFormat.Number(v)));
            facts.Add(Of("Volume reçu", counters.BytesReceived, ValueFormat.Bytes));
            facts.Add(Of("Volume émis", counters.BytesSent, ValueFormat.Bytes));

            facts.Add(Of("Trames en erreur",
                Sum(counters.ErrorsReceived, counters.ErrorsSent), v => ValueFormat.Number(v)));
            facts.Add(Of("Trames écartées",
                Sum(counters.DiscardsReceived, counters.DiscardsSent), v => ValueFormat.Number(v)));

            // Les deux bornes sont celles du barème d'origine, un millième et un centième. La
            // fiche ne lit pas le barème ajusté : elle n'y a pas accès, et un écran qui teinterait
            // en orange une valeur sur laquelle aucun constat ne se déclenche se contredirait tout
            // seul. Les garder alignées sur les valeurs par défaut est le moindre écart possible.
            facts.Add(Of("Taux d'erreur", counters.ErrorsPerMillion,
                v => v.ToString("0.#", CultureInfo.CurrentCulture) + " par million",
                v => v >= 10000 ? FactTone.Bad : v >= 1000 ? FactTone.Warning : FactTone.Good));
        }

        /// <summary>Somme de deux compteurs, absente dès que l'un des deux manque.</summary>
        private static Measured<long> Sum(Measured<long> first, Measured<long> second)
            => first.HasValue && second.HasValue
                ? Measured.Ok(first.Value + second.Value, first.Source)
                : Measured.Missing<long>(first.Reason ?? second.Reason ?? "Compteur indisponible.");

        /// <summary>
        /// Ce que la machine écoute, et par quel programme.
        /// </summary>
        /// <remarks>
        /// La colonne « visible depuis » est celle qui compte : un service qui n'écoute que sur
        /// l'adresse de bouclage ne regarde que lui-même, quel que soit son port. Le même
        /// programme, le même numéro, et deux situations sans rapport.
        /// </remarks>
        private static void AddListening(ICollection<FactGroup> groups, ListeningPortsInfo listening)
        {
            if (listening.Ports.Count == 0 && !listening.Count.HasValue) return;

            var rows = new List<FactRow>();
            var exposed = 0;

            foreach (var port in listening.Ports)
            {
                if (port.AllInterfaces) exposed++;

                rows.Add(new FactRow(new[]
                {
                    port.Port.ToString(CultureInfo.CurrentCulture),
                    port.Service ?? "-",
                    port.ProcessName.Or("programme non identifié"),
                    port.ProcessId.ToString(CultureInfo.CurrentCulture),
                    port.AllInterfaces ? "Tout le réseau" : "Cette machine seulement",
                }, port.AllInterfaces && WellKnown.IsEntryPoint(port.Port)
                    ? FactTone.Warning
                    : FactTone.Neutral));
            }

            Add(groups, "Ports en écoute",
                "Ce qui attend des connexions sur cette machine. Un service ouvert sur tout le réseau " +
                "est une porte d'entrée ; le même service sur cette machine seulement n'en est pas une.",
                new[]
                {
                    Of("Ports en écoute", listening.Count, v => ValueFormat.Number(v)),
                    Known("Ouverts sur le réseau", ValueFormat.Number(exposed),
                        exposed > 0 ? FactTone.Neutral : FactTone.Good),
                    Of("Connexions établies", listening.Established, v => ValueFormat.Number(v)),
                },
                new FactTable(
                    new[] { "Port", "Usage habituel", "Programme", "Identifiant", "Visible depuis" },
                    rows,
                    "Aucun port en écoute n'a été relevé."));
        }

        /// <summary>
        /// Ce qui décide du chemin d'un paquet, et du sens d'un nom.
        /// </summary>
        /// <remarks>
        /// Quatre cadres, parce qu'aucun des quatre ne se lit pour la même raison. La table de
        /// routage se lit quand un serveur ne répond pas ; le fichier hosts quand un seul site ne
        /// s'ouvre pas ; le catalogue Winsock quand plus rien ne sort alors que tout est correct ;
        /// les serveurs de noms quand on veut savoir qui décide de ce que « google.fr » veut dire
        /// ici. Les mêler obligerait à lire les quatre pour en trouver un.
        /// </remarks>
        private static void AddPaths(ICollection<FactGroup> groups, NetworkPathsInfo paths)
        {
            AddRouting(groups, paths.Routing);
            AddHosts(groups, paths.Hosts);
            AddWinsock(groups, paths.Winsock);
            AddNameServers(groups, paths);
        }

        private static void AddRouting(ICollection<FactGroup> groups, RoutingTable routing)
        {
            if (routing.Routes.Count == 0 && !routing.Count.HasValue) return;

            var rows = new List<FactRow>();

            foreach (var route in routing.Routes)
            {
                rows.Add(new FactRow(new[]
                {
                    route.Prefix,
                    route.NextHop == "0.0.0.0" ? "directement branché" : route.NextHop,
                    route.InterfaceName ?? "interface " + route.InterfaceIndex.ToString(CultureInfo.CurrentCulture),
                    route.Metric.ToString(CultureInfo.CurrentCulture),
                    DescribeOrigin(route.Origin),
                },
                    route.Origin == RouteOrigin.Manual ? FactTone.Warning
                    : route.IsDefault ? FactTone.Good
                    : FactTone.Neutral));
            }

            Add(groups, "Table de routage",
                "Où part réellement le trafic. Une carte peut porter la bonne adresse et la bonne " +
                "passerelle, et voir son trafic emporté par une route qu'un tunnel a laissée derrière lui.",
                new[]
                {
                    Of("Routes", routing.Count, v => ValueFormat.Number(v)),
                    Known("Chemins de sortie", ValueFormat.Number(routing.DefaultRoutes.Count),
                        routing.DefaultRoutes.Count == 1 ? FactTone.Good : FactTone.Warning),
                    Known("Posées à la main", ValueFormat.Number(routing.Manual.Count),
                        routing.Manual.Count == 0 ? FactTone.Good : FactTone.Warning),
                },
                new FactTable(
                    new[] { "Destination", "Par", "Interface", "Priorité", "Origine" },
                    rows,
                    "La table de routage n'a pas pu être lue."));
        }

        private static string DescribeOrigin(RouteOrigin origin) => origin switch
        {
            RouteOrigin.System => "posée par Windows",
            RouteOrigin.Configured => "venue de la configuration",
            RouteOrigin.Manual => "ajoutée à la main",
            RouteOrigin.Redirect => "imposée par un routeur",
            _ => "origine indéterminée",
        };

        /// <summary>
        /// Le fichier hosts, ligne à ligne et telles qu'elles sont écrites.
        /// </summary>
        /// <remarks>
        /// Le cadre s'affiche même quand le fichier ne contient aucune redirection : « aucune » est
        /// ici une information, parce qu'elle écarte d'un coup la cause la plus discrète des « ce
        /// site-là ne s'ouvre pas ».
        /// </remarks>
        private static void AddHosts(ICollection<FactGroup> groups, HostsFile hosts)
        {
            if (!hosts.ActiveCount.HasValue && hosts.Entries.Count == 0) return;

            var rows = new List<FactRow>();
            var consequential = 0;

            foreach (var entry in hosts.Entries)
            {
                var serious = false;
                foreach (var name in entry.Names) if (HostsFile.IsConsequential(name)) serious = true;
                if (serious) consequential++;

                rows.Add(new FactRow(new[]
                {
                    entry.Line.ToString(CultureInfo.CurrentCulture),
                    string.Join(", ", entry.Names),
                    entry.Address,
                    DescribeTarget(entry.Target),
                },
                    serious ? FactTone.Bad
                    : entry.Target == HostsTarget.Redirected ? FactTone.Warning
                    : FactTone.Neutral));
            }

            Add(groups, "Fichier hosts",
                "Ce fichier l'emporte sur tous les serveurs de noms : ni la box, ni le fournisseur " +
                "d'accès, ni le navigateur ne peuvent le contredire, et rien ne le signale à l'écran.",
                new[]
                {
                    Of("Lignes actives", hosts.ActiveCount, v => ValueFormat.Number(v)),
                    Known("Domaines de mise à jour visés", ValueFormat.Number(consequential),
                        consequential == 0 ? FactTone.Good : FactTone.Bad),
                },
                new FactTable(
                    new[] { "Ligne", "Nom", "Renvoyé vers", "Effet" },
                    rows,
                    "Aucune redirection : le fichier ne contient que ses commentaires d'origine."));
        }

        private static string DescribeTarget(HostsTarget target) => target switch
        {
            HostsTarget.Blocked => "le nom devient injoignable",
            HostsTarget.Loopback => "renvoyé vers cette machine",
            _ => "détourné vers une autre machine",
        };

        private static void AddWinsock(ICollection<FactGroup> groups, WinsockCatalog winsock)
        {
            if (winsock.Providers.Count == 0 && !winsock.Count.HasValue) return;

            var rows = new List<FactRow>();
            foreach (var provider in winsock.Providers)
                rows.Add(new FactRow(new[]
                {
                    provider.FileName,
                    provider.LibraryPath,
                    provider.FromWindows ? "livrée avec Windows" : "ajoutée par un programme",
                }, provider.FromWindows ? FactTone.Neutral : FactTone.Warning));

            Add(groups, "Pile réseau",
                "Tout ce que la machine envoie et reçoit traverse cette liste de bibliothèques. Une " +
                "entrée restée là après une désinstallation arrête le réseau alors que tout le reste " +
                "paraît correct.",
                new[]
                {
                    Of("Entrées au catalogue", winsock.Count, v => ValueFormat.Number(v)),
                    Known("Bibliothèques étrangères", ValueFormat.Number(winsock.Foreign.Count),
                        winsock.Foreign.Count == 0 ? FactTone.Good : FactTone.Warning),
                },
                new FactTable(
                    new[] { "Bibliothèque", "Emplacement", "Provenance" },
                    rows,
                    "Le catalogue n'a pas pu être lu."));
        }

        /// <summary>
        /// Qui décide du sens d'un nom, et l'état du partage.
        /// </summary>
        /// <remarks>
        /// La nature du serveur compte plus que son adresse : reconnaître un résolveur public
        /// répandu évite de signaler comme suspect le premier geste de tout technicien devant une
        /// box dont le résolveur rame.
        /// </remarks>
        private static void AddNameServers(ICollection<FactGroup> groups, NetworkPathsInfo paths)
        {
            if (paths.DnsServers.Count == 0 && !paths.Sharing.Smb1Installed.HasValue) return;

            var rows = new List<FactRow>();
            var unknown = 0;

            foreach (var server in paths.DnsServers)
            {
                if (server.Nature == DnsServerNature.Unknown) unknown++;

                rows.Add(new FactRow(new[]
                {
                    server.Address,
                    DescribeNature(server.Nature),
                    server.Operator ?? "-",
                    server.InterfaceName ?? "-",
                }, server.Nature == DnsServerNature.Unknown ? FactTone.Warning : FactTone.Neutral));
            }

            var facts = new List<Fact>
            {
                Known("Serveurs de noms", ValueFormat.Number(paths.DnsServers.Count)),
                Known("Non identifiés", ValueFormat.Number(unknown),
                    unknown == 0 ? FactTone.Good : FactTone.Warning),
                Of("Partage de première génération", paths.Sharing.Smb1Enabled,
                    v => v ? "actif" : "absent"),
            };

            Add(groups, "Résolution des noms",
                "Ce serveur décide de la machine à laquelle correspond chaque adresse de site : celui " +
                "qui tient ce rôle voit passer tout ce qui est consulté depuis cet ordinateur.",
                facts,
                new FactTable(
                    new[] { "Serveur", "Nature", "Opérateur", "Interface" },
                    rows,
                    "Aucun serveur de noms n'a été relevé."));
        }

        private static string DescribeNature(DnsServerNature nature) => nature switch
        {
            DnsServerNature.Gateway => "la box ou le routeur",
            DnsServerNature.LocalMachine => "cette machine",
            DnsServerNature.LocalNetwork => "une machine du même réseau",
            DnsServerNature.KnownPublic => "service public identifié",
            _ => "non identifié",
        };

        /// <summary>
        /// L'environnement : ce qui commande le réseau par-dessus ce qui est branché.
        /// </summary>
        /// <remarks>
        /// Trois cadres séparés plutôt qu'un seul. Le réseau reconnu par Windows, le chemin de
        /// sortie du trafic web et l'appartenance au domaine répondent à trois questions
        /// différentes, posées par trois personnes différentes, et les mêler obligerait à lire
        /// les trois pour en trouver une.
        /// </remarks>
        private static void AddEnvironment(ICollection<FactGroup> groups, NetworkSnapshot network)
        {
            var environment = network.Environment;

            // --- ce que Windows pense du réseau
            var rows = new List<FactRow>();
            foreach (var location in environment.Locations)
                rows.Add(new FactRow(new[]
                {
                    location.Name,
                    DescribeCategory(location.Category),
                    DescribeReach(location.Reach),
                    location.Managed ? "Réseau de domaine" : "Hors domaine",
                }, location.Reach == NetworkReach.None ? FactTone.Bad
                    : location.Reach == NetworkReach.LocalOnly ? FactTone.Warning
                    : FactTone.Good));

            Add(groups, "Réseaux reconnus par Windows",
                "Le classement décide du pare-feu et de la découverte ; la portée est ce que dit " +
                "l'icône à côté de l'horloge. Ni l'un ni l'autre ne se déduit d'un ping qui passe.",
                new[]
                {
                    Known("Sorties par défaut", ValueFormat.Number(network.DefaultGatewayCount),
                        network.DefaultGatewayCount > 1 ? FactTone.Warning : FactTone.Neutral),
                },
                new FactTable(
                    new[] { "Réseau", "Classement", "Portée", "Domaine" },
                    rows,
                    "Le service de localisation réseau n'a signalé aucun réseau connecté."));

            // --- par où sort le trafic web
            //
            // Le détail n'apparaît que pour un mandataire réellement configuré. Sur les neuf
            // machines sur dix qui n'en ont aucun, six lignes de « non mesuré » diraient six fois
            // la même chose que la première, et « non mesuré » est réservé à ce qu'on n'a pas su
            // lire, pas à ce qui n'existe pas.
            var proxies = new List<Fact>
            {
                Flag("Proxy de la session", environment.UserProxy.Enabled,
                    "Activé", "Aucun", FactTone.Warning, FactTone.Good),
            };

            if (environment.UserProxy.Configured)
            {
                proxies.Add(Str("Serveur", environment.UserProxy.Server));
                proxies.Add(Str("Exceptions", environment.UserProxy.Bypass));
                proxies.Add(Str("Configuration automatique", environment.UserProxy.AutoConfigUrl));
            }

            proxies.Add(Flag("Détection automatique (WPAD)", environment.UserProxy.AutoDetect,
                "Activée", "Désactivée", FactTone.Neutral, FactTone.Neutral));

            proxies.Add(Flag("Proxy de la machine", environment.MachineProxy.Enabled,
                "Activé", "Aucun", FactTone.Warning, FactTone.Good));

            if (environment.MachineProxy.Configured)
            {
                proxies.Add(Str("Serveur de la machine", environment.MachineProxy.Server));
                proxies.Add(Str("Exceptions de la machine", environment.MachineProxy.Bypass));
            }

            Add(groups, "Serveur mandataire",
                "Windows en tient deux, sans lien entre elles : les navigateurs suivent celle de la " +
                "session, les services (Windows Update en tête) celle de la machine.",
                proxies);

            // --- le domaine
            var domain = environment.Domain;
            Add(groups, "Domaine et résolution de noms",
                "Sur un poste d'entreprise, ces quatre lignes expliquent la plupart des pannes qui " +
                "n'ont pas l'air d'en être : session lente, partages introuvables, stratégies non appliquées.",
                new[]
                {
                    Flag("Appartenance", domain.Joined,
                        "Machine jointe à un domaine", "Machine hors domaine",
                        FactTone.Neutral, FactTone.Neutral),
                    Str("Domaine", domain.Name),
                    Str("Suffixe DNS principal", domain.PrimaryDnsSuffix),
                    List("Suffixes de recherche", domain.SearchSuffixes, "Aucun suffixe de recherche"),
                    Str("Serveur d'ouverture de session", domain.LogonServer),
                });
        }

        private static string DescribeCategory(NetworkCategory category) => category switch
        {
            NetworkCategory.Public => "Public",
            NetworkCategory.Private => "Privé",
            NetworkCategory.DomainAuthenticated => "Domaine",
            _ => "Inconnu",
        };

        private static string DescribeReach(NetworkReach reach) => reach switch
        {
            NetworkReach.Internet => "Internet",
            NetworkReach.LocalOnly => "Réseau local seulement",
            NetworkReach.None => "Aucune connectivité",
            _ => "Indéterminée",
        };

        /// <summary>
        /// Ce que la carte entend autour d'elle.
        /// </summary>
        /// <remarks>
        /// Les voisins ne sont pas listés nommément au-delà des trois plus forts : le relevé sert
        /// à expliquer un canal partagé, pas à dresser l'inventaire des box du palier.
        /// </remarks>
        private static void AddNeighbourhood(ICollection<Fact> facts, WifiNeighbourhood? neighbourhood)
        {
            if (neighbourhood == null) return;

            facts.Add(Of("Réseaux entendus", neighbourhood.Total,
                v => v.ToString(CultureInfo.CurrentCulture)));
            facts.Add(Of("Sur le même canal", neighbourhood.SameChannel,
                v => v.ToString(CultureInfo.CurrentCulture),
                v => v >= 6 ? FactTone.Bad : v >= 3 ? FactTone.Warning : FactTone.Good));
            facts.Add(Of("Sur un canal qui le recouvre", neighbourhood.OverlappingChannel,
                v => v.ToString(CultureInfo.CurrentCulture),
                v => v >= 6 ? FactTone.Warning : FactTone.Neutral));

            if (neighbourhood.Strongest.Count == 0) return;

            var parts = new List<string>();
            foreach (var neighbour in neighbourhood.Strongest)
            {
                if (parts.Count == 3) break;
                var name = string.IsNullOrWhiteSpace(neighbour.Ssid) ? "réseau masqué" : neighbour.Ssid;
                parts.Add(name + " (canal " + neighbour.Channel + ", " + Dbm(neighbour.RssiDbm) + ")");
            }

            facts.Add(Known("Voisins les plus forts", string.Join(", ", parts)));
        }

        /// <summary>
        /// Puissance reçue, avec le vrai signe moins : un rapport remis au client se lit comme un
        /// document, pas comme une sortie de console.
        /// </summary>
        private static string Dbm(int value)
            => (value < 0 ? "−" + (-value).ToString(CultureInfo.CurrentCulture)
                          : value.ToString(CultureInfo.CurrentCulture)) + " dBm";

        /// <summary>
        /// Qualité de la liaison : pertes, régularité, taille de paquet.
        /// </summary>
        /// <remarks>
        /// Séparé des tests de connectivité parce que ce n'est pas la même question. Ceux-ci
        /// disent si ça marche ; celui-ci dit à quel point.
        /// </remarks>
        private static void AddQuality(ICollection<FactGroup> groups, NetworkTests tests)
        {
            var quality = tests.Quality;
            var facts = new List<Fact>();

            if (quality != null)
            {
                facts.Add(Known("Cible", quality.Target));
                facts.Add(Of("Paquets envoyés", quality.Sent, v => v.ToString(CultureInfo.CurrentCulture)));
                facts.Add(Of("Paquets revenus", quality.Received, v => v.ToString(CultureInfo.CurrentCulture)));
                facts.Add(Of("Perte", quality.LossPercent, ValueFormat.Percent,
                    v => v >= 25 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good));
                facts.Add(Of("Latence moyenne", quality.AverageMs, ValueFormat.Ms,
                    v => v >= 150 ? FactTone.Bad : v >= 80 ? FactTone.Warning : FactTone.Good));
                facts.Add(Of("Latence minimale", quality.MinMs, ValueFormat.Ms));
                facts.Add(Of("Latence maximale", quality.MaxMs, ValueFormat.Ms));
                facts.Add(Of("Gigue", quality.JitterMs, ValueFormat.Ms,
                    v => v >= 30 ? FactTone.Warning : FactTone.Good));
            }

            facts.Add(Of("Taille de paquet du chemin", tests.PathMtu,
                v => v + " octets", v => v < 1500 ? FactTone.Warning : FactTone.Good));

            Add(groups, "Qualité de la liaison",
                "Mesures qu'un simple test de connexion ne donne pas : ce sont elles qui expliquent " +
                "une visioconférence qui hache sur une ligne qui « marche ».", facts);
        }

        /// <summary>
        /// Chemin jusqu'à Internet.
        /// </summary>
        /// <remarks>
        /// La première étape est la box du client ; les suivantes appartiennent à l'opérateur.
        /// C'est ce que le tableau rend visible, et c'est ce qui permet de dire où s'arrête ce
        /// sur quoi on peut agir. Une étape silencieuse est fréquente et sans signification :
        /// elle est marquée comme telle et non comme défaillante.
        /// </remarks>
        private static void AddRoute(ICollection<FactGroup> groups, NetworkTests tests)
        {
            if (tests.Route.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var hop in tests.Route)
            {
                rows.Add(new FactRow(new[]
                {
                    hop.Distance.ToString(CultureInfo.CurrentCulture),
                    hop.Silent ? "Ne répond pas" : Cell(hop.Address, v => v),
                    Cell(hop.RoundTripMs, ValueFormat.Ms),
                    hop.IsLocal ? "Réseau local" : hop.Silent ? "-" : "Opérateur",
                }, hop.IsLocal ? FactTone.Good : FactTone.Neutral));
            }

            Add(groups, "Chemin jusqu'à Internet",
                "Une étape qui ne répond pas est courante et sans conséquence : beaucoup " +
                "d'équipements sont réglés pour ignorer ces paquets.",
                Array.Empty<Fact>(),
                new FactTable(new[] { "Étape", "Adresse", "Temps", "Réseau" }, rows, "Chemin non relevé."));
        }

        private static void AddPings(ICollection<FactGroup> groups, NetworkTests tests)
        {
            var facts = new List<Fact>();
            AppendPing(facts, tests.Gateway, "Passerelle");
            AppendPing(facts, tests.Internet, "Internet");
            if (facts.Count == 0) return;

            Add(groups, "Tests de connectivité",
                "Ce sont les seules sorties réseau du logiciel ; les cibles sont listées ci-dessous.", facts);
        }

        private static void AppendPing(ICollection<Fact> facts, PingResult? ping, string label)
        {
            if (ping == null) return;

            var target = string.IsNullOrEmpty(ping.Label) ? ping.Target : ping.Label + " (" + ping.Target + ")";
            facts.Add(Known(label + " : cible", target));
            facts.Add(Flag(label + " : joignable", ping.Reachable, "Oui", "Non", FactTone.Good, FactTone.Bad));
            facts.Add(Of(label + " : latence moyenne", ping.AverageMs, ValueFormat.Ms,
                v => v >= 150 ? FactTone.Bad : v >= 80 ? FactTone.Warning : FactTone.Good));
            facts.Add(Of(label + " : perte de paquets", ping.LossPercent, ValueFormat.Percent,
                v => v >= 20 ? FactTone.Bad : v > 0 ? FactTone.Warning : FactTone.Good));
        }

        private static void AddDns(ICollection<FactGroup> groups, NetworkTests tests)
        {
            if (tests.DnsResolutions.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var resolution in tests.DnsResolutions)
            {
                var resolved = resolution.Resolved.Or(false);
                rows.Add(new FactRow(new[]
                {
                    resolution.Host,
                    resolved ? "Résolu" : "Échec",
                    resolution.Addresses.Count > 0 ? string.Join(", ", Copy(resolution.Addresses)) : "-",
                    Cell(resolution.DurationMs, v => v + " ms"),
                }, resolved ? FactTone.Good : FactTone.Bad));
            }

            Add(groups, "Résolution de noms", null, Array.Empty<Fact>(),
                new FactTable(new[] { "Nom", "Résultat", "Adresses", "Durée" }, rows, "Aucune résolution testée."));
        }

        private static void AddHttp(ICollection<FactGroup> groups, NetworkTests tests)
        {
            if (tests.HttpChecks.Count == 0) return;

            var rows = new List<FactRow>();
            foreach (var check in tests.HttpChecks)
            {
                var ok = check.Succeeded.Or(false);
                rows.Add(new FactRow(new[]
                {
                    check.Url,
                    ok ? "Accessible" : "Inaccessible",
                    Cell(check.StatusCode, v => v.ToString(CultureInfo.CurrentCulture)),
                    Cell(check.DurationMs, v => v + " ms"),
                }, ok ? FactTone.Good : FactTone.Bad));
            }

            Add(groups, "Accès web", null, Array.Empty<Fact>(),
                new FactTable(new[] { "Adresse", "Résultat", "Code", "Durée" }, rows, "Aucun accès testé."));
        }

        /// <summary>
        /// Températures relevables sans pilote, et ce qui reste hors de portée.
        /// </summary>
        /// <remarks>
        /// Le groupe est produit même sans aucune zone lisible, contrairement à la règle générale
        /// qui écarte les cadres vides. C'est délibéré : « pourquoi n'y a-t-il pas la température
        /// du processeur, alors que d'autres logiciels l'affichent ? » est une question qu'un
        /// technicien se pose devant un écran muet, et à laquelle un cadre absent ne répond pas.
        /// </remarks>
        /// <summary>
        /// Les ports série, et les numéros que Windows garde en réserve.
        /// </summary>
        /// <remarks>
        /// Un domaine que le grand public a oublié et qu'un atelier croise toutes les semaines :
        /// automate, caisse, balance, terminal de paiement, appareil de mesure, carte à
        /// programmer. Le cadre n'apparaît que lorsqu'il y a quelque chose à montrer, un port
        /// présent, ou des numéros retenus.
        /// </remarks>
        private static void AddSerialPorts(ICollection<FactGroup> groups, SystemSnapshot snapshot)
        {
            var ports = SerialPorts.Build(snapshot);
            var reserved = snapshot.Windows.SerialPorts;

            if (ports.Count == 0 && !reserved.ReservedCount.HasValue) return;

            var rows = new List<FactRow>();
            foreach (var port in ports)
                rows.Add(new FactRow(new[]
                {
                    port.PortName ?? "-",
                    port.Name,
                    DescribeSerialKind(port.Kind),
                    port.Manufacturer ?? "-",
                    port.ProblemCode == 0
                        ? port.PortName == null ? "Aucun numéro attribué" : "Fonctionne"
                        : port.ProblemLabel ?? "Code " + port.ProblemCode,
                }, port.Usable ? FactTone.Good : FactTone.Warning));

            var orphaned = SerialPorts.Orphaned(snapshot);

            var facts = new List<Fact>
            {
                Known("Ports présents", ValueFormat.Number(ports.Count)),
                Of("Numéros retenus par Windows", reserved.ReservedCount, v => ValueFormat.Number(v)),
                Known("Retenus sans appareil", ValueFormat.Number(orphaned.Count),
                    orphaned.Count >= 4 ? FactTone.Warning : FactTone.Neutral),
            };

            Add(groups, "Ports série",
                "Windows ne rend jamais un numéro de port qu'il a attribué : chaque convertisseur " +
                "rebranché sur une autre prise prend le suivant. Beaucoup de logiciels industriels " +
                "n'acceptent que COM1 à COM9.",
                facts,
                new FactTable(
                    new[] { "Port", "Appareil", "Nature", "Fabricant", "État" },
                    rows,
                    "Aucun port série n'est présent sur cette machine."));
        }

        private static string DescribeSerialKind(SerialPortKind kind) => kind switch
        {
            SerialPortKind.Native => "Port de la carte mère",
            SerialPortKind.UsbAdapter => "Convertisseur USB",
            SerialPortKind.Bluetooth => "Bluetooth",
            SerialPortKind.Virtual => "Créé par un logiciel",
            _ => "Nature indéterminée",
        };

        /// <summary>
        /// Les écrans branchés, et l'écart entre ce qu'ils valent et ce qu'on leur demande.
        /// </summary>
        /// <remarks>
        /// La définition d'origine figure à côté de celle qui est affichée, systématiquement,
        /// même quand les deux concordent : c'est la comparaison qui informe, et une valeur seule
        /// n'apprend rien à qui ne connaît pas la dalle.
        /// </remarks>
        private static void AddDisplays(ICollection<FactGroup> groups, DisplaySnapshot displays)
        {
            if (displays.Monitors.Count == 0) return;

            var rows = new List<FactRow>();

            foreach (var monitor in displays.Monitors)
            {
                var tone = monitor.Current.HasValue && monitor.Native.HasValue
                    ? monitor.AtNativeResolution ? FactTone.Good : FactTone.Warning
                    : FactTone.Neutral;

                rows.Add(new FactRow(new[]
                {
                    monitor.Model.Or(monitor.Output) + (monitor.IsPrimary ? " (principal)" : string.Empty),
                    monitor.Manufacturer.Or("-"),
                    Cell(monitor.Current, v => v.ToString()),
                    monitor.Native.HasValue
                        ? monitor.Native.Value.Width + " × " + monitor.Native.Value.Height
                        : "-",
                    Cell(monitor.MaxRefreshHz, v => v + " Hz"),
                    Cell(monitor.DiagonalInches, v => v.ToString("0.#", CultureInfo.CurrentCulture) + " pouces"),
                    Cell(monitor.YearOfManufacture, v => v.ToString(CultureInfo.CurrentCulture)),
                }, tone));
            }

            var facts = new List<Fact>
            {
                Known("Écrans branchés", ValueFormat.Number(displays.Monitors.Count)),
                Of("Mise à l'échelle", displays.ScalingPercent, v => v + " %"),
            };

            var first = displays.Monitors[0];
            facts.Add(Of("Densité de l'écran principal", first.PixelsPerInch,
                v => Math.Round(v) + " points par pouce"));
            facts.Add(Str("Numéro de série de l'écran principal", first.SerialNumber));

            Add(groups, "Écrans",
                "La définition d'origine vient de la dalle elle-même : elle est nette à celle-là et à " +
                "aucune autre. C'est l'écart entre les deux colonnes qui explique « c'est flou ».",
                facts,
                new FactTable(
                    new[] { "Écran", "Fabricant", "Affiché", "Définition d'origine", "Maximum", "Taille", "Année" },
                    rows,
                    "Aucun écran rattaché au bureau n'a été relevé."));
        }

        private static void AddThermal(ICollection<FactGroup> groups, SystemSnapshot snapshot)
        {
            var thermal = snapshot.Hardware.Thermal;
            var facts = new List<Fact>();

            foreach (var zone in thermal.Zones)
            {
                // « Zone » ne vaut que pour une zone ACPI, dont on ignore ce qu'elle suit. Un
                // capteur physique mesure un composant nommé : l'appeler « zone » brouillerait
                // justement la distinction que tout le reste de ce groupe cherche à tenir.
                var label = zone.Source == ThermalSource.AcpiZone ? "Zone " + zone.Name : zone.Name;

                facts.Add(Of(
                    label, zone.Celsius,
                    v => ValueFormat.Celsius((int)Math.Round(v)),
                    v => !zone.Plausible ? FactTone.Neutral
                        : v >= 85 ? FactTone.Bad
                        : v >= 70 ? FactTone.Warning : FactTone.Good));

                if (zone.ActivelyCooled.HasValue)
                    facts.Add(Flag(label + " : refroidissement actif", zone.ActivelyCooled,
                        "Ventilateur piloté par cette zone", "Refroidissement passif",
                        FactTone.Neutral, FactTone.Neutral));
            }

            // Les températures qui ne demandent aucun pilote sont rassemblées ici, même si elles
            // figurent déjà dans leur propre fiche : un technicien qui vient chercher « est-ce que
            // ça chauffe » ne doit pas avoir à visiter trois écrans pour répondre. Et quand les
            // capteurs sont éteints, ce cadre serait sans cela réduit à une explication.
            foreach (var gpu in snapshot.Hardware.Gpus)
                if (gpu.TemperatureCelsius.HasValue)
                    facts.Add(Of("Carte graphique : " + gpu.Model.Or("carte inconnue"), gpu.TemperatureCelsius,
                        v => ValueFormat.Celsius((int)Math.Round(v)),
                        v => v >= 90 ? FactTone.Bad : v >= 80 ? FactTone.Warning : FactTone.Good));

            foreach (var disk in snapshot.Storage.PhysicalDisks)
                if (disk.Smart.TemperatureCelsius.HasValue)
                    facts.Add(Of(disk.Model.Or("Disque " + disk.Index), disk.Smart.TemperatureCelsius,
                        v => ValueFormat.Celsius(v),
                        v => v >= 60 ? FactTone.Bad : v >= 50 ? FactTone.Warning : FactTone.Good));

            if (thermal.Limitation != null)
                // « Non mesuré » et rien d'autre : une valeur absente s'écrit toujours de la
                // même façon, sans quoi le lecteur devrait deviner, ligne par ligne, si le texte
                // qu'il voit est une donnée ou une excuse. L'explication va dans la raison.
                facts.Add(new Fact(
                    "Température du processeur", "Non mesuré", FactState.Missing, thermal.Limitation));

            if (facts.Count == 0) return;

            var group = new FactGroup(
                "Températures",
                thermal.AdvancedSensorsUsed
                    ? "Capteurs matériels actifs : ces valeurs viennent des sondes physiques du " +
                      "processeur et de la carte mère, lues par pilote."
                    : thermal.Zones.Count == 0
                        ? "Cette machine n'expose aucune zone thermique."
                        : "Une zone ACPI n'est pas un composant : le firmware décide de ce qu'elle mesure " +
                          "et ne le dit pas. Les noms sont donc rendus tels quels.",
                facts);

            groups.Add(group);
        }

        // ================================================================= Sécurité

        /// <summary>
        /// Fiche Sécurité : l'état des protections, jamais un verdict sur la propreté de la machine.
        /// </summary>
        /// <remarks>
        /// Le logiciel ne cherche aucune menace et n'ouvre aucun fichier de l'utilisateur. Tout
        /// ce qui figure ici est une lecture de configuration : ce que Windows déclare de ses
        /// protections. La nuance compte pour le client comme pour ce qu'on a le droit d'écrire
        /// dans un rapport.
        /// </remarks>
        private static IReadOnlyList<FactGroup> BuildSecurity(SecuritySnapshot security)
        {
            var groups = new List<FactGroup>();

            var defender = security.Defender;
            Add(groups, "Protection antivirus", null, new[]
            {
                Flag("Antivirus Microsoft Defender", defender.AntivirusEnabled,
                    "Actif", "Inactif", FactTone.Good, FactTone.Bad),
                Flag("Protection en temps réel", defender.RealTimeProtectionEnabled,
                    "Active", "Inactive", FactTone.Good, FactTone.Bad),
                Of("Date des signatures", defender.SignatureDate, ValueFormat.Date),
                Of("Ancienneté des signatures", defender.SignatureAgeDays,
                    v => v + " jour" + (v > 1 ? "s" : string.Empty),
                    v => v >= 30 ? FactTone.Bad : v >= 7 ? FactTone.Warning : FactTone.Good),
                Of("Dernier examen rapide", defender.LastScan, ValueFormat.DateTime),
                Flag("Protection contre les modifications", defender.TamperProtectionEnabled,
                    "Activée", "Désactivée", FactTone.Good, FactTone.Warning),
            }, SecurityProducts(security));

            var firewall = new List<Fact>();
            foreach (var profile in security.Firewall)
                firewall.Add(Flag("Profil " + profile.Profile, profile.Enabled,
                    "Actif", "Désactivé", FactTone.Good, FactTone.Bad));

            firewall.Add(Flag("Filtre SmartScreen", security.SmartScreenEnabled,
                "Activé", "Désactivé", FactTone.Good, FactTone.Warning));

            Add(groups, "Pare-feu et filtrage",
                "Le profil « Public » est celui qui s'applique aux réseaux inconnus.", firewall);

            var uac = security.Uac;
            Add(groups, "Contrôle de compte d'utilisateur", null, new[]
            {
                Flag("Contrôle de compte", uac.Enabled, "Activé", "Désactivé", FactTone.Good, FactTone.Bad),
                Of("Niveau d'invite", uac.AdminPromptBehavior, DescribePrompt,
                    v => v == 0 ? FactTone.Bad : FactTone.Good),
                Flag("Bureau sécurisé pendant l'invite", uac.SecureDesktop,
                    "Oui", "Non", FactTone.Good, FactTone.Warning),
            });

            var remote = security.RemoteAccess;
            Add(groups, "Accès à distance", null, new[]
            {
                Flag("Bureau à distance", remote.RemoteDesktopEnabled,
                    "Activé", "Désactivé", FactTone.Warning, FactTone.Good),
                Flag("Authentification préalable", remote.NetworkLevelAuthentication,
                    "Exigée", "Non exigée", FactTone.Good, FactTone.Bad),
                Of("Port d'écoute", remote.Port, v => v.ToString(CultureInfo.CurrentCulture)),
            });

            Add(groups, "Comptes locaux",
                "Seuls les comptes locaux de cette machine. Les comptes d'un domaine, s'il y en a, " +
                "ne sont pas gérés ici.",
                Array.Empty<Fact>(), LocalAccounts(security));

            return groups;
        }

        private static FactTable? SecurityProducts(SecuritySnapshot security)
        {
            if (security.Products.Count == 0) return null;

            var rows = new List<FactRow>();
            foreach (var product in security.Products)
            {
                var state = product.State;
                var tone = state.HasValue
                    ? state.Value switch
                    {
                        ProtectionState.Enabled => FactTone.Good,
                        ProtectionState.Expired => FactTone.Bad,
                        ProtectionState.Disabled => FactTone.Bad,
                        _ => FactTone.Neutral,
                    }
                    : FactTone.Neutral;

                rows.Add(new FactRow(new[]
                {
                    product.Name,
                    DescribeProductKind(product.Kind),
                    Cell(state, DescribeProtection),
                    Cell(product.UpToDate, v => v ? "À jour" : "Anciennes"),
                }, tone));
            }

            return new FactTable(
                new[] { "Produit", "Type", "État", "Signatures" }, rows,
                "Aucun produit déclaré au Centre de sécurité.");
        }

        /// <summary>
        /// Comptes locaux.
        /// </summary>
        /// <remarks>
        /// Les comptes désactivés figurent au tableau (un compte Administrateur intégré désactivé
        /// est une information utile) mais en tonalité neutre : ce sont les comptes actifs, et
        /// eux seuls, qui portent un risque.
        /// </remarks>
        private static FactTable? LocalAccounts(SecuritySnapshot security)
        {
            if (security.Accounts.Count == 0) return null;

            var rows = new List<FactRow>();
            foreach (var account in security.Accounts)
            {
                var active = account.Enabled.Or(false);
                var exposed = active && account.NoPasswordRequired.Or(false);

                rows.Add(new FactRow(new[]
                {
                    account.Name,
                    Cell(account.Enabled, v => v ? "Actif" : "Désactivé"),
                    Cell(account.IsAdministrator, v => v ? "Administrateur" : "Standard"),
                    Cell(account.NoPasswordRequired, v => v ? "Aucun mot de passe requis" : "Mot de passe requis"),
                }, exposed ? FactTone.Bad : active ? FactTone.Neutral : FactTone.Neutral));
            }

            return new FactTable(
                new[] { "Compte", "État", "Droits", "Mot de passe" }, rows, "Aucun compte local énuméré.");
        }

        private static string DescribeProductKind(SecurityProductKind kind) => kind switch
        {
            SecurityProductKind.Antivirus => "Antivirus",
            SecurityProductKind.Antispyware => "Anti-espiogiciel",
            SecurityProductKind.Firewall => "Pare-feu",
            _ => "Indéterminé",
        };

        private static string DescribeProtection(ProtectionState state) => state switch
        {
            ProtectionState.Enabled => "Actif",
            ProtectionState.Disabled => "Désactivé",
            ProtectionState.Expired => "Expiré",
            _ => "Indéterminé",
        };

        /// <summary>
        /// Niveaux de <c>ConsentPromptBehaviorAdmin</c>, traduits.
        /// </summary>
        /// <remarks>
        /// Afficher « 5 » n'apprendrait rien à personne. Le zéro mérite d'être nommé sans détour :
        /// c'est le réglage qui laisse le contrôle de compte activé tout en le rendant muet.
        /// </remarks>
        private static string DescribePrompt(int level) => level switch
        {
            0 => "Ne jamais avertir : la protection ne s'exerce pas",
            1 => "Demander identifiants sur le bureau sécurisé",
            2 => "Demander confirmation sur le bureau sécurisé",
            3 => "Demander identifiants",
            4 => "Demander confirmation",
            5 => "Avertir en cas de modification par un programme (réglage par défaut)",
            _ => "Niveau " + level.ToString(CultureInfo.CurrentCulture),
        };

        // ================================================================= Performances

        /// <summary>
        /// Fiche Performances.
        /// </summary>
        /// <remarks>
        /// Le sous-titre de chaque groupe rappelle la nature de la mesure, parce qu'elle change
        /// tout : la charge est un instantané pris pendant l'analyse, la durée de démarrage a été
        /// chronométrée par Windows à froid. Confondre les deux ferait conclure d'une pointe de
        /// charge passagère à une machine lente.
        /// </remarks>
        private static IReadOnlyList<FactGroup> BuildPerformance(PerformanceSnapshot performance)
        {
            var groups = new List<FactGroup>();
            var responsiveness = performance.Responsiveness;

            Add(groups, "Charge au moment de l'analyse",
                "Mesure instantanée : elle décrit la machine pendant l'analyse, pas son " +
                "comportement habituel.", new[]
            {
                Of("Charge processeur", responsiveness.CpuUsagePercent, ValueFormat.Percent,
                    v => v >= 85 ? FactTone.Bad : v >= 60 ? FactTone.Warning : FactTone.Good),
                Of("Occupation mémoire", responsiveness.MemoryUsagePercent, ValueFormat.Percent,
                    v => v >= 93 ? FactTone.Bad : v >= 85 ? FactTone.Warning : FactTone.Good),
                Of("Mémoire réclamée", responsiveness.CommittedBytes, ValueFormat.Bytes),
                Of("Limite de validation", responsiveness.CommitLimitBytes, ValueFormat.Bytes),
                Of("Mémoire réclamée / installée", responsiveness.CommitRatioPercent, ValueFormat.Percent,
                    v => v >= 150 ? FactTone.Bad : v >= 100 ? FactTone.Warning : FactTone.Good),
                Of("Processus en cours", performance.ProcessCount, v => v.ToString(CultureInfo.CurrentCulture)),
                Of("Temps de fonctionnement", performance.Uptime, ValueFormat.Duration),
            });

            var boot = performance.Boot;
            Add(groups, "Démarrage",
                "Mesuré par Windows lui-même à chaque allumage, et non pendant l'analyse.", new[]
            {
                Of("Dernier démarrage", boot.Duration, ValueFormat.Duration,
                    v => v.TotalSeconds >= 120 ? FactTone.Bad
                        : v.TotalSeconds >= 60 ? FactTone.Warning : FactTone.Good),
                Of("Dont chemin principal", boot.MainPathDuration, ValueFormat.Duration),
                Of("Mesuré le", boot.MeasuredAt, ValueFormat.DateTime),
                Of("Démarrages relevés", boot.SampleCount, v => v.ToString(CultureInfo.CurrentCulture)),
                Of("Dont dégradés", boot.DegradedBootCount, v => v.ToString(CultureInfo.CurrentCulture),
                    v => v >= 3 ? FactTone.Warning : FactTone.Good),
            });

            var power = performance.Power;
            Add(groups, "Alimentation et bridage",
                "Des réglages, pas des pannes : ils ne cassent rien et retiennent la machine.", new[]
            {
                Of("Plan d'alimentation", power.Plan, DescribePlan,
                    v => v == PowerPlanKind.PowerSaver ? FactTone.Warning : FactTone.Neutral),
                Str("Identifiant du plan", power.PlanId),
                Of("Fréquence maximale sur secteur", power.ProcessorMaximumOnAc, v => v + " %",
                    v => v < 100 ? FactTone.Bad : FactTone.Good),
                Of("Fréquence minimale sur secteur", power.ProcessorMinimumOnAc, v => v + " %"),
                Of("Fréquence maximale sur batterie", power.ProcessorMaximumOnBattery, v => v + " %"),
                Of("Fichier d'échange", power.PageFile, DescribePageFile,
                    v => v == PageFileMode.Disabled ? FactTone.Bad : FactTone.Good),
                Str("Réglage du fichier d'échange", power.PageFileSetting),
            });

            Add(groups, "Programmes les plus gourmands",
                "Classement instantané. Un navigateur en tête n'a rien d'anormal ; un programme " +
                "dont personne ne se sert, si.",
                Array.Empty<Fact>(), Processes(performance));

            return groups;
        }

        private static string DescribePlan(PowerPlanKind plan) => plan switch
        {
            PowerPlanKind.Balanced => "Équilibré",
            PowerPlanKind.HighPerformance => "Performances élevées",
            PowerPlanKind.PowerSaver => "Économie d'énergie",
            PowerPlanKind.Ultimate => "Performances ultimes",
            PowerPlanKind.Custom => "Personnalisé",
            _ => "Indéterminé",
        };

        private static string DescribePageFile(PageFileMode mode) => mode switch
        {
            PageFileMode.SystemManaged => "Géré par Windows",
            PageFileMode.FixedSize => "Taille imposée",
            PageFileMode.Disabled => "Aucun",
            _ => "Indéterminé",
        };

        private static FactTable? Processes(PerformanceSnapshot performance)
        {
            if (performance.TopByMemory.Count == 0) return null;

            var rows = new List<FactRow>();
            foreach (var process in performance.TopByMemory)
                rows.Add(new FactRow(new[]
                {
                    process.Name,
                    process.ProcessId.ToString(CultureInfo.CurrentCulture),
                    Cell(process.WorkingSetBytes, ValueFormat.Bytes),
                    Cell(process.CpuPercent, ValueFormat.Percent),
                    process.IsSystem ? "Composant de Windows" : "-",
                }));

            return new FactTable(
                new[] { "Programme", "PID", "Mémoire", "Processeur", "Origine" }, rows,
                "Aucun processus relevé.");
        }

        // ================================================================= fabriques

        /// <summary>
        /// Traduit une mesure en ligne affichable. Unique passage obligé : c'est ici, et nulle
        /// part ailleurs, que se décide ce qu'on affiche quand on n'a pas su mesurer.
        /// </summary>
        private static Fact Of<T>(string label, Measured<T> measured, Func<T, string> format, Func<T, FactTone>? tone = null)
        {
            switch (measured.Availability)
            {
                case Availability.Available:
                    return new Fact(label, format(measured.Value), FactState.Known, null,
                        tone == null ? FactTone.Neutral : tone(measured.Value));

                case Availability.Partial:
                    return new Fact(label, format(measured.Value), FactState.Partial, measured.Reason,
                        tone == null ? FactTone.Neutral : tone(measured.Value));

                case Availability.RequiresElevation:
                    return new Fact(label, "Non lu", FactState.NeedsElevation,
                        measured.Reason ?? "Privilèges administrateur requis.");

                case Availability.Unavailable:
                    return new Fact(label, "Non mesuré", FactState.Missing,
                        measured.Reason ?? "Aucune raison enregistrée.");

                default:
                    return new Fact(label, "Non relevé", FactState.NotCollected, measured.Reason);
            }
        }

        private static Fact Str(string label, Measured<string> measured)
            => Of(label, measured, v => string.IsNullOrWhiteSpace(v) ? "-" : v);

        private static Fact Flag(
            string label, Measured<bool> measured, string whenTrue, string whenFalse, FactTone toneTrue, FactTone toneFalse)
            => Of(label, measured, v => v ? whenTrue : whenFalse, v => v ? toneTrue : toneFalse);

        /// <summary>Valeur calculée à partir d'une liste déjà obtenue : elle n'a pas d'état de mesure propre.</summary>
        private static Fact Known(string label, string value, FactTone tone = FactTone.Neutral)
            => new Fact(label, value, FactState.Known, null, tone);

        private static Fact List(string label, IReadOnlyList<string> values, string emptyLabel)
            => new Fact(label, values.Count == 0 ? emptyLabel : string.Join(", ", Copy(values)), FactState.Known);

        private static void Add(
            ICollection<FactGroup> groups, string title, string? subtitle, IReadOnlyList<Fact> facts, FactTable? table = null)
        {
            var group = new FactGroup(title, subtitle, facts, table);

            // Un cadre vide est ambigu : on ne sait pas s'il n'y a rien à dire ou si la mesure a
            // échoué. Le compte rendu du module, lui, répond déjà à la question.
            if (group.HasFacts || (group.Table != null && group.Table.HasRows)) groups.Add(group);
        }

        private static string Cell<T>(Measured<T> measured, Func<T, string> format)
            => measured.HasValue ? format(measured.Value) : "-";

        private static string Cell(Measured<string> measured)
            => measured.HasValue && !string.IsNullOrWhiteSpace(measured.Value) ? measured.Value : "-";

        private static string Cell(Measured<string> primary, Measured<string> fallback)
        {
            if (primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value)) return primary.Value;
            if (fallback.HasValue && !string.IsNullOrWhiteSpace(fallback.Value)) return fallback.Value;
            return "-";
        }

        private static string Suffix(Measured<string> model)
            => model.HasValue && !string.IsNullOrWhiteSpace(model.Value) ? " : " + model.Value : string.Empty;

        private static string[] Copy(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++) copy[i] = values[i];
            return copy;
        }

        private static string Shorten(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "-";
            var single = message!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return single.Length <= 160 ? single : single.Substring(0, 157) + "…";
        }

        // ================================================================= libellés

        private static string DescribeRpm(int rpm)
            => ValueFormat.Number(rpm) + " tours/min";

        /// <summary>
        /// Une liaison en deçà de ce que le disque sait tenir se signale dès la fiche.
        /// </summary>
        /// <remarks>
        /// C'est la seule ligne des caractéristiques dont la couleur dépend d'une autre : le
        /// chiffre seul ne dit rien, c'est l'écart avec le maximum du disque qui compte.
        /// </remarks>
        private static FactTone LinkTone(AtaIdentity identity)
        {
            if (!identity.LinkGigabitsPerSecond.HasValue || !identity.MaximumGigabitsPerSecond.HasValue)
                return FactTone.Neutral;

            return identity.LinkGigabitsPerSecond.Value < identity.MaximumGigabitsPerSecond.Value
                ? FactTone.Warning
                : FactTone.Good;
        }

        private static string DescribeFirmware(FirmwareMode mode) => mode switch
        {
            FirmwareMode.Uefi => "UEFI",
            FirmwareMode.LegacyBios => "BIOS hérité",
            _ => "Indéterminé",
        };

        private static string DescribeBus(StorageBusType bus) => bus switch
        {
            StorageBusType.Nvme => "NVMe",
            StorageBusType.Sata => "SATA",
            StorageBusType.Ata => "ATA",
            StorageBusType.Usb => "USB",
            StorageBusType.Scsi => "SCSI",
            StorageBusType.Sas => "SAS",
            StorageBusType.Raid => "RAID",
            StorageBusType.Virtual => "Virtuel",
            StorageBusType.SecureDigital => "Carte SD",
            _ => "Indéterminée",
        };

        private static string DescribeMedia(StorageMediaType media) => media switch
        {
            StorageMediaType.Nvme => "SSD NVMe",
            StorageMediaType.Ssd => "SSD",
            StorageMediaType.Hdd => "Disque mécanique",
            StorageMediaType.Removable => "Support amovible",
            StorageMediaType.Virtual => "Disque virtuel",
            _ => "Indéterminé",
        };

        private static string DescribeSmart(SmartOverallStatus status) => status switch
        {
            // « Aucun seuil franchi » supposait que les seuils avaient été lus. Sur un compte
            // standard ils ne le sont pas, et le disque rend son verdict sans les publier :
            // le libellé décrit donc ce qui est su, et la raison de la mesure dit le reste.
            SmartOverallStatus.Ok => "Sain : aucune alerte du disque",
            SmartOverallStatus.Warning => "À surveiller : attributs dégradés",
            SmartOverallStatus.Failing => "Défaillance annoncée par le disque",
            SmartOverallStatus.NotSupported => "Non exposé par ce disque ou son contrôleur",
            _ => "Indéterminé",
        };

        private static FactTone SmartTone(SmartOverallStatus status) => status switch
        {
            SmartOverallStatus.Ok => FactTone.Good,
            SmartOverallStatus.Warning => FactTone.Warning,
            SmartOverallStatus.Failing => FactTone.Bad,
            _ => FactTone.Neutral,
        };

        private static string DescribeSfc(SfcStatus status) => status switch
        {
            SfcStatus.Clean => "Aucune anomalie",
            SfcStatus.RepairedSuccessfully => "Anomalies réparées lors d'un passage précédent",
            SfcStatus.CorruptionFound => "Fichiers système altérés",
            SfcStatus.RepairFailed => "Fichiers altérés, réparation impossible",
            SfcStatus.NotRun => "Jamais exécuté sur cette machine",
            _ => "Indéterminé",
        };

        private static FactTone SfcTone(SfcStatus status) => status switch
        {
            SfcStatus.Clean => FactTone.Good,
            SfcStatus.RepairedSuccessfully => FactTone.Warning,
            SfcStatus.CorruptionFound => FactTone.Bad,
            SfcStatus.RepairFailed => FactTone.Bad,
            _ => FactTone.Neutral,
        };

        private static string DescribeStore(ComponentStoreState state) => state switch
        {
            ComponentStoreState.Healthy => "Sain",
            ComponentStoreState.Repairable => "Altéré, réparable",
            ComponentStoreState.NonRepairable => "Altéré, non réparable en l'état",
            _ => "Indéterminé",
        };

        private static FactTone StoreTone(ComponentStoreState state) => state switch
        {
            ComponentStoreState.Healthy => FactTone.Good,
            ComponentStoreState.Repairable => FactTone.Warning,
            ComponentStoreState.NonRepairable => FactTone.Bad,
            _ => FactTone.Neutral,
        };

        private static string DescribeStartupLocation(StartupLocation location) => location switch
        {
            StartupLocation.RegistryRunMachine => "Registre : toutes sessions",
            StartupLocation.RegistryRunUser => "Registre : session courante",
            StartupLocation.RegistryRunOnce => "Registre : exécution unique",
            StartupLocation.StartupFolderMachine => "Dossier Démarrage : toutes sessions",
            StartupLocation.StartupFolderUser => "Dossier Démarrage : session courante",
            StartupLocation.ScheduledTask => "Tâche planifiée",
            _ => "Emplacement indéterminé",
        };

        private static string DescribeAdapter(NetworkAdapterKind kind) => kind switch
        {
            NetworkAdapterKind.Ethernet => "Ethernet",
            NetworkAdapterKind.WiFi => "Wi-Fi",
            NetworkAdapterKind.Bluetooth => "Bluetooth",
            NetworkAdapterKind.Mobile => "Réseau mobile",
            NetworkAdapterKind.Virtual => "Interface virtuelle",
            NetworkAdapterKind.Tunnel => "Tunnel",
            NetworkAdapterKind.Vpn => "Liaison VPN",
            NetworkAdapterKind.Loopback => "Boucle locale",
            _ => "Type indéterminé",
        };
    }
}
