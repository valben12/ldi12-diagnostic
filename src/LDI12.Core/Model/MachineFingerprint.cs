using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Identité de la machine, déduite de ce que les sondes ont déjà relevé.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi une déduction et non une sonde.</b> Tout ce qu'il faut est déjà mesuré par la
    /// sonde carte mère et la sonde disques : identifiant SMBIOS, numéro de série de la carte,
    /// numéro de série du disque système. Ajouter une sonde pour relire les mêmes valeurs aurait
    /// coûté une seconde d'analyse pour rien, et l'empreinte hérite ainsi de l'état de
    /// disponibilité de ses sources plutôt que d'en inventer un.
    ///
    /// <b>Une seule source à la fois, jamais un mélange.</b> La conception d'origine prévoyait un
    /// condensé de l'identifiant SMBIOS, des numéros de série des disques et des adresses MAC.
    /// C'est une mauvaise idée : mélanger les sources fait changer l'empreinte dès qu'un seul
    /// élément bouge. Un disque remplacé, une carte Wi-Fi USB retirée, et l'historique de la
    /// machine se coupe en deux : exactement le jour où l'on voudrait comparer un avant et un
    /// après. Les sources sont donc essayées dans l'ordre de leur stabilité, et la première
    /// disponible sert seule.
    ///
    /// Les adresses MAC sont écartées de bout en bout : une station d'accueil, un adaptateur
    /// USB ou un client VPN en ajoutent et en retirent, et Windows sait tirer au sort celle des
    /// cartes sans fil.
    /// </remarks>
    public static class MachineFingerprint
    {
        /// <summary>
        /// Identifiants SMBIOS que des cartes entières partagent : les traiter comme uniques
        /// rapprocherait deux machines différentes sous la même empreinte.
        /// </summary>
        private static readonly string[] SharedUuids =
        {
            "00000000-0000-0000-0000-000000000000",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            "03000200-0400-0500-0006-000700080009",
        };

        public static MachineIdentity Describe(
            string machineName, string userName, HardwareSnapshot hardware, StorageSnapshot storage)
        {
            if (hardware == null) throw new ArgumentNullException(nameof(hardware));
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            var board = hardware.Motherboard;

            return new MachineIdentity
            {
                MachineName = machineName ?? string.Empty,
                UserName = userName ?? string.Empty,
                Manufacturer = Prefer(board.SystemManufacturer, board.Manufacturer),
                Model = Prefer(board.SystemModel, board.Model),
                SerialNumber = board.SerialNumber,
                Fingerprint = Compute(board, storage),
            };
        }

        /// <summary>
        /// Empreinte stable, ou l'explication de son absence.
        /// </summary>
        /// <remarks>
        /// Un condensé et non les valeurs elles-mêmes : le diagnostic archivé porte alors de quoi
        /// rapprocher deux passages sans porter le numéro de série de la machine du client une
        /// seconde fois.
        /// </remarks>
        private static Measured<string> Compute(MotherboardInfo board, StorageSnapshot storage)
        {
            var uuid = Usable(board.SystemUuid);
            if (uuid != null && !IsShared(uuid))
                return Measured.Ok(Hash("U", uuid), DataSource.Inferred);

            var serial = Usable(board.SerialNumber);
            if (serial != null)
                return Measured.Ok(Hash("B", serial), DataSource.Inferred);

            var disk = SystemDiskSerial(storage);
            if (disk != null)
            {
                return Measured.Partial(Hash("D", disk), DataSource.Inferred,
                    "Établie sur le seul numéro de série du disque système : le remplacement du " +
                    "disque suffirait à la rendre différente.");
            }

            return Measured.Missing<string>(
                "Ni l'identifiant système, ni le numéro de série de la carte mère, ni celui du " +
                "disque système n'ont pu être lus : deux diagnostics de cette machine ne pourront " +
                "être rapprochés que par son nom.");
        }

        private static string? SystemDiskSerial(StorageSnapshot storage)
        {
            foreach (var disk in storage.PhysicalDisks)
                if (disk.IsSystemDisk.Or(false)) return Usable(disk.SerialNumber);
            return null;
        }

        /// <summary>Valeur exploitable : mesurée, non vide, et pas une suite de zéros.</summary>
        private static string? Usable(Measured<string> measured)
        {
            if (!measured.HasValue) return null;

            var value = measured.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            foreach (var character in value!)
                if (character != '0' && character != '-' && character != ' ') return value;

            return null;
        }

        private static bool IsShared(string uuid)
        {
            var comparable = uuid.ToLowerInvariant();
            foreach (var shared in SharedUuids)
                if (comparable == shared) return true;
            return false;
        }

        /// <summary>
        /// Condensé tronqué à seize caractères : assez pour distinguer les machines d'un atelier
        /// sans allonger inutilement chaque ligne d'historique.
        /// </summary>
        private static string Hash(string prefix, string value)
        {
            using (var algorithm = SHA256.Create())
            {
                var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(prefix + ":" + value.ToUpperInvariant()));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++) builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static Measured<string> Prefer(Measured<string> first, Measured<string> second)
            => first.IsReliable ? first : second;
    }
}
