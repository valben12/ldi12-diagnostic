using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// Où se trouvent les données d'un compte : celui de la session ouverte, ou celui d'un autre
    /// Windows, sur un disque branché.
    /// </summary>
    /// <remarks>
    /// <b>La même sauvegarde, d'où qu'elle vienne.</b> Pour le compte ouvert, Windows dit où sont
    /// ses dossiers, y compris quand ils ont été déplacés. Pour un compte d'un autre Windows (le
    /// disque d'un PC en panne, branché à l'atelier), personne ne le dit : ce sont les
    /// emplacements par défaut, sous le dossier du profil. Un Bureau ou des Documents déplacés dans
    /// OneDrive sont couverts par la copie du dossier OneDrive lui-même.
    /// </remarks>
    public sealed class ProfileRoots
    {
        /// <summary>Le dossier du profil : « C:\Users\Marie », « E:\Users\Marie ».</summary>
        public string Profile { get; init; } = string.Empty;

        public string LocalAppData { get; init; } = string.Empty;

        public string RoamingAppData { get; init; } = string.Empty;

        public string Documents { get; init; } = string.Empty;

        /// <summary>Les dossiers personnels copiés, avec leur nom dans la sauvegarde.</summary>
        public IReadOnlyList<(string Label, string Path)> Personal { get; init; } = Array.Empty<(string, string)>();

        /// <summary>Faux pour la session ouverte : ses processus, son Wi-Fi et winget sont là.</summary>
        public bool IsOffline { get; init; }

        public static ProfileRoots Current() => new ProfileRoots
        {
            Profile = Special(Environment.SpecialFolder.UserProfile),
            LocalAppData = Special(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Special(Environment.SpecialFolder.ApplicationData),
            Documents = Special(Environment.SpecialFolder.MyDocuments),
            Personal = UserDataSurveyor.PersonalFolders(),
        };

        /// <summary>Le compte d'un autre Windows, par ses emplacements par défaut.</summary>
        public static ProfileRoots Offline(string profile)
        {
            var root = profile.TrimEnd('\\');
            return new ProfileRoots
            {
                Profile = root,
                LocalAppData = Path.Combine(root, @"AppData\Local"),
                RoamingAppData = Path.Combine(root, @"AppData\Roaming"),
                Documents = Path.Combine(root, "Documents"),
                IsOffline = true,

                // Les mêmes noms que pour la session ouverte : la restauration ne voit aucune différence.
                Personal = new[]
                {
                    ("Bureau", Path.Combine(root, "Desktop")),
                    ("Documents", Path.Combine(root, "Documents")),
                    ("Images", Path.Combine(root, "Pictures")),
                    ("Musique", Path.Combine(root, "Music")),
                    ("Vidéos", Path.Combine(root, "Videos")),
                    ("Téléchargements", Path.Combine(root, "Downloads")),
                    ("OneDrive", Path.Combine(root, "OneDrive")),
                },
            };
        }

        private static string Special(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is ArgumentException) { return string.Empty; }
        }
    }
}
