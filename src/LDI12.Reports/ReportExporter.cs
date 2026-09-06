using System;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Model;
using LDI12.Reports.Html;
using LDI12.Reports.Json;

namespace LDI12.Reports
{
    public enum ReportKind
    {
        /// <summary>Rapport complet, destiné au dossier d'intervention.</summary>
        Technician = 0,

        /// <summary>Bilan simplifié, remis au client.</summary>
        Client = 1,

        /// <summary>Instantané brut. Relisible par l'outil, archivable, comparable.</summary>
        Json = 2,

        /// <summary>
        /// Comparatif avant / après.
        /// </summary>
        /// <remarks>
        /// Le seul document qui ne sort pas d'un instantané unique : il en rapproche deux. Il
        /// s'écrit donc par <see cref="ReportExporter.ExportComparison"/> et non par
        /// <see cref="ReportExporter.Export"/>, qui ne saurait pas où prendre le second.
        /// </remarks>
        Comparison = 3,
    }

    public sealed class ExportedReport
    {
        public ExportedReport(ReportKind kind, string path, int bytes)
        {
            Kind = kind;
            Path = path;
            Bytes = bytes;
        }

        public ReportKind Kind { get; }
        public string Path { get; }
        public int Bytes { get; }
    }

    /// <summary>
    /// Écriture des rapports sur disque.
    /// </summary>
    /// <remarks>
    /// Les trois formats sortent du même instantané et ne se recoupent pas par hasard : le JSON
    /// est la source, les deux HTML en sont des lectures. Un rapport archivé peut donc être
    /// rouvert des mois plus tard et réédité à l'identique, ce qui est la condition pour comparer
    /// un avant et un après réparation.
    ///
    /// Les fichiers HTML sont autonomes : aucune ressource externe, aucune police téléchargée.
    /// Ils s'ouvrent dans n'importe quel navigateur, y compris hors ligne, et s'impriment en PDF
    /// sans outil supplémentaire : c'est ce qui évite d'embarquer un moteur PDF dans un logiciel
    /// qui doit rester léger sur des machines lentes.
    /// </remarks>
    public static class ReportExporter
    {
        /// <summary>Génère le contenu sans l'écrire. Sert à l'aperçu et aux tests.</summary>
        public static string Render(SystemSnapshot snapshot, ReportKind kind, ReportContext? context = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            return kind switch
            {
                ReportKind.Technician => TechnicianReport.Render(snapshot, context),
                ReportKind.Client => ClientReport.Render(snapshot, context),
                ReportKind.Json => SnapshotSerializer.Serialize(snapshot),
                ReportKind.Comparison => throw new ArgumentException(
                    "Le comparatif rapproche deux diagnostics : il s'écrit par ExportComparison.", nameof(kind)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        /// <summary>
        /// Écrit un rapport dans <paramref name="directory"/> et retourne le fichier produit.
        /// Le dossier est créé au besoin.
        /// </summary>
        public static ExportedReport Export(
            SystemSnapshot snapshot, ReportKind kind, string directory, ReportContext? context = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Dossier requis.", nameof(directory));

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, SuggestFileName(snapshot, kind, context));
            var content = Render(snapshot, kind, context);

            // UTF-8 avec marque d'ordre : sans elle, Internet Explorer et les vieux Edge en mode
            // fichier local devinent la page en Windows-1252 et affichent « Ã© » partout. Le
            // rapport est justement fait pour être ouvert sur la machine du client, dont on ne
            // choisit pas le navigateur.
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return new ExportedReport(kind, path, Encoding.UTF8.GetByteCount(content));
        }

        /// <summary>
        /// Écrit le comparatif avant / après de deux diagnostics déjà rapprochés.
        /// </summary>
        public static ExportedReport ExportComparison(
            SnapshotDelta delta, string directory, ReportContext? context = null)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Dossier requis.", nameof(directory));

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, SuggestComparisonFileName(delta, context));
            var content = ComparisonReport.Render(delta, context);

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return new ExportedReport(ReportKind.Comparison, path, Encoding.UTF8.GetByteCount(content));
        }

        /// <summary>
        /// Nom du comparatif : daté du <b>second</b> diagnostic, celui qui fait foi de l'état
        /// remis au client.
        /// </summary>
        public static string SuggestComparisonFileName(SnapshotDelta delta, ReportContext? context = null)
        {
            var stamp = delta.After.CreatedAt.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
            var machine = Sanitize(delta.After.MachineName);
            var reference = Sanitize(context?.ClientReference ?? delta.After.ClientReference ?? string.Empty);
            var middle = reference.Length > 0 ? machine + "-" + reference : machine;

            return "LDI12-" + stamp + "-" + middle + "-comparatif.html";
        }

        /// <summary>
        /// Nom de fichier proposé : machine, date, nature. Trié naturellement dans un dossier
        /// d'intervention, et lisible sans l'ouvrir.
        /// </summary>
        public static string SuggestFileName(SystemSnapshot snapshot, ReportKind kind, ReportContext? context = null)
        {
            var stamp = snapshot.Metadata.CreatedAt.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
            var machine = Sanitize(snapshot.Machine.MachineName);
            var reference = Sanitize(context?.ClientReference ?? snapshot.Metadata.ClientReference ?? string.Empty);

            var suffix = kind switch
            {
                ReportKind.Technician => "diagnostic",
                ReportKind.Client => "bilan-client",
                _ => "donnees",
            };

            var extension = kind == ReportKind.Json ? ".json" : ".html";
            var middle = reference.Length > 0 ? machine + "-" + reference : machine;
            return "LDI12-" + stamp + "-" + middle + "-" + suffix + extension;
        }

        /// <summary>
        /// Réduit une chaîne à ce qu'un nom de fichier accepte partout : lettres non accentuées,
        /// chiffres, tirets. Les caractères interdits de Windows ne sont pas les seuls écartés :
        /// un rapport finit souvent sur une clé USB, dans une pièce jointe ou sur un partage
        /// réseau, et chacun a ses propres réticences.
        /// </summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "machine";

            var builder = new StringBuilder(value.Length);
            var lastDash = false;

            foreach (var c in value.Normalize(NormalizationForm.FormD))
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue;

                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                    lastDash = false;
                }
                else if (!lastDash && builder.Length > 0)
                {
                    builder.Append('-');
                    lastDash = true;
                }
            }

            var result = builder.ToString().Trim('-');
            if (result.Length == 0) return "machine";
            return result.Length <= 48 ? result : result.Substring(0, 48).TrimEnd('-');
        }
    }
}
