using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LDI12.Core.Execution
{
    /// <summary>
    /// Une instance WMI réduite à ses propriétés, sans aucun type de System.Management.
    /// Le reste de l'application ignore donc que WMI existe, et les tests n'en ont pas besoin.
    /// </summary>
    public sealed class WmiRecord
    {
        private readonly IReadOnlyDictionary<string, object?> _properties;

        public WmiRecord(IReadOnlyDictionary<string, object?> properties)
            => _properties = properties ?? throw new ArgumentNullException(nameof(properties));

        public IReadOnlyDictionary<string, object?> Properties => _properties;

        public object? this[string name] => _properties.TryGetValue(name, out var v) ? v : null;

        public string? GetString(string name)
        {
            var value = this[name];
            if (value == null) return null;
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }

        public ulong? GetUInt64(string name) => TryConvert(name, v => Convert.ToUInt64(v, CultureInfo.InvariantCulture));

        public long? GetInt64(string name) => TryConvert(name, v => Convert.ToInt64(v, CultureInfo.InvariantCulture));

        public int? GetInt32(string name) => TryConvert(name, v => Convert.ToInt32(v, CultureInfo.InvariantCulture));

        public uint? GetUInt32(string name) => TryConvert(name, v => Convert.ToUInt32(v, CultureInfo.InvariantCulture));

        public bool? GetBoolean(string name) => TryConvert(name, v => Convert.ToBoolean(v, CultureInfo.InvariantCulture));

        public string[]? GetStringArray(string name) => this[name] as string[];

        /// <summary>
        /// Convertit un DMTF datetime WMI (« 20240115143000.000000+060 ») en DateTimeOffset.
        /// </summary>
        public DateTimeOffset? GetDmtfDate(string name)
        {
            var raw = GetString(name);
            if (raw == null || raw.Length < 21) return null;
            try
            {
                var year = int.Parse(raw.Substring(0, 4), CultureInfo.InvariantCulture);
                var month = int.Parse(raw.Substring(4, 2), CultureInfo.InvariantCulture);
                var day = int.Parse(raw.Substring(6, 2), CultureInfo.InvariantCulture);
                var hour = int.Parse(raw.Substring(8, 2), CultureInfo.InvariantCulture);
                var minute = int.Parse(raw.Substring(10, 2), CultureInfo.InvariantCulture);
                var second = int.Parse(raw.Substring(12, 2), CultureInfo.InvariantCulture);
                var offsetMinutes = int.Parse(raw.Substring(21), CultureInfo.InvariantCulture);
                if (year < 1601) return null;
                return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetMinutes));
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException || ex is OverflowException)
            {
                return null;
            }
        }

        private T? TryConvert<T>(string name, Func<object, T> convert) where T : struct
        {
            var value = this[name];
            if (value == null) return null;
            try { return convert(value); }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Résultat d'une requête WMI. Une requête qui échoue ne lève pas : elle renvoie
    /// un résultat en échec, avec sa raison rédigée pour être affichée.
    /// </summary>
    public sealed class WmiQueryResult
    {
        public static WmiQueryResult Ok(IReadOnlyList<WmiRecord> records, TimeSpan duration)
            => new WmiQueryResult { Records = records, Succeeded = true, Duration = duration };

        public static WmiQueryResult Failure(string reason, TimeSpan duration, Exception? ex = null)
            => new WmiQueryResult { Reason = reason, Duration = duration, Exception = ex };

        public static WmiQueryResult NamespaceMissing(string @namespace)
            => new WmiQueryResult { Reason = "Espace de noms WMI absent sur cette version de Windows : " + @namespace };

        public bool Succeeded { get; private init; }

        public IReadOnlyList<WmiRecord> Records { get; private init; } = Array.Empty<WmiRecord>();

        public string? Reason { get; private init; }

        public TimeSpan Duration { get; private init; }

        public Exception? Exception { get; private init; }

        public WmiRecord? First => Records.Count > 0 ? Records[0] : null;
    }

    /// <summary>
    /// État d'un espace de noms WMI.
    /// </summary>
    /// <remarks>
    /// La distinction entre <see cref="Absent"/> et <see cref="AccessDenied"/> est essentielle :
    /// dire « cette machine n'a pas de TPM » alors qu'on n'a simplement pas les droits de le lire,
    /// c'est produire un diagnostic faux. Un accès refusé se corrige par une élévation ; une
    /// absence, non.
    /// </remarks>
    public enum WmiNamespaceState
    {
        /// <summary>L'espace de noms n'existe pas sur cette version de Windows.</summary>
        Absent = 0,

        /// <summary>Présent et interrogeable.</summary>
        Present = 1,

        /// <summary>Présent, mais inaccessible sans privilèges administrateur.</summary>
        AccessDenied = 2,
    }

    /// <summary>
    /// Accès WMI. Porte les délais maximaux et convertit tout échec en résultat exploitable.
    /// </summary>
    /// <remarks>
    /// Un appel WMI peut se bloquer sans possibilité d'annulation : un CancellationToken ne
    /// libère pas un thread coincé dans un RPC. Les requêtes connues comme dangereuses tournent
    /// dans LDI12.ProbeHost, un processus que l'on peut tuer.
    /// <para>
    /// <c>Win32_Product</c> est interdit : son énumération déclenche une reconfiguration MSI de
    /// chaque logiciel installé. La passerelle rejette la requête, et un test unitaire vérifie
    /// qu'aucune occurrence n'apparaît dans le code.
    /// </para>
    /// </remarks>
    public interface IWmiGateway
    {
        Task<WmiQueryResult> QueryAsync(
            string @namespace,
            string query,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        /// <summary>
        /// Teste un espace de noms sans exécuter de requête coûteuse, en distinguant
        /// l'absence du refus d'accès.
        /// </summary>
        Task<WmiNamespaceState> ProbeNamespaceAsync(string @namespace, CancellationToken cancellationToken);

        /// <summary>Raccourci : vrai uniquement si l'espace de noms est présent ET accessible.</summary>
        Task<bool> NamespaceExistsAsync(string @namespace, CancellationToken cancellationToken);

        /// <summary>Teste l'existence d'une classe via meta_class, bien plus rapide qu'une vraie requête.</summary>
        Task<bool> ClassExistsAsync(string @namespace, string className, CancellationToken cancellationToken);
    }

    /// <summary>Espaces de noms WMI utilisés par l'application.</summary>
    public static class WmiNamespaces
    {
        public const string CimV2 = @"root\CIMV2";
        public const string Wmi = @"root\WMI";
        public const string Storage = @"root\Microsoft\Windows\Storage";
        public const string SecurityCenter2 = @"root\SecurityCenter2";
        public const string Defender = @"root\Microsoft\Windows\Defender";
        public const string Tpm = @"root\CIMV2\Security\MicrosoftTpm";
        public const string StandardCimV2 = @"root\StandardCimv2";
    }
}
