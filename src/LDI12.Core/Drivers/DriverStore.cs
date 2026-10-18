using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LDI12.Core.Drivers
{
    /// <summary>Ce qu'un paquet du magasin de pilotes devient au nettoyage.</summary>
    public enum PackageStatus
    {
        /// <summary>Un périphérique présent s'en sert : on n'y touche pas.</summary>
        InUse,

        /// <summary>La version la plus récente de ce pilote, sans périphérique branché : gardée (matériel débranché).</summary>
        Latest,

        /// <summary>Remplacé par une version plus récente du même pilote, et inutilisé : c'est ce qu'on supprime.</summary>
        OldVersion,
    }

    /// <summary>Un paquet ajouté au magasin de pilotes (« oem12.inf »).</summary>
    public sealed class DriverPackage
    {
        /// <summary>Le nom publié : « oem12.inf ».</summary>
        public string PublishedName { get; init; } = string.Empty;

        /// <summary>Le nom d'origine du fichier : « nvlddmkm.inf », d'après son dossier dans le magasin.</summary>
        public string? OriginalName { get; init; }

        /// <summary>Le dossier du paquet dans FileRepository.</summary>
        public string? Folder { get; init; }

        public string? Provider { get; init; }

        public string? DeviceClass { get; init; }

        public string? Version { get; init; }

        public DateTime? Date { get; init; }

        public long SizeBytes { get; init; }

        /// <summary>Le premier matériel que l'INF déclare équiper.</summary>
        public string? Device { get; init; }

        /// <summary>Les périphériques présents qui l'utilisent.</summary>
        public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();

        public bool InUse => UsedBy.Count > 0;

        public PackageStatus Status { get; set; }

        /// <summary>« nvlddmkm.inf · NVIDIA », ce qui regroupe les versions successives d'un même pilote.</summary>
        public string Family => ((OriginalName ?? Device ?? PublishedName) + "|" + (Provider ?? string.Empty) + "|" + (DeviceClass ?? string.Empty)).ToLowerInvariant();
    }

    /// <summary>
    /// Range les paquets comme le fait le « nettoyage intelligent » de Driver Store Explorer :
    /// pour chaque pilote, la version la plus récente reste, celles qui servent restent, les
    /// versions plus anciennes et inutilisées sont proposées à la suppression.
    /// </summary>
    /// <remarks>
    /// Un paquet seul et inutilisé n'est jamais proposé : c'est souvent le pilote d'une
    /// imprimante ou d'un téléphone débranché, et l'effacer obligerait à le réinstaller au
    /// prochain branchement.
    /// </remarks>
    public static class DriverStoreAnalyzer
    {
        public static void Classify(IReadOnlyList<DriverPackage> packages)
        {
            foreach (var family in packages.GroupBy(p => p.Family))
            {
                var newest = family.OrderByDescending(p => p.Date ?? DateTime.MinValue)
                                   .ThenByDescending(p => ParseVersion(p.Version))
                                   .First();
                foreach (var package in family)
                    package.Status = package.InUse
                        ? PackageStatus.InUse
                        : ReferenceEquals(package, newest) || IsNotOlder(package, newest)
                            ? PackageStatus.Latest
                            : PackageStatus.OldVersion;
            }
        }

        /// <summary>Même date et même version que le plus récent : ce n'est pas une version plus ancienne.</summary>
        private static bool IsNotOlder(DriverPackage package, DriverPackage newest)
            => (package.Date ?? DateTime.MinValue) == (newest.Date ?? DateTime.MinValue) &&
               ParseVersion(package.Version) == ParseVersion(newest.Version);

        public static Version ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Version(0, 0);
            var parts = text!.Split('.').Select(p => int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 0).Take(4).ToList();
            while (parts.Count < 2) parts.Add(0);
            return parts.Count switch
            {
                2 => new Version(parts[0], parts[1]),
                3 => new Version(parts[0], parts[1], parts[2]),
                _ => new Version(parts[0], parts[1], parts[2], parts[3]),
            };
        }

        /// <summary>Le nom de la classe de pilote, en français.</summary>
        public static string ClassLabel(string? deviceClass) => (deviceClass ?? string.Empty).ToLowerInvariant() switch
        {
            "display" => "Affichage",
            "media" => "Son et vidéo",
            "net" => "Réseau",
            "bluetooth" => "Bluetooth",
            "usb" => "USB",
            "hidclass" => "Périphérique d'entrée",
            "keyboard" => "Clavier",
            "mouse" => "Souris",
            "printer" => "Imprimante",
            "printqueue" => "File d'impression",
            "system" => "Système",
            "scsiadapter" => "Contrôleur de stockage",
            "hdc" => "Contrôleur de disque",
            "diskdrive" => "Disque",
            "camera" => "Caméra",
            "image" => "Image",
            "biometric" => "Biométrie",
            "sensor" => "Capteur",
            "extension" => "Extension",
            "softwarecomponent" => "Composant logiciel",
            "firmware" => "Micrologiciel",
            "monitor" => "Moniteur",
            "audioendpoint" => "Sortie audio",
            "ports" => "Ports",
            "wpd" => "Appareil portable",
            "smartcardreader" => "Lecteur de cartes",
            "" => "Classe inconnue",
            _ => deviceClass!,
        };
    }
}
