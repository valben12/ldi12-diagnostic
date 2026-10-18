using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using LDI12.Core.Programs;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// L'identité d'un exécutable : éditeur, description, version, et signature vérifiée.
    /// Retenue par chemin et date de modification : un fichier remplacé est revérifié.
    /// </summary>
    public sealed class FileIdentitySource : IFileIdentitySource
    {
        private readonly ConcurrentDictionary<string, (DateTime Written, FileIdentity Identity)> _cache =
            new ConcurrentDictionary<string, (DateTime, FileIdentity)>(StringComparer.OrdinalIgnoreCase);

        public FileIdentity Identify(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new FileIdentity { Path = path ?? string.Empty, Signature = SignatureState.Missing };

            DateTime written;
            try
            {
                if (!File.Exists(path)) return new FileIdentity { Path = path, Signature = SignatureState.Missing };
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return new FileIdentity { Path = path, Signature = SignatureState.Missing };
            }

            if (_cache.TryGetValue(path, out var known) && known.Written == written) return known.Identity;

            string? company = null, description = null, version = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                company = Clean(info.CompanyName);
                description = Clean(info.FileDescription);
                version = Clean(info.FileVersion);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }

            var (state, signer) = Safe(() => TrustNative.Verify(path));
            var identity = new FileIdentity
            {
                Path = path,
                Exists = true,
                Company = company,
                Description = description,
                Version = version,
                Signature = state,
                Signer = signer,
            };
            _cache[path] = (written, identity);
            return identity;
        }

        private static (SignatureState, string?) Safe(Func<(SignatureState, string?)> verify)
        {
            try { return verify(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is System.Runtime.InteropServices.ExternalException)
            {
                return (SignatureState.Unknown, null);
            }
        }

        private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }
}
