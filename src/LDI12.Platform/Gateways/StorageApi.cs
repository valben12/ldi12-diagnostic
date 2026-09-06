using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Native;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Gateways
{
    public sealed class StorageApi : IStorageApi
    {
        private const string Category = "Platform.Storage";

        /// <summary>Au-delà, on est certainement sur une machine hors de la cible du logiciel.</summary>
        private const int MaxDrivesToProbe = 32;

        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidFunction = 1;
        private const int ErrorNotSupported = 50;

        private readonly IScopedLogger _log;

        public StorageApi(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public Measured<IReadOnlyList<PhysicalDriveDescriptor>> EnumeratePhysicalDrives()
        {
            var drives = new List<PhysicalDriveDescriptor>();
            var accessDenied = 0;

            for (var index = 0; index < MaxDrivesToProbe; index++)
            {
                // Droit d'accès nul : suffisant pour interroger les propriétés du périphérique,
                // et surtout utilisable sans privilèges administrateur.
                using var handle = Open(index, desiredAccess: 0);
                if (handle == null || handle.IsInvalid)
                {
                    if (Marshal.GetLastWin32Error() == ErrorAccessDenied) accessDenied++;
                    handle?.Dispose();
                    continue;
                }

                var descriptor = ReadDeviceDescriptor(handle, index);
                if (descriptor != null) drives.Add(descriptor);
            }

            if (drives.Count == 0)
            {
                return Measured.Missing<IReadOnlyList<PhysicalDriveDescriptor>>(
                    accessDenied > 0
                        ? "L'accès aux disques physiques a été refusé. Des privilèges administrateur sont nécessaires."
                        : "Aucun disque physique n'a pu être ouvert sur cette machine.",
                    DataSource.NativeApi);
            }

            _log.Debug(drives.Count + " disque(s) physique(s) énuméré(s).");
            return Measured.Ok((IReadOnlyList<PhysicalDriveDescriptor>)drives, DataSource.NativeApi);
        }

        public int? ReadVolumeDiskIndex(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter)) return null;

            // Le chemin d'un volume s'écrit \\.\C:, sans barre oblique finale, sinon l'ouverture
            // vise le système de fichiers et non le périphérique.
            var path = @"\\.\" + driveLetter.TrimEnd('\\');
            SafeFileHandle? handle = null;
            try
            {
                handle = StorageNative.CreateFileW(path, 0,
                    StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE,
                    IntPtr.Zero, StorageNative.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid) return null;

                // VOLUME_DISK_EXTENTS : NumberOfDiskExtents (4) + alignement (4), puis les
                // extents de 24 octets. Un volume réparti sur plusieurs disques (agrégation ou
                // miroir) en compte plusieurs ; le premier suffit à rattacher le volume.
                var buffer = new byte[8 + (24 * 16)];
                if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 12)
                {
                    return null;
                }

                return BitConverter.ToUInt32(buffer, 0) == 0 ? (int?)null : BitConverter.ToInt32(buffer, 8);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        /// <summary>
        /// Données SMART d'un disque ATA, avec ou sans privilèges.
        /// </summary>
        /// <remarks>
        /// <b>Deux chemins, essayés dans cet ordre.</b> Le premier ouvre le disque en lecture et
        /// envoie les commandes SMART : il rend la table d'attributs <b>et les seuils
        /// constructeur</b>, mais exige l'élévation. Le second n'exige rien du tout et rend la
        /// même table d'attributs, sans les seuils, accompagnée du verdict du disque lui-même.
        /// <para>
        /// Le second existe parce que la promesse du logiciel est de fonctionner en compte
        /// standard. Sans lui, les heures de fonctionnement et les cycles d'allumage d'un disque
        /// SATA restaient invisibles à moins de demander une élévation, alors que Windows les
        /// donne à qui les demande correctement.
        /// </para>
        /// </remarks>
        public Measured<SmartData> ReadAtaSmart(int driveIndex)
        {
            var privileged = ReadAtaSmartElevated(driveIndex);
            if (privileged.HasValue) return privileged;

            // La raison rendue est celle du chemin non privilégié : c'est le seul qui ne bute
            // jamais sur les droits, donc le seul dont l'échec décrit vraiment le disque.
            return ReadAtaSmartByPrediction(driveIndex);
        }

        /// <summary>Chemin complet : attributs et seuils, mais réservé aux administrateurs.</summary>
        private Measured<SmartData> ReadAtaSmartElevated(int driveIndex)
        {
            using var handle = Open(driveIndex, StorageNative.GENERIC_READ);
            if (handle == null || handle.IsInvalid)
                return Measured.Missing<SmartData>(
                    "Le disque n'a pas pu être ouvert en lecture.", DataSource.NativeApi);

            // SMART_GET_VERSION échoue sur les contrôleurs qui ne relaient pas les commandes ATA :
            // typiquement les boîtiers USB et la plupart des cartes RAID.
            var version = new byte[24];
            if (!StorageNative.DeviceIoControl(handle, StorageNative.SMART_GET_VERSION,
                    null, 0, version, version.Length, out _, IntPtr.Zero))
            {
                return Measured.Missing<SmartData>(
                    "Ce contrôleur ne relaie pas les commandes SMART.", DataSource.NativeApi);
            }

            var attributes = SendSmartCommand(handle, driveIndex, StorageNative.SMART_READ_ATTRIBUTES);
            if (attributes == null)
                return Measured.Missing<SmartData>(
                    "La table d'attributs SMART n'a pas été rendue.", DataSource.NativeApi);

            var thresholds = SendSmartCommand(handle, driveIndex, StorageNative.SMART_READ_THRESHOLDS);

            _log.Debug("SMART du disque " + driveIndex + " lu par commande privilégiée.");
            return Measured.Ok(SmartParser.Parse(attributes, thresholds), DataSource.NativeApi);
        }

        /// <summary>
        /// Chemin sans privilège : la prédiction de panne, qui transporte la table d'attributs.
        /// </summary>
        /// <remarks>
        /// Le descripteur est ouvert sans aucun droit d'accès : c'est ce que Windows accorde à un
        /// compte standard sur un disque physique. L'appel rend le verdict du disque suivi de ses
        /// 512 octets d'attributs, dont la disposition est celle de la commande privilégiée.
        /// <para>
        /// Ce qui manque, ce sont les seuils constructeur : ils se lisent par une seconde commande
        /// qui, elle, exige l'élévation. Leur absence est visible (un seuil inconnu s'affiche
        /// comme inconnu) et l'entier de prédiction la compense pour l'essentiel, puisqu'il est
        /// justement le résultat de la comparaison que le disque fait lui-même.
        /// </para>
        /// </remarks>
        private Measured<SmartData> ReadAtaSmartByPrediction(int driveIndex)
        {
            using var handle = Open(driveIndex, desiredAccess: 0);
            if (handle == null || handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                return Measured.Missing<SmartData>(
                    "Le disque " + driveIndex + " n'a pas pu être ouvert (erreur " +
                    error.ToString(CultureInfo.InvariantCulture) + ").", DataSource.NativeApi);
            }

            var buffer = new byte[StorageNative.PredictFailureSize];
            if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_STORAGE_PREDICT_FAILURE,
                    null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                return NotSupported(driveIndex, Marshal.GetLastWin32Error());
            }

            if (returned < StorageNative.PredictFailureSize) return NotSupported(driveIndex, 0);

            var sector = new byte[StorageNative.SmartDataSize];
            Buffer.BlockCopy(buffer, StorageNative.PredictFailureDataOffset, sector, 0, sector.Length);

            // Un disque qui ne remplit pas ce champ rend 512 octets nuls : mieux vaut le dire que
            // d'annoncer un disque neuf de zéro heure.
            if (IsEmpty(sector))
                return Measured.Missing<SmartData>(
                    "Ce disque répond à la demande de prédiction de panne sans y joindre ses " +
                    "attributs SMART. C'est le cas de beaucoup de boîtiers externes.",
                    DataSource.NativeApi);

            var predicted = BitConverter.ToUInt32(buffer, 0) != 0;

            _log.Debug("SMART du disque " + driveIndex + " lu sans privilège par prédiction de panne.");

            return Measured.Partial(
                SmartParser.Parse(sector, thresholdSector: null, predictedFailure: predicted),
                DataSource.NativeApi,
                "Lu sans privilèges administrateur : les attributs et le verdict du disque sont " +
                "complets, mais les seuils constructeur, qui exigent une seconde commande " +
                "privilégiée, ne sont pas connus.");
        }

        private static bool IsEmpty(byte[] sector)
        {
            foreach (var value in sector) if (value != 0) return false;
            return true;
        }

        /// <summary>
        /// Rotation et génération de liaison, par la commande ATA « IDENTIFY DEVICE ».
        /// </summary>
        /// <remarks>
        /// Le seul appel de cette passerelle pour lequel l'élévation n'a pas d'alternative. Le
        /// refus d'accès est donc rendu comme tel, et non comme une absence de données : le
        /// technicien saura qu'il obtiendra ces lignes en relançant en administrateur.
        /// </remarks>
        public Measured<AtaIdentity> ReadAtaIdentity(int driveIndex)
        {
            using var handle = Open(driveIndex, StorageNative.GENERIC_READ);
            if (handle == null || handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle?.Dispose();

                return error == ErrorAccessDenied
                    ? Measured.NeedsElevation<AtaIdentity>(
                        "lecture de l'identité du disque " + driveIndex +
                        " (rotation et génération de liaison)")
                    : Measured.Missing<AtaIdentity>(
                        "Le disque " + driveIndex + " n'a pas pu être ouvert (erreur " +
                        error.ToString(CultureInfo.InvariantCulture) + ").", DataSource.NativeApi);
            }

            var identify = SendIdentify(handle);
            if (identify == null)
                return Measured.Missing<AtaIdentity>(
                    "Ce contrôleur ne relaie pas la commande d'identification ATA. C'est le cas " +
                    "des disques NVMe, des boîtiers externes et de la plupart des cartes RAID.",
                    DataSource.NativeApi);

            _log.Debug("Identité ATA du disque " + driveIndex + " lue.");
            return Measured.Ok(AtaIdentityParser.Parse(identify), DataSource.NativeApi);
        }

        /// <summary>
        /// Envoie IDENTIFY DEVICE et rend les 512 octets de réponse.
        /// </summary>
        /// <remarks>
        /// La structure d'en-tête et le tampon de données voyagent dans un seul bloc : c'est ce
        /// que veut dire <c>DataBufferOffset</c>, qui désigne une position à l'intérieur du bloc
        /// et non un pointeur indépendant.
        /// </remarks>
        private static byte[]? SendIdentify(SafeFileHandle handle)
        {
            var headerSize = Marshal.SizeOf(typeof(StorageNative.ATA_PASS_THROUGH_EX));
            var total = headerSize + StorageNative.IdentifySize;

            var request = new StorageNative.ATA_PASS_THROUGH_EX
            {
                Length = (ushort)headerSize,
                AtaFlags = StorageNative.ATA_FLAGS_DATA_IN | StorageNative.ATA_FLAGS_DRDY_REQUIRED,
                DataTransferLength = StorageNative.IdentifySize,
                TimeOutValue = 5,
                DataBufferOffset = new IntPtr(headerSize),
                PreviousTaskFile = new byte[8],
                CurrentTaskFile = new byte[8],
            };

            request.CurrentTaskFile[StorageNative.TaskFileSectorCountIndex] = 1;
            request.CurrentTaskFile[StorageNative.TaskFileCommandIndex] = StorageNative.ATA_COMMAND_IDENTIFY;

            var buffer = new byte[total];
            var header = StorageNative.StructureToBytes(request);
            Buffer.BlockCopy(header, 0, buffer, 0, Math.Min(header.Length, headerSize));

            // Même tampon en entrée et en sortie : le pilote y écrit la réponse à la position
            // que l'en-tête lui a désignée.
            if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_ATA_PASS_THROUGH,
                    buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                return null;
            }

            if (returned < total) return null;

            var identify = new byte[StorageNative.IdentifySize];
            Buffer.BlockCopy(buffer, headerSize, identify, 0, identify.Length);

            return IsEmpty(identify) ? null : identify;
        }

        public Measured<SmartData> ReadNvmeSmart(int driveIndex)
        {
            using var handle = Open(driveIndex, desiredAccess: 0);
            if (handle == null || handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                return Measured.Missing<SmartData>(
                    "Le disque NVMe " + driveIndex + " n'a pas pu être ouvert (erreur " +
                    error.ToString(CultureInfo.InvariantCulture) + ").", DataSource.NativeApi);
            }

            var protocolSize = Marshal.SizeOf(typeof(StorageNative.STORAGE_PROTOCOL_SPECIFIC_DATA));

            // Le tampon d'entrée est un STORAGE_PROPERTY_QUERY dont AdditionalParameters (qui
            // commence à l'octet 8, après PropertyId et QueryType) porte en réalité une
            // structure STORAGE_PROTOCOL_SPECIFIC_DATA suivie de la zone de réception.
            // Se fier à Marshal.SizeOf(STORAGE_PROPERTY_QUERY) donnerait 12 à cause de
            // l'alignement, et décalerait toute la requête.
            const int additionalParametersOffset = 8;
            const int logSize = 512;
            const int descriptorHeader = 8;                 // Version + Size

            var buffer = new byte[descriptorHeader + protocolSize + logSize];

            var query = new StorageNative.STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageNative.StorageDeviceProtocolSpecificProperty,
                QueryType = StorageNative.PropertyStandardQuery,
                AdditionalParameters = new byte[1],
            };
            var protocol = new StorageNative.STORAGE_PROTOCOL_SPECIFIC_DATA
            {
                ProtocolType = StorageNative.ProtocolTypeNvme,
                DataType = StorageNative.NVMeDataTypeLogPage,
                ProtocolDataRequestValue = StorageNative.NvmeLogPageHealth,
                ProtocolDataRequestSubValue = 0,
                ProtocolDataOffset = (uint)protocolSize,
                ProtocolDataLength = logSize,
            };

            var input = new byte[additionalParametersOffset + protocolSize + logSize];
            Buffer.BlockCopy(StorageNative.StructureToBytes(query), 0, input, 0, additionalParametersOffset);
            Buffer.BlockCopy(StorageNative.StructureToBytes(protocol), 0, input, additionalParametersOffset, protocolSize);

            if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_STORAGE_QUERY_PROPERTY,
                    input, input.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) ||
                returned < descriptorHeader + protocolSize)
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorInvalidFunction || error == ErrorNotSupported
                    ? Measured.Missing<SmartData>(
                        "Ce contrôleur NVMe n'expose pas son journal de santé à Windows.", DataSource.NativeApi)
                    : Measured.Missing<SmartData>(
                        "Le journal de santé NVMe n'a pas pu être lu (erreur " +
                        error.ToString(CultureInfo.InvariantCulture) + ").", DataSource.NativeApi);
            }

            var returnedProtocol = StorageNative.BytesToStructure<StorageNative.STORAGE_PROTOCOL_SPECIFIC_DATA>(
                buffer, descriptorHeader);
            var dataOffset = descriptorHeader + (int)returnedProtocol.ProtocolDataOffset;
            var dataLength = (int)returnedProtocol.ProtocolDataLength;

            if (dataOffset <= 0 || dataLength <= 0 || dataOffset + dataLength > buffer.Length)
            {
                return Measured.Missing<SmartData>(
                    "Le journal de santé NVMe a été renvoyé dans un format inattendu.", DataSource.NativeApi);
            }

            var log = new byte[dataLength];
            Buffer.BlockCopy(buffer, dataOffset, log, 0, dataLength);
            return Measured.Ok(SmartParser.ParseNvmeHealthLog(log), DataSource.NativeApi);
        }

        private Measured<SmartData> NotSupported(int driveIndex, int error)
        {
            _log.Debug("SMART indisponible sur le disque " + driveIndex + " (erreur " + error + ").");
            return Measured.Missing<SmartData>(
                "Ce disque n'expose pas ses données SMART. C'est le cas des disques externes USB et " +
                "de la plupart des contrôleurs RAID, qui masquent les commandes ATA du disque.",
                DataSource.NativeApi);
        }

        private static byte[]? SendSmartCommand(SafeFileHandle handle, int driveIndex, byte feature)
        {
            var request = new StorageNative.SENDCMDINPARAMS
            {
                cBufferSize = StorageNative.SmartDataSize,
                bDriveNumber = (byte)driveIndex,
                bReserved = new byte[3],
                dwReserved = new uint[4],
                irDriveRegs = new StorageNative.IDEREGS
                {
                    bFeaturesReg = feature,
                    bSectorCountReg = 1,
                    bSectorNumberReg = 1,
                    bCylLowReg = StorageNative.SMART_CYL_LOW,
                    bCylHighReg = StorageNative.SMART_CYL_HIGH,
                    bDriveHeadReg = 0xA0,
                    bCommandReg = StorageNative.SMART_CMD,
                },
            };

            var input = StorageNative.StructureToBytes(request);
            var output = new byte[StorageNative.SendCmdOutHeaderSize + StorageNative.SmartDataSize];

            if (!StorageNative.DeviceIoControl(handle, StorageNative.SMART_RCV_DRIVE_DATA,
                    input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
            {
                return null;
            }

            if (returned < StorageNative.SendCmdOutHeaderSize + StorageNative.SmartDataSize) return null;

            var payload = new byte[StorageNative.SmartDataSize];
            Buffer.BlockCopy(output, StorageNative.SendCmdOutHeaderSize, payload, 0, StorageNative.SmartDataSize);
            return payload;
        }

        private PhysicalDriveDescriptor? ReadDeviceDescriptor(SafeFileHandle handle, int index)
        {
            var descriptorBuffer = QueryProperty(handle, StorageNative.StorageDeviceProperty, 1024);
            if (descriptorBuffer == null) return null;

            var descriptor = StorageNative.BytesToStructure<StorageNative.STORAGE_DEVICE_DESCRIPTOR>(descriptorBuffer);

            return new PhysicalDriveDescriptor
            {
                Index = index,
                Vendor = StorageNative.ReadAnsiAt(descriptorBuffer, descriptor.VendorIdOffset),
                Model = StorageNative.ReadAnsiAt(descriptorBuffer, descriptor.ProductIdOffset),
                Firmware = StorageNative.ReadAnsiAt(descriptorBuffer, descriptor.ProductRevisionOffset),
                SerialNumber = StorageNative.ReadAnsiAt(descriptorBuffer, descriptor.SerialNumberOffset),
                BusType = MapBusType(descriptor.BusType),
                IsRemovable = descriptor.RemovableMedia,
                SizeBytes = ReadLength(handle),
                IncursSeekPenalty = ReadSeekPenalty(handle),
            };
        }

        private static bool? ReadSeekPenalty(SafeFileHandle handle)
        {
            var buffer = QueryProperty(handle, StorageNative.StorageDeviceSeekPenaltyProperty,
                Marshal.SizeOf(typeof(StorageNative.DEVICE_SEEK_PENALTY_DESCRIPTOR)));
            if (buffer == null) return null;

            var descriptor = StorageNative.BytesToStructure<StorageNative.DEVICE_SEEK_PENALTY_DESCRIPTOR>(buffer);
            return descriptor.IncursSeekPenalty;
        }

        private static long ReadLength(SafeFileHandle handle)
        {
            // DISK_GEOMETRY_EX : DISK_GEOMETRY (24 octets) puis DiskSize sur 64 bits.
            var buffer = new byte[32];
            if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) ||
                returned < StorageNative.DiskGeometryExSizeOffset + 8)
            {
                return 0;
            }

            return BitConverter.ToInt64(buffer, StorageNative.DiskGeometryExSizeOffset);
        }

        private static byte[]? QueryProperty(SafeFileHandle handle, int propertyId, int outputSize)
        {
            var query = new StorageNative.STORAGE_PROPERTY_QUERY
            {
                PropertyId = propertyId,
                QueryType = StorageNative.PropertyStandardQuery,
                AdditionalParameters = new byte[1],
            };

            var input = StorageNative.StructureToBytes(query);
            var output = new byte[outputSize];

            return StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_STORAGE_QUERY_PROPERTY,
                       input, input.Length, output, output.Length, out var returned, IntPtr.Zero) && returned > 0
                ? output
                : null;
        }

        private static SafeFileHandle? Open(int index, uint desiredAccess)
        {
            try
            {
                return StorageNative.CreateFileW(
                    @"\\.\PhysicalDrive" + index.ToString(CultureInfo.InvariantCulture),
                    desiredAccess,
                    StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE,
                    IntPtr.Zero, StorageNative.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return null;
            }
        }

        private static StorageBusType MapBusType(int busType) => busType switch
        {
            1 => StorageBusType.Scsi,
            2 => StorageBusType.Ata,
            3 => StorageBusType.Ata,
            7 => StorageBusType.Usb,
            8 => StorageBusType.Raid,
            10 => StorageBusType.Sas,
            11 => StorageBusType.Sata,
            12 => StorageBusType.SecureDigital,
            13 => StorageBusType.SecureDigital,
            14 => StorageBusType.Virtual,
            15 => StorageBusType.Virtual,
            17 => StorageBusType.Nvme,
            _ => StorageBusType.Unknown,
        };
    }
}
