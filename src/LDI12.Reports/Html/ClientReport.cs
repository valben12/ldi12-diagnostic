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
    /// <summary>
    /// Bilan client : ce que la personne qui possède la machine a besoin de savoir.
    /// </summary>
    /// <remarks>
    /// Ce document n'est pas un rapport technicien allégé, c'est un autre document. Trois règles
    /// le gouvernent, et elles sont vérifiées par les tests :
    ///
    /// 1. <b>Aucun jargon, aucun identifiant de règle, aucun code d'erreur.</b> Seul
    ///    <see cref="Finding.PlainExplanation"/> est utilisé, jamais
    ///    <see cref="Finding.TechnicalDetail"/>, jamais les mesures d'appui. C'est ce que le
    ///    double libellé des règles rend possible : les deux rapports sortent d'une seule analyse.
    ///
    /// 2. <b>On dit aussi ce qui va bien.</b> Un bilan qui n'énumère que des problèmes se lit
    ///    comme un argumentaire de vente. Un domaine n'est déclaré sain que s'il a réellement été
    ///    évalué et qu'aucun constat n'y dépasse l'information, pas parce qu'on n'a rien mesuré.
    ///
    /// 3. <b>On dit ce qu'on n'a pas pu vérifier.</b> C'est la contrepartie de la deuxième règle :
    ///    sans elle, « tout va bien » et « nous n'avons pas regardé » deviennent indiscernables
    ///    pour quelqu'un qui n'a pas les moyens de faire la différence.
    /// </remarks>
    public static class ClientReport
    {
        public static string Render(SystemSnapshot snapshot, ReportContext? context = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            context ??= ReportContext.Default;

            var machine = snapshot.Machine.MachineName;
            var html = new StringBuilder(24 * 1024);

            ReportParts.Document(html, "Bilan de votre ordinateur : " + machine, body =>
            {
                ReportParts.Masthead(body, "Bilan pour le client", "Bilan de votre ordinateur", snapshot, context);
                WriteMeta(body, snapshot);
                WriteNote(body, context);
                WriteScore(body, snapshot);
                WriteStrengths(body, snapshot);
                WriteMeaning(body, snapshot);
                WriteConcerns(body, snapshot);
                WriteInterventions(body, context);
                WriteRecommendations(body, snapshot);
                WriteNotes(body, snapshot);
                WriteLimits(body, snapshot);

                ReportParts.Footer(body, snapshot,
                    "Ce bilan décrit l'état de votre ordinateur au moment de notre intervention. " +
                    "Aucun document, courriel, photo ni mot de passe n'a été ouvert ni analysé.");
            });

            return html.ToString();
        }

        private static void WriteMeta(StringBuilder html, SystemSnapshot snapshot)
        {
            var entries = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Ordinateur", snapshot.Machine.MachineName),
                new KeyValuePair<string, string>("Vérifié le", ValueFormat.Date(snapshot.Metadata.CreatedAt)),
            };

            if (snapshot.Machine.Model.HasValue)
                entries.Insert(1, new KeyValuePair<string, string>("Modèle", snapshot.Machine.Model.Value));

            ReportParts.MetaRow(html, entries);
        }

        private static void WriteNote(StringBuilder html, ReportContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Note)) return;
            html.Append("<div class=\"card tight\"><p>").Append(Html.Escape(context.Note)).Append("</p></div>");
        }

        /// <summary>
        /// Ce qui a été fait sur l'ordinateur, dit au client.
        /// </summary>
        /// <remarks>
        /// Un client a le droit de savoir ce qu'on a touché chez lui, et de le retrouver écrit
        /// plutôt que de s'en souvenir. Seules les opérations réellement exécutées figurent ici :
        /// une prévisualisation n'a rien modifié, et l'ouverture d'une console non plus.
        /// </remarks>
        private static void WriteInterventions(StringBuilder html, ReportContext context)
        {
            if (context.ClientLog.Count == 0) return;

            html.Append("<h2>Ce que nous avons fait sur votre ordinateur</h2>");
            html.Append("<div class=\"card\"><ul class=\"log\">");

            foreach (var line in context.ClientLog)
                html.Append("<li>").Append(Html.Escape(line)).Append("</li>");

            html.Append("</ul></div>");
        }

        private static void WriteScore(StringBuilder html, SystemSnapshot snapshot)
        {
            var score = snapshot.Score;
            if (score == null)
            {
                html.Append("<div class=\"card\"><p>L'analyse n'a pas pu être menée à son terme. ")
                    .Append("Ce document ne comporte donc pas d'état général.</p></div>");
                return;
            }

            html.Append("<h2>État général</h2><div class=\"card\"><div class=\"score\">");
            ReportParts.ScoreDial(html, score);

            html.Append("<div class=\"grow\"><div class=\"band\" style=\"color:")
                .Append(ReportParts.BandColor(score.Band)).Append("\">")
                .Append(Html.Escape(DiagnosticScore.BandLabel(score.Band))).Append("</div>")
                .Append("<p style=\"margin-top:8px\">")
                .Append(Html.Escape(BandSentence(score.Band, snapshot.Recommendations.Count))).Append("</p>");

            if (score.Confidence != ScoreConfidence.Full)
                html.Append("<p class=\"tiny muted\" style=\"margin-top:8px\">")
                    .Append("Cette note porte sur ce que nous avons pu vérifier. Les points que nous n'avons ")
                    .Append("pas pu contrôler sont indiqués en fin de document.</p>");

            html.Append("</div></div></div>");
        }

        private static void WriteStrengths(StringBuilder html, SystemSnapshot snapshot)
        {
            var score = snapshot.Score;
            if (score == null) return;

            var lines = new List<string>();
            foreach (var dimension in score.Dimensions)
            {
                if (!dimension.IsEvaluated) continue;
                if (HasConcern(snapshot, dimension.Dimension)) continue;

                var sentence = StrengthSentence(dimension.Dimension);
                if (sentence != null) lines.Add(sentence);
            }

            if (lines.Count == 0) return;

            html.Append("<h2>Ce qui va bien</h2><div class=\"card\"><ul style=\"margin:0;padding-left:20px\">");
            foreach (var line in lines)
                html.Append("<li style=\"padding:3px 0\">").Append(Html.Escape(line)).Append("</li>");
            html.Append("</ul></div>");
        }

        private static void WriteMeaning(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.Correlations.Count == 0) return;

            html.Append("<h2>Ce que cela signifie</h2>");
            foreach (var correlation in snapshot.Correlations)
                html.Append("<div class=\"finding\"><div class=\"stripe\" style=\"background:")
                    .Append(ReportParts.SeverityColor(correlation.Severity)).Append("\"></div><div class=\"body\">")
                    .Append("<b>").Append(Html.Escape(correlation.Title)).Append("</b>")
                    .Append("<p class=\"detail\">").Append(Html.Escape(correlation.Narrative)).Append("</p>")
                    .Append("</div></div>");
        }

        private static void WriteConcerns(StringBuilder html, SystemSnapshot snapshot)
        {
            var written = false;

            // Deux règles distinctes peuvent aboutir au même message pour le client : c'est le
            // propre du double libellé, qui sépare ce que le technicien lit de ce que le client
            // comprend. Répéter la phrase à l'identique ne dit rien de plus et donne l'impression
            // d'un document mal relu.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var severity in new[] { Severity.Critical, Severity.Problem, Severity.Warning })
            {
                foreach (var finding in snapshot.Findings)
                {
                    if (finding.Severity != severity) continue;
                    if (!seen.Add(finding.Title + "" + finding.PlainExplanation)) continue;

                    if (!written)
                    {
                        html.Append("<h2>Ce qu'il faut surveiller</h2>");
                        written = true;
                    }

                    html.Append("<div class=\"finding\"><div class=\"stripe\" style=\"background:")
                        .Append(ReportParts.SeverityColor(severity)).Append("\"></div><div class=\"body\">")
                        .Append("<div class=\"head\"><b>").Append(Html.Escape(finding.Title)).Append("</b>")
                        .Append("<span class=\"tag ").Append(ReportParts.SeverityClass(severity)).Append("\">")
                        .Append(Html.Escape(ClientSeverity(severity))).Append("</span></div>")
                        .Append("<p class=\"detail\">").Append(Html.Escape(finding.PlainExplanation))
                        .Append("</p></div></div>");
                }
            }

            if (!written && snapshot.Score != null)
                html.Append("<h2>Ce qu'il faut surveiller</h2><div class=\"card\"><p>")
                    .Append("Rien à signaler : aucun des points que nous avons vérifiés ne demande ")
                    .Append("d'intervention.</p></div>");
        }

        private static void WriteRecommendations(StringBuilder html, SystemSnapshot snapshot)
        {
            if (snapshot.Recommendations.Count == 0) return;

            html.Append("<h2>Ce que nous vous recommandons</h2><div class=\"actions\">");
            var rank = 1;

            foreach (var recommendation in snapshot.Recommendations)
            {
                html.Append("<div class=\"action\"><div class=\"rank\">")
                    .Append(rank++.ToString(CultureInfo.CurrentCulture)).Append("</div><div class=\"grow\">")
                    .Append("<b>").Append(Html.Escape(recommendation.Title)).Append("</b>")
                    .Append("<p class=\"muted\" style=\"margin-top:4px\">")
                    .Append(Html.Escape(recommendation.Rationale)).Append("</p>");

                if (recommendation.RequiresHardwarePurchase)
                    html.Append("<p class=\"tiny\" style=\"margin-top:4px\">")
                        .Append("Cette action suppose l'achat d'une pièce ; nous vous en communiquerons le prix ")
                        .Append("avant toute intervention.</p>");

                html.Append("</div><div class=\"side muted\">")
                    .Append(Html.Escape(ReportParts.EffortLabel(recommendation.Effort)))
                    .Append("</div></div>");
            }

            html.Append("</div>");
        }

        /// <summary>
        /// Constats d'information. Ils ne demandent aucune action, mais les taire donnerait
        /// l'impression d'un document qui cache la moitié de ce qu'il a vu.
        /// </summary>
        private static void WriteNotes(StringBuilder html, SystemSnapshot snapshot)
        {
            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var finding in snapshot.Findings)
                if (finding.Severity == Severity.Info && seen.Add(finding.PlainExplanation))
                    lines.Add(finding.PlainExplanation);

            if (lines.Count == 0) return;

            html.Append("<h2>À titre d'information</h2><div class=\"card\"><ul style=\"margin:0;padding-left:20px\">");
            foreach (var line in lines)
                html.Append("<li style=\"padding:3px 0\">").Append(Html.Escape(line)).Append("</li>");
            html.Append("</ul></div>");
        }

        private static void WriteLimits(StringBuilder html, SystemSnapshot snapshot)
        {
            var blocked = ReportParts.ElevationBlockedCount(snapshot);
            var failed = 0;
            foreach (var report in snapshot.ModuleReports)
                if (report.Status == ProbeStatus.Failed || report.Status == ProbeStatus.TimedOut ||
                    report.Status == ProbeStatus.Unavailable)
                    failed++;

            var reduced = snapshot.Platform.Windows.Level != CompatibilityLevel.Full;
            if (blocked == 0 && failed == 0 && !reduced) return;

            html.Append("<h2>Ce que nous n'avons pas pu vérifier</h2><div class=\"card\">")
                .Append("<p>Un bilan honnête doit aussi dire où il s'arrête. Les points ci-dessous n'ont pas été ")
                .Append("contrôlés : cela ne veut pas dire qu'ils vont bien, seulement que nous ne les avons ")
                .Append("pas mesurés.</p><ul style=\"margin:10px 0 0;padding-left:20px\">");

            if (blocked > 0)
                html.Append("<li style=\"padding:3px 0\">")
                    .Append(blocked > 1 ? "Certaines vérifications demandent" : "Une vérification demande")
                    .Append(" des droits d'administrateur sur l'ordinateur. Nous pouvons les refaire lors ")
                    .Append("d'un prochain passage.</li>");

            if (failed > 0)
                html.Append("<li style=\"padding:3px 0\">")
                    .Append("Certaines informations n'ont pas pu être lues sur cet ordinateur.</li>");

            if (reduced)
                html.Append("<li style=\"padding:3px 0\">")
                    .Append("La version de Windows installée ne permet pas tous les contrôles que nous ")
                    .Append("effectuons habituellement.</li>");

            html.Append("</ul></div>");
        }

        private static bool HasConcern(SystemSnapshot snapshot, DiagnosticCategory category)
        {
            foreach (var finding in snapshot.Findings)
                if (finding.Category == category && finding.Severity != Severity.Info)
                    return true;
            return false;
        }

        /// <summary>
        /// Phrase d'état général. Elle tient compte du nombre de recommandations : annoncer
        /// « aucune intervention nécessaire » au-dessus d'une liste de sept actions est la
        /// contradiction la plus sûre pour qu'un client cesse de croire le document.
        /// </summary>
        private static string BandSentence(ScoreBand band, int recommendations)
        {
            if (band == ScoreBand.Excellent && recommendations > 0)
                return "Votre ordinateur est en bon état général. Les points ci-dessous sont des " +
                       "améliorations à prévoir, pas des réparations urgentes.";

            return BandSentence(band);
        }

        private static string BandSentence(ScoreBand band) => band switch
        {
            ScoreBand.Excellent => "Votre ordinateur est en bon état. Aucune intervention n'est nécessaire dans l'immédiat.",
            ScoreBand.Good => "Votre ordinateur fonctionne correctement. Quelques points méritent d'être suivis.",
            ScoreBand.Attention => "Votre ordinateur demande votre attention sur plusieurs points, sans urgence immédiate.",
            ScoreBand.Degraded => "Votre ordinateur rencontre des problèmes qui gênent son fonctionnement au quotidien.",
            _ => "Votre ordinateur présente un problème sérieux, à traiter rapidement.",
        };

        /// <summary>
        /// Vocabulaire de gravité destiné au client. « Critique » se lit comme une alarme et
        /// pousse à la décision précipitée ; « Urgent » dit la même chose et laisse réfléchir.
        /// </summary>
        private static string ClientSeverity(Severity severity) => severity switch
        {
            Severity.Critical => "Urgent",
            Severity.Problem => "Important",
            _ => "À surveiller",
        };

        /// <summary>
        /// Ce qu'on peut affirmer d'un domaine sain. Chaque phrase nomme ce qui a été contrôlé
        /// plutôt que d'affirmer que « tout va bien » : un bilan qui déclare les composants sans
        /// anomalie, puis recommande de vérifier l'alimentation, se contredit aux yeux du client.
        /// Une affirmation bornée ne peut pas entrer en conflit avec une recommandation qui porte
        /// sur autre chose.
        /// </summary>
        private static string? StrengthSentence(DiagnosticCategory category) => category switch
        {
            DiagnosticCategory.Hardware =>
                "Le processeur, la mémoire et la carte graphique ne présentent pas d'anomalie.",
            DiagnosticCategory.Storage =>
                "Les disques et l'espace disponible sont en bon état.",
            DiagnosticCategory.Windows =>
                "L'installation de Windows, ses fichiers système et ses mises à jour sont en ordre.",
            DiagnosticCategory.Network =>
                "La connexion au réseau et à Internet fonctionne normalement.",
            DiagnosticCategory.Security =>
                "Les protections que nous vérifions (pare-feu, antivirus, démarrage sécurisé) sont en place.",
            DiagnosticCategory.Performance =>
                "Les mesures de rapidité relevées sont conformes à ce qu'on attend de cette machine.",
            _ => null,
        };
    }
}
