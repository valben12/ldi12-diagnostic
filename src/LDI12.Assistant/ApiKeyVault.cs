using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LDI12.Assistant
{
    /// <summary>Où la clé est rangée, pour le dire au technicien.</summary>
    public enum ApiKeyLocation
    {
        None = 0,

        /// <summary>À côté du logiciel, chiffrée pour ce support : elle suit la clé USB d'un poste à l'autre.</summary>
        Portable = 1,

        /// <summary>Dans le profil Windows, protégée par DPAPI : quand le dossier du logiciel n'est pas inscriptible.</summary>
        Profile = 2,
    }

    /// <summary>
    /// La clé d'API Claude du technicien : saisie une fois, retrouvée à chaque lancement, jamais
    /// livrée avec le logiciel.
    /// </summary>
    /// <remarks>
    /// <b>Elle n'est pas dans l'exécutable.</b> Le logiciel publié ne contient aucune clé : celui
    /// qui le télécharge saisit la sienne. Celle du technicien vit dans un fichier à part, posé à
    /// côté de l'exécutable sur sa clé USB ; elle le suit d'un poste client à l'autre sans jamais
    /// être retapée.
    /// <para>
    /// <b>Chiffrée pour ce support.</b> AES-256, authentifiée par HMAC-SHA256, avec une clé
    /// dérivée (PBKDF2) d'un secret du logiciel et du numéro de série du volume qui porte le
    /// fichier. Copié sur un autre support, le fichier ne se déchiffre plus : une copie de la clé
    /// USB faite à la va-vite, ou le dossier envoyé à quelqu'un, n'emporte pas la clé avec lui.
    /// Ce n'est pas un coffre contre qui détient la clé USB elle-même : celui-là a le logiciel et
    /// le support, donc tout ce qu'il faut ; la protection vise la copie, pas le vol.
    /// </para>
    /// <para>
    /// Quand le dossier du logiciel n'est pas inscriptible (Program Files, partage en lecture
    /// seule), la clé va dans le profil Windows, protégée par DPAPI pour ce compte.
    /// </para>
    /// </remarks>
    public sealed class ApiKeyVault
    {
        public const string FileName = "LDI12-Claude.cle";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LDI12CLE1");

        // Le secret du logiciel : il ne protège rien seul (il est dans l'exécutable), il rend
        // seulement le fichier inutilisable par un autre programme qui connaîtrait le format.
        private static readonly byte[] AppSecret = Encoding.UTF8.GetBytes("LDI12 Diagnostic · clé Claude · Laguiole");

        private const int Iterations = 120_000;

        private readonly string _portablePath;
        private readonly string _profilePath;
        private readonly Func<string, string?> _volumeIdentity;

        public ApiKeyVault(string softwareFolder, string profileFolder, Func<string, string?>? volumeIdentity = null)
        {
            _portablePath = Path.Combine(softwareFolder ?? throw new ArgumentNullException(nameof(softwareFolder)), FileName);
            _profilePath = Path.Combine(profileFolder ?? throw new ArgumentNullException(nameof(profileFolder)), FileName);
            _volumeIdentity = volumeIdentity ?? VolumeSerial;
        }

        /// <summary>Le coffre du logiciel en cours : à côté de l'exécutable, ou dans le profil.</summary>
        public static ApiKeyVault Default()
            => new ApiKeyVault(
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LDI12 Diagnostic"));

        public ApiKeyLocation Location
            => File.Exists(_portablePath) ? ApiKeyLocation.Portable
                : File.Exists(_profilePath) ? ApiKeyLocation.Profile
                : ApiKeyLocation.None;

        /// <summary>La clé, ou null si aucune n'est rangée ou si le fichier ne se déchiffre pas ici.</summary>
        public string? Read()
        {
            try
            {
                if (File.Exists(_portablePath))
                {
                    var key = DecryptPortable(File.ReadAllBytes(_portablePath), _volumeIdentity(_portablePath));
                    if (key != null) return key;
                }
                if (File.Exists(_profilePath))
                {
                    var data = ProtectedData.Unprotect(File.ReadAllBytes(_profilePath), AppSecret, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(data);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException)
            {
            }
            return null;
        }

        /// <summary>Range la clé : à côté du logiciel si possible, sinon dans le profil.</summary>
        public ApiKeyLocation Save(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Clé vide.", nameof(apiKey));
            apiKey = apiKey.Trim();

            var volume = _volumeIdentity(_portablePath);
            if (volume != null)
            {
                try
                {
                    WriteAtomically(_portablePath, EncryptPortable(apiKey, volume));
                    TryDelete(_profilePath);
                    return ApiKeyLocation.Portable;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            WriteAtomically(_profilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), AppSecret, DataProtectionScope.CurrentUser));
            return ApiKeyLocation.Profile;
        }

        /// <summary>Oublie la clé, où qu'elle soit rangée.</summary>
        public void Forget()
        {
            TryDelete(_portablePath);
            TryDelete(_profilePath);
        }

        /// <summary>« sk-ant-…4f2a » : assez pour reconnaître sa clé, pas assez pour s'en servir.</summary>
        public static string Mask(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) return string.Empty;
            var tail = apiKey.Length > 4 ? apiKey.Substring(apiKey.Length - 4) : apiKey;
            var head = apiKey.StartsWith("sk-ant-", StringComparison.Ordinal) ? "sk-ant-" : string.Empty;
            return head + "…" + tail;
        }

        internal static byte[] EncryptPortable(string apiKey, string volume)
        {
            var salt = Random(16);
            var (encryption, authentication) = Derive(volume, salt);

            using var aes = Aes.Create();
            aes.Key = encryption;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            byte[] cipher;
            using (var encryptor = aes.CreateEncryptor())
            {
                var plain = Encoding.UTF8.GetBytes(apiKey);
                cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            }

            using var stream = new MemoryStream();
            stream.Write(Magic, 0, Magic.Length);
            stream.Write(salt, 0, salt.Length);
            stream.Write(aes.IV, 0, aes.IV.Length);
            stream.Write(cipher, 0, cipher.Length);

            using var hmac = new HMACSHA256(authentication);
            var tag = hmac.ComputeHash(stream.ToArray());
            stream.Write(tag, 0, tag.Length);
            return stream.ToArray();
        }

        internal static string? DecryptPortable(byte[] data, string? volume)
        {
            const int Header = 9 + 16 + 16;
            if (volume == null || data.Length < Header + 16 + 32) return null;
            for (var index = 0; index < Magic.Length; index++)
                if (data[index] != Magic[index]) return null;

            var salt = new byte[16];
            Array.Copy(data, Magic.Length, salt, 0, 16);
            var (encryption, authentication) = Derive(volume, salt);

            var signedLength = data.Length - 32;
            using (var hmac = new HMACSHA256(authentication))
            {
                var expected = hmac.ComputeHash(data, 0, signedLength);
                var difference = 0;
                for (var index = 0; index < 32; index++) difference |= expected[index] ^ data[signedLength + index];
                // Autre support, fichier retouché : on s'arrête là, sans rien déchiffrer.
                if (difference != 0) return null;
            }

            var iv = new byte[16];
            Array.Copy(data, Magic.Length + 16, iv, 0, 16);
            using var aes = Aes.Create();
            aes.Key = encryption;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(data, Header, signedLength - Header);
            return Encoding.UTF8.GetString(plain);
        }

        private static (byte[] Encryption, byte[] Authentication) Derive(string volume, byte[] salt)
        {
            var secret = new byte[AppSecret.Length + Encoding.UTF8.GetByteCount(volume)];
            Array.Copy(AppSecret, secret, AppSecret.Length);
            Encoding.UTF8.GetBytes(volume, 0, volume.Length, secret, AppSecret.Length);

            using var derive = new Rfc2898DeriveBytes(secret, salt, Iterations);
            return (derive.GetBytes(32), derive.GetBytes(32));
        }

        private static byte[] Random(int length)
        {
            var bytes = new byte[length];
            using var generator = RandomNumberGenerator.Create();
            generator.GetBytes(bytes);
            return bytes;
        }

        private static void WriteAtomically(string path, byte[] content)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Le numéro de série du volume qui porte ce fichier, ou null s'il ne se lit pas.</summary>
        private static string? VolumeSerial(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return null;
                if (!root.EndsWith("\\", StringComparison.Ordinal)) root += "\\";
                return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0)
                    ? serial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is DllNotFoundException
                                       || ex is EntryPointNotFoundException || ex is PathTooLongException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationW(
            string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
            out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
    }
}
