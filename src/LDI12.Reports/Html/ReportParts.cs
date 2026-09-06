using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Reports.Html
{
    /// <summary>Fragments communs aux deux rapports : en-tête, cartouche de score, pied.</summary>
    internal static class ReportParts
    {
        /// <summary>Hexagone au trait du logo LDI12, en SVG pour rester net à l'impression.</summary>
        public const string Mark =
            "<svg class=\"mark\" viewBox=\"0 0 24 26\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\">" +
            "<path d=\"M12 1.6 21.5 7v12L12 24.4 2.5 19V7Z\" stroke=\"" + ReportStyles.AccentRed +
            "\" stroke-width=\"2.4\" stroke-linejoin=\"round\"/></svg>";

        public static void Document(StringBuilder html, string title, Action<StringBuilder> body)
        {
            html.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>").Append(Html.Escape(title)).Append("</title>")
                .Append("<style>").Append(ReportStyles.Css).Append("</style></head><body>")
                .Append("<div class=\"sheet\">");
            body(html);
            html.Append("</div></body></html>");
        }

        public static void Masthead(StringBuilder html, string kicker, string title, SystemSnapshot snapshot, ReportContext context)
            => Masthead(html, kicker, title, context.ClientReference ?? snapshot.Metadata.ClientReference, context);

        /// <summary>
        /// En-tête d'un document qui ne porte pas sur un instantané unique, le comparatif.
        /// </summary>
        public static void Masthead(StringBuilder html, string kicker, string title, string? reference, ReportContext context)
        {
            html.Append("<header class=\"masthead\">").Append(Mark).Append("<div class=\"grow\">")
                .Append("<div class=\"eyebrow\">").Append(Html.Escape(kicker)).Append("</div>")
                .Append("<h1>").Append(Html.Escape(title)).Append("</h1>")
                .Append("<p class=\"muted\">Laguiole Dépannage Informatique : LDI12</p>")
                .Append("</div><div class=\"issued tiny muted\">")
                .Append("Établi le ").Append(Html.Escape(ValueFormat.DateTime(context.IssuedAt)));

            if (!string.IsNullOrWhiteSpace(context.Technician))
                html.Append("<br>par ").Append(Html.Escape(context.Technician));

            if (!string.IsNullOrWhiteSpace(reference))
                html.Append("<br>Dossier ").Append(Html.Escape(reference));

            html.Append("</div></header>");
        }

        public static void MetaRow(StringBuilder html, IEnumerable<KeyValuePair<string, string>> entries)
        {
            html.Append("<dl class=\"meta\">");
            foreach (var entry in entries)
                html.Append("<div><dt>").Append(Html.Escape(entry.Key)).Append("</dt><dd>")
                    .Append(Html.Escape(entry.Value)).Append("</dd></div>");
            html.Append("</dl>");
        }

        public static void ScoreDial(StringBuilder html, DiagnosticScore score)
        {
            html.Append("<div class=\"dial\">")
                .Append(Html.ScoreArc(score.Global, BandColor(score.Band)))
                .Append("<div class=\"value\"><b>").Append(score.Global.ToString(CultureInfo.CurrentCulture))
                .Append("</b><span>sur 100</span></div></div>");
        }

        public static string BandColor(ScoreBand band) => band switch
        {
            ScoreBand.Excellent => ReportStyles.Good,
            ScoreBand.Good => ReportStyles.Good,
            ScoreBand.Attention => ReportStyles.Warning,
            ScoreBand.Degraded => ReportStyles.Problem,
            _ => ReportStyles.Critical,
        };

        public static string SeverityColor(Severity severity) => severity switch
        {
            Severity.Critical => ReportStyles.Critical,
            Severity.Problem => ReportStyles.Problem,
            Severity.Warning => ReportStyles.Warning,
            _ => ReportStyles.Info,
        };

        public static string SeverityClass(Severity severity) => severity switch
        {
            Severity.Critical => "crit",
            Severity.Problem => "bad",
            Severity.Warning => "warn",
            _ => "info",
        };

        public static string SeverityLabel(Severity severity) => severity switch
        {
            Severity.Critical => "Critique",
            Severity.Problem => "Problème",
            Severity.Warning => "À surveiller",
            _ => "Information",
        };

        public static string CategoryLabel(DiagnosticCategory category) => category switch
        {
            DiagnosticCategory.Hardware => "Matériel",
            DiagnosticCategory.Storage => "Stockage",
            DiagnosticCategory.Windows => "Windows",
            DiagnosticCategory.Security => "Sécurité",
            DiagnosticCategory.Network => "Réseau",
            DiagnosticCategory.Performance => "Performances",
            _ => "Plateforme",
        };

        public static string ConfidenceLabel(ScoreConfidence confidence) => confidence switch
        {
            ScoreConfidence.Full => "complète",
            ScoreConfidence.Partial => "partielle",
            _ => "faible",
        };

        public static string PriorityLabel(RecommendationPriority priority) => priority switch
        {
            RecommendationPriority.Immediate => "Immédiat",
            RecommendationPriority.High => "Prioritaire",
            RecommendationPriority.Normal => "Normal",
            _ => "Optionnel",
        };

        public static string EffortLabel(EffortLevel effort) => effort switch
        {
            EffortLevel.Minutes => "quelques minutes",
            EffortLevel.Extended => "intervention courte",
            _ => "intervention complète",
        };

        public static string StatusLabel(ProbeStatus status) => status switch
        {
            ProbeStatus.Ok => "Terminé",
            ProbeStatus.Partial => "Partiel",
            ProbeStatus.Failed => "Échec",
            ProbeStatus.Skipped => "Écarté",
            ProbeStatus.Unavailable => "Indisponible",
            ProbeStatus.TimedOut => "Délai dépassé",
            ProbeStatus.Cancelled => "Annulé",
            ProbeStatus.ElevationRequired => "Élévation requise",
            _ => "Non exécuté",
        };

        public static string ElevationLabel(ElevationState state) => state switch
        {
            ElevationState.Elevated => "Session administrateur",
            ElevationState.NotElevated => "Session utilisateur standard",
            ElevationState.ElevatedByDefault => "Compte administrateur sans contrôle de compte",
            _ => "Niveau de privilèges indéterminé",
        };

        public static string ModeLabel(RunMode mode) => mode switch
        {
            RunMode.Quick => "Analyse rapide",
            RunMode.Full => "Diagnostic complet",
            _ => "Analyse personnalisée",
        };

        /// <summary>
        /// Nombre de modules restés muets faute de privilèges. Sert aux deux rapports : la portée
        /// réelle du diagnostic est une information que le client a le droit de connaître.
        /// </summary>
        public static int ElevationBlockedCount(SystemSnapshot snapshot)
        {
            var blocked = 0;
            foreach (var report in snapshot.ModuleReports)
                if (report.Status == ProbeStatus.ElevationRequired) blocked++;
            return blocked;
        }

        public static void Footer(StringBuilder html, SystemSnapshot snapshot, string closing)
        {
            html.Append("<footer class=\"foot\"><p>").Append(Html.Escape(closing)).Append("</p>")
                .Append("<p>Diagnostic effectué le ")
                .Append(Html.Escape(ValueFormat.DateTime(snapshot.Metadata.CreatedAt)))
                .Append(" : LDI12 Diagnostic ")
                .Append(Html.Escape(string.IsNullOrEmpty(snapshot.Metadata.ToolVersion) ? "-" : snapshot.Metadata.ToolVersion))
                .Append(" : référence interne ").Append(Html.Escape(snapshot.Metadata.SnapshotId.ToString()))
                .Append("</p></footer>");
        }
    }
}
