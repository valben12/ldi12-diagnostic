using System;
using System.Security.Cryptography;

namespace LDI12.Core.Updates
{
    /// <summary>Ce que vaut la signature d'un manifeste.</summary>
    public enum SignatureVerdict
    {
        /// <summary>Signée par la clé que porte cet exécutable.</summary>
        Valid,

        /// <summary>
        /// Signée par une clé que cet exécutable ne connaît pas.
        /// </summary>
        /// <remarks>
        /// À distinguer soigneusement d'une signature fausse : « cette version du logiciel ne
        /// connaît pas la clé du serveur » désigne un exécutable trop ancien, et se répare en
        /// téléchargeant à la main. « Signature invalide » désignerait quelqu'un qui s'interpose,
        /// et n'appelle pas du tout la même réaction.
        /// </remarks>
        UnknownKey,

        /// <summary>La signature ne correspond pas aux octets signés.</summary>
        Invalid,

        /// <summary>Signature absente, tronquée, ou non déchiffrable depuis sa forme base64.</summary>
        Malformed,

        /// <summary>La vérification elle-même n'a pas pu avoir lieu sur cette machine.</summary>
        Unavailable,
    }

    /// <summary>
    /// La clé publique du serveur, compilée dans l'exécutable, et la vérification qu'elle permet.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi signer un échange déjà chiffré.</b> Ce logiciel tourne chez des clients dont
    /// le réseau ne nous appartient pas : filtrage d'entreprise, antivirus qui déchiffre le
    /// trafic, borne Wi-Fi d'hôtel. HTTPS protège du voisin de table, pas de la machine qui
    /// s'interpose légitimement au milieu. La signature déplace la confiance du transport vers
    /// cette clé-ci, et l'empreinte du fichier téléchargé fait le reste.
    /// <para>
    /// <b>La clé publiée par le serveur ne sert jamais ici.</b> Qui peut servir un faux manifeste
    /// peut servir une fausse clé : l'adresse <c>/api/updates/v1/public-key</c> n'existe que pour
    /// récupérer la clé une fois, au moment d'écrire ce fichier. Celle qui fait foi est celle-ci.
    /// </para>
    /// </remarks>
    public static class UpdateSignature
    {
        /// <summary>
        /// Identifiant de la clé : les seize premiers caractères de l'empreinte SHA-256 de sa
        /// forme SPKI. Il permet de dire « je ne connais pas cette clé » plutôt que « signature
        /// invalide ».
        /// </summary>
        public const string KeyId = "753c6075c5f3df96";

        /// <summary>
        /// Module RSA-2048 de la clé publique, en base64, 256 octets.
        /// </summary>
        /// <remarks>
        /// Stocké sous cette forme et non en PEM parce que
        /// <c>RSA.ImportSubjectPublicKeyInfo</c> n'existe pas en .NET Framework : elle est
        /// arrivée avec .NET Core 3.0. Reconstruire la clé à partir de ses paramètres est la
        /// seule voie disponible ici.
        /// </remarks>
        private const string ModulusBase64 =
            "xc5K5TqxljPAVSzC+m2muzgEMep1u0JKRI9w6a2QbdI97KmZglCqMcNdwOro8YdVXpjjstytGr5wdneCPy11" +
            "tTGCI7AzekB/yFmMldArG4WwKLx6KndNdGQhusSk/DcWVf5W2cRVpaCkYbivIDUEczCe3Qt2OUAVFcmilnvn" +
            "L5xRHMnNk+QKJbCGJKMZWYzEI5pfSL9UrK4FXsxg49yABFrCsiBJokT1fLvtWG0PwMBSLrKg3ffI8ACgZ94+" +
            "9FwL3HpEI5B6B1tzRiHA+3swViTQS4e0evmfv7dXqj/iKpbAD0REfqX4sThBvjYaW3t3CX9HSIi+RYWbHQma" +
            "Z6pWfw==";

        /// <summary>65537, l'exposant public habituel.</summary>
        private static readonly byte[] Exponent = { 0x01, 0x00, 0x01 };

        /// <summary>
        /// Vérifie une signature sur les octets qui ont été signés.
        /// </summary>
        /// <param name="payload">Les octets exacts signés par le serveur, déjà décodés.</param>
        /// <param name="signature">La signature, déjà décodée.</param>
        /// <param name="keyId">L'identifiant de clé annoncé par le serveur.</param>
        public static SignatureVerdict Verify(byte[]? payload, byte[]? signature, string? keyId)
        {
            if (payload == null || payload.Length == 0) return SignatureVerdict.Malformed;
            if (signature == null || signature.Length == 0) return SignatureVerdict.Malformed;

            // L'identifiant est comparé avant la cryptographie : distinguer les deux cas n'a de
            // valeur que si l'on regarde le bon en premier.
            if (!string.Equals(keyId, KeyId, StringComparison.OrdinalIgnoreCase))
                return SignatureVerdict.UnknownKey;

            try
            {
                return VerifyWithCng(payload, signature)
                    ? SignatureVerdict.Valid
                    : SignatureVerdict.Invalid;
            }
            catch (Exception ex) when (IsCryptoFailure(ex))
            {
                // RSACng peut manquer sur une installation abîmée : le fournisseur historique
                // sait faire le même travail, à condition d'être construit correctement.
                try
                {
                    return VerifyWithCsp(payload, signature)
                        ? SignatureVerdict.Valid
                        : SignatureVerdict.Invalid;
                }
                catch (Exception fallback) when (IsCryptoFailure(fallback))
                {
                    return SignatureVerdict.Unavailable;
                }
            }
        }

        /// <summary>Une signature encodée en base64, décodée sans lever d'exception.</summary>
        public static byte[]? Decode(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;

            try
            {
                return Convert.FromBase64String(base64!.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>Phrase d'explication d'un verdict, écrite pour être affichée telle quelle.</summary>
        public static string Describe(SignatureVerdict verdict)
        {
            switch (verdict)
            {
                case SignatureVerdict.Valid:
                    return "La réponse du serveur est authentique.";
                case SignatureVerdict.UnknownKey:
                    return "Cette version du logiciel ne connaît pas la clé du serveur : elle est trop " +
                           "ancienne. Téléchargez la dernière version depuis ldi12.fr.";
                case SignatureVerdict.Invalid:
                    return "La réponse reçue n'est pas celle du serveur de LDI12. Quelque chose s'interpose " +
                           "sur ce réseau : rien n'est proposé au téléchargement.";
                case SignatureVerdict.Malformed:
                    return "La réponse du serveur est incomplète : rien n'est proposé au téléchargement.";
                default:
                    return "La signature n'a pas pu être vérifiée sur cette machine : rien n'est proposé " +
                           "au téléchargement.";
            }
        }

        /// <summary>
        /// Vérification par CNG, disponible de Vista à Windows 11.
        /// </summary>
        /// <remarks>
        /// C'est la voie recommandée précisément parce qu'elle évite le piège du fournisseur
        /// historique, décrit dans <see cref="VerifyWithCsp"/>.
        /// </remarks>
        private static bool VerifyWithCng(byte[] payload, byte[] signature)
        {
            using (var rsa = new RSACng())
            {
                rsa.ImportParameters(PublicKey());
                return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        /// <summary>
        /// Vérification par le fournisseur historique, en repli.
        /// </summary>
        /// <remarks>
        /// <b>Le piège qui rend ce repli délicat.</b> Un <c>RSACryptoServiceProvider</c> construit
        /// sans précaution utilise <c>PROV_RSA_FULL</c>, qui ne connaît que SHA-1 : la
        /// vérification en SHA-256 échoue alors sur une exception peu parlante, et sur une
        /// machine où tout est pourtant en ordre. Le numéro 24 désigne <c>PROV_RSA_AES</c>, qui
        /// sait faire.
        /// </remarks>
        private static bool VerifyWithCsp(byte[] payload, byte[] signature)
        {
            var parameters = new CspParameters(24) { Flags = CspProviderFlags.UseMachineKeyStore };

            using (var rsa = new RSACryptoServiceProvider(parameters))
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(PublicKey());
                return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        private static RSAParameters PublicKey() => new RSAParameters
        {
            Modulus = Convert.FromBase64String(ModulusBase64),
            Exponent = Exponent,
        };

        private static bool IsCryptoFailure(Exception ex)
            => ex is CryptographicException || ex is PlatformNotSupportedException ||
               ex is TypeInitializationException || ex is NotSupportedException ||
               ex is DllNotFoundException || ex is EntryPointNotFoundException;
    }
}
