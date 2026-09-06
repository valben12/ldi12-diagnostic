using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LDI12.Core.Platform;
using LDI12.Platform.Native;

namespace LDI12.Platform.Detection
{
    /// <summary>
    /// Détermine si le processus courant dispose de privilèges administrateur, et surtout
    /// <b>comment</b> il les a obtenus.
    /// </summary>
    /// <remarks>
    /// La distinction compte : un processus administrateur sans élévation signifie que l'UAC est
    /// désactivé ou que la session tourne sous le compte Administrateur intégré. C'est un constat
    /// de sécurité à remonter au technicien, pas un simple détail technique.
    /// </remarks>
    public static class ElevationDetector
    {
        public static ElevationState Detect()
        {
            bool isAdministrator;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return ElevationState.Unknown;
            }

            if (!isAdministrator) return ElevationState.NotElevated;

            return GetElevationType() switch
            {
                NativeMethods.TOKEN_ELEVATION_TYPE.Full => ElevationState.Elevated,
                // Jeton non filtré alors que le compte est administrateur : UAC désactivé
                // ou compte Administrateur intégré.
                NativeMethods.TOKEN_ELEVATION_TYPE.Default => ElevationState.ElevatedByDefault,
                _ => ElevationState.NotElevated,
            };
        }

        private static NativeMethods.TOKEN_ELEVATION_TYPE? GetElevationType()
        {
            var token = IntPtr.Zero;
            var buffer = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY, out token))
                    return null;

                buffer = Marshal.AllocHGlobal(sizeof(int));
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevationType, buffer, sizeof(int), out _))
                    return null;

                return (NativeMethods.TOKEN_ELEVATION_TYPE)Marshal.ReadInt32(buffer);
            }
            catch (Exception ex) when (ex is OutOfMemoryException || ex is EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            }
        }
    }
}
