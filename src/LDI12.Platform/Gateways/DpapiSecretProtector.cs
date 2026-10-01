using System.Security.Cryptography;
using LDI12.Core.Execution;

namespace LDI12.Platform.Gateways
{
    /// <summary>DPAPI, pour le compte sous lequel tourne ce processus.</summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        public byte[]? Unprotect(byte[] data)
        {
            try
            {
                return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        public byte[]? Protect(byte[] data)
        {
            try
            {
                return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
