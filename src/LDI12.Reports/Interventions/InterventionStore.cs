using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Model;
using LDI12.Reports.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LDI12.Reports.Interventions
{
    /// <summary>
    /// Range les dossiers d'intervention, un sous-dossier chacun : dossier.json, les diagnostics
    /// avant et après, et le rapport.
    /// </summary>
    /// <remarks>
    /// <b>À côté du logiciel d'abord.</b> Lancé depuis la clé de l'atelier, le logiciel range ses
    /// dossiers sur la clé : la réinstallation efface le disque du client, pas la clé, et le
    /// dossier est là quand le logiciel est relancé sur le Windows neuf. Quand l'emplacement du
    /// programme n'est pas inscriptible (Program Files), les Documents prennent le relais.
    /// </remarks>
    public sealed class InterventionStore
    {
        public const string FolderName = "LDI12-Dossiers";
        public const string CaseFileName = "dossier.json";
        public const string BeforeFileName = "avant.json";
        public const string AfterFileName = "apres.json";
        public const string ReportFileName = "rapport-intervention.html";

        /// <summary>Le nom du dossier d'intervention déposé dans une sauvegarde.</summary>
        public const string BackupCopyName = "Dossier-intervention";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            DateParseHandling = DateParseHandling.DateTimeOffset,
        };

        public InterventionStore(string root) => Root = root ?? throw new ArgumentNullException(nameof(root));

        public string Root { get; }

        /// <summary>À côté de l'exécutable s'il est inscriptible, sinon dans les Documents.</summary>
        public static InterventionStore Default()
        {
            // Sonde l'emplacement du programme sans y créer de dossier : rien n'est laissé à côté
            // d'un programme lancé depuis Téléchargements tant qu'aucun dossier n'est ouvert.
            var home = AppDomain.CurrentDomain.BaseDirectory;
            if (Writable(home)) return new InterventionStore(Path.Combine(home, FolderName));

            return new InterventionStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LDI12 Diagnostic", "Dossiers"));
        }

        /// <summary>Le sous-dossier d'un dossier d'intervention : date, client, début de l'identifiant.</summary>
        public string FolderOf(InterventionCase intervention)
        {
            if (intervention == null) throw new ArgumentNullException(nameof(intervention));

            // Fixé au premier enregistrement : renommer le client ensuite ne perd pas le dossier.
            if (!string.IsNullOrEmpty(intervention.Folder)) return Path.Combine(Root, intervention.Folder!);

            var name = intervention.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" +
                       Sanitize(intervention.Title) + "-" + intervention.Id.Substring(0, Math.Min(8, intervention.Id.Length));
            intervention.Folder = name;
            return Path.Combine(Root, name);
        }

        public void Save(InterventionCase intervention)
        {
            var folder = FolderOf(intervention);
            Directory.CreateDirectory(folder);
            intervention.UpdatedAt = DateTimeOffset.Now;
            Write(Path.Combine(folder, CaseFileName), JsonConvert.SerializeObject(intervention, Settings));
        }

        /// <summary>Range un diagnostic dans le dossier, et rend le nom du fichier.</summary>
        public string SaveSnapshot(InterventionCase intervention, SystemSnapshot snapshot, bool after)
        {
            var folder = FolderOf(intervention);
            Directory.CreateDirectory(folder);
            var name = after ? AfterFileName : BeforeFileName;
            SnapshotSerializer.Save(snapshot, Path.Combine(folder, name));
            return name;
        }

        public SystemSnapshot? LoadSnapshot(InterventionCase intervention, string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var path = Path.Combine(FolderOf(intervention), name!);
            try { return File.Exists(path) ? SnapshotSerializer.Load(path) : null; }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { return null; }
        }

        public string SaveReport(InterventionCase intervention, string html)
        {
            var path = Path.Combine(FolderOf(intervention), ReportFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, html);
            return path;
        }

        /// <summary>Les dossiers rangés ici, du plus récent au plus ancien ; les illisibles sont ignorés.</summary>
        public IReadOnlyList<InterventionCase> List() => ListIn(Root);

        /// <summary>Les dossiers rangés sous une racine, une sauvegarde par exemple.</summary>
        public static IReadOnlyList<InterventionCase> ListIn(string root)
        {
            var result = new List<InterventionCase>();
            if (!Directory.Exists(root)) return result;

            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return result; }

            foreach (var folder in folders)
            {
                var loaded = Read(Path.Combine(folder, CaseFileName));
                if (loaded != null) result.Add(loaded);
            }

            result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return result;
        }

        public static InterventionCase? Read(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<InterventionCase>(File.ReadAllText(path, Encoding.UTF8), Settings)
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Recopie le dossier dans une sauvegarde : si la clé de l'atelier se perd, le dossier
        /// repart du disque de la sauvegarde.
        /// </summary>
        public bool CopyInto(InterventionCase intervention, string backupFolder)
        {
            try
            {
                var source = FolderOf(intervention);
                var target = Path.Combine(backupFolder, BackupCopyName, Path.GetFileName(source));
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(source))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reprend ici un dossier trouvé dans une sauvegarde, quand la clé ne l'a pas.
        /// </summary>
        public InterventionCase? Adopt(string backupFolder)
        {
            // Un disque entier désigné : sa sauvegarde LDI12 la plus récente qui porte un dossier.
            if (!Directory.Exists(Path.Combine(backupFolder, BackupCopyName)))
            {
                try
                {
                    string? latest = null;
                    foreach (var folder in Directory.GetDirectories(backupFolder, "LDI12-Sauvegarde-*"))
                        if (Directory.Exists(Path.Combine(folder, BackupCopyName)) &&
                            (latest == null || string.CompareOrdinal(folder, latest) > 0))
                            latest = folder;
                    if (latest == null) return null;
                    backupFolder = latest;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    return null;
                }
            }

            foreach (var found in ListIn(Path.Combine(backupFolder, BackupCopyName)))
            {
                if (Read(Path.Combine(FolderOf(found), CaseFileName)) != null) return null;

                try
                {
                    var source = Path.Combine(backupFolder, BackupCopyName, Path.GetFileName(FolderOf(found)));
                    var target = FolderOf(found);
                    Directory.CreateDirectory(target);
                    foreach (var file in Directory.GetFiles(source))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
                    return found;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            return null;
        }

        private static void Write(string path, string content)
        {
            // Écrit à côté puis remplace : une clé arrachée en pleine écriture ne laisse pas un
            // dossier à moitié écrit à la place du bon.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static bool Writable(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return false;
                var probe = Path.Combine(folder, ".ecriture-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return false;
            }
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value)
                builder.Append(char.IsLetterOrDigit(c) ? c : c == ' ' || c == '-' ? '-' : '_');
            var text = builder.ToString().Trim('-', '_');
            return text.Length == 0 ? "client" : text.Length > 40 ? text.Substring(0, 40) : text;
        }
    }
}
