using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    /// <summary>Ce qu'est devenu un dossier de la sauvegarde.</summary>
    public sealed class BackupFolderResult
    {
        public string Label { get; init; } = string.Empty;

        public string Target { get; init; } = string.Empty;

        /// <summary>Application d'origine. Nul pour un dossier personnel.</summary>
        public string? Application { get; init; }

        public int Copied { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        /// <summary>Fichiers refusés parce qu'un programme les tenait ouverts.</summary>
        public int Locked { get; set; }

        public long Bytes { get; set; }
    }

    /// <summary>Tout ce que la fiche raconte, établi avant de l'écrire.</summary>
    internal sealed class ReinstallSheetModel
    {
        public string Machine { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;

        public string? Windows { get; init; }

        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        public string Destination { get; init; } = string.Empty;

        public string? ToolVersion { get; init; }

        public IReadOnlyList<BackupFolderResult> Folders { get; init; } = Array.Empty<BackupFolderResult>();

        public IReadOnlyList<AppDataApplication> Applications { get; init; } = Array.Empty<AppDataApplication>();

        /// <summary>Nul quand l'export n'a pas été demandé.</summary>
        public WifiExportResult? Wifi { get; init; }

        /// <summary>Nul quand aucune analyse n'a relevé les logiciels.</summary>
        public SoftwareInventory? Software { get; init; }

        public int CloudOnlyFiles { get; init; }

        public bool Interrupted { get; init; }
    }

    /// <summary>
    /// La fiche de réinstallation : ce qui a été sauvegardé, ce qui ne l'a pas été, et ce qu'il
    /// faudra réinstaller.
    /// </summary>
    /// <remarks>
    /// <b>Déposée dans la sauvegarde, pas dans un rapport.</b> Elle voyage avec les données, et
    /// c'est là qu'on la cherche le jour où l'on remet la machine en service. Un fichier HTML
    /// autonome, clair et imprimable, pour les mêmes raisons que les rapports : il s'ouvre sur
    /// n'importe quel poste, sans Internet, et la seule voie vers le papier est l'impression du
    /// navigateur.
    /// <para>
    /// L'ordre des sections suit celui de l'intervention : ce qu'il faut faire tant que l'ancien
    /// disque existe, puis ce qui a été copié, puis ce qu'il faudra réinstaller.
    /// </para>
    /// </remarks>
    internal static class ReinstallSheet
    {
        public const string FileName = "fiche-reinstallation.html";

        public const string SoftwareFileName = "logiciels.csv";

        private const string Css = @"
*{box-sizing:border-box}
html{-webkit-print-color-adjust:exact;print-color-adjust:exact}
body{margin:0;padding:32px 20px 64px;background:#F4F3F7;color:#131116;
     font-family:'Segoe UI',system-ui,-apple-system,'Helvetica Neue',Arial,sans-serif;font-size:13px;line-height:1.55}
.sheet{max-width:960px;margin:0 auto}
h1,h2,h3{margin:0;font-weight:600;line-height:1.25;text-wrap:balance}
h1{font-size:25px}
h2{font-size:16px;margin:32px 0 10px}
h3{font-size:14px}
p{margin:0}
.eyebrow{font-size:10.5px;font-weight:600;letter-spacing:.09em;text-transform:uppercase;color:#86838F}
.muted{color:#6B6873}
.num{font-variant-numeric:tabular-nums;text-align:right;white-space:nowrap}
.masthead{padding-bottom:18px;border-bottom:3px solid #DE2028}
.meta{display:flex;flex-wrap:wrap;gap:0 28px;margin:12px 0 0;padding:0}
.meta div{padding:5px 0}
.meta dt{font-size:10.5px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:#86838F}
.meta dd{margin:2px 0 0}
.card{background:#fff;border:1px solid #E4E1E9;border-radius:10px;padding:16px 18px;margin:10px 0;break-inside:avoid}
.todo{background:#FDF6EC;border:1px solid #B45309;border-radius:10px;padding:14px 18px;margin:10px 0}
.todo ul{margin:6px 0 0;padding-left:0;list-style:none}
.todo li{padding:5px 0 5px 26px;position:relative}
.todo li::before{content:'';position:absolute;left:2px;top:8px;width:13px;height:13px;border:1.5px solid #B45309;border-radius:3px;background:#fff}
.card ul,.card ol{margin:6px 0 0;padding-left:20px}
.card li{padding:2px 0}
.split{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-top:10px}
.head{display:flex;justify-content:space-between;align-items:baseline;gap:12px;flex-wrap:wrap}
.pill{display:inline-block;font-size:11.5px;padding:1px 9px;border-radius:999px;background:#EAF5F0;color:#047857;white-space:nowrap}
.pill.warn{background:#FDF1E4;color:#B45309}
.pill.none{background:#EFEDF3;color:#6B6873}
.scroll{overflow-x:auto}
table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #E4E1E9;border-radius:10px}
th,td{padding:7px 10px;text-align:left;vertical-align:top;border-bottom:1px solid #EFEDF3}
th{font-size:10.5px;font-weight:600;letter-spacing:.07em;text-transform:uppercase;color:#86838F;background:#FAF9FC}
tr:last-child td{border-bottom:0}
tr{break-inside:avoid}
.key{font-family:Consolas,'Cascadia Mono',monospace;font-size:12px}
footer{margin-top:36px;padding-top:12px;border-top:1px solid #E4E1E9;font-size:11.5px;color:#86838F}
@media (max-width:640px){.split{grid-template-columns:1fr}}
@media print{body{background:#fff;padding:0}.card,.todo,table{border-color:#CFCBD6}}
";

        public static string Render(ReinstallSheetModel model)
        {
            var html = new StringBuilder(64 * 1024);

            html.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>Fiche de réinstallation · ").Append(E(model.Machine)).Append("</title>")
                .Append("<style>").Append(Css).Append("</style></head><body><main class=\"sheet\">");

            Masthead(html, model);
            BeforeWipe(html, model);
            Copied(html, model);
            Applications(html, model);
            Wifi(html, model);
            Software(html, model);

            html.Append("<footer>Établie par LDI12 Diagnostic")
                .Append(string.IsNullOrEmpty(model.ToolVersion) ? string.Empty : " " + E(model.ToolVersion!))
                .Append(". Le détail fichier par fichier de la copie est dans manifeste.csv, et la liste des ")
                .Append("logiciels, prête pour un tableur, dans ").Append(SoftwareFileName).Append(".</footer>")
                .Append("</main></body></html>");

            return html.ToString();
        }

        private static void Masthead(StringBuilder html, ReinstallSheetModel model)
        {
            html.Append("<header class=\"masthead\"><p class=\"eyebrow\">Sauvegarde avant réinstallation</p>")
                .Append("<h1>Fiche de réinstallation</h1><dl class=\"meta\">");

            Meta(html, "Machine", model.Machine);
            Meta(html, "Compte", model.Account);
            if (!string.IsNullOrEmpty(model.Windows)) Meta(html, "Windows", model.Windows!);
            Meta(html, "Sauvegardé le", model.At.ToString("d MMMM yyyy 'à' HH:mm", French));
            Meta(html, "Dossier", model.Destination);

            html.Append("</dl></header>");
        }

        /// <summary>
        /// Ce qu'il faut régler tant que l'ancien disque existe.
        /// </summary>
        /// <remarks>
        /// En tête de fiche, présenté en cases à cocher : c'est la seule section dont l'oubli ne se
        /// rattrape pas. Chaque ligne découle de ce qui a été mesuré pendant la copie, ou de ce que
        /// le catalogue sait de chaque application trouvée : aucune n'est générique.
        /// </remarks>
        private static void BeforeWipe(StringBuilder html, ReinstallSheetModel model)
        {
            var items = new List<string>();

            var failed = 0;
            foreach (var folder in model.Folders) failed += folder.Failed;

            if (model.Interrupted)
                items.Add("La copie s'est arrêtée avant la fin : la destination était pleine. Libérer de la place, " +
                          "ou changer de support, puis relancer la sauvegarde.");

            foreach (var folder in model.Folders)
                if (folder.Locked > 0 && folder.Application != null)
                    items.Add(folder.Label + " : " + folder.Locked + " fichier(s) refusé(s) parce que le programme " +
                              "était ouvert. Le fermer, puis relancer la sauvegarde.");

            if (failed > 0)
                items.Add(failed + " fichier(s) n'ont pas été copiés en tout. La raison de chacun est dans manifeste.csv.");

            if (model.CloudOnlyFiles > 0)
                items.Add(model.CloudOnlyFiles + " fichier(s) ne sont présents que dans le nuage et n'ont pas été " +
                          "copiés. Vérifier que le client connaît le compte en ligne qui les détient.");

            foreach (var application in model.Applications)
                foreach (var line in application.BeforeWipe)
                    items.Add(line);

            if (model.Wifi == null)
                items.Add("Codes Wi-Fi : non exportés. Les demander au client, ou les relever sur sa box.");
            else if (model.Wifi.Profiles.Count == 0)
                items.Add("Codes Wi-Fi : " + model.Wifi.Describe());
            else if (model.Wifi.WithProtectedKey > 0)
                items.Add(model.Wifi.WithProtectedKey + " code(s) Wi-Fi n'ont pas pu être lus en clair : les relever " +
                          "avant d'effacer, ou relancer la sauvegarde avec les droits administrateur.");

            items.Add("Licences des logiciels payants (suite bureautique, antivirus, logiciels métier) : ce logiciel " +
                      "ne relève pas les clés. Les retrouver avec le client, ou dans ses courriels d'achat.");

            html.Append("<h2>Avant d'effacer le disque</h2><section class=\"todo\"><ul>");
            foreach (var item in items) html.Append("<li>").Append(E(item)).Append("</li>");
            html.Append("</ul></section>");
        }

        private static void Copied(StringBuilder html, ReinstallSheetModel model)
        {
            html.Append("<h2>Ce qui a été copié</h2><div class=\"scroll\"><table><thead><tr>")
                .Append("<th>Dossier</th><th class=\"num\">Copiés</th><th class=\"num\">Déjà présents</th>")
                .Append("<th class=\"num\">Non copiés</th><th class=\"num\">Taille écrite</th></tr></thead><tbody>");

            foreach (var folder in model.Folders)
            {
                html.Append("<tr><td>").Append(E(folder.Label))
                    .Append("<br><span class=\"muted\">").Append(E(folder.Target)).Append("</span></td>")
                    .Append("<td class=\"num\">").Append(folder.Copied.ToString(French)).Append("</td>")
                    .Append("<td class=\"num\">").Append(folder.Skipped.ToString(French)).Append("</td>")
                    .Append("<td class=\"num\">").Append(folder.Failed.ToString(French)).Append("</td>")
                    .Append("<td class=\"num\">").Append(E(ValueFormat.Bytes(folder.Bytes))).Append("</td></tr>");
            }

            html.Append("</tbody></table></div>");
        }

        private static void Applications(StringBuilder html, ReinstallSheetModel model)
        {
            if (model.Applications.Count == 0) return;

            html.Append("<h2>Données d'applications</h2>");

            foreach (var application in model.Applications)
            {
                var copied = 0;
                var failed = 0;
                foreach (var folder in model.Folders)
                    if (folder.Application == application.Name)
                    {
                        copied += folder.Copied + folder.Skipped;
                        failed += folder.Failed;
                    }

                var pill = copied == 0 && failed == 0
                    ? "<span class=\"pill none\">rien à copier</span>"
                    : failed > 0
                        ? "<span class=\"pill warn\">" + copied + " fichier(s) copiés, " + failed + " non copié(s)</span>"
                        : "<span class=\"pill\">" + copied + " fichier(s) copiés</span>";

                html.Append("<section class=\"card\"><div class=\"head\"><h3>").Append(E(application.Name));
                if (!string.IsNullOrEmpty(application.Detail))
                    html.Append(" <span class=\"muted\">· ").Append(E(application.Detail!)).Append("</span>");
                html.Append("</h3>").Append(pill).Append("</div><div class=\"split\">");

                html.Append("<div><p class=\"eyebrow\">Ce qui ne suit pas</p><ul>");
                foreach (var line in application.Limits) html.Append("<li>").Append(E(line)).Append("</li>");
                html.Append("</ul></div>");

                html.Append("<div><p class=\"eyebrow\">Pour remettre en place</p><ol>");
                foreach (var line in application.Restore) html.Append("<li>").Append(E(line)).Append("</li>");
                html.Append("</ol></div></div></section>");
            }
        }

        private static void Wifi(StringBuilder html, ReinstallSheetModel model)
        {
            if (model.Wifi == null) return;

            html.Append("<h2>Profils Wi-Fi</h2>");

            if (model.Wifi.Profiles.Count == 0)
            {
                html.Append("<section class=\"card\"><p>").Append(E(model.Wifi.Describe())).Append("</p></section>");
                return;
            }

            html.Append("<section class=\"card\"><p>").Append(E(model.Wifi.Describe())).Append("</p>")
                .Append("<p class=\"muted\" style=\"margin-top:6px\">Les fichiers du dossier « ").Append(WifiExport.Folder)
                .Append(" » contiennent les clés en clair. Les effacer du support une fois la machine rendue au client.</p>")
                .Append("<p style=\"margin-top:8px\">Pour réimporter un profil, dans une invite de commandes : ")
                .Append("<span class=\"key\">netsh wlan add profile filename=\"fichier.xml\" user=all</span></p></section>")
                .Append("<div class=\"scroll\"><table><thead><tr><th>Réseau</th><th>Clé</th></tr></thead><tbody>");

            foreach (var profile in model.Wifi.Profiles)
            {
                var state = profile.Key switch
                {
                    WifiKeyState.Clear => "<span class=\"pill\">en clair dans l'export</span>",
                    WifiKeyState.Protected => "<span class=\"pill warn\">chiffrée pour cette machine</span>",
                    _ => "<span class=\"pill none\">aucune (réseau ouvert ou d'entreprise)</span>",
                };

                html.Append("<tr><td>").Append(E(profile.Name)).Append("</td><td>").Append(state).Append("</td></tr>");
            }

            html.Append("</tbody></table></div>");
        }

        private static void Software(StringBuilder html, ReinstallSheetModel model)
        {
            html.Append("<h2>Logiciels à réinstaller</h2>");

            var software = model.Software;
            if (software == null)
            {
                html.Append("<section class=\"card\"><p>Aucune analyse n'avait relevé les logiciels installés au moment ")
                    .Append("de la sauvegarde : cette liste n'a pas pu être établie. Lancer une analyse, puis refaire ")
                    .Append("la sauvegarde pour la compléter.</p></section>");
                return;
            }

            html.Append("<p class=\"muted\">").Append(software.Programs.Count.ToString(French))
                .Append(" programme(s) déclarés à Windows");
            if (software.HiddenEntries.HasValue && software.HiddenEntries.Value > 0)
                html.Append(", sans les ").Append(software.HiddenEntries.Value.ToString(French))
                    .Append(" mises à jour et composants système que Windows n'affiche pas non plus");
            html.Append(". ");
            if (!string.IsNullOrEmpty(software.Limitation)) html.Append(E(software.Limitation!));
            html.Append("</p>");

            if (software.Programs.Count > 0)
            {
                html.Append("<div class=\"scroll\" style=\"margin-top:10px\"><table><thead><tr><th>Logiciel</th>")
                    .Append("<th>Version</th><th>Éditeur</th><th>Installé pour</th></tr></thead><tbody>");

                foreach (var program in software.Programs)
                    html.Append("<tr><td>").Append(E(program.Name)).Append("</td><td class=\"key\">")
                        .Append(E(program.Version ?? "non déclarée")).Append("</td><td>")
                        .Append(E(program.Publisher ?? "")).Append("</td><td style=\"white-space:nowrap\">")
                        .Append(program.Scope == SoftwareScope.User ? "ce compte" : "tous les comptes")
                        .Append("</td></tr>");

                html.Append("</tbody></table></div>");
            }

            if (software.StoreApps.Count == 0) return;

            html.Append("<h2>Applications du Microsoft Store</h2><p class=\"muted\">")
                .Append(software.StoreApps.Count.ToString(French))
                .Append(" application(s) installées pour ce compte, sans les composants techniques qui reviennent seuls ")
                .Append("avec Windows. Elles se réinstallent depuis le Microsoft Store, avec le compte du client.</p>")
                .Append("<div class=\"scroll\" style=\"margin-top:10px\"><table><thead><tr><th>Application</th>")
                .Append("<th>Version</th></tr></thead><tbody>");

            foreach (var app in software.StoreApps)
                html.Append("<tr><td>").Append(E(app.Name)).Append("</td><td class=\"key\">")
                    .Append(E(app.Version)).Append("</td></tr>");

            html.Append("</tbody></table></div>");
        }

        /// <summary>La liste des logiciels pour un tableur : point-virgule, comme l'attend Excel en français.</summary>
        public static string SoftwareCsv(SoftwareInventory software)
        {
            var csv = new StringBuilder();
            csv.AppendLine("nom;version;éditeur;installé pour;origine");

            foreach (var program in software.Programs)
                csv.Append(Csv(program.Name)).Append(';')
                    .Append(Csv(program.Version ?? string.Empty)).Append(';')
                    .Append(Csv(program.Publisher ?? string.Empty)).Append(';')
                    .Append(program.Scope == SoftwareScope.User ? "ce compte" : "tous les comptes").Append(';')
                    .AppendLine("programme");

            foreach (var app in software.StoreApps)
                csv.Append(Csv(app.Name)).Append(';').Append(Csv(app.Version)).Append(";;ce compte;")
                    .AppendLine("Microsoft Store");

            return csv.ToString();
        }

        private static void Meta(StringBuilder html, string label, string value)
            => html.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>");

        private static string Csv(string value)
            => value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;

        /// <summary>
        /// Échappement minimal.
        /// </summary>
        /// <remarks>
        /// Pas <c>WebUtility.HtmlEncode</c> : sous .NET Framework, il transforme chaque lettre
        /// accentuée en entité numérique. La page resterait juste, mais son source deviendrait
        /// illisible pour qui l'ouvre dans un éditeur, et le fichier est en UTF-8 de toute façon.
        /// </remarks>
        private static string E(string value)
            => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;");

        private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    }
}
