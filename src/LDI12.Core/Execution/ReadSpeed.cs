using System;
using System.Globalization;

namespace LDI12.Core.Execution
{
    /// <summary>
    /// Ce que coûte la lecture d'un support : un prix par octet et un prix fixe par fichier.
    /// </summary>
    /// <remarks>
    /// Le pendant de <see cref="CopySpeed"/> pour un support qu'on lit sans y écrire : le disque
    /// d'une sauvegarde qu'on restaure, que la restauration s'engage à ne jamais modifier, et que
    /// sa mesure ne modifie donc pas non plus.
    /// </remarks>
    public sealed class ReadSpeed
    {
        public double ReadBytesPerSecond { get; init; }

        /// <summary>Ouverture, lecture et fermeture d'un fichier, hors transfert de ses octets.</summary>
        public TimeSpan PerFile { get; init; }

        public string? Failure { get; init; }

        public bool IsValid => Failure == null && ReadBytesPerSecond > 0;

        public string Encode()
            => ReadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture) + ";" +
               PerFile.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture);

        public static ReadSpeed? Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var parts = text!.Split(';');
            if (parts.Length != 2) return null;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var read) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var perFile) ||
                read <= 0 || perFile < 0 || double.IsInfinity(read))
                return null;

            return new ReadSpeed { ReadBytesPerSecond = read, PerFile = TimeSpan.FromMilliseconds(perFile) };
        }
    }
}
