using System;
using System.IO;

namespace LDI12.Core.Programs
{
    /// <summary>
    /// Retrouve le fichier qu'une commande de démarrage lance : guillemets, variables
    /// d'environnement, chemins à la façon du noyau (« \SystemRoot\… », « \??\C:\… »),
    /// rundll32 et ses DLL, extensions omises.
    /// </summary>
    public static class ImagePaths
    {
        /// <summary>Le fichier lancé, ou nul si la commande n'en désigne pas.</summary>
        /// <param name="command">La commande telle qu'écrite dans le registre ou la tâche.</param>
        /// <param name="windows">Le dossier de Windows (« C:\Windows »).</param>
        /// <param name="exists">Teste l'existence d'un fichier ; sert à retrouver une extension omise.</param>
        public static string? Extract(string? command, string windows, Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var text = Environment.ExpandEnvironmentVariables(command!.Trim());
            var system32 = Join(windows, "System32");

            string first;
            string rest;
            if (text[0] == '"')
            {
                var end = text.IndexOf('"', 1);
                first = end > 1 ? text.Substring(1, end - 1) : text.Trim('"');
                rest = end > 1 && end + 1 < text.Length ? text.Substring(end + 1).Trim() : string.Empty;
            }
            else
            {
                // Sans guillemets, un chemin avec espaces s'arrête à la première extension connue.
                var cut = FirstExtensionEnd(text);
                first = cut > 0 ? text.Substring(0, cut) : (text.IndexOf(' ') is var space && space > 0 ? text.Substring(0, space) : text);
                rest = cut > 0 && cut < text.Length ? text.Substring(cut).Trim() : string.Empty;
            }

            first = Normalize(first, windows, system32);

            // rundll32 lance une DLL : c'est elle qui compte.
            var name = Path.GetFileName(first);
            if (string.Equals(name, "rundll32.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "rundll32", StringComparison.OrdinalIgnoreCase))
            {
                var dll = rest.Trim().Trim('"');
                var comma = dll.IndexOf(',');
                if (comma > 0) dll = dll.Substring(0, comma).Trim().Trim('"');
                if (dll.Length > 0) return Resolve(Normalize(dll, windows, system32), system32, exists, ".dll");
            }

            return Resolve(first, system32, exists, ".exe");
        }

        private static int FirstExtensionEnd(string text)
        {
            var best = -1;
            foreach (var extension in new[] { ".exe", ".dll", ".sys", ".bat", ".cmd", ".vbs", ".lnk", ".com", ".scr", ".ps1" })
            {
                var at = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
                while (at >= 0)
                {
                    var end = at + extension.Length;
                    if (end == text.Length || text[end] == ' ' || text[end] == ',' || text[end] == '"')
                    {
                        if (best < 0 || end < best) best = end;
                        break;
                    }
                    at = text.IndexOf(extension, at + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return best;
        }

        /// <summary>Les écritures propres au noyau, ramenées à un chemin ordinaire.</summary>
        public static string Normalize(string path, string windows, string system32)
        {
            var text = path.Trim().Trim('"');
            if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text.Substring(4);
            if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) text = Join(windows, text.Substring(12));
            else if (text.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) text = Join(windows, text.Substring(11));
            else if (text.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) text = Join(windows, text);
            else if (text.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) text = Join(windows.Length >= 2 ? windows.Substring(0, 2) : "C:", text.Substring(1));
            return text;
        }

        private static string? Resolve(string path, string system32, Func<string, bool> exists, string extension)
        {
            if (path.Length == 0) return null;
            var candidate = path;
            // Un nom seul (« ctfmon ») se cherche dans System32, comme Windows le fait.
            if (candidate.IndexOf('\\') < 0 && candidate.IndexOf('/') < 0) candidate = Join(system32, candidate);
            if (!Path.HasExtension(candidate) && SafeExists(exists, candidate + extension)) return candidate + extension;
            return candidate;
        }

        /// <summary>Des chemins Windows, assemblés comme tels quel que soit le système qui exécute.</summary>
        private static string Join(string folder, string name) => folder.TrimEnd('\\') + "\\" + name.TrimStart('\\');

        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            try { return exists(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return false; }
        }
    }
}
