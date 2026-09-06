using System;
using LDI12.Core.Diagnostics;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Raccourci d'écriture vers <see cref="ValueFormat"/>, conservé pour la concision des 57
    /// règles qui l'appellent à chaque ligne.
    /// </summary>
    /// <remarks>
    /// Le formatage lui-même a été remonté dans le noyau : les rapports en ont besoin autant que
    /// les règles, et deux implémentations auraient fini par afficher la même mesure sous deux
    /// formes dans le même document. Ce type ne contient donc plus aucune logique : s'il devait
    /// en reprendre, c'est que la règle serait au mauvais endroit.
    /// </remarks>
    internal static class Fmt
    {
        public static string Bytes(long value) => ValueFormat.Bytes(value);

        public static string Percent(double value) => ValueFormat.Percent(value);

        public static string Number(long value) => ValueFormat.Number(value);

        public static string Ms(double value) => ValueFormat.Ms(value);

        public static string Celsius(int value) => ValueFormat.Celsius(value);

        public static string Speed(double gigabitsPerSecond) => ValueFormat.LinkGigabits(gigabitsPerSecond);

        public static string Ratio(double value) => ValueFormat.Ratio(value);

        public static string Date(DateTimeOffset value) => ValueFormat.Date(value);

        public static int MonthsSince(DateTimeOffset value, DateTimeOffset now)
            => ValueFormat.MonthsSince(value, now);

        public static string Years(int months) => ValueFormat.Years(months);

        public static string Duration(TimeSpan value) => ValueFormat.Duration(value);
    }
}
