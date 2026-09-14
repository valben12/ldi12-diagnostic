using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// Fusionne le fichier « Local State » d'un navigateur Chromium.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi ne pas simplement le recopier.</b> Ce fichier porte deux choses sans rapport :
    /// la liste des profils, avec leurs noms et leurs avatars, et la clé qui chiffre les mots de
    /// passe et les cookies, elle-même protégée par Windows pour l'ancienne installation. Recopié
    /// tel quel, il ramènerait une clé que la nouvelle installation ne peut pas lire, à la place
    /// de celle qu'elle vient de créer.
    /// <para>
    /// La fusion garde donc le fichier de la machine quand il existe, avec sa clé, et n'y ajoute
    /// que la description des profils restaurés. Quand le navigateur n'a jamais été lancé, le
    /// fichier de la sauvegarde est repris sans sa clé : le navigateur en crée une neuve au
    /// premier lancement.
    /// </para>
    /// </remarks>
    internal static class ChromiumLocalState
    {
        private const string CryptoSection = "os_crypt";

        /// <summary>Le contenu à écrire, ou nul si la sauvegarde n'est pas un JSON lisible.</summary>
        public static string? Merge(string backup, string? current, IReadOnlyList<string> profiles)
        {
            JObject saved;
            try
            {
                saved = JObject.Parse(backup);
            }
            catch (JsonException)
            {
                return null;
            }

            JObject result;
            if (current == null)
            {
                result = (JObject)saved.DeepClone();
                result.Remove(CryptoSection);
                return result.ToString(Formatting.None);
            }

            try
            {
                result = JObject.Parse(current);
            }
            catch (JsonException)
            {
                // Un fichier illisible sur la machine ne vaut pas mieux qu'un fichier absent.
                result = (JObject)saved.DeepClone();
                result.Remove(CryptoSection);
                return result.ToString(Formatting.None);
            }

            if (saved.SelectToken("profile.info_cache") is JObject savedCache)
            {
                if (result["profile"] is not JObject profile)
                {
                    profile = new JObject();
                    result["profile"] = profile;
                }

                if (profile["info_cache"] is not JObject cache)
                {
                    cache = new JObject();
                    profile["info_cache"] = cache;
                }

                foreach (var name in profiles)
                    if (savedCache[name] != null) cache[name] = savedCache[name]!.DeepClone();

                var order = profile["profiles_order"] as JArray ?? new JArray();
                foreach (var name in profiles)
                {
                    var known = false;
                    foreach (var entry in order)
                        if (string.Equals((string?)entry, name, StringComparison.Ordinal)) { known = true; break; }
                    if (!known && savedCache[name] != null) order.Add(name);
                }

                profile["profiles_order"] = order;

                if (saved.SelectToken("profile.last_used") is JValue lastUsed) profile["last_used"] = lastUsed.DeepClone();
            }

            return result.ToString(Formatting.None);
        }
    }
}
