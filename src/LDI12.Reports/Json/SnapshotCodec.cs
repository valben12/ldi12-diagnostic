using System;
using LDI12.Core.Probes;
using Newtonsoft.Json;

namespace LDI12.Reports.Json
{
    /// <summary>
    /// Sérialisation des sections du relevé, avec les réglages de l'archivage.
    /// </summary>
    /// <remarks>
    /// Les mêmes réglages que <see cref="SnapshotSerializer"/>, et c'est tout l'intérêt : une
    /// mesure absente qui traverse une frontière de processus doit arriver absente, avec la
    /// raison de son absence. Un sérialiseur par défaut la rendrait présente et nulle : c'est-à-
    /// dire un zéro affirmé, exactement ce que ce logiciel refuse de faire.
    /// </remarks>
    public sealed class SnapshotCodec : ISnapshotCodec
    {
        private readonly JsonSerializerSettings _settings = SnapshotSerializer.CreateSettings(indented: false);

        public string Serialize(object value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return JsonConvert.SerializeObject(value, _settings);
        }

        public object? Deserialize(string json, Type type)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (type == null) throw new ArgumentNullException(nameof(type));

            return JsonConvert.DeserializeObject(json, type, _settings);
        }
    }
}
