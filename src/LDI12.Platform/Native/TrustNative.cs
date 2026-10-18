using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LDI12.Core.Programs;
using Microsoft.Win32.SafeHandles;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// La signature numérique d'un fichier, vérifiée comme le fait Process Explorer : la signature
    /// intégrée d'abord, puis les catalogues de Windows, qui signent la plupart des fichiers du
    /// système sans rien inscrire dans les fichiers eux-mêmes.
    /// </summary>
    /// <remarks>
    /// Aucune vérification en ligne de révocation : sur la machine d'un client hors connexion, une
    /// vérification qui attend le réseau figerait l'écran pour rien.
    /// </remarks>
    public static class TrustNative
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;
        private const uint RevokeNone = 0;
        private const uint ChoiceFile = 1;
        private const uint StateVerify = 1;
        private const uint StateClose = 2;
        private const uint CacheOnlyUrlRetrieval = 0x1000;
        private const uint TrustNoSignature = 0x800B0100;
        private const uint TrustSubjectFormUnknown = 0x800B0003;
        private const uint TrustProviderUnknown = 0x800B0001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CatalogInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminAcquireContext(out IntPtr admin, IntPtr subsystem, uint flags);

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr policy, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle(SafeFileHandle file, ref uint size, [In, Out] byte[]? hash, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint size, [In, Out] byte[]? hash, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, IntPtr previous);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

        /// <summary>L'état de la signature et son titulaire.</summary>
        public static (SignatureState State, string? Signer) Verify(string path)
        {
            if (!File.Exists(path)) return (SignatureState.Missing, null);

            var embedded = VerifyEmbedded(path);
            if (embedded == 0) return (SignatureState.Signed, SignerOf(path));
            if (embedded != TrustNoSignature && embedded != TrustSubjectFormUnknown && embedded != TrustProviderUnknown)
                return (SignatureState.Invalid, SignerOf(path));

            var catalog = CatalogOf(path);
            return catalog != null ? (SignatureState.Signed, SignerOf(catalog)) : (SignatureState.Unsigned, null);
        }

        private static uint VerifyEmbedded(string path)
        {
            var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = path };
            var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(file, filePointer, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = UiNone,
                    fdwRevocationChecks = RevokeNone,
                    dwUnionChoice = ChoiceFile,
                    pFile = filePointer,
                    dwStateAction = StateVerify,
                    dwProvFlags = CacheOnlyUrlRetrieval,
                };
                var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = StateClose;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(filePointer, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(filePointer);
            }
        }

        /// <summary>Le catalogue de Windows qui porte l'empreinte du fichier, s'il y en a un.</summary>
        private static string? CatalogOf(string path)
        {
            // SHA-256 depuis Windows 8, SHA-1 avant : on essaie le plus récent, puis l'ancien.
            return CatalogOf(path, "SHA256") ?? CatalogOf(path, null);
        }

        private static string? CatalogOf(string path, string? algorithm)
        {
            IntPtr admin;
            try
            {
                if (algorithm == null ? !CryptCATAdminAcquireContext(out admin, IntPtr.Zero, 0)
                                      : !CryptCATAdminAcquireContext2(out admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
                    return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null; // Windows 7 : pas de SHA-256 pour les catalogues
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                uint size = 0;
                var ok = algorithm == null
                    ? CryptCATAdminCalcHashFromFileHandle(stream.SafeFileHandle, ref size, null, 0)
                    : CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle, ref size, null, 0);
                if (size == 0 || size > 64) return null;
                var hash = new byte[size];
                ok = algorithm == null
                    ? CryptCATAdminCalcHashFromFileHandle(stream.SafeFileHandle, ref size, hash, 0)
                    : CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle, ref size, hash, 0);
                if (!ok) return null;

                var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) return null;
                try
                {
                    var info = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf(typeof(CatalogInfo)) };
                    return CryptCATCatalogInfoFromContext(catalog, ref info, 0) ? info.wszCatalogFile : string.Empty;
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }

        /// <summary>« Microsoft Windows », « Google LLC » : le nom commun du certificat.</summary>
        private static string? SignerOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var certificate = X509Certificate.CreateFromSignedFile(path);
                var subject = certificate.Subject;
                foreach (var part in subject.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(3).Trim('"');
                }
                return subject;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
