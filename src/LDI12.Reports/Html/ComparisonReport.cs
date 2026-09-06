using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Reports.Html
{
    /// <summary>
    /// Comparatif avant / après : ce que l'intervention a changé.
    /// </summary>
    /// <remarks>
    /// Le document le plus utile de la série, et le plus facile à rendre malhonnête. Un client
    /// qui paie une réparation veut voir ce qu'elle a produit ; tout ce qui a disparu du second
    /// diagnostic ressemble à une réussite, et il suffit de tout compter ensemble pour obtenir un
    /// document flatteur qui ne prouve rien.
    ///
    /// Il est donc écrit dans la rédaction client (aucun identifiant de règle, aucun jargon,
    /// les mêmes garde-fous vérifiés que le bilan) avec trois principes propres :
    ///
    /// 1. <b>Les réserves passent avant le résultat.</b> Elles sont en tête du document, pas en
    ///    note de bas de page : un score qui monte parce que le second passage a moins regardé
    ///    n'est pas une réparation, et le lecteur doit le savoir avant de lire le chiffre.
    /// 2. <b>Ce qui n'a pas été revérifié a sa propre section</b>, jamais mélangé aux constats
    ///    réglés.
    /// 3. <b>Les deux scores ne se soustraient que si le barème est le même</b> ; sinon les deux
    ///    chiffres sont donnés sans différence, avec la raison.
    /// </remarks>
    public static class ComparisonReport
    {
        public static string Render(SnapshotDelta delta, ReportContext? context = null)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            context ??= ReportContext.Default;

            var machine = string.IsNullOrWhiteSpace(delta.After.MachineName)
                ? "votre ordinateur"
                : delta.After.MachineName;

            var html = new StringBuilder(20 * 1024);

            ReportParts.Document(html, "Ce qui a changé : " + machine, body =>
            {
                ReportParts.Masthead(body, "Comparatif avant / après", "Ce qui a changé sur votre ordinateur",
                    context.ClientReference ?? delta.After.ClientReference, context);

                WriteMeta(body, delta);
                WriteNote(body, context);
                WriteIdentity(body, delta);
                WriteCaveats(body, delta);
                WriteScore(body, delta);
                WriteInterventions(body, context);
                WriteResolved(body, delta);
                WriteAppeared(body, delta);
                WriteNotRechecked(body, delta);
                WritePersisting(body, delta);
                WriteMeasures(body, delta);

                WriteFooter(body, delta);
            });

            return html.ToString();
        }

        // ============================================================ en-tête

        private static void WriteMeta(StringBuilder html, SnapshotDelta delta)
        {
            var entries = new List<KeyValuePair<string, string>>();

            // Un intitulé sans valeur en face est pire que pas d'intitulé du tout : le lecteur
            // croit que l'information manque alors qu'elle n'a jamais existé sur cette machine.
            if (!string.IsNullOrWhiteSpace(delta.After.MachineName))
                entries.Add(new KeyValuePair<string, string>("Ordinateur", delta.After.MachineName));

            entries.Add(new KeyValuePair<string, string>(
                "Première vérification", ValueFormat.DateTime(delta.Before.CreatedAt)));
            entries.Add(new KeyValuePair<string, string>(
                "Seconde vérification", ValueFormat.DateTime(delta.After.CreatedAt)));

            var elapsed = delta.After.CreatedAt - delta.Before.CreatedAt;
            if (elapsed > TimeSpan.Zero)
                entries.Add(new KeyValuePair<string, string>("Entre les deux", ValueFormat.Duration(elapsed)));

            ReportParts.MetaRow(html, entries);
        }

        private static void WriteNote(StringBuilder html, ReportContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Note)) return;

            html.Append("<div class=\"card\"><div class=\"eyebrow\">Observation</div><p style=\"margin-top:7px\">")
                .Append(Html.Escape(context.Note)).Append("</p></div>");
        }

        /// <summary>
        /// Deux diagnostics qui ne viennent pas de la même machine, ou dont rien ne le prouve.
        /// </summary>
        /// <remarks>
        /// Silencieux dans le seul cas où la question est tranchée par l'empreinte : ajouter
        /// « il s'agit bien du même ordinateur » à un document qui n'aurait aucune raison d'en
        /// douter installerait un doute que personne n'avait.
        /// </remarks>
        private static void WriteIdentity(StringBuilder html, SnapshotDelta delta)
        {
            if (delta.SameMachine.IsReliable && delta.SameMachine.Value) return;

            if (delta.SameMachine.HasValue && !delta.SameMachine.Value)
            {
                Banner(html, "warn",
                    "Ces deux vérifications ne portent pas sur le même ordinateur. La comparaison " +
                    "ci-dessous n'a donc pas de sens : elle est conservée telle quelle, sans être " +
                    "interprétée.");
                return;
            }

            Banner(html, "info",
                "Rien ne permet d'établir avec certitude que les deux vérifications portent sur le même " +
                "ordinateur. Les éléments qui l'identifient n'ont pas pu être relevés lors de l'une des deux.");
        }

        /// <summary>
        /// Les réserves, avant le résultat.
        /// </summary>
        /// <remarks>
        /// Leur place dans le document est la décision de conception la plus importante de ce
        /// fichier. En bas de page, personne ne les lit ; en tête, elles cadrent la lecture du
        /// chiffre qui suit.
        /// </remarks>
        private static void WriteCaveats(StringBuilder html, SnapshotDelta delta)
        {
            if (delta.Caveats.Count == 0) return;

            html.Append("<div class=\"banner warn\"><div><b>À lire avant le reste</b>");
            foreach (var caveat in delta.Caveats)
                html.Append("<p style=\"margin-top:7px\">").Append(Html.Escape(caveat)).Append("</p>");
            html.Append("</div></div>");
        }

        // ============================================================ score

        private static void WriteScore(StringBuilder html, SnapshotDelta delta)
        {
            html.Append("<h2>État général</h2>");

            if (!delta.ScoreComparable || !delta.After.Score.HasValue || !delta.Before.Score.HasValue)
            {
                WriteIncomparableScore(html, delta);
                return;
            }

            var after = delta.After.Score!.Value;
            var band = DiagnosticScore.BandOf(after);
            var change = delta.ScoreDelta ?? 0;

            html.Append("<div class=\"card\"><div class=\"score\">");
            html.Append("<div class=\"dial\">").Append(Html.ScoreArc(after, ReportParts.BandColor(band)))
                .Append("<div class=\"value\"><b>").Append(after.ToString(CultureInfo.CurrentCulture))
                .Append("</b><span>sur 100</span></div></div>");

            html.Append("<div><div class=\"eyebrow\">Après intervention</div><div class=\"band\">")
                .Append(Html.Escape(DiagnosticScore.BandLabel(band))).Append("</div><p style=\"margin-top:9px\">")
                .Append(Html.Escape(Sentence(delta.Before.Score!.Value, after, change)))
                .Append("</p></div></div>");

            WriteDimensions(html, delta);
            html.Append("</div>");
        }

        private static string Sentence(int before, int after, int change)
        {
            var day = "Lors de la première vérification, l'ordinateur était noté " +
                      before.ToString(CultureInfo.CurrentCulture) + " sur 100. ";

            if (change == 0) return day + "Il obtient la même note aujourd'hui.";

            return day + "Il obtient aujourd'hui " + after.ToString(CultureInfo.CurrentCulture) +
                   (change > 0
                       ? ", soit " + change + (change > 1 ? " points de mieux." : " point de mieux.")
                       : ", soit " + Math.Abs(change) + (change < -1 ? " points de moins." : " point de moins."));
        }

        private static void WriteIncomparableScore(StringBuilder html, SnapshotDelta delta)
        {
            html.Append("<div class=\"card\"><p>");

            if (!delta.Before.Score.HasValue || !delta.After.Score.HasValue)
            {
                html.Append("L'une des deux vérifications n'a pas produit de note d'ensemble : seuls les " +
                            "points relevés se comparent ci-dessous.");
            }
            else
            {
                html.Append("Note de la première vérification : ")
                    .Append(delta.Before.Score!.Value.ToString(CultureInfo.CurrentCulture))
                    .Append(" sur 100. Note de la seconde : ")
                    .Append(delta.After.Score!.Value.ToString(CultureInfo.CurrentCulture))
                    .Append(" sur 100. Ces deux notes n'ont pas été calculées de la même façon : " +
                            "leur différence ne veut rien dire, et n'est donc pas donnée.");
            }

            html.Append("</p></div>");
        }

        /// <summary>Évolution par domaine, seulement pour ceux qui ont été notés des deux côtés.</summary>
        private static void WriteDimensions(StringBuilder html, SnapshotDelta delta)
        {
            var rows = new List<DimensionDelta>();
            foreach (var dimension in delta.Dimensions)
                if (dimension.Delta.HasValue) rows.Add(dimension);

            if (rows.Count == 0) return;

            html.Append("<div class=\"dims\">");
            foreach (var dimension in rows)
            {
                var after = dimension.After!.Value;
                var change = dimension.Delta!.Value;
                var color = ReportParts.BandColor(DiagnosticScore.BandOf(after));

                html.Append("<div class=\"dim\"><div class=\"top\"><span>")
                    .Append(Html.Escape(ReportParts.CategoryLabel(dimension.Dimension)))
                    .Append("</span><b>").Append(after.ToString(CultureInfo.CurrentCulture)).Append("</b></div>")
                    .Append("<div class=\"track\"><i style=\"width:").Append(after.ToString(CultureInfo.InvariantCulture))
                    .Append("%;background:").Append(color).Append("\"></i></div>")
                    .Append("<div class=\"tiny muted\">")
                    .Append(Html.Escape(DescribeChange(dimension.Before!.Value, change)))
                    .Append("</div></div>");
            }
            html.Append("</div>");
        }

        private static string DescribeChange(int before, int change)
        {
            if (change == 0) return "inchangé (" + before.ToString(CultureInfo.CurrentCulture) + " auparavant)";
            var word = change > 0 ? "+" : "−";
            return word + Math.Abs(change) + " depuis la première vérification (" +
                   before.ToString(CultureInfo.CurrentCulture) + ")";
        }

        // ============================================================ interventions

        private static void WriteInterventions(StringBuilder html, ReportContext context)
        {
            if (context.ClientLog.Count == 0) return;

            html.Append("<h2>Ce qui a été fait</h2><div class=\"card\"><ul class=\"log\">");
            foreach (var line in context.ClientLog)
                html.Append("<li>").Append(Html.Escape(line)).Append("</li>");
            html.Append("</ul></div>");
        }

        // ============================================================ constats

        private static void WriteResolved(StringBuilder html, SnapshotDelta delta)
        {
            var changes = Select(delta, FindingChangeKind.Resolved);
            if (changes.Count == 0) return;

            html.Append("<h2>Ce qui a été réglé</h2>")
                .Append("<p class=\"muted\">Ces points étaient présents lors de la première vérification. " +
                        "La seconde les a contrôlés de nouveau et ne les trouve plus.</p>");

            foreach (var change in changes) WriteFinding(html, change, "Ce que c'était", resolved: true);
        }

        private static void WriteAppeared(StringBuilder html, SnapshotDelta delta)
        {
            var changes = Select(delta, FindingChangeKind.Appeared);
            if (changes.Count == 0) return;

            html.Append("<h2>Ce qui est apparu depuis</h2>")
                .Append("<p class=\"muted\">Ces points n'existaient pas lors de la première vérification.</p>");

            foreach (var change in changes) WriteFinding(html, change, "Ce que cela signifie", resolved: false);
        }

        /// <summary>
        /// La section qui empêche ce document de mentir.
        /// </summary>
        /// <remarks>
        /// Sans elle, ces constats disparaîtraient purement et simplement du comparatif, et leur
        /// absence se lirait comme une réparation. L'explication est donnée en clair, sans citer
        /// le contrôle concerné : ce qui compte pour le lecteur, c'est qu'on ne lui affirme rien.
        /// </remarks>
        private static void WriteNotRechecked(StringBuilder html, SnapshotDelta delta)
        {
            var changes = Select(delta, FindingChangeKind.NotRechecked);
            if (changes.Count == 0) return;

            html.Append("<h2>Ce qui n'a pas pu être revérifié</h2>");
            Banner(html, "warn",
                "Ces points n'apparaissent plus dans la seconde vérification, mais le contrôle qui les " +
                "avait détectés n'a pas pu être refait : la seconde vérification n'a pas porté sur les " +
                "mêmes éléments que la première. Ils ne sont donc ni réglés ni aggravés : ils n'ont pas " +
                "été revus. Une vérification complète les tranchera.");

            foreach (var change in changes) WriteFinding(html, change, "Ce que c'était", resolved: false);
        }

        private static void WritePersisting(StringBuilder html, SnapshotDelta delta)
        {
            var changes = Select(delta, FindingChangeKind.Persisting);
            if (changes.Count == 0) return;

            html.Append("<h2>Ce qui n'a pas changé</h2>")
                .Append("<p class=\"muted\">Ces points étaient présents avant et le sont toujours.</p>");

            foreach (var change in changes) WriteFinding(html, change, "Ce que cela signifie", resolved: false);
        }

        /// <summary>
        /// Un constat, dans la rédaction destinée au client.
        /// </summary>
        /// <remarks>
        /// Seule <see cref="Finding.PlainExplanation"/> est utilisée, jamais le détail technique,
        /// jamais les mesures d'appui, jamais l'identifiant de la règle. C'est la même discipline
        /// que le bilan client, et les mêmes tests la vérifient.
        /// </remarks>
        private static void WriteFinding(StringBuilder html, FindingChange change, string label, bool resolved)
        {
            var finding = change.Finding;
            var color = resolved ? ReportStyles.Good : ReportParts.SeverityColor(finding.Severity);

            html.Append("<div class=\"finding\"><div class=\"stripe\" style=\"background:").Append(color)
                .Append("\"></div><div class=\"body\"><div class=\"head\"><b>")
                .Append(Html.Escape(finding.Title)).Append("</b><span class=\"tag ")
                .Append(resolved ? "good" : ReportParts.SeverityClass(finding.Severity)).Append("\">")
                .Append(Html.Escape(resolved ? "Réglé" : ReportParts.SeverityLabel(finding.Severity)))
                .Append("</span></div>");

            if (!string.IsNullOrWhiteSpace(finding.PlainExplanation))
            {
                html.Append("<div class=\"plain\"><b class=\"tiny\">").Append(Html.Escape(label))
                    .Append(" : </b>").Append(Html.Escape(finding.PlainExplanation)).Append("</div>");
            }

            html.Append("</div></div>");
        }

        private static IReadOnlyList<FindingChange> Select(SnapshotDelta delta, FindingChangeKind kind)
        {
            var changes = new List<FindingChange>();
            foreach (var change in delta.Findings)
                if (change.Kind == kind) changes.Add(change);
            return changes;
        }

        // ============================================================ mesures

        private static void WriteMeasures(StringBuilder html, SnapshotDelta delta)
        {
            if (delta.Measures.Count == 0) return;

            html.Append("<h2>Mesures suivies</h2><div class=\"card\"><div class=\"wrap\"><table>")
                .Append("<thead><tr><th>Mesure</th><th>Avant</th><th>Après</th><th>Évolution</th></tr></thead><tbody>");

            foreach (var measure in delta.Measures)
            {
                var row = measure.Direction == ChangeDirection.Worsened ? " class=\"warn\"" : string.Empty;

                html.Append("<tr").Append(row).Append("><td>").Append(Html.Escape(measure.Label)).Append("</td>")
                    .Append("<td>").Append(Html.Escape(Cell(measure.Before, measure.Unit))).Append("</td>")
                    .Append("<td>").Append(Html.Escape(Cell(measure.After, measure.Unit))).Append("</td>")
                    .Append("<td>").Append(Html.Escape(Verdict(measure))).Append("</td></tr>");

                if (!string.IsNullOrWhiteSpace(measure.Note))
                {
                    html.Append("<tr><td colspan=\"4\" class=\"tiny muted\">")
                        .Append(Html.Escape(measure.Note)).Append("</td></tr>");
                }
            }

            html.Append("</tbody></table></div></div>");
        }

        private static string Cell(Measured<double> measured, string unit)
        {
            if (!measured.HasValue) return "non mesuré";

            var value = measured.Value.ToString(
                Math.Abs(measured.Value) < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
            return unit.Length == 0 ? value : value + " " + unit;
        }

        private static string Verdict(MeasureChange measure) => measure.Direction switch
        {
            ChangeDirection.Improved => "amélioration",
            ChangeDirection.Worsened => "en recul",
            ChangeDirection.Unchanged => "inchangé",
            _ => "non comparable",
        };

        // ============================================================ pied

        private static void WriteFooter(StringBuilder html, SnapshotDelta delta)
        {
            if (delta.NothingChanged)
            {
                Banner(html, "info",
                    "Aucune différence n'a été relevée entre les deux vérifications, et tous les contrôles " +
                    "de la première ont bien été refaits par la seconde.");
            }

            html.Append("<footer class=\"foot\"><p>")
                .Append("Ce comparatif rapproche deux vérifications de votre ordinateur. Aucun document, " +
                        "courriel, photo ni mot de passe n'a été ouvert ni analysé.")
                .Append("</p><p>Vérifications du ")
                .Append(Html.Escape(ValueFormat.DateTime(delta.Before.CreatedAt)))
                .Append(" et du ").Append(Html.Escape(ValueFormat.DateTime(delta.After.CreatedAt)))
                .Append(" : références internes ").Append(Html.Escape(delta.Before.SnapshotId.ToString()))
                .Append(" et ").Append(Html.Escape(delta.After.SnapshotId.ToString()))
                .Append("</p></footer>");
        }

        private static void Banner(StringBuilder html, string kind, string text)
            => html.Append("<div class=\"banner ").Append(kind).Append("\"><div><p>")
                   .Append(Html.Escape(text)).Append("</p></div></div>");
    }
}
