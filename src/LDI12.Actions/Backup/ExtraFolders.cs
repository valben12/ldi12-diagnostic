using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LDI12.Actions.Backup
{
    /// <summary>Un dossier ajouté à la main, et où il va dans la sauvegarde.</summary>
    public sealed class ExtraFolder
    {
        /// <summary>Le dossier d'origine, tel que le technicien l'a choisi : « C:\Compta », « E:\ ».</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>Son nom dans la sauvegarde, unique : « Compta », « Disque E ».</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>« Autres dossiers\Compta ».</summary>
        public string Target => ExtraFolders.Folder + "\\" + Name;

        /// <summary>La racine d'un volume : ses dossiers système sont écartés.</summary>
        public bool IsVolumeRoot { get; init; }
    }

    /// <summary>
    /// Les dossiers que le technicien ajoute à la sauvegarde, où qu'ils soient.
    /// </summary>
    /// <remarks>
    /// <b>Les données qui ne sont pas là où Windows les attend.</b> Une comptabilité dans
    /// <c>C:\Compta</c>, des photos sur un second disque, un dossier « Clients » à la racine : la
    /// sauvegarde des dossiers personnels ne les voit pas, et c'est précisément ce qu'on oublie.
    /// <para>
    /// Chacun est rangé sous « Autres dossiers », à son nom. Leur emplacement d'origine est noté
    /// dans la sauvegarde : la restauration les remet au même endroit quand ce disque existe sur
    /// la nouvelle machine, et sinon dans les Documents, sans jamais rien écraser.
    /// </para>
    /// <para>
    /// La racine d'un disque entier se choisit aussi : ses dossiers et fichiers système (corbeille,
    /// points de restauration, fichier d'échange) sont alors écartés d'office, ils n'ont rien à faire
    /// dans une sauvegarde et plusieurs sont illisibles.
    /// </para>
    /// </remarks>
    public static class ExtraFolders
    {
        public const string Folder = "Autres dossiers";

        public const string MapFileName = "autres-dossiers.txt";

        /// <summary>Dossiers système d'une racine de volume, jamais sauvegardés.</summary>
        public static readonly IReadOnlyList<string> SystemDirectories = new[]
        {
            "$Recycle.Bin", "$RECYCLE.BIN", "System Volume Information", "Recovery", "Config.Msi",
            "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "RECYCLER", "found.000",
        };

        private static readonly HashSet<string> SystemFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
        };

        /// <summary>Faux pour les fichiers système d'une racine de volume.</summary>
        public static bool KeepAtRoot(string fileName) => !SystemFiles.Contains(fileName);

        /// <summary>
        /// Les dossiers choisis, dédoublonnés, chacun avec un nom unique dans la sauvegarde.
        /// </summary>
        public static IReadOnlyList<ExtraFolder> Plan(IEnumerable<string> paths)
        {
            var result = new List<ExtraFolder>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in paths)
            {
                var path = Normalize(raw);
                if (path.Length == 0 || !seenPaths.Add(path)) continue;

                var isRoot = path.Length == 3 && path[1] == ':';
                var name = isRoot ? "Disque " + char.ToUpperInvariant(path[0]) : Sanitize(System.IO.Path.GetFileName(path));
                if (name.Length == 0) name = "Dossier";

                var unique = name;
                for (var index = 2; !seenNames.Add(unique); index++) unique = name + " (" + index + ")";

                result.Add(new ExtraFolder { Path = path, Name = unique, IsVolumeRoot = isRoot });
            }

            return result;
        }

        /// <summary>« C:\Compta\ » et « C:\Compta » sont le même dossier ; « E: » est « E:\ ».</summary>
        public static string Normalize(string? path)
        {
            var value = (path ?? string.Empty).Trim();
            if (value.Length == 2 && value[1] == ':') return value + "\\";
            if (value.Length > 3) value = value.TrimEnd('\\', '/');
            return value;
        }

        /// <summary>Une ligne par dossier : son nom dans la sauvegarde, une tabulation, son emplacement d'origine.</summary>
        public static string Map(IEnumerable<ExtraFolder> folders)
        {
            var builder = new StringBuilder();
            builder.Append("# Dossiers ajoutés à la sauvegarde, et leur emplacement d'origine.\r\n");
            foreach (var folder in folders) builder.Append(folder.Name).Append('\t').Append(folder.Path).Append("\r\n");
            return builder.ToString();
        }

        internal static Dictionary<string, string> ParseMap(string? text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (text == null) return map;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r').TrimStart('\uFEFF');
                if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                var tab = trimmed.IndexOf('\t');
                if (tab <= 0) continue;
                map[trimmed.Substring(0, tab)] = trimmed.Substring(tab + 1);
            }

            return map;
        }

        /// <summary>Une ligne par chemin : la forme dans laquelle l'écran transmet les dossiers choisis.</summary>
        public static string Encode(IEnumerable<string> paths) => string.Join("\n", paths);

        internal static IReadOnlyList<string> Decode(string? text)
            => string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text!.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        private static string Sanitize(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (var character in name)
                builder.Append(Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
            return builder.ToString().Trim();
        }
    }
}
