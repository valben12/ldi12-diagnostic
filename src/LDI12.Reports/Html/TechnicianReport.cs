using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using LDI12.Reports.Facts;

namespace LDI12.Reports.Html
{
    /// <summary>
    /// Rapport technicien : tout ce qui a été mesuré, tout ce qui ne l'a pas été, et pourquoi.
    /// </summary>
    /// <remarks>
    /// Ce document doit pouvoir être relu des mois plus tard, éventuellement par quelqu'un
    /// d'autre, et résister à la contestation. D'où trois sections qu'on serait tenté d'omettre :
    ///
    /// - <b>Les caractéristiques relevées</b>, y compris celles qui manquent avec leur raison.
    ///   Un rapport qui ne montre que ce qui a fonctionné laisse croire à une couverture complète.
    /// - <b>Les modules de collecte</b> et leur état. « Pourquoi cette information manque » est
    ///   une information.
    /// - <b>Le grand livre des pénalités</b>. Un score sans sa justification ligne à ligne n'est
    ///   qu'une opinion chiffrée ; avec elle, chaque point perdu se rattache à une mesure.
    /// </remarks>
    public static class TechnicianReport
    {
        public static string Render(SystemSnapshot snapshot, ReportContext? context = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            context ??= ReportContext.Default;

            var machine = snapshot.Machine.MachineName;
            var html = new StringBuilder(64 * 1024);

            ReportParts.Document(html, "Rapport de diagnostic : " + machine, body =>
            {
                ReportParts.Masthead(body, "Rapport technicien", "Diagnostic de " + machine, snapshot, context);
                WriteMeta(body, snapshot);
                WriteNote(body, context);
                WriteBanners(body, snapshot);
                WriteScore(body, snapshot);
                WriteCorrelations(body, snapshot);
                WriteRecommendations(body, snapshot);
                WriteInterventions(body, context);
                WriteFindings(body, snapshot);
                WriteFactSheets(body, snapshot);
                WriteModules(body, snapshot);
                WriteLedger(body, snapshot);

                ReportParts.Footer(body, snapshot,
                    "Ce rapport décrit l'état de la machine au moment de l'analyse. Les mesures non " +
                    "obtenues sont signalées comme telles : elles ne valent ni constat, ni absence de problème.");
            });

            return html.ToString();
        }

        private static void WriteMeta(StringBuilder html, SystemSnapshot snapshot)
        {
            var profile = snapshot.Platform.Windows;
            var entries = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Machine", snapshot.Machine.MachineName),
                new KeyValuePair<string, string>("Session", snapshot.Machine.UserName),
                new KeyValuePair<string, string>("Système", profile.DisplayName),
                new KeyValuePair<string, string>("Privilèges", ReportParts.ElevationLabel(snapshot.Platform.Elevation)),
                new KeyValuePair<string, string>("Mode d'analyse", ReportParts.ModeLabel(snapshot.Metadata.RunMode)),
                new KeyValuePair<string, string>("Durée",
                    (snapshot.Metadata.DurationMs / 1000d).ToString("0.0", CultureInfo.CurrentCulture) + " s"),
                new KeyValuePair<string, string>("Analysé le", ValueFormat.DateTime(snapshot.Metadata.CreatedAt)),
            };

            if (snapshot.Machine.Model.HasValue)
                entries.Insert(1, new KeyValuePair<string, string>("Modèle", snapshot.Machine.Model.Value));

            ReportParts.MetaRow(html, entries);
        }

        private static void WriteNote(StringBuilder html, ReportContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Note)) return;
            html.Append("<div class=\"card tight\"><div class=\"eyebrow\">Observation du technicien</div><p>")
                .Append(Html.Escape(context.Note)).Append("</p></div>");
        }

        /// <summary>
        /// Ce qui a été fait pendant l'intervention.
        /// </summary>
        /// <remarks>
        /// Placé avant les constats, et non en annexe : le lecteur du rapport doit savoir que la
        /// machine a été touchée avant de lire l'état dans lequel elle a été trouvée. Rien n'est
        /// écrit ici quand rien n'a été fait : un titre suivi de « aucune intervention » se lit
        /// aussi bien qu'une absence de titre, et cette section n'a pas à occuper une page pour
        /// dire qu'il n'y a rien à dire.
        /// </remarks>
        private static void WriteInterventions(StringBuilder html, ReportContext context)
        {
            if (context.TechnicianLog.Count == 0) return;

            html.Append("<h2>Journal d'intervention</h2>");
            html.Append("<div class=\"card\"><p class=\"muted\">")
                .Append("Opérations menées sur cette machine pendant la session, dans l'ordre. ")
                .Append("Les constats ci-dessous décrivent l'état relevé à l'analyse, avant ces opérations.")
                .Append("</p><ul class=\"log\">");

            foreach (var line in context.TechnicianLog)
                html.Append("<li>").Append(Html.Escape(line)).Append("</li>");

            html.Append("</ul></div>");
        }

        private static void WriteBanners(StringBuilder html, SystemSnapshot snapshot)
        {
            var profile = snapshot.Platform.Windows;
            if (profile.Level != CompatibilityLevel.Full)
            {
                var unavailable = 0;
                foreach (var feature in snapshot.Platform.Features)
                    if (feature.Availability == Availability.Unavailable) unavailable++;

                html.Append("<div class=\"banner warn\"><div><b>Couverture réduite sur cette version de Windows.</b> ")
                    .Append(Html.Escape(profile.LevelReason));
                if (unavailable > 0)
                    html.Append(' ').Append(unavailable.ToString(CultureInfo.CurrentCulture))
                        .Append(" contrôle(s) ne sont pas réalisables ici.");
                html.Append("</div></div>");
            }

            var blocked = ReportParts.ElevationBlockedCount(snapshot);
            if (blocked > 0)
            {
                html.Append("<div class=\"banner info\"><div><b>")
                    .Append(blocked.ToString(CultureInfo.CurrentCulture))
                    .Append(blocked > 1 ? " modules n'ont" : " module n'a")
                    .Append(" pas pu s'exécuter faute de privilèges administrateur.</b> ")
                    .Append("Les mesures concernées sont marquées « non lu » dans les tableaux qui suivent. ")
                    .Append("Une analyse relancée en session administrateur les compléterait.</div></div>");
            }
        }

        private static void WriteScore(StringBuilder html, SystemSnapshot snapshot)
        {
            var score = snapshot.Score;
            if (score == null) return;

            html.Append("<h2>État général</h2><div class=\"card\"><div class=\"score\">");
            ReportParts.ScoreDial(html, score);

            html.Append("<div class=\"grow\"><div class=\"eyebrow\">Score global</div>")
                .Append("<div class=\"band\" style=\"color:").Append(ReportParts.BandColor(score.Band))
                .Append("\">").Append(Html.Escape(DiagnosticScore.BandLabel(score.Band))).Append("</div>")
                .Append("<p style=\"margin-top:8px\">").Append(Html.Escape(Summarise(snapshot.Findings))).Append("</p>")
                .Append("<p class=\"tiny muted\" style=\"margin-top:6px\">Confiance ")
                .Append(Html.Escape(ReportParts.ConfidenceLabel(score.Confidence)))
                .Append(" : barème v").Append(Html.Escape(score.ProfileVersion)).Append("</p>");

            if (score.CapApplied != null)
                html.Append("<p class=\"tiny warn\" style=\"margin-top:6px\">")
                    .Append(Html.Escape(score.CapApplied)).Append("</p>");

            html.Append("</div></div></div>");

            html.Append("<div class=\"dims\">");
            foreach (var dimension in score.Dimensions)
            {
                var evaluated = dimension.IsEvaluated;
                var band = evaluated ? DiagnosticScore.BandOf(dimension.Score!.Value) : ScoreBand.Attention;
                var color = evaluated ? ReportParts.BandColor(band) : ReportStyles.Muted;
                var total = dimension.EvaluatedRules + dimension.SkippedRules;

                html.Append("<div class=\"dim\"><div class=\"top\"><span>")
                    .Append(Html.Escape(ReportParts.CategoryLabel(dimension.Dimension)))
                    .Append("</span><b style=\"color:").Append(color).Append("\">")
                    .Append(evaluated ? dimension.Score!.Value.ToString(CultureInfo.CurrentCulture) : "-")
                    .Append("</b></div><div class=\"track\"><i style=\"width:")
                    .Append(evaluated ? dimension.Score!.Value : 0).Append("%;background:").Append(color)
                    .Append("\"></i></div><div class=\"tiny muted\">");

                html.Append(evaluated
                    ? Html.Escape(dimension.EvaluatedRules + " contrôle" + (dimension.EvaluatedRules > 1 ? "s" : "") +
                                  " sur " + total + " · confiance " + ReportParts.ConfidenceLabel(dimension.Confidence))
                    : Html.Escape("aucun contrôle exploitable sur " + total + " : dimension exclue du calcul"));

                html.Append("</div>");

                if (dimension.CapApplied != null)
                    html.Append("<div class=\"tiny warn\" style=\"margin-top:4px\">")
                        .Append(Html.Escape(dimension.CapApplied)).Append("</div>");

                html.Append("</div>");
            }
            html.Append("</div>");
        }

        private static void WriteCorrelations(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.Correlations.Count == 0) return;

            html.Append("<h2>Conclusions</h2>");
            foreach (var correlation in snapshot.Correlations)
            {
                html.Append("<div class=\"finding\"><div class=\"stripe\" style=\"background:")
                    .Append(ReportParts.SeverityColor(correlation.Severity)).Append("\"></div><div class=\"body\">")
                    .Append("<div class=\"head\"><b>").Append(Html.Escape(correlation.Title)).Append("</b>")
                    .Append("<span class=\"tag ").Append(ReportParts.SeverityClass(correlation.Severity)).Append("\">")
                    .Append(Html.Escape(ReportParts.SeverityLabel(correlation.Severity)))
                    .Append("<span>").Append(Html.Escape(correlation.Id)).Append("</span></span></div>")
                    .Append("<p class=\"detail\">").Append(Html.Escape(correlation.Narrative)).Append("</p>")
                    .Append("</div></div>");
            }
        }

        private static void WriteRecommendations(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.Recommendations.Count == 0) return;

            html.Append("<h2>Plan d'action</h2><div class=\"actions\">");
            var rank = 1;
            foreach (var recommendation in snapshot.Recommendations)
            {
                html.Append("<div class=\"action\"><div class=\"rank\">")
                    .Append(rank++.ToString(CultureInfo.CurrentCulture)).Append("</div><div class=\"grow\">")
                    .Append("<b>").Append(Html.Escape(recommendation.Title)).Append("</b>")
                    .Append("<p class=\"muted\" style=\"margin-top:4px\">")
                    .Append(Html.Escape(recommendation.Rationale)).Append("</p>");

                if (recommendation.RequiresHardwarePurchase)
                    html.Append("<p class=\"tiny warn\" style=\"margin-top:4px\">Implique un achat de matériel.</p>");

                html.Append("</div><div class=\"side\"><b>")
                    .Append(Html.Escape(ReportParts.PriorityLabel(recommendation.Priority)))
                    .Append("</b><br><span class=\"muted\">")
                    .Append(Html.Escape(ReportParts.EffortLabel(recommendation.Effort)))
                    .Append("</span></div></div>");
            }
            html.Append("</div>");
        }

        private static void WriteFindings(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.Findings.Count == 0)
            {
                html.Append("<h2>Constats</h2><div class=\"card\"><p>Aucun constat : tous les contrôles ")
                    .Append("effectivement évalués sont conformes. Se reporter aux modules de collecte pour ")
                    .Append("connaître l'étendue réelle de l'analyse.</p></div>");
                return;
            }

            html.Append("<h2>Constats</h2>");

            foreach (DiagnosticCategory category in Enum.GetValues(typeof(DiagnosticCategory)))
            {
                var written = false;
                foreach (var finding in snapshot.Findings)
                {
                    if (finding.Category != category) continue;

                    if (!written)
                    {
                        html.Append("<h3 style=\"margin-top:18px\">")
                            .Append(Html.Escape(ReportParts.CategoryLabel(category))).Append("</h3>");
                        written = true;
                    }

                    WriteFinding(html, finding);
                }
            }
        }

        private static void WriteFinding(StringBuilder html, Finding finding)
        {
            html.Append("<div class=\"finding\"><div class=\"stripe\" style=\"background:")
                .Append(ReportParts.SeverityColor(finding.Severity)).Append("\"></div><div class=\"body\">")
                .Append("<div class=\"head\"><b>").Append(Html.Escape(finding.Title)).Append("</b>")
                .Append("<span class=\"tag ").Append(ReportParts.SeverityClass(finding.Severity)).Append("\">")
                .Append(Html.Escape(ReportParts.SeverityLabel(finding.Severity)))
                .Append("<span>").Append(Html.Escape(finding.RuleId)).Append("</span></span></div>")
                .Append("<p class=\"detail\">").Append(Html.Escape(finding.TechnicalDetail)).Append("</p>")
                .Append("<div class=\"plain\"><span class=\"eyebrow\">À dire au client</span><br>")
                .Append(Html.Escape(finding.PlainExplanation)).Append("</div>");

            if (finding.Evidence.Count > 0)
            {
                html.Append("<ul class=\"evidence\">");
                foreach (var evidence in finding.Evidence)
                {
                    html.Append("<li>").Append(Html.Escape(evidence.Label)).Append(" : ")
                        .Append(Html.Escape(evidence.Value));
                    if (evidence.Threshold != null)
                        html.Append("  (seuil ").Append(Html.Escape(evidence.Threshold)).Append(')');
                    html.Append(" : source ").Append(Html.Escape(SourceLabel(evidence.Source))).Append("</li>");
                }
                html.Append("</ul>");
            }

            html.Append("</div></div>");
        }

        private static void WriteFactSheets(StringBuilder html, SystemSnapshot snapshot)
        {
            var sheets = FactSheetBuilder.Build(snapshot);
            if (sheets.Count == 0) return;

            html.Append("<h2>Caractéristiques relevées</h2>");

            foreach (var sheet in sheets)
            {
                html.Append("<h3 style=\"margin-top:18px\">").Append(Html.Escape(sheet.Title)).Append("</h3>");
                foreach (var group in sheet.Groups) WriteGroup(html, group);
            }
        }

        private static void WriteGroup(StringBuilder html, FactGroup group)
        {
            html.Append("<div class=\"card\"><div class=\"eyebrow\">").Append(Html.Escape(group.Title))
                .Append("</div>");

            if (group.HasSubtitle)
                html.Append("<p class=\"tiny muted\" style=\"margin-top:3px\">")
                    .Append(Html.Escape(group.Subtitle)).Append("</p>");

            if (group.HasFacts)
            {
                html.Append("<dl class=\"facts\" style=\"margin-top:10px\">");
                foreach (var fact in group.Visible)
                {
                    var absent = !fact.IsKnown;
                    html.Append("<div class=\"fact").Append(absent ? " absent" : string.Empty).Append("\"><dt>")
                        .Append(Html.Escape(fact.Label)).Append("</dt><dd");

                    var tone = ToneClass(fact.Tone);
                    if (tone != null) html.Append(" class=\"").Append(tone).Append('"');

                    html.Append('>').Append(Html.Escape(fact.Value));

                    if (!string.IsNullOrEmpty(fact.Note))
                        html.Append("<span class=\"why\">").Append(Html.Escape(fact.Note)).Append("</span>");

                    html.Append("</dd></div>");
                }
                html.Append("</dl>");
            }

            if (group.HasNotCollected)
                html.Append("<p class=\"tiny muted\" style=\"margin-top:8px\">")
                    .Append(Html.Escape(group.NotCollectedNote)).Append("</p>");

            if (group.Table != null) WriteTable(html, group.Table);

            html.Append("</div>");
        }

        private static void WriteTable(StringBuilder html, FactTable table)
        {
            if (!table.HasRows)
            {
                html.Append("<p class=\"tiny muted\" style=\"margin-top:10px\">")
                    .Append(Html.Escape(table.EmptyMessage)).Append("</p>");
                return;
            }

            html.Append("<div class=\"wrap\"><table><thead><tr>");
            foreach (var column in table.Columns)
                html.Append("<th>").Append(Html.Escape(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");

            foreach (var row in table.Rows)
            {
                var cls = RowClass(row.Tone);
                html.Append(cls == null ? "<tr>" : "<tr class=\"" + cls + "\">");
                foreach (var cell in row.Cells)
                    html.Append("<td>").Append(Html.Escape(cell)).Append("</td>");
                html.Append("</tr>");
            }

            html.Append("</tbody></table></div>");
        }

        private static void WriteModules(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.ModuleReports.Count == 0) return;

            html.Append("<h2>Modules de collecte</h2><div class=\"card\">")
                .Append("<p class=\"tiny muted\">Un module qui n'aboutit pas ne signifie pas que la machine ")
                .Append("va bien sur ce point : il signifie que le point n'a pas été mesuré.</p>")
                .Append("<div class=\"wrap\"><table><thead><tr><th>Module</th><th>Domaine</th><th>État</th>")
                .Append("<th>Durée</th><th>Observation</th></tr></thead><tbody>");

            foreach (var report in snapshot.ModuleReports)
            {
                var cls = report.Status == ProbeStatus.Ok ? null
                    : report.Status == ProbeStatus.Failed || report.Status == ProbeStatus.TimedOut ? "bad"
                    : "warn";

                html.Append(cls == null ? "<tr>" : "<tr class=\"" + cls + "\">")
                    .Append("<td>").Append(Html.Escape(report.DisplayName)).Append("</td>")
                    .Append("<td>").Append(Html.Escape(ReportParts.CategoryLabel(report.Category))).Append("</td>")
                    .Append("<td>").Append(Html.Escape(ReportParts.StatusLabel(report.Status))).Append("</td>")
                    .Append("<td>").Append(report.DurationMs.ToString(CultureInfo.CurrentCulture)).Append(" ms</td>")
                    .Append("<td>").Append(Html.Escape(report.Message ?? report.ExceptionSummary ?? "-"))
                    .Append("</td></tr>");
            }

            html.Append("</tbody></table></div></div>");
        }

        private static void WriteLedger(StringBuilder html, SystemSnapshot snapshot)
        {
            var score = snapshot.Score;
            if (score == null) return;

            var rows = 0;
            var body = new StringBuilder();

            foreach (var dimension in score.Dimensions)
            {
                foreach (var penalty in dimension.Ledger)
                {
                    rows++;
                    body.Append("<tr><td>").Append(Html.Escape(ReportParts.CategoryLabel(penalty.Dimension)))
                        .Append("</td><td>").Append(Html.Escape(penalty.RuleId))
                        .Append("</td><td>").Append(Html.Escape(ReportParts.SeverityLabel(penalty.Severity)))
                        .Append("</td><td>−").Append(penalty.Points.ToString(CultureInfo.CurrentCulture))
                        .Append("</td><td>");

                    body.Append(penalty.CapApplied == null
                        ? "-"
                        : Html.Escape("plafond « " + penalty.CapApplied + " », " +
                                      penalty.PointsBeforeCap.ToString(CultureInfo.CurrentCulture) +
                                      " pt avant plafonnement"));

                    body.Append("</td><td>").Append(Html.Escape(penalty.Reason)).Append("</td></tr>");
                }
            }

            html.Append("<h2>Justification du score</h2><div class=\"card\">");

            if (rows == 0)
            {
                html.Append("<p>Aucun point retiré : le score global de ")
                    .Append(score.Global.ToString(CultureInfo.CurrentCulture))
                    .Append(" découle uniquement des dimensions évaluées.</p></div>");
                return;
            }

            html.Append("<p class=\"tiny muted\">Chaque point retiré se rattache à une règle et à une mesure. ")
                .Append("Pour chaque dimension, la somme des pénalités et le score brut redonnent exactement 100.</p>")
                .Append("<div class=\"wrap\"><table><thead><tr><th>Domaine</th><th>Règle</th><th>Gravité</th>")
                .Append("<th>Points</th><th>Plafonnement</th><th>Motif</th></tr></thead><tbody>")
                .Append(body).Append("</tbody></table></div></div>");
        }

        private static string Summarise(IReadOnlyList<Finding> findings)
        {
            if (findings.Count == 0) return "Aucun constat sur les contrôles évalués.";

            int critical = 0, problem = 0, warning = 0, info = 0;
            foreach (var finding in findings)
            {
                switch (finding.Severity)
                {
                    case Severity.Critical: critical++; break;
                    case Severity.Problem: problem++; break;
                    case Severity.Warning: warning++; break;
                    default: info++; break;
                }
            }

            var parts = new List<string>();
            if (critical > 0) parts.Add(critical + " constat" + (critical > 1 ? "s" : "") + " critique" + (critical > 1 ? "s" : ""));
            if (problem > 0) parts.Add(problem + " problème" + (problem > 1 ? "s" : ""));
            if (warning > 0) parts.Add(warning + " point" + (warning > 1 ? "s" : "") + " à surveiller");
            if (info > 0) parts.Add(info + " information" + (info > 1 ? "s" : ""));
            return string.Join(" · ", parts.ToArray()) + ".";
        }

        private static string? ToneClass(FactTone tone) => tone switch
        {
            FactTone.Good => "good",
            FactTone.Warning => "warn",
            FactTone.Bad => "bad",
            _ => null,
        };

        private static string? RowClass(FactTone tone) => tone switch
        {
            FactTone.Warning => "warn",
            FactTone.Bad => "bad",
            _ => null,
        };

        private static string SourceLabel(DataSource source) => source switch
        {
            DataSource.NativeApi => "API système",
            DataSource.Registry => "registre",
            DataSource.Wmi => "WMI",
            DataSource.Cli => "outil système",
            DataSource.PerformanceCounter => "compteur de performance",
            DataSource.EventLog => "journal d'événements",
            DataSource.FileSystem => "fichier système",
            DataSource.Inferred => "déduction",
            _ => "indéterminée",
        };
    }
}
