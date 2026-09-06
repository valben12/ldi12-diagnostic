using System;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Collectors.Internal
{
    /// <summary>
    /// Conversion d'un enregistrement WMI en <see cref="Measured{T}"/>.
    /// </summary>
    /// <remarks>
    /// Une propriété absente de l'instance n'est pas une erreur : beaucoup de champs WMI ne sont
    /// renseignés que sur certaines versions de Windows ou certains matériels. Ces aides
    /// produisent donc systématiquement une mesure absente <b>motivée</b>, plutôt qu'un zéro.
    /// </remarks>
    internal static class Measure
    {
        /// <summary>
        /// Valeurs de remplissage que les fabricants laissent dans le SMBIOS. Les traiter comme
        /// des données réelles ferait afficher « Numéro de série : To be filled by O.E.M. » dans
        /// un rapport client.
        /// </summary>
        private static readonly string[] OemPlaceholders =
        {
            "to be filled by o.e.m.", "to be filled by oem", "default string", "system serial number",
            "system manufacturer", "system product name", "base board version", "not specified",
            "not applicable", "none", "unknown", "o.e.m.", "oem", "chassis serial number",
            "0123456789", "123456789", "xxxxxxx", "invalid", "empty",
        };

        public static Measured<string> Text(WmiRecord? record, string property, string label)
        {
            var value = Clean(record?.GetString(property));
            return value == null
                ? Measured.Missing<string>(Absent(label), DataSource.Wmi)
                : Measured.Ok(value, DataSource.Wmi);
        }

        /// <summary>Renvoie null si la valeur est vide ou n'est qu'un texte de remplissage SMBIOS.</summary>
        public static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value!.Trim();
            var comparable = trimmed.ToLowerInvariant();
            foreach (var placeholder in OemPlaceholders)
                if (comparable == placeholder) return null;
            return trimmed;
        }

        public static Measured<int> Int32(WmiRecord? record, string property, string label)
        {
            var value = record?.GetInt32(property);
            return value == null
                ? Measured.Missing<int>(Absent(label), DataSource.Wmi)
                : Measured.Ok(value.Value, DataSource.Wmi);
        }

        public static Measured<long> Int64(WmiRecord? record, string property, string label)
        {
            var value = record?.GetInt64(property);
            return value == null
                ? Measured.Missing<long>(Absent(label), DataSource.Wmi)
                : Measured.Ok(value.Value, DataSource.Wmi);
        }

        public static Measured<long> Bytes(WmiRecord? record, string property, string label)
        {
            var value = record?.GetUInt64(property);
            return value == null
                ? Measured.Missing<long>(Absent(label), DataSource.Wmi)
                : Measured.Ok((long)value.Value, DataSource.Wmi);
        }

        public static Measured<bool> Bool(WmiRecord? record, string property, string label)
        {
            var value = record?.GetBoolean(property);
            return value == null
                ? Measured.Missing<bool>(Absent(label), DataSource.Wmi)
                : Measured.Ok(value.Value, DataSource.Wmi);
        }

        public static Measured<DateTimeOffset> DmtfDate(WmiRecord? record, string property, string label)
        {
            var value = record?.GetDmtfDate(property);
            return value == null
                ? Measured.Missing<DateTimeOffset>(Absent(label), DataSource.Wmi)
                : Measured.Ok(value.Value, DataSource.Wmi);
        }

        public static Measured<string> Registry(string? value, string label)
            => string.IsNullOrWhiteSpace(value)
                ? Measured.Missing<string>(Absent(label), DataSource.Registry)
                : Measured.Ok(value!.Trim(), DataSource.Registry);

        /// <summary>Propage l'échec d'une requête à un champ, avec la raison réelle de l'échec.</summary>
        public static Measured<T> FromFailure<T>(WmiQueryResult result, string label)
            => Measured.Missing<T>(
                label + " : " + (result.Reason ?? "la requête système a échoué."), DataSource.Wmi);

        public static string Absent(string label)
            => label + " n'est pas renseigné par cette machine.";

        /// <summary>Formate un entier sans dépendre de la culture, pour les libellés techniques.</summary>
        public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
