using System;
using System.Globalization;

namespace LDI12.Core.Diagnostics
{
    /// <summary>
    /// Formatage des valeurs destinées à être lues : constats, écrans de détail, rapports.
    /// </summary>
    /// <remarks>
    /// Centralisé pour que « 456,2 Go » et « 95,7 % » s'écrivent partout de la même façon. Un
    /// rapport où les unités changent de forme d'une ligne à l'autre inspire peu confiance, et
    /// le client le remarque avant le technicien.
    ///
    /// Ce type vit dans le noyau, et non dans le moteur de règles où il est né, parce qu'il a
    /// désormais deux consommateurs : les règles et les rapports. Les laisser formater chacun de
    /// leur côté garantissait qu'un constat et le tableau qui l'appuie finiraient par afficher la
    /// même mesure sous deux formes différentes, dans le même document.
    ///
    /// Le formatage est en français quelle que soit la machine, voir <see cref="Formats"/>.
    /// Ces textes sont destinés à la lecture, et les données d'échange passent par le JSON, qui
    /// reste en culture invariante.
    /// </remarks>
    public static class ValueFormat
    {
        private const long Kilo = 1024;
        private const long Mega = Kilo * 1024;
        private const long Giga = Mega * 1024;
        private const long Tera = Giga * 1024;

        public static string Bytes(long value)
        {
            if (value >= Tera) return Round((double)value / Tera) + " To";
            if (value >= Giga) return Round((double)value / Giga) + " Go";
            if (value >= Mega) return Round((double)value / Mega) + " Mo";
            if (value >= Kilo) return Round((double)value / Kilo) + " Ko";
            return value.ToString(Formats.French) + " octets";
        }

        public static string Percent(double value)
            => Round(value) + " %";

        public static string Number(long value)
            => value.ToString("N0", Formats.French);

        public static string Ms(double value)
            => Round(value) + " ms";

        public static string Celsius(int value)
            => value.ToString(Formats.French) + " °C";

        public static string Date(DateTimeOffset value)
            => value.ToString("d MMMM yyyy", Formats.French);

        public static string DateTime(DateTimeOffset value)
            => value.ToString("d MMMM yyyy 'à' HH:mm", Formats.French);

        /// <summary>Débit d'une liaison : « 6 Gb/s », « 1,5 Gb/s ».</summary>
        public static string LinkGigabits(double value)
            => value.ToString(value < 10 && value != Math.Floor(value) ? "0.0" : "0", Formats.French) + " Gb/s";

        /// <summary>Rapport entre deux mesures : « deux », « 2,5 ».</summary>
        public static string Ratio(double value)
            => value.ToString(value == Math.Floor(value) ? "0" : "0.0", Formats.French);

        public static string Mhz(int value)
            => Round(value / 1000d) + " GHz";

        /// <summary>Débit de lien, exprimé dans l'unité dans laquelle il est annoncé sur la boîte.</summary>
        public static string LinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond >= 1_000_000_000L) return Round(bitsPerSecond / 1_000_000_000d) + " Gb/s";
            if (bitsPerSecond >= 1_000_000L) return Round(bitsPerSecond / 1_000_000d) + " Mb/s";
            if (bitsPerSecond >= 1_000L) return Round(bitsPerSecond / 1_000d) + " kb/s";
            return bitsPerSecond.ToString(Formats.French) + " b/s";
        }

        /// <summary>Ancienneté en mois pleins, pour les règles portant sur des dates de pilote ou de firmware.</summary>
        public static int MonthsSince(DateTimeOffset value, DateTimeOffset now)
        {
            var months = ((now.Year - value.Year) * 12) + now.Month - value.Month;
            if (now.Day < value.Day) months--;
            return Math.Max(0, months);
        }

        public static string Years(int months)
        {
            var years = months / 12;
            var rest = months % 12;
            if (years == 0) return months + " mois";
            return rest == 0
                ? years + " an" + (years > 1 ? "s" : "")
                : years + " an" + (years > 1 ? "s" : "") + " et " + rest + " mois";
        }

        public static string Duration(TimeSpan value)
        {
            if (value.TotalDays >= 1) return (int)value.TotalDays + " jour" + ((int)value.TotalDays > 1 ? "s" : "");
            if (value.TotalHours >= 1) return (int)value.TotalHours + " heure" + ((int)value.TotalHours > 1 ? "s" : "");

            // Une durée d'action peut se compter en secondes : un vidage de cache DNS est
            // instantané. Sans ce cas, l'écran annonçait « 0 minutes », ce qui se lit comme
            // une mesure ratée plutôt que comme une opération rapide.
            if (value.TotalMinutes < 1)
                return Math.Max(1, (int)Math.Round(value.TotalSeconds)) + " seconde" +
                       ((int)Math.Round(value.TotalSeconds) > 1 ? "s" : "");

            return (int)value.TotalMinutes + " minute" + ((int)value.TotalMinutes > 1 ? "s" : "");
        }

        /// <summary>Heures de fonctionnement d'un disque, converties en années lisibles.</summary>
        public static string OperatingHours(long hours)
        {
            var years = hours / 8766d;
            if (years < 1) return Number(hours) + " heures";
            return Number(hours) + " heures (" + Round(years) + " ans de fonctionnement)";
        }

        private static string Round(double value)
            => Math.Round(value, 1).ToString("0.#", Formats.French);
    }
}
