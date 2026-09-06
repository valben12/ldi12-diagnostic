using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Model;
using LDI12.Reports.Json;
using Newtonsoft.Json;

namespace LDI12.Reports.History
{
    /// <summary>Un diagnostic archivé, tel que la liste le présente sans l'ouvrir en entier.</summary>
    public sealed class HistoryEntry
    {
        public string FilePath { get; init; } = string.Empty;
        public long SizeBytes { get; init; }

        public Guid SnapshotId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public RunMode RunMode { get; init; }
        public string ToolVersion { get; init; } = string.Empty;
        public string MachineName { get; init; } = string.Empty;
        public string? Fingerprint { get; init; }
        public string? ClientReference { get; init; }
        public int? Score { get; init; }
        public int FindingCount { get; init; }

        /// <summary>
        /// Faux quand le fichier n'a pas pu être lu.
        /// </summary>
        /// <remarks>
        /// L'entrée reste listée, avec son problème. Masquer un fichier illisible ferait croire à
        /// un archivage qui n'a pas eu lieu, ce qui est pire que de montrer une ligne en défaut.
        /// </remarks>
        public bool Readable { get; init; } = true;

        public string? Problem { get; init; }

        public string FileName => Path.GetFileName(FilePath);
    }

    /// <summary>
    /// Historique local des diagnostics : un dossier de fichiers JSON, rien d'autre.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi pas une base de données.</b> SQLite aurait été le choix réflexe. Il embarque
    /// des binaires natifs x86 et x64, incompatibles avec l'exécutable unique AnyCPU que le
    /// technicien copie sur une clé USB, la même raison qui a écarté QuestPDF. Et un dossier de
    /// fichiers JSON a deux propriétés qu'une base n'a pas : chaque diagnostic reste lisible sans
    /// l'application qui l'a produit, et il se supprime d'un glissement dans la corbeille.
    ///
    /// <b>Rien n'est archivé sans geste du technicien</b>, et rien n'est supprimé sans lui non
    /// plus. Un diagnostic contient des numéros de série, des noms de comptes, des réseaux Wi-Fi :
    /// c'est de la donnée client qui voyage sur la clé USB de l'atelier. Aucune purge automatique
    /// n'existe ici : décider à la place du technicien ce qu'il garde du dossier d'un client
    /// serait supprimer des données personnelles sans le lui dire.
    /// </remarks>
    public sealed class HistoryStore
    {
        private readonly string _directory;

        public HistoryStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Dossier requis.", nameof(directory));
            _directory = directory;
        }

        public string Directory => _directory;

        /// <summary>Dossier par défaut, à côté des journaux et des réglages.</summary>
        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LDI12", "Diagnostic", "historique");

        public static HistoryStore Default() => new HistoryStore(DefaultDirectory);

        /// <summary>
        /// Archive un diagnostic et rend son entrée.
        /// </summary>
        /// <remarks>
        /// Le nom du fichier porte la date, la machine et le début de l'identifiant : il doit
        /// rester identifiable dans un explorateur de fichiers, sans l'application, des mois plus
        /// tard. L'identifiant évite qu'un second diagnostic lancé dans la même seconde écrase le
        /// premier.
        /// </remarks>
        public HistoryEntry Archive(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            System.IO.Directory.CreateDirectory(_directory);

            var name = snapshot.Metadata.CreatedAt.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) +
                       "-" + Sanitize(snapshot.Machine.MachineName) +
                       "-" + snapshot.Metadata.SnapshotId.ToString("N").Substring(0, 8) + ".json";

            var path = Path.Combine(_directory, name);
            SnapshotSerializer.Save(snapshot, path);

            return Describe(snapshot, path, new FileInfo(path).Length);
        }

        /// <summary>
        /// Diagnostics archivés, du plus récent au plus ancien.
        /// </summary>
        /// <remarks>
        /// Chaque fichier est parcouru sans être entièrement reconstruit : seuls l'en-tête, la
        /// machine et le score sont désérialisés. Sur un dossier de plusieurs dizaines de
        /// diagnostics de 250 Ko, la différence entre parcourir et charger est celle entre une
        /// liste instantanée et une seconde d'attente à chaque ouverture de l'écran.
        /// </remarks>
        public IReadOnlyList<HistoryEntry> List()
        {
            var entries = new List<HistoryEntry>();
            if (!System.IO.Directory.Exists(_directory)) return entries;

            string[] files;
            try { files = System.IO.Directory.GetFiles(_directory, "*.json"); }
            catch (IOException) { return entries; }
            catch (UnauthorizedAccessException) { return entries; }

            foreach (var file in files) entries.Add(ReadHeader(file));

            entries.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
            return entries;
        }

        public SystemSnapshot Load(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return SnapshotSerializer.Load(entry.FilePath);
        }

        /// <summary>
        /// Supprime un diagnostic archivé. Appelée uniquement sur demande explicite.
        /// </summary>
        public void Delete(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            File.Delete(entry.FilePath);
        }

        public long TotalBytes()
        {
            long total = 0;
            foreach (var entry in List()) total += entry.SizeBytes;
            return total;
        }

        // ============================================================ lecture d'en-tête

        private HistoryEntry ReadHeader(string path)
        {
            var size = 0L;
            try { size = new FileInfo(path).Length; }
            catch (IOException) { }

            try
            {
                SnapshotMetadata? metadata = null;
                MachineIdentity? machine = null;
                DiagnosticScore? score = null;
                var findings = 0;

                var serializer = JsonSerializer.Create(SnapshotSerializer.CreateSettings());

                using (var stream = new StreamReader(path, Encoding.UTF8))
                using (var reader = new JsonTextReader(stream))
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1) continue;

                        switch ((string?)reader.Value)
                        {
                            case "metadata":
                                reader.Read();
                                metadata = serializer.Deserialize<SnapshotMetadata>(reader);
                                break;
                            case "machine":
                                reader.Read();
                                machine = serializer.Deserialize<MachineIdentity>(reader);
                                break;
                            case "score":
                                reader.Read();
                                score = serializer.Deserialize<DiagnosticScore>(reader);
                                break;
                            case "findings":
                                reader.Read();
                                findings = CountArray(reader);
                                break;
                        }
                    }
                }

                if (metadata == null)
                {
                    return Unreadable(path, size, "Ce fichier ne contient pas d'en-tête de diagnostic.");
                }

                return new HistoryEntry
                {
                    FilePath = path,
                    SizeBytes = size,
                    SnapshotId = metadata.SnapshotId,
                    CreatedAt = metadata.CreatedAt,
                    RunMode = metadata.RunMode,
                    ToolVersion = metadata.ToolVersion,
                    ClientReference = metadata.ClientReference,
                    MachineName = machine?.MachineName ?? string.Empty,
                    Fingerprint = machine != null && machine.Fingerprint.IsReliable ? machine.Fingerprint.Value : null,
                    Score = score?.Global,
                    FindingCount = findings,
                };
            }
            catch (JsonException ex)
            {
                return Unreadable(path, size, "Ce fichier n'est pas un diagnostic lisible : " + ex.Message);
            }
            catch (IOException ex)
            {
                return Unreadable(path, size, "Ce fichier n'a pas pu être ouvert : " + ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Unreadable(path, size, "L'accès à ce fichier a été refusé par Windows.");
            }
        }

        /// <summary>Compte les éléments d'un tableau sans construire les objets qu'il contient.</summary>
        private static int CountArray(JsonReader reader)
        {
            if (reader.TokenType != JsonToken.StartArray) return 0;

            var count = 0;
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndArray && reader.Depth == depth) break;
                if (reader.Depth == depth + 1 && reader.TokenType == JsonToken.StartObject)
                {
                    count++;
                    reader.Skip();
                }
            }

            return count;
        }

        private static HistoryEntry Unreadable(string path, long size, string problem) => new HistoryEntry
        {
            FilePath = path,
            SizeBytes = size,
            CreatedAt = ReadDateFromName(path),
            MachineName = string.Empty,
            Readable = false,
            Problem = problem,
        };

        /// <summary>
        /// Date lue dans le nom du fichier, quand son contenu n'est plus lisible : elle permet au
        /// moins de ranger l'entrée en défaut à sa place dans la liste.
        /// </summary>
        private static DateTimeOffset ReadDateFromName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length < 17) return DateTimeOffset.MinValue;

            return DateTimeOffset.TryParseExact(
                name.Substring(0, 17), "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
        }

        private HistoryEntry Describe(SystemSnapshot snapshot, string path, long size) => new HistoryEntry
        {
            FilePath = path,
            SizeBytes = size,
            SnapshotId = snapshot.Metadata.SnapshotId,
            CreatedAt = snapshot.Metadata.CreatedAt,
            RunMode = snapshot.Metadata.RunMode,
            ToolVersion = snapshot.Metadata.ToolVersion,
            ClientReference = snapshot.Metadata.ClientReference,
            MachineName = snapshot.Machine.MachineName,
            Fingerprint = snapshot.Machine.Fingerprint.IsReliable ? snapshot.Machine.Fingerprint.Value : null,
            Score = snapshot.Score?.Global,
            FindingCount = snapshot.Findings.Count,
        };

        /// <summary>
        /// Nom de machine réduit à ce qu'un nom de fichier accepte.
        /// </summary>
        /// <remarks>
        /// Les noms de poste contiennent des accents, des espaces, parfois une apostrophe, et un
        /// diagnostic dont l'archivage échoue à cause du nom de la machine serait un diagnostic
        /// perdu.
        /// </remarks>
        internal static string Sanitize(string machineName)
        {
            if (string.IsNullOrWhiteSpace(machineName)) return "machine";

            var builder = new StringBuilder(machineName.Length);
            foreach (var character in machineName)
            {
                if (char.IsLetterOrDigit(character) && character < 128) builder.Append(character);
                else if (character == '-' || character == '_') builder.Append(character);
            }

            var cleaned = builder.ToString();
            if (cleaned.Length == 0) return "machine";
            return cleaned.Length > 32 ? cleaned.Substring(0, 32) : cleaned;
        }
    }
}
