using System;
using System.Collections.Concurrent;
using System.Reflection;
using LDI12.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.Reports.Json
{
    /// <summary>
    /// Sérialise un <see cref="Measured{T}"/> en conservant l'état de disponibilité, la provenance
    /// et la raison d'une absence.
    /// </summary>
    /// <remarks>
    /// Une mesure disponible s'écrit sous sa forme la plus courte possible (la valeur nue)
    /// pour que le JSON reste lisible par un humain. Dès qu'une mesure est partielle, absente ou
    /// nécessite une élévation, elle s'écrit sous forme d'objet portant sa justification.
    /// C'est ce qui permet de relire un rapport et de savoir <b>pourquoi</b> une donnée manque.
    /// </remarks>
    public sealed class MeasuredJsonConverter : JsonConverter
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> RehydrateCache =
            new ConcurrentDictionary<Type, MethodInfo>();

        private static readonly MethodInfo RehydrateDefinition =
            typeof(Measured).GetMethod(nameof(Measured.Rehydrate), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Measured.Rehydrate est introuvable.");

        public override bool CanConvert(Type objectType)
            => objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(Measured<>);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is not IMeasured measured)
            {
                writer.WriteNull();
                return;
            }

            if (measured.Availability == Availability.Available)
            {
                serializer.Serialize(writer, measured.RawValue);
                return;
            }

            writer.WriteStartObject();

            if (measured.HasValue)
            {
                writer.WritePropertyName("value");
                serializer.Serialize(writer, measured.RawValue);
            }

            writer.WritePropertyName("availability");
            writer.WriteValue(measured.Availability.ToString());

            if (measured.Source != DataSource.Unknown)
            {
                writer.WritePropertyName("source");
                writer.WriteValue(measured.Source.ToString());
            }

            if (!string.IsNullOrEmpty(measured.Reason))
            {
                writer.WritePropertyName("reason");
                writer.WriteValue(measured.Reason);
            }

            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            var valueType = objectType.GetGenericArguments()[0];
            var rehydrate = RehydrateCache.GetOrAdd(valueType, t => RehydrateDefinition.MakeGenericMethod(t));

            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Null)
            {
                return rehydrate.Invoke(null, new object?[]
                {
                    null, Availability.Unavailable, DataSource.Unknown, "Valeur absente du rapport.", DateTimeOffset.MinValue,
                })!;
            }

            // Forme courte : la valeur nue, donc une mesure disponible.
            if (token.Type != JTokenType.Object || token["availability"] == null)
            {
                return rehydrate.Invoke(null, new object?[]
                {
                    token.ToObject(valueType, serializer), Availability.Available, DataSource.Unknown, null, DateTimeOffset.MinValue,
                })!;
            }

            var payload = (JObject)token;
            var availability = Parse(payload.Value<string>("availability"), Availability.Unknown);
            var source = Parse(payload.Value<string>("source"), DataSource.Unknown);
            var reason = payload.Value<string>("reason");
            var raw = payload["value"];

            return rehydrate.Invoke(null, new object?[]
            {
                raw?.ToObject(valueType, serializer), availability, source, reason, DateTimeOffset.MinValue,
            })!;
        }

        private static TEnum Parse<TEnum>(string? text, TEnum fallback) where TEnum : struct
            => Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
