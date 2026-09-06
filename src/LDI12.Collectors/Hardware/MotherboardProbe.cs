using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Carte mère, machine, BIOS/UEFI, démarrage sécurisé et module TPM.
    /// </summary>
    /// <remarks>
    /// Le mode de démarrage vient de l'API firmware plutôt que du registre : c'est la seule
    /// méthode qui fonctionne identiquement de Windows 7 à Windows 11. Le démarrage sécurisé et
    /// le TPM, eux, n'existent pas partout : leur absence est rapportée avec son motif, jamais
    /// comme un défaut de la machine.
    /// </remarks>
    public sealed class MotherboardProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Motherboard,
            DisplayName = "Carte mère et firmware",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(1.5),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(10);

            var board = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard",
                timeout, cancellationToken).ConfigureAwait(false);

            var system = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem",
                timeout, cancellationToken).ConfigureAwait(false);

            var product = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT UUID FROM Win32_ComputerSystemProduct",
                timeout, cancellationToken).ConfigureAwait(false);

            var enclosure = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT ChassisTypes FROM Win32_SystemEnclosure",
                timeout, cancellationToken).ConfigureAwait(false);

            var bios = await context.Wmi.QueryAsync(WmiNamespaces.CimV2,
                "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
                timeout, cancellationToken).ConfigureAwait(false);

            var motherboard = new MotherboardInfo
            {
                Manufacturer = board.Succeeded
                    ? Measure.Text(board.First, "Manufacturer", "Le fabricant de la carte mère")
                    : Measure.FromFailure<string>(board, "Le fabricant de la carte mère"),
                Model = board.Succeeded
                    ? Measure.Text(board.First, "Product", "Le modèle de la carte mère")
                    : Measure.FromFailure<string>(board, "Le modèle de la carte mère"),
                Version = board.Succeeded
                    ? Measure.Text(board.First, "Version", "La révision de la carte mère")
                    : Measure.FromFailure<string>(board, "La révision de la carte mère"),
                SerialNumber = board.Succeeded
                    ? Measure.Text(board.First, "SerialNumber", "Le numéro de série de la carte mère")
                    : Measure.FromFailure<string>(board, "Le numéro de série de la carte mère"),

                SystemManufacturer = system.Succeeded
                    ? Measure.Text(system.First, "Manufacturer", "Le constructeur de la machine")
                    : Measure.FromFailure<string>(system, "Le constructeur de la machine"),
                SystemModel = system.Succeeded
                    ? Measure.Text(system.First, "Model", "Le modèle de la machine")
                    : Measure.FromFailure<string>(system, "Le modèle de la machine"),
                SystemSku = system.Succeeded
                    ? Measure.Text(system.First, "SystemSKUNumber", "La référence commerciale de la machine")
                    : Measure.FromFailure<string>(system, "La référence commerciale de la machine"),
                SystemUuid = product.Succeeded
                    ? Measure.Text(product.First, "UUID", "L'identifiant unique de la machine")
                    : Measure.FromFailure<string>(product, "L'identifiant unique de la machine"),
                ChassisType = ReadChassis(enclosure),

                Bios = new BiosInfo
                {
                    Vendor = bios.Succeeded
                        ? Measure.Text(bios.First, "Manufacturer", "L'éditeur du BIOS")
                        : Measure.FromFailure<string>(bios, "L'éditeur du BIOS"),
                    Version = bios.Succeeded
                        ? Measure.Text(bios.First, "SMBIOSBIOSVersion", "La version du BIOS")
                        : Measure.FromFailure<string>(bios, "La version du BIOS"),
                    ReleaseDate = bios.Succeeded
                        ? Measure.DmtfDate(bios.First, "ReleaseDate", "La date du BIOS")
                        : Measure.FromFailure<DateTimeOffset>(bios, "La date du BIOS"),
                    Mode = context.Native.ReadFirmwareMode(),
                },

                SecureBootEnabled = ReadSecureBoot(context),
                Tpm = await ReadTpmAsync(context, cancellationToken).ConfigureAwait(false),
            };

            context.Draft.SetMotherboard(motherboard);

            if (!board.Succeeded && !system.Succeeded && !bios.Succeeded)
                return ProbeOutcome.Failed("Aucune information de carte mère n'a pu être lue : " + board.Reason);

            var label = motherboard.Manufacturer.Or(string.Empty) + " " + motherboard.Model.Or(string.Empty);
            return string.IsNullOrWhiteSpace(label)
                ? ProbeOutcome.Partial("La carte mère n'a pas été identifiée par son fabricant.")
                : ProbeOutcome.Ok(label.Trim());
        }

        private static Measured<string> ReadChassis(WmiQueryResult enclosure)
        {
            if (!enclosure.Succeeded) return Measure.FromFailure<string>(enclosure, "Le type de châssis");

            var types = enclosure.First?.GetStringArray("ChassisTypes");
            if (types == null || types.Length == 0)
            {
                // ChassisTypes est déclaré uint16[] : selon les pilotes il arrive en entiers.
                var single = enclosure.First?["ChassisTypes"];
                if (single is ushort[] numeric && numeric.Length > 0)
                    return Measured.Ok(DescribeChassis(numeric[0]), DataSource.Wmi);
                return Measured.Missing<string>("Le type de châssis n'est pas renseigné par cette machine.");
            }

            return int.TryParse(types[0], out var code)
                ? Measured.Ok(DescribeChassis(code), DataSource.Wmi)
                : Measured.Ok(types[0], DataSource.Wmi);
        }

        private static Measured<bool> ReadSecureBoot(ProbeContext context)
        {
            var feature = context.Platform.Features.Get(FeatureId.SecureBootState);
            if (feature.Availability != Availability.Available)
                return Measured.Missing<bool>(feature.Reason);

            var value = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, RegistryPaths.SecureBootState, "UEFISecureBootEnabled");

            return value.HasValue
                ? Measured.Ok(value.Value == 1, DataSource.Registry)
                : Measured.Missing<bool>("L'état du démarrage sécurisé n'est pas exposé par ce firmware.");
        }

        private static async Task<TpmInfo> ReadTpmAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var feature = context.Platform.Features.Get(FeatureId.TpmWmi);

            if (feature.Availability == Availability.RequiresElevation)
            {
                var pending = Measured.NeedsElevation<bool>("lecture de l'état du module TPM");
                return new TpmInfo
                {
                    Present = pending,
                    Enabled = pending,
                    Ready = pending,
                    SpecVersion = Measured.NeedsElevation<string>("lecture de la version du TPM"),
                    Manufacturer = Measured.NeedsElevation<string>("lecture du fabricant du TPM"),
                };
            }

            if (feature.Availability != Availability.Available)
            {
                return new TpmInfo
                {
                    Present = Measured.Ok(false, DataSource.Inferred),
                    Enabled = Measured.Missing<bool>(feature.Reason),
                    Ready = Measured.Missing<bool>(feature.Reason),
                    SpecVersion = Measured.Missing<string>(feature.Reason),
                    Manufacturer = Measured.Missing<string>(feature.Reason),
                };
            }

            var query = await context.Wmi.QueryAsync(WmiNamespaces.Tpm,
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion, ManufacturerIdTxt FROM Win32_Tpm",
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

            if (!query.Succeeded)
            {
                return new TpmInfo
                {
                    Present = Measure.FromFailure<bool>(query, "La présence d'un module TPM"),
                    Enabled = Measure.FromFailure<bool>(query, "L'activation du TPM"),
                    Ready = Measure.FromFailure<bool>(query, "L'état de préparation du TPM"),
                    SpecVersion = Measure.FromFailure<string>(query, "La version du TPM"),
                    Manufacturer = Measure.FromFailure<string>(query, "Le fabricant du TPM"),
                };
            }

            var record = query.First;
            if (record == null)
            {
                var absent = Measured.Ok(false, DataSource.Wmi);
                return new TpmInfo
                {
                    Present = absent,
                    Enabled = absent,
                    Ready = absent,
                    SpecVersion = Measured.Missing<string>("Cette machine ne dispose pas de module TPM."),
                    Manufacturer = Measured.Missing<string>("Cette machine ne dispose pas de module TPM."),
                };
            }

            var enabled = Measure.Bool(record, "IsEnabled_InitialValue", "L'activation du TPM");
            var activated = Measure.Bool(record, "IsActivated_InitialValue", "L'activation du TPM");

            return new TpmInfo
            {
                Present = Measured.Ok(true, DataSource.Wmi),
                Enabled = enabled,
                Ready = enabled.HasValue && activated.HasValue
                    ? Measured.Ok(enabled.Value && activated.Value, DataSource.Inferred)
                    : Measured.Missing<bool>("L'état de préparation du TPM n'est pas exposé."),
                SpecVersion = Measure.Text(record, "SpecVersion", "La version du TPM"),
                Manufacturer = Measure.Text(record, "ManufacturerIdTxt", "Le fabricant du TPM"),
            };
        }

        private static string DescribeChassis(int code) => code switch
        {
            3 => "Ordinateur de bureau",
            4 => "Boîtier bas",
            6 => "Mini-tour",
            7 => "Tour",
            8 or 9 or 10 or 14 => "Ordinateur portable",
            11 => "Appareil de poche",
            12 => "Station d'accueil",
            13 => "Tout-en-un",
            15 => "Boîtier compact",
            17 => "Châssis principal",
            23 => "Serveur en rack",
            30 => "Tablette",
            31 => "Portable convertible",
            32 => "Portable détachable",
            _ => "Châssis de type " + code,
        };
    }
}
