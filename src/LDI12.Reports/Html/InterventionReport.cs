using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Reports.Interventions;

namespace LDI12.Reports.Html
{
    /// <summary>
    /// Le rapport d'intervention : de la demande du client au résultat contrôlé.
    /// </summary>
    /// <remarks>
    /// Écrit au fil de l'intervention plutôt que de mémoire à la fin : la demande vient de
    /// l'accueil, les constats du diagnostic de départ, la décision du moment où elle a été
    /// prise, les travaux du journal, le résultat du diagnostic de contrôle. Le technicien n'a
    /// plus qu'à relire, et retoucher la conclusion que le logiciel lui propose.
    /// <para>
    /// Rédaction client : seules les explications en clair des constats y figurent, jamais leur
    /// détail technique ni leur identifiant.
    /// </para>
    /// </remarks>
    public static class InterventionReport
    {
        private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

        public static string Render(
            InterventionCase intervention, SystemSnapshot? before, SystemSnapshot? after, SnapshotDelta? delta,
            DateTimeOffset? issuedAt = null)
        {
            if (intervention == null) throw new ArgumentNullException(nameof(intervention));

            var context = new ReportContext
            {
                Technician = Blank(intervention.Technician),
                ClientReference = Blank(intervention.Reference),
                IssuedAt = issuedAt ?? DateTimeOffset.Now,
            };

            var html = new StringBuilder(24 * 1024);
            ReportParts.Document(html, "Rapport d'intervention : " + intervention.Title, body =>
            {
                ReportParts.Masthead(body, "Rapport d'intervention", intervention.Title, context.ClientReference, context);

                var meta = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Ordinateur", Blank(intervention.Machine) ?? after?.Machine.MachineName ?? "-"),
                    new KeyValuePair<string, string>("Pris en charge le", ValueFormat.Date(intervention.CreatedAt)),
                    new KeyValuePair<string, string>("Intervention", Describe(intervention.Path)),
                };
                ReportParts.MetaRow(body, meta);

                Request(body, intervention);
                Findings(body, before);
                Decision(body, intervention);
                Work(body, intervention);
                Result(body, before, after, delta);
                Remaining(body, after);
                Conclusion(body, intervention);

                body.Append("<div class=\"signatures\"><div><p class=\"eyebrow\">Le technicien</p></div>")
                    .Append("<div><p class=\"eyebrow\">Le client</p></div></div>");
                body.Append("<footer class=\"foot\"><p>Établi par LDI12 Diagnostic. Aucun document, courriel, photo ni ")
                    .Append("mot de passe du client n'a été ouvert ni analysé.</p></footer>");
            });

            return html.ToString();
        }

        /// <summary>
        /// Une conclusion proposée d'après le dossier : ce qui a été fait, et ce que ça a donné.
        /// </summary>
        public static string DraftConclusion(InterventionCase intervention, SystemSnapshot? before, SystemSnapshot? after, SnapshotDelta? delta)
        {
            if (intervention == null) throw new ArgumentNullException(nameof(intervention));
            var parts = new List<string>();

            if (intervention.Path == InterventionPath.Reinstall)
            {
                parts.Add("Windows a été réinstallé" +
                          (string.IsNullOrWhiteSpace(intervention.BackupSummary) ? "." : ", après une sauvegarde vérifiée des données."));
                if (!string.IsNullOrWhiteSpace(intervention.RestoreSummary))
                    parts.Add("Les données ont été restaurées sur le système neuf.");
            }
            else if (intervention.Actions.Count > 0)
            {
                parts.Add(intervention.Actions.Count + " opération(s) de réparation ont été menées.");
            }

            if (delta != null)
            {
                var resolved = delta.Count(FindingChangeKind.Resolved);
                if (resolved > 0) parts.Add(resolved + " problème(s) relevé(s) au départ ne se présentent plus.");
                var appeared = delta.Count(FindingChangeKind.Appeared);
                if (appeared > 0) parts.Add(appeared + " point(s) nouveau(x) à surveiller.");
                if (delta.ScoreComparable && delta.Before.Score.HasValue && delta.After.Score.HasValue)
                    parts.Add("L'état général passe de " + delta.Before.Score + " à " + delta.After.Score + " sur 100.");
            }
            else if (after?.Score != null)
            {
                parts.Add("État général au contrôle final : " + after.Score.Global + " sur 100.");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
        }

        private static void Request(StringBuilder html, InterventionCase intervention)
        {
            if (intervention.Reasons.Count == 0 && string.IsNullOrWhiteSpace(intervention.Request)) return;

            html.Append("<h2>Votre demande</h2><div class=\"card\">");
            if (intervention.Reasons.Count > 0)
                html.Append("<p><strong>").Append(Html.Escape(string.Join(" · ", intervention.Reasons))).Append("</strong></p>");
            if (!string.IsNullOrWhiteSpace(intervention.Request))
                html.Append("<p>").Append(Html.Escape(intervention.Request)).Append("</p>");
            Notes(html, intervention, InterventionStage.Welcome);
            html.Append("</div>");
        }

        private static void Findings(StringBuilder html, SystemSnapshot? before)
        {
            if (before == null) return;

            var shown = new List<Finding>();
            foreach (var finding in before.Findings)
                if (finding.Severity >= Severity.Warning) shown.Add(finding);
            shown.Sort((a, b) => b.Severity.CompareTo(a.Severity));

            html.Append("<h2>Ce que nous avons constaté</h2><div class=\"card\">");
            if (before.Score != null)
                html.Append("<p>État général à l'arrivée : <strong>").Append(before.Score.Global).Append(" sur 100</strong>.</p>");

            if (shown.Count == 0)
            {
                html.Append("<p>Aucun problème notable n'a été relevé par le diagnostic.</p>");
            }
            else
            {
                html.Append("<ul class=\"log\">");
                foreach (var finding in shown.Count > 12 ? shown.GetRange(0, 12) : shown)
                    html.Append("<li>").Append(Html.Escape(finding.PlainExplanation)).Append("</li>");
                html.Append("</ul>");
            }

            html.Append("</div>");
        }

        private static void Decision(StringBuilder html, InterventionCase intervention)
        {
            if (intervention.Path == InterventionPath.Undecided) return;

            html.Append("<h2>Notre décision</h2><div class=\"card\"><p><strong>")
                .Append(Html.Escape(intervention.Path == InterventionPath.Reinstall
                    ? "Réinstallation de Windows, avec sauvegarde et restauration de vos données."
                    : "Réparation du système en place."))
                .Append("</strong></p>");
            Notes(html, intervention, InterventionStage.Decision);
            html.Append("</div>");
        }

        private static void Work(StringBuilder html, InterventionCase intervention)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(intervention.BackupSummary)) lines.Add("Sauvegarde : " + intervention.BackupSummary);
            if (!string.IsNullOrWhiteSpace(intervention.VerificationSummary)) lines.Add("Contrôle de la sauvegarde : " + intervention.VerificationSummary);
            if (intervention.Path == InterventionPath.Reinstall && intervention.Stage > InterventionStage.Reinstall)
                lines.Add("Réinstallation de Windows.");
            if (!string.IsNullOrWhiteSpace(intervention.RestoreSummary)) lines.Add("Restauration : " + intervention.RestoreSummary);
            foreach (var action in intervention.Actions)
                lines.Add(action.Title + (string.IsNullOrWhiteSpace(action.Summary) ? "" : " : " + action.Summary));

            var notes = new List<InterventionNote>();
            foreach (var note in intervention.Notes)
                if (note.Stage >= InterventionStage.Backup && note.Stage <= InterventionStage.Verification) notes.Add(note);

            if (lines.Count == 0 && notes.Count == 0) return;

            html.Append("<h2>Ce que nous avons fait</h2><div class=\"card\"><ul class=\"log\">");
            foreach (var line in lines) html.Append("<li>").Append(Html.Escape(line)).Append("</li>");
            foreach (var note in notes) html.Append("<li>").Append(Html.Escape(note.Text)).Append("</li>");
            html.Append("</ul></div>");
        }

        private static void Result(StringBuilder html, SystemSnapshot? before, SystemSnapshot? after, SnapshotDelta? delta)
        {
            if (after == null) return;

            html.Append("<h2>Le résultat</h2><div class=\"card\">");
            if (delta != null && delta.ScoreComparable && delta.Before.Score.HasValue && delta.After.Score.HasValue)
                html.Append("<p>État général : <strong>").Append(delta.Before.Score).Append(" → ").Append(delta.After.Score)
                    .Append(" sur 100</strong>.</p>");
            else if (after.Score != null)
                html.Append("<p>État général au contrôle final : <strong>").Append(after.Score.Global).Append(" sur 100</strong>.</p>");

            if (delta != null)
            {
                List("Réglé", delta, FindingChangeKind.Resolved, html);
                List("Toujours présent", delta, FindingChangeKind.Persisting, html);
                List("Nouveau, à surveiller", delta, FindingChangeKind.Appeared, html);
            }

            html.Append("</div>");
        }

        private static void List(string title, SnapshotDelta delta, FindingChangeKind kind, StringBuilder html)
        {
            var items = new List<string>();
            foreach (var change in delta.Findings)
                if (change.Kind == kind && change.Finding.Severity >= Severity.Warning) items.Add(change.Finding.PlainExplanation);
            if (items.Count == 0) return;

            html.Append("<p class=\"eyebrow\">").Append(Html.Escape(title)).Append("</p><ul class=\"log\">");
            foreach (var item in items) html.Append("<li>").Append(Html.Escape(item)).Append("</li>");
            html.Append("</ul>");
        }

        private static void Remaining(StringBuilder html, SystemSnapshot? after)
        {
            if (after == null || after.Recommendations.Count == 0) return;

            html.Append("<h2>Nos conseils</h2><div class=\"card\"><ul class=\"log\">");
            var shown = 0;
            foreach (var recommendation in after.Recommendations)
            {
                if (shown++ == 6) break;
                html.Append("<li><strong>").Append(Html.Escape(recommendation.Title)).Append("</strong> : ")
                    .Append(Html.Escape(recommendation.Rationale)).Append("</li>");
            }
            html.Append("</ul></div>");
        }

        private static void Conclusion(StringBuilder html, InterventionCase intervention)
        {
            if (string.IsNullOrWhiteSpace(intervention.Conclusion)) return;
            html.Append("<h2>En résumé</h2><div class=\"card\"><p>").Append(Html.Escape(intervention.Conclusion))
                .Append("</p>");
            Notes(html, intervention, InterventionStage.Done);
            html.Append("</div>");
        }

        private static void Notes(StringBuilder html, InterventionCase intervention, InterventionStage stage)
        {
            foreach (var note in intervention.Notes)
                if (note.Stage == stage) html.Append("<p>").Append(Html.Escape(note.Text)).Append("</p>");
        }

        private static string Describe(InterventionPath path) => path switch
        {
            InterventionPath.Reinstall => "Réinstallation",
            InterventionPath.Repair => "Réparation",
            _ => "En cours",
        };

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}
