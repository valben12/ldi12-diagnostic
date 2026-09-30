using System;
using System.Globalization;

namespace LDI12.Core.Execution
{
    /// <summary>
    /// Ce qu'une copie coûte sur un support : un prix par octet écrit, un par octet relu, et un
    /// prix fixe par fichier.
    /// </summary>
    /// <remarks>
    /// Les trois comptent, et pas dans les mêmes sauvegardes : une vidéo de quarante gigaoctets ne
    /// paie que les deux premiers, un profil de navigateur de cent mille petits fichiers surtout le
    /// troisième. Un débit seul se tromperait sur l'un ou sur l'autre, voir <c>CopyProgress</c>.
    /// </remarks>
    public sealed class CopySpeed
    {
        public double WriteBytesPerSecond { get; init; }

        public double ReadBytesPerSecond { get; init; }

        /// <summary>Création, fermeture, renommage et relecture d'un fichier, hors transfert de ses octets.</summary>
        public TimeSpan PerFile { get; init; }

        /// <summary>Pourquoi la mesure n'a pas abouti. Nul si elle a abouti.</summary>
        public string? Failure { get; init; }

        public bool IsValid => Failure == null && WriteBytesPerSecond > 0 && ReadBytesPerSecond > 0;

        /// <summary>Forme compacte, pour passer dans les paramètres d'une action.</summary>
        public string Encode()
            => WriteBytesPerSecond.ToString("R", CultureInfo.InvariantCulture) + ";" +
               ReadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture) + ";" +
               PerFile.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture);

        public static CopySpeed? Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var parts = text!.Split(';');
            if (parts.Length != 3) return null;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var write) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var read) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var perFile) ||
                write <= 0 || read <= 0 || perFile < 0 || double.IsInfinity(write) || double.IsInfinity(read))
                return null;

            return new CopySpeed
            {
                WriteBytesPerSecond = write,
                ReadBytesPerSecond = read,
                PerFile = TimeSpan.FromMilliseconds(perFile),
            };
        }
    }
}
