using System;
using System.Text;
using LDI12.Core.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// La clé des mots de passe d'un navigateur Chromium, emportée d'un Windows à l'autre.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi les mots de passe se perdaient.</b> Chrome, Edge et les autres chiffrent les
    /// mots de passe et les cookies d'un profil par une clé à eux, rangée dans « Local State »,
    /// elle-même chiffrée par Windows pour le compte (DPAPI). Recopiée sur un autre Windows, cette
    /// clé ne s'ouvre plus : tout ce qu'elle protège est perdu, même si les fichiers sont là.
    /// <para>
    /// La sauvegarde, qui tourne dans la session du client, demande donc à Windows de déchiffrer
    /// la clé, et la dépose dans la sauvegarde. La restauration la rechiffre pour le compte du
    /// nouveau PC et la remet dans « Local State » : les profils restaurés retrouvent leurs mots
    /// de passe. Les cookies protégés en plus par le navigateur lui-même (Chrome 127 et suivants)
    /// ne suivent pas : il faudra se reconnecter à certains sites.
    /// </para>
    /// <para>
    /// La clé déposée ouvre les mots de passe de la sauvegarde : comme les clés Wi-Fi, elle n'est
    /// emportée que si le technicien le demande, et elle est à effacer une fois la machine rendue.
    /// </para>
    /// </remarks>
    public static class BrowserKeys
    {
        public const string FileName = "cle-mots-de-passe.txt";

        private const string DpapiPrefix = "DPAPI";

        /// <summary>La clé en clair, ou nul si elle n'a pas pu être déchiffrée par ce compte.</summary>
        public static byte[]? Extract(ISecretProtector secrets, string localState)
        {
            string? encoded;
            try
            {
                encoded = (string?)JObject.Parse(localState).SelectToken("os_crypt.encrypted_key");
            }
            catch (JsonException)
            {
                return null;
            }

            if (string.IsNullOrEmpty(encoded)) return null;

            byte[] wrapped;
            try { wrapped = Convert.FromBase64String(encoded); }
            catch (FormatException) { return null; }

            var prefix = Encoding.ASCII.GetBytes(DpapiPrefix);
            if (wrapped.Length <= prefix.Length) return null;
            for (var index = 0; index < prefix.Length; index++)
                if (wrapped[index] != prefix[index]) return null;

            var blob = new byte[wrapped.Length - prefix.Length];
            Buffer.BlockCopy(wrapped, prefix.Length, blob, 0, blob.Length);
            return secrets.Unprotect(blob);
        }

        /// <summary>Le fichier déposé dans la sauvegarde : une explication, puis la clé.</summary>
        public static string Document(byte[] key)
            => "# Clé des mots de passe de ce navigateur. Elle ouvre les mots de passe de cette sauvegarde :\r\n" +
               "# à effacer une fois la machine rendue au client.\r\n" +
               Convert.ToBase64String(key) + "\r\n";

        public static byte[]? Read(string? document)
        {
            if (document == null) return null;
            foreach (var raw in document.Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                try
                {
                    var key = Convert.FromBase64String(line);
                    return key.Length > 0 ? key : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// « Local State » avec la clé rechiffrée pour ce compte, ou nul si Windows a refusé.
        /// </summary>
        /// <remarks>
        /// Seule la clé change. La clé propre au navigateur (« app_bound_encrypted_key »), elle,
        /// est celle de cette installation : elle est gardée.
        /// </remarks>
        public static string? Install(ISecretProtector secrets, string localState, byte[] key)
        {
            var blob = secrets.Protect(key);
            if (blob == null) return null;

            JObject state;
            try { state = JObject.Parse(localState); }
            catch (JsonException) { return null; }

            var prefix = Encoding.ASCII.GetBytes(DpapiPrefix);
            var wrapped = new byte[prefix.Length + blob.Length];
            Buffer.BlockCopy(prefix, 0, wrapped, 0, prefix.Length);
            Buffer.BlockCopy(blob, 0, wrapped, prefix.Length, blob.Length);

            if (state["os_crypt"] is not JObject crypto)
            {
                crypto = new JObject();
                state["os_crypt"] = crypto;
            }

            crypto["encrypted_key"] = Convert.ToBase64String(wrapped);
            return state.ToString(Formatting.None);
        }
    }
}
