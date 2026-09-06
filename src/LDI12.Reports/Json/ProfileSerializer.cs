using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace LDI12.Reports.Json
{
    /// <summary>
    /// Lecture et écriture du profil de diagnostic (barème et seuils).
    /// </summary>
    /// <remarks>
    /// Le profil vit dans <c>LDI12.Engine</c>, qui ne dépend d'aucune bibliothèque tierce : sa
    /// sérialisation est donc ici. Un technicien exporte le profil par défaut, ajuste un seuil,
    /// et le repasse à l'outil, sans recompiler quoi que ce soit.
    /// </remarks>
    public static class ProfileSerializer
    {
        private static JsonSerializerSettings Settings => new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Culture = System.Globalization.CultureInfo.InvariantCulture,
            Converters = { new StringEnumConverter() },
        };

        public static string Serialize<TProfile>(TProfile profile) where TProfile : class
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return JsonConvert.SerializeObject(profile, Settings);
        }

        public static TProfile Load<TProfile>(string path) where TProfile : class
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonConvert.DeserializeObject<TProfile>(json, Settings)
                   ?? throw new InvalidDataException(
                       "Le fichier « " + Path.GetFileName(path) + " » ne contient pas de profil de diagnostic exploitable.");
        }

        public static void Save<TProfile>(TProfile profile, string path) where TProfile : class
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
            File.WriteAllText(path, Serialize(profile), new UTF8Encoding(false));
        }
    }
}
