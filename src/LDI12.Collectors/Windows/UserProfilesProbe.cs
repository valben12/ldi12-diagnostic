using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Les comptes qui ont ouvert une session ici, et l'état de leurs profils.
    /// </summary>
    /// <remarks>
    /// <b>Trois plaintes d'atelier différentes tiennent dans ce seul relevé.</b> « J'ai tout
    /// perdu » : Windows a mis un profil de côté et en a créé un neuf, le dossier d'origine étant
    /// intact à côté. « La machine rame », trois sessions verrouillées gardent chacune leur
    /// mémoire. « Le disque est plein » : quatre profils d'anciens salariés que personne n'a
    /// supprimés.
    /// <para>
    /// Tout se lit dans une clé de registre accessible sans privilèges, et dans la liste des
    /// ruches chargées. Aucune élévation, aucun outil externe.
    /// </para>
    /// </remarks>
    public sealed class UserProfilesProbe : IDiagnosticProbe
    {
        private const string ProfileList =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        /// <summary>
        /// Suffixe que Windows ajoute à la clé d'un profil qu'il n'a pas su charger.
        /// </summary>
        /// <remarks>
        /// Le mécanisme complet : la clé <c>&lt;SID&gt;</c> est renommée <c>&lt;SID&gt;.bak</c>,
        /// une clé neuve est créée, et l'utilisateur ouvre sa session sur un bureau vide. Ses
        /// documents n'ont pas bougé d'un octet.
        /// </remarks>
        private const string SetAsideSuffix = ".bak";

        /// <summary>Dossier que Windows attribue à une session dont le profil ne se charge pas.</summary>
        private const string TemporaryFolder = "TEMP";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.UserProfiles,
            DisplayName = "Comptes et profils",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var keys = context.Registry.GetSubKeyNames(RegistryHive.LocalMachine, ProfileList);
            if (keys.Count == 0)
                return Task.FromResult(ProbeOutcome.Failed(
                    "La liste des profils utilisateurs n'a pas pu être lue."));

            var loaded = LoadedHives(context);
            var currentSid = CurrentSid();
            var currentPath = CurrentPath();

            // Une session dont le profil n'a pas pu être chargé travaille dans un dossier
            // provisoire. C'est le chemin réel de la session en cours qui le dit, pas le registre :
            // le registre décrit ce que le profil devrait être, pas ce qu'il est devenu.
            var currentIsTemporary = IsTemporaryPath(currentPath);

            var profiles = new List<UserProfile>();

            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var setAside = key.EndsWith(SetAsideSuffix, StringComparison.OrdinalIgnoreCase);
                var sid = setAside ? key.Substring(0, key.Length - SetAsideSuffix.Length) : key;

                if (!IsUserSid(sid)) continue;

                var path = context.Registry.ReadString(
                    RegistryHive.LocalMachine, ProfileList + "\\" + key, "ProfileImagePath") ?? string.Empty;

                var exists = path.Length > 0 && SafeExists(path);
                var isCurrent = !setAside && string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase);

                profiles.Add(new UserProfile
                {
                    Sid = sid,
                    AccountName = ResolveName(sid),
                    Path = path,
                    FolderExists = path.Length == 0
                        ? Measured.Missing<bool>("Aucun dossier n'est enregistré pour ce profil.")
                        : Measured.Ok(exists, DataSource.FileSystem),
                    LastUsed = ReadLastUse(context, ProfileList + "\\" + key),
                    Loaded = !setAside && loaded.Contains(sid),
                    IsCurrent = isCurrent,
                    State = Classify(setAside, exists, path, isCurrent && currentIsTemporary),
                });
            }

            context.Draft.SetProfiles(new ProfileInventory
            {
                Profiles = profiles,
                LoadedCount = Measured.Ok(CountLoaded(profiles), DataSource.Registry),
            });

            return Task.FromResult(Summarize(profiles));
        }

        private static ProbeOutcome Summarize(IReadOnlyList<UserProfile> profiles)
        {
            if (profiles.Count == 0)
                return ProbeOutcome.Partial("Aucun profil d'utilisateur n'a été trouvé sur cette machine.");

            int setAside = 0, orphaned = 0, temporary = 0, loaded = 0;
            foreach (var profile in profiles)
            {
                if (profile.State == ProfileState.SetAside) setAside++;
                if (profile.State == ProfileState.Orphaned) orphaned++;
                if (profile.State == ProfileState.Temporary) temporary++;
                if (profile.Loaded) loaded++;
            }

            var parts = new List<string> { profiles.Count + " profil(s)", loaded + " session(s) ouverte(s)" };
            if (temporary > 0) parts.Add("profil provisoire en cours d'usage");
            if (setAside > 0) parts.Add(setAside + " profil(s) mis de côté");
            if (orphaned > 0) parts.Add(orphaned + " profil(s) sans dossier");

            return ProbeOutcome.Ok(string.Join(", ", parts));
        }

        // ================================================================= lectures

        /// <summary>
        /// Les ruches chargées, qui disent quelles sessions sont ouvertes.
        /// </summary>
        /// <remarks>
        /// Une session verrouillée reste une session ouverte : son profil est chargé, sa mémoire
        /// occupée, ses programmes en cours. C'est la réponse la moins coûteuse à « pourquoi
        /// cette machine rame alors que personne ne s'en sert ».
        /// </remarks>
        private static HashSet<string> LoadedHives(ProbeContext context)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in context.Registry.GetSubKeyNames(RegistryHive.Users, string.Empty))
            {
                // Les ruches de classes doublent chaque SID : les compter ferait deux sessions
                // pour un seul utilisateur.
                if (name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsUserSid(name)) found.Add(name);
            }

            return found;
        }

        private static int CountLoaded(IReadOnlyList<UserProfile> profiles)
        {
            var count = 0;
            foreach (var profile in profiles) if (profile.Loaded) count++;
            return count;
        }

        /// <summary>Date de la dernière fermeture de session, en deux moitiés de FILETIME.</summary>
        private static Measured<DateTimeOffset> ReadLastUse(ProbeContext context, string key)
        {
            var high = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, "LocalProfileUnloadTimeHigh");
            var low = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, "LocalProfileUnloadTimeLow");

            if (high == null || low == null)
                return Measured.Missing<DateTimeOffset>(
                    "Ce profil n'a jamais été fermé proprement, ou Windows n'en a pas gardé la date.");

            var ticks = ((long)(uint)high.Value << 32) | (uint)low.Value;
            if (ticks <= 0)
                return Measured.Missing<DateTimeOffset>("La date de dernière utilisation est vide.");

            try
            {
                return Measured.Ok(DateTimeOffset.FromFileTime(ticks), DataSource.Registry);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Measured.Missing<DateTimeOffset>("La date enregistrée n'est pas exploitable.");
            }
        }

        /// <summary>
        /// Nom du compte derrière un identifiant de sécurité.
        /// </summary>
        /// <remarks>
        /// La traduction passe par l'autorité locale, qui interroge le contrôleur de domaine pour
        /// un compte de domaine. Sur une machine qui ne le joint plus, elle échoue, et c'est une
        /// information en soi, pas une panne du relevé : le motif d'absence le dit plutôt que
        /// d'afficher l'identifiant brut comme s'il s'agissait d'un nom.
        /// </remarks>
        private static Measured<string> ResolveName(string sid)
        {
            try
            {
                var account = (NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount));
                var name = account.Value;

                var separator = name.LastIndexOf('\\');
                return Measured.Ok(separator >= 0 ? name.Substring(separator + 1) : name, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is ArgumentException ||
                                       ex is SystemException)
            {
                return Measured.Missing<string>(
                    "Aucun compte ne porte cet identifiant : il a été supprimé, ou il appartient à un " +
                    "domaine que cette machine ne joint plus.");
            }
        }

        private static ProfileState Classify(bool setAside, bool exists, string path, bool temporary)
        {
            if (temporary) return ProfileState.Temporary;
            if (setAside) return ProfileState.SetAside;
            if (path.Length > 0 && IsTemporaryPath(path)) return ProfileState.Temporary;
            if (!exists) return ProfileState.Orphaned;
            return ProfileState.Normal;
        }

        /// <summary>Un profil dont le dossier s'appelle « TEMP » n'est pas un profil, c'est un sursis.</summary>
        private static bool IsTemporaryPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var leaf = Path.GetFileName(path!.TrimEnd(Path.DirectorySeparatorChar));
            return string.Equals(leaf, TemporaryFolder, StringComparison.OrdinalIgnoreCase) ||
                   leaf.StartsWith(TemporaryFolder + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Identifiant d'un compte d'utilisateur, par opposition à ceux des services de Windows.
        /// </summary>
        /// <remarks>
        /// S-1-5-18, -19 et -20 sont le système et ses deux services : ils ont un profil comme
        /// les autres et n'intéressent personne. S-1-5-21 désigne un compte local ou de domaine,
        /// S-1-12-1 un compte Microsoft ou Azure AD.
        /// </remarks>
        private static bool IsUserSid(string sid)
            => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
               sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

        private static string? CurrentSid()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? CurrentPath()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SafeExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException)
            {
                return false;
            }
        }
    }
}
