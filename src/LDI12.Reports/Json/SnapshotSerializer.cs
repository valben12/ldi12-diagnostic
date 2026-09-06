using System;
using System.IO;
using System.Text;
using LDI12.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace LDI12.Reports.Json
{
    /// <summary>
    /// Lecture et écriture du <see cref="SystemSnapshot"/> en JSON.
    /// </summary>
    /// <remarks>
    /// Le JSON n'est pas qu'un format d'export : c'est le format d'archivage du diagnostic, la
    /// base de la comparaison avant / après réparation, le jeu de tests du moteur de règles, et
    /// plus tard le contenu déposé dans le dossier client Klarvi. D'où le numéro de schéma dès la
    /// première version, et les énumérations écrites en toutes lettres plutôt qu'en entiers :
    /// un rapport doit rester lisible sans l'application qui l'a produit.
    /// </remarks>
    public static class SnapshotSerializer
    {
        public static JsonSerializerSettings CreateSettings(bool indented = true) => new JsonSerializerSettings
        {
            Formatting = indented ? Formatting.Indented : Formatting.None,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Local,
            Culture = System.Globalization.CultureInfo.InvariantCulture,
            Converters =
            {
                new MeasuredJsonConverter(),
                new StringEnumConverter(),
                new VersionConverter(),
            },
        };

        public static string Serialize(SystemSnapshot snapshot, bool indented = true)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return JsonConvert.SerializeObject(snapshot, CreateSettings(indented));
        }

        public static SystemSnapshot Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return JsonConvert.DeserializeObject<SystemSnapshot>(json, CreateSettings())
                   ?? throw new InvalidDataException("Le fichier ne contient pas de diagnostic exploitable.");
        }

        public static void Save(SystemSnapshot snapshot, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

            // UTF-8 sans BOM : le fichier doit rester lisible par n'importe quel outil, y compris
            // un éditeur de texte sur la machine du client.
            File.WriteAllText(path, Serialize(snapshot), new UTF8Encoding(false));
        }

        public static SystemSnapshot Load(string path)
        {
            var snapshot = Deserialize(File.ReadAllText(path, Encoding.UTF8));
            if (snapshot.Metadata.SchemaVersion > SystemSnapshot.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Ce diagnostic a été produit par une version plus récente de LDI12 Diagnostic " +
                    "(schéma " + snapshot.Metadata.SchemaVersion + ", cette version lit le schéma " +
                    SystemSnapshot.CurrentSchemaVersion + ").");
            }
            return snapshot;
        }
    }
}
