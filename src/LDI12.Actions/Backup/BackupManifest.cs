using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Une ligne du manifeste : un fichier de la sauvegarde, et ce qu'il est devenu.</summary>
    public sealed class ManifestEntry
    {
        /// <summary>Le dossier dans la sauvegarde : « Documents », « Applications\Google Chrome ».</summary>
        public string Folder { get; init; } = string.Empty;

        /// <summary>Le chemin du fichier sous ce dossier.</summary>
        public string Relative { get; init; } = string.Empty;

        public long Size { get; init; }

        /// <summary>Le résultat en clair : « copié et vérifié », « déjà présent », « ouvert par un programme »…</summary>
        public string Result { get; init; } = string.Empty;

        /// <summary>L'empreinte SHA-256 du contenu sauvegardé ; nulle dans un manifeste d'avant les empreintes.</summary>
        public string? Fingerprint { get; init; }

        /// <summary>Vrai si le fichier est dans la sauvegarde : copié, ou déjà là d'un passage précédent.</summary>
        public bool IsSaved => Result == BackupManifest.Copied || Result == BackupManifest.Present;

        /// <summary>Le chemin du fichier dans la sauvegarde, relatif à sa racine.</summary>
        public string Key => Path.Combine(Folder, Relative);
    }

    /// <summary>
    /// Le manifeste d'une sauvegarde : chaque fichier, sa taille, ce qu'il est devenu, son empreinte.
    /// </summary>
    /// <remarks>
    /// <b>L'empreinte est ce qui fait d'une liste une preuve.</b> La copie la calcule de toute façon
    /// pour sa relecture ; longtemps, elle était jetée aussitôt. Écrite ici, elle permet de vérifier
    /// la sauvegarde n'importe quand, sans la source : avant d'effacer le disque du client, après le
    /// transport du support, et à la restauration, où chaque fichier relu est comparé à ce qu'il
    /// était le jour de la sauvegarde. Une clé USB qui abîme un fichier en dormant dans un tiroir
    /// se voit alors, au lieu de passer pour une copie fidèle.
    /// <para>
    /// Un fichier repris d'un passage précédent garde l'empreinte que ce passage avait notée. Le
    /// manifeste est réécrit à la fin de chaque dossier : un support arraché en route laisse ce
    /// qui était déjà acquis, et la reprise retrouve ses empreintes.
    /// </para>
    /// <para>
    /// Format : CSV séparé par des points-virgules, lisible dans un tableur. Les manifestes
    /// d'avant les empreintes, à quatre colonnes, se relisent toujours.
    /// </para>
    /// </remarks>
    public static class BackupManifest
    {
        public const string FileName = "manifeste.csv";

        public const string Header = "dossier;fichier;octets;résultat;sha256";

        internal const string Copied = "copié et vérifié";
        internal const string Present = "déjà présent";

        /// <summary>Une ligne, terminée par un retour à la ligne.</summary>
        public static void Append(StringBuilder manifest, string folder, string relative, long size, string result, string? fingerprint)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.Append(Csv(folder)).Append(';')
                .Append(Csv(relative)).Append(';')
                .Append(size.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(Csv(result)).Append(';')
                .AppendLine(fingerprint ?? string.Empty);
        }

        /// <summary>Les lignes d'un manifeste ; les lignes illisibles sont ignorées.</summary>
        public static IReadOnlyList<ManifestEntry> Parse(string? text)
        {
            var entries = new List<ManifestEntry>();
            if (string.IsNullOrEmpty(text)) return entries;

            var first = true;
            foreach (var raw in text!.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (first)
                {
                    first = false;
                    if (line.TrimStart('﻿').StartsWith("dossier;", StringComparison.Ordinal)) continue;
                }

                if (line.Length == 0) continue;

                var fields = Split(line);
                if (fields.Count < 4) continue;
                if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) continue;

                var fingerprint = fields.Count > 4 && IsFingerprint(fields[4]) ? fields[4].ToLowerInvariant() : null;
                entries.Add(new ManifestEntry
                {
                    Folder = fields[0],
                    Relative = fields[1],
                    Size = size,
                    Result = fields[3],
                    Fingerprint = fingerprint,
                });
            }

            return entries;
        }

        /// <summary>
        /// Les empreintes connues, par chemin dans la sauvegarde.
        /// </summary>
        /// <remarks>
        /// La dernière ligne d'un fichier l'emporte : un manifeste repris peut le nommer deux fois.
        /// </remarks>
        public static Dictionary<string, string> Fingerprints(IEnumerable<ManifestEntry> entries)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (entry.IsSaved && entry.Fingerprint != null) result[entry.Key] = entry.Fingerprint;
            return result;
        }

        /// <summary>Relit le manifeste d'une sauvegarde ; vide s'il n'y en a pas.</summary>
        public static IReadOnlyList<ManifestEntry> Read(IFileSystemGateway files, string backup)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            var text = files.ReadText(Path.Combine(backup, FileName));
            return text.HasValue ? Parse(text.Value) : Array.Empty<ManifestEntry>();
        }

        internal static bool IsFingerprint(string value)
        {
            if (value.Length != 64) return false;
            foreach (var c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }

        internal static string Csv(string value)
            => value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;

        /// <summary>Découpe une ligne CSV à points-virgules, guillemets doublés compris.</summary>
        internal static List<string> Split(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            var quoted = false;

            for (var index = 0; index < line.Length; index++)
            {
                var c = line[index];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                        else quoted = false;
                    }
                    else current.Append(c);
                }
                else if (c == '"' && current.Length == 0) quoted = true;
                else if (c == ';') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }

            fields.Add(current.ToString());
            return fields;
        }
    }
}
