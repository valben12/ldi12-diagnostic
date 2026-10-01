namespace LDI12.Core.Execution
{
    /// <summary>
    /// Le chiffrement de Windows pour le compte ouvert (DPAPI).
    /// </summary>
    /// <remarks>
    /// Ce que Windows chiffre pour un compte ne se déchiffre que par ce compte, sur cette
    /// installation : c'est le cas de la clé des mots de passe des navigateurs Chromium. La
    /// sauvegarde la déchiffre dans la session du client, la restauration la rechiffre pour le
    /// compte du nouveau PC.
    /// </remarks>
    public interface ISecretProtector
    {
        /// <summary>Nul si les données ne sont pas lisibles par ce compte.</summary>
        byte[]? Unprotect(byte[] data);

        /// <summary>Nul si Windows a refusé de chiffrer.</summary>
        byte[]? Protect(byte[] data);
    }

    /// <summary>Aucun chiffrement disponible : tout est refusé.</summary>
    public sealed class NoSecretProtector : ISecretProtector
    {
        public static readonly NoSecretProtector Instance = new NoSecretProtector();

        public byte[]? Unprotect(byte[] data) => null;

        public byte[]? Protect(byte[] data) => null;
    }
}
