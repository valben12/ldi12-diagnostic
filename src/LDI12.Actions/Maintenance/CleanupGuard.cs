using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Actions.Maintenance
{
    /// <summary>
    /// Ce qu'un nettoyage n'a pas le droit de prendre pour racine, et ce qu'il n'a pas le droit
    /// de supprimer.
    /// </summary>
    /// <remarks>
    /// Le garde-fou est volontairement du calcul de chaînes, sans accès disque : il est donc
    /// testable en entier, et un test peut lui présenter les cas qu'on espère ne jamais voir
    /// en production. Une erreur de racine dans un fournisseur de nettoyage ne se rattrape pas
    /// après coup : les fichiers sont partis.
    /// </remarks>
    public static class CleanupGuard
    {
        /// <summary>
        /// Dossiers interdits comme racine : ni eux-mêmes, ni aucun de leurs ancêtres.
        /// </summary>
        /// <remarks>
        /// L'interdiction porte sur les ancêtres et pas seulement sur l'égalité : refuser
        /// <c>C:\Users\Paul\Documents</c> mais accepter <c>C:\Users\Paul</c> ne protégerait rien,
        /// puisque le balayage descend.
        /// </remarks>
        public static IReadOnlyList<string> DefaultForbidden()
        {
            var folders = new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.StartMenu,
                Environment.SpecialFolder.Favorites,
            };

            var forbidden = new List<string>();
            foreach (var folder in folders)
            {
                string path;
                try
                {
                    path = Environment.GetFolderPath(folder);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(path)) forbidden.Add(path);
            }

            // Le dossier Téléchargements n'a pas de SpecialFolder avant .NET Core : il se
            // déduit du profil, et il contient précisément ce qu'un client ne pardonne pas
            // qu'on supprime.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) forbidden.Add(Path.Combine(profile, "Downloads"));

            return forbidden;
        }

        public static bool IsAcceptableRoot(string? root, out string? reason)
            => IsAcceptableRoot(root, DefaultForbidden(), out reason);

        public static bool IsAcceptableRoot(string? root, IReadOnlyList<string> forbidden, out string? reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(root))
            {
                reason = "Racine vide.";
                return false;
            }

            // « C: » n'est pas la racine du volume C mais le dossier courant sur ce volume :
            // Path.GetFullPath le résoudrait en silence vers le répertoire de travail de
            // l'application. Une racine relative à un lecteur est refusée avant toute résolution.
            var raw = root!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (raw.Length == 2 && raw[1] == ':')
            {
                reason = "Une racine de volume ne désigne pas un dossier nettoyable.";
                return false;
            }

            string full;
            try
            {
                full = Normalize(Path.GetFullPath(root!));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                reason = "Chemin invalide.";
                return false;
            }

            var driveRoot = Normalize(Path.GetPathRoot(full) ?? string.Empty);
            if (driveRoot.Length == 0 || string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "La racine d'un volume ne peut pas être nettoyée.";
                return false;
            }

            foreach (var candidate in forbidden)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                string other;
                try
                {
                    other = Normalize(Path.GetFullPath(candidate));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
                {
                    continue;
                }

                if (string.Equals(full, other, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "« " + full + " » est un dossier système ou personnel.";
                    return false;
                }

                if (Contains(full, other))
                {
                    reason = "« " + full + " » contient « " + other + " ».";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Vrai si <paramref name="path"/> est bien situé sous <paramref name="root"/>.</summary>
        public static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                return Contains(Normalize(Path.GetFullPath(root)), Normalize(Path.GetFullPath(path)));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Comparaison par segments, et non par préfixe de chaîne : <c>C:\Temp2</c> commence par
        /// <c>C:\Temp</c> sans être dedans.
        /// </summary>
        private static bool Contains(string ancestor, string descendant)
            => descendant.Length > ancestor.Length &&
               descendant.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase) &&
               (descendant[ancestor.Length] == Path.DirectorySeparatorChar ||
                descendant[ancestor.Length] == Path.AltDirectorySeparatorChar);

        private static string Normalize(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Un chemin de volume nu perd son séparateur au trim : « C: » n'est pas « C:\ ».
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
    }
}
