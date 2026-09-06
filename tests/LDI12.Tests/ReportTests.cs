using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Comparison;
using LDI12.Engine.Profile;
using LDI12.Reports;
using LDI12.Reports.Facts;
using LDI12.Reports.Html;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les rapports sont la partie du logiciel qui sort de l'atelier : une fois le fichier remis
    /// au client, on ne peut plus le corriger. Ces tests portent donc moins sur la mise en forme
    /// que sur ce que les documents ont le droit d'affirmer.
    /// </summary>
    public class ReportTests
    {
        public static IEnumerable<object[]> AllFixtures()
        {
            foreach (var fixture in Fixtures.All())
                yield return new object[] { fixture.Name, new AnalysisEngine().Analyze(fixture.Snapshot) };
        }

        // ============================================================ fiches de caractéristiques

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Une_mesure_absente_n_est_jamais_presentee_comme_une_valeur(string name, SystemSnapshot snapshot)
        {
            // Le cœur de la promesse du logiciel, appliqué à l'affichage : rien de ce qui n'a pas
            // été mesuré ne doit ressortir sous la forme d'un « 0 », d'un « Inconnu » ou d'un vide.
            foreach (var fact in AllFacts(snapshot))
            {
                Assert.False(string.IsNullOrWhiteSpace(fact.Value),
                    name + " : la caractéristique « " + fact.Label + " » n'a pas de texte.");

                if (fact.IsKnown) continue;

                // « Non connue » est le quatrième et dernier terme du vocabulaire : il vise ce que
                // le logiciel ne peut pas savoir plutôt que ce qu'il n'a pas su mesurer : une date
                // de fin de support absente de la table embarquée, par exemple. La liste reste
                // close : c'est ce qui empêche une absence de se déguiser en valeur.
                Assert.True(
                    fact.Value == "Non mesuré" || fact.Value == "Non lu" || fact.Value == "Non relevé" ||
                    fact.Value == "Non connue",
                    name + " : « " + fact.Label + " » est absente mais affiche « " + fact.Value + " ».");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Une_absence_porte_toujours_sa_raison(string name, SystemSnapshot snapshot)
        {
            // Sans raison affichée, « non mesuré » est une impasse : le technicien ne sait pas
            // s'il doit relancer en administrateur, changer de machine, ou conclure.
            foreach (var fact in AllFacts(snapshot))
            {
                if (fact.State != FactState.Missing && fact.State != FactState.NeedsElevation) continue;

                Assert.False(string.IsNullOrWhiteSpace(fact.Note),
                    name + " : « " + fact.Label + " » est absente sans raison enregistrée.");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Une_valeur_absente_n_est_jamais_coloree_comme_un_defaut(string name, SystemSnapshot snapshot)
        {
            // Ne pas avoir su mesurer n'est pas un défaut de la machine. Les confondre visuellement
            // reviendrait à inventer des problèmes là où il n'y a qu'une lacune d'observation.
            foreach (var fact in AllFacts(snapshot))
                if (!fact.IsKnown)
                    Assert.True(fact.Tone == FactTone.Neutral,
                        name + " : « " + fact.Label + " » est absente mais colorée en " + fact.Tone + ".");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Aucun_groupe_vide_n_est_produit(string name, SystemSnapshot snapshot)
        {
            // Un cadre vide est ambigu : rien à dire, ou mesure échouée ? Le compte rendu des
            // modules répond déjà à la question, le groupe n'a pas à la reposer.
            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    Assert.True(group.HasFacts || (group.Table != null && group.Table.HasRows),
                        name + " : le groupe « " + group.Title + " » ne contient rien.");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Chaque_ligne_de_tableau_a_le_nombre_de_colonnes_annonce(string name, SystemSnapshot snapshot)
        {
            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                {
                    if (group.Table == null) continue;
                    foreach (var row in group.Table.Rows)
                        Assert.True(row.Cells.Count == group.Table.Columns.Count,
                            name + " / " + group.Title + " : ligne de " + row.Cells.Count +
                            " cellules pour " + group.Table.Columns.Count + " colonnes.");
                }
        }

        // ============================================================ rapport technicien

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_rapport_technicien_est_un_document_autonome(string name, SystemSnapshot snapshot)
        {
            var html = TechnicianReport.Render(snapshot);

            Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("</html>", html.TrimEnd(), StringComparison.OrdinalIgnoreCase);

            // Aucune ressource externe : le rapport doit s'ouvrir hors ligne, chez le client,
            // des années plus tard, sur une machine dont on ne choisit pas la configuration.
            //
            // Ce qui est interdit, c'est ce que le navigateur irait chercher, pas toute
            // occurrence du mot « http ». Un rapport cite légitimement les adresses que les tests
            // réseau ont contactées, et l'espace de noms SVG est une URL qui n'est jamais résolue.
            Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("LDI12", html);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void Le_rapport_technicien_dit_pourquoi_une_information_manque()
        {
            // « Pourquoi cette information manque » est une information. Un rapport qui ne montre
            // que les modules ayant abouti laisse croire à une couverture complète.
            var analyzed = new AnalysisEngine().Analyze(Fixtures.Healthy());
            var withModules = Clone(analyzed, new[]
            {
                new ModuleReport
                {
                    ProbeId = "STO-SMART",
                    DisplayName = "Santé des disques (SMART)",
                    Category = DiagnosticCategory.Storage,
                    Status = Core.Probes.ProbeStatus.ElevationRequired,
                    DurationMs = 12,
                    Message = "3 disque(s) nécessitent une élévation.",
                },
            });

            var html = TechnicianReport.Render(withModules);

            Assert.Contains("Modules de collecte", html);
            Assert.Contains("Santé des disques (SMART)", html);
            Assert.Contains("Élévation requise", html);
            Assert.Contains("3 disque(s) nécessitent une élévation.", html);
        }

        [Fact]
        public void Le_rapport_technicien_justifie_chaque_point_retire()
        {
            var snapshot = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var html = TechnicianReport.Render(snapshot);

            Assert.Contains("Justification du score", html);

            foreach (var dimension in snapshot.Score!.Dimensions)
                foreach (var penalty in dimension.Ledger)
                    Assert.Contains(Escape(penalty.RuleId), html);
        }

        [Fact]
        public void Le_texte_venu_de_la_machine_est_echappe()
        {
            // Les noms de volumes, de périphériques et les extraits de journaux viennent de
            // constructeurs et de pilotes, pas de nous. Un rapport est destiné à être ouvert dans
            // un navigateur et transmis par courriel : ce texte est du contenu, jamais du balisage.
            var snapshot = new AnalysisEngine().Analyze(Fixtures.Healthy());
            var hostile = new SystemSnapshot
            {
                Metadata = snapshot.Metadata,
                Machine = new MachineIdentity
                {
                    MachineName = "<script>alert('x')</script>",
                    UserName = "\"quote\" & <tag>",
                },
                Platform = snapshot.Platform,
                Hardware = snapshot.Hardware,
                Storage = snapshot.Storage,
                Windows = snapshot.Windows,
                Network = snapshot.Network,
                ModuleReports = snapshot.ModuleReports,
                Findings = snapshot.Findings,
                Correlations = snapshot.Correlations,
                Recommendations = snapshot.Recommendations,
                Score = snapshot.Score,
            };

            var technician = TechnicianReport.Render(hostile);
            var client = ClientReport.Render(hostile);

            Assert.DoesNotContain("<script>alert", technician, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script>alert", client, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;", technician);
            Assert.Contains("&lt;script&gt;", client);
        }

        // ============================================================ bilan client

        /// <summary>
        /// Vocabulaire qui n'a rien à faire dans un document remis à un client. La liste est
        /// volontairement courte et sans ambiguïté : elle ne cherche pas à juger le style, mais à
        /// attraper les fuites de langage technicien : un identifiant de règle, un code
        /// hexadécimal, un nom d'outil système.
        /// </summary>
        /// <remarks>
        /// Les motifs sont ancrés sur des limites de mot. Sans cela, « registre » se déclenche
        /// dans « Windows enregistre des erreurs disque », une phrase parfaitement claire pour
        /// un client. Un garde-fou qui crie au loup sur du français correct finit désactivé, et
        /// ne garde plus rien.
        /// </remarks>
        private static readonly (string Label, Regex Pattern)[] ForbiddenInClientReport =
        {
            ("un code hexadécimal", new Regex(@"0x[0-9A-Fa-f]", RegexOptions.Compiled)),
            ("un identifiant de sécurité Windows", new Regex(@"S-1-5-", RegexOptions.Compiled)),
            ("un nom d'outil système", new Regex(@"\b(DISM|SFC|CHKDSK|WMI|IOCTL|APIPA|CBS\.log|regedit)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("le mot « registre »", new Regex(@"\bregistres?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("le mot « privilèges »", new Regex(@"\bprivilèges?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("une consigne adressée au technicien",
                new Regex(@"\b(relancer l'analyse|en tant qu'administrateur)\b",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        };

        private static readonly Regex RuleIdPattern = new Regex(@"\b[A-Z]{3}-\d{3}\b", RegexOptions.Compiled);

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_bilan_client_ne_contient_aucun_jargon(string name, SystemSnapshot snapshot)
        {
            // Critère de sortie de la phase : un bilan relu par une personne non technique doit
            // être compris sans explication. Ce test ne prouve pas la compréhension, mais il
            // interdit mécaniquement ce qui l'empêche à coup sûr.
            var text = StripMarkup(ClientReport.Render(snapshot));

            Assert.False(RuleIdPattern.IsMatch(text),
                name + ", le bilan client cite un identifiant de règle : « " +
                (RuleIdPattern.Match(text).Value) + " ».");

            foreach (var forbidden in ForbiddenInClientReport)
            {
                var match = forbidden.Pattern.Match(text);
                Assert.False(match.Success,
                    name + " : le bilan client contient " + forbidden.Label + " : « " + match.Value + " ».");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_bilan_client_n_expose_jamais_le_detail_technique(string name, SystemSnapshot snapshot)
        {
            // Le double libellé des règles n'a de sens que si le rapport client s'y tient : dès
            // qu'un TechnicalDetail passe, la séparation ne tient plus nulle part.
            var text = StripMarkup(ClientReport.Render(snapshot));

            foreach (var finding in snapshot.Findings)
            {
                if (finding.TechnicalDetail.Length < 40) continue;
                if (finding.TechnicalDetail == finding.PlainExplanation) continue;

                Assert.False(text.Contains(Normalise(finding.TechnicalDetail)),
                    name + " : le détail technique de " + finding.RuleId + " apparaît dans le bilan client.");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Le_bilan_client_ne_se_repete_pas(string name, SystemSnapshot snapshot)
        {
            // Deux règles distinctes peuvent produire la même phrase pour le client. La répéter
            // n'ajoute rien et donne l'impression d'un document mal relu.
            var text = StripMarkup(ClientReport.Render(snapshot));

            foreach (var finding in snapshot.Findings)
            {
                var sentence = Normalise(finding.PlainExplanation);
                if (sentence.Length < 40) continue;

                var first = text.IndexOf(sentence, StringComparison.Ordinal);
                if (first < 0) continue;

                Assert.True(text.IndexOf(sentence, first + 1, StringComparison.Ordinal) < 0,
                    name + " : la phrase « " + sentence.Substring(0, Math.Min(50, sentence.Length)) +
                    "… » apparaît deux fois dans le bilan client.");
            }
        }

        [Fact]
        public void Le_bilan_client_dit_ce_qui_n_a_pas_pu_etre_verifie()
        {
            // Sans cette section, « tout va bien » et « nous n'avons pas regardé » deviennent
            // indiscernables pour quelqu'un qui n'a pas les moyens de faire la différence.
            var snapshot = new AnalysisEngine().Analyze(Fixtures.Windows7Minimal());
            var html = ClientReport.Render(snapshot);

            Assert.Contains("Ce que nous n'avons pas pu vérifier", html);
        }

        [Fact]
        public void Un_etat_excellent_assorti_de_recommandations_ne_se_contredit_pas()
        {
            // Annoncer « aucune intervention nécessaire » au-dessus d'une liste d'actions est la
            // contradiction la plus sûre pour qu'un client cesse de croire le document.
            var snapshot = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var html = ClientReport.Render(snapshot);

            if (snapshot.Score!.Band == ScoreBand.Excellent && snapshot.Recommendations.Count > 0)
                Assert.DoesNotContain("Aucune intervention n'est nécessaire", html);
        }

        // ============================================================ export

        [Theory]
        [InlineData(ReportKind.Technician, ".html")]
        [InlineData(ReportKind.Client, ".html")]
        [InlineData(ReportKind.Json, ".json")]
        public void Le_nom_de_fichier_est_utilisable_partout(ReportKind kind, string extension)
        {
            var snapshot = new AnalysisEngine().Analyze(Fixtures.Healthy());
            var hostile = new SystemSnapshot
            {
                Metadata = snapshot.Metadata,
                Machine = new MachineIdentity { MachineName = "POSTE ÉTÉ / N°3 : bureau*" },
                Platform = snapshot.Platform,
                Score = snapshot.Score,
            };

            var name = ReportExporter.SuggestFileName(hostile, kind, new ReportContext { ClientReference = "Dupont/2026" });

            Assert.EndsWith(extension, name, StringComparison.Ordinal);
            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.DoesNotContain(" ", name);
            Assert.Contains("POSTE-ETE", name);
        }

        [Fact]
        public void Les_trois_documents_s_ecrivent_et_se_relisent()
        {
            var snapshot = new AnalysisEngine().Analyze(Fixtures.DyingDisk());
            var directory = Path.Combine(Path.GetTempPath(), "ldi12-tests-" + Guid.NewGuid().ToString("N"));

            try
            {
                foreach (var kind in new[] { ReportKind.Technician, ReportKind.Client, ReportKind.Json })
                {
                    var exported = ReportExporter.Export(snapshot, kind, directory);

                    Assert.True(File.Exists(exported.Path), kind + " : fichier absent.");
                    Assert.True(new FileInfo(exported.Path).Length > 500, kind + " : fichier suspicieusement court.");
                }

                // Le JSON est la source dont les deux HTML sont des lectures : il doit revenir
                // intact, sans quoi comparer un avant et un après réparation serait illusoire.
                var jsonPath = Path.Combine(directory, ReportExporter.SuggestFileName(snapshot, ReportKind.Json));
                var reloaded = Reports.Json.SnapshotSerializer.Load(jsonPath);

                Assert.Equal(snapshot.Metadata.SnapshotId, reloaded.Metadata.SnapshotId);
                Assert.Equal(snapshot.Findings.Count, reloaded.Findings.Count);
                Assert.Equal(snapshot.Score!.Global, reloaded.Score!.Global);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        // ============================================================ outillage

        /// <summary>Recopie un instantané en remplaçant les seuls comptes rendus de module.</summary>
        private static SystemSnapshot Clone(SystemSnapshot source, IReadOnlyList<ModuleReport> modules)
            => new SystemSnapshot
            {
                Metadata = source.Metadata,
                Machine = source.Machine,
                Platform = source.Platform,
                Hardware = source.Hardware,
                Storage = source.Storage,
                Windows = source.Windows,
                Network = source.Network,
                ModuleReports = modules,
                Findings = source.Findings,
                Correlations = source.Correlations,
                Recommendations = source.Recommendations,
                Score = source.Score,
            };

        private static IEnumerable<Fact> AllFacts(SystemSnapshot snapshot)
        {
            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    foreach (var fact in group.Facts)
                        yield return fact;
        }

        /// <summary>Retire le balisage et rend les entités, pour raisonner sur le texte lu.</summary>
        // ============================================================ comparatif avant / après

        /// <summary>
        /// Le comparatif part chez le client : il suit exactement les mêmes interdits que le
        /// bilan. Rien de ce que la comparaison sait du moteur ne doit y transparaître.
        /// </summary>
        [Fact]
        public void Le_comparatif_ne_contient_aucun_jargon()
        {
            var text = StripMarkup(ComparisonReport.Render(Delta(Fixtures.SlowMachine(), Fixtures.Healthy())));

            Assert.False(RuleIdPattern.IsMatch(text),
                "le comparatif cite un identifiant de règle : « " + RuleIdPattern.Match(text).Value + " ».");

            foreach (var forbidden in ForbiddenInClientReport)
            {
                var match = forbidden.Pattern.Match(text);
                Assert.False(match.Success,
                    "le comparatif contient " + forbidden.Label + " : « " + match.Value + " ».");
            }
        }

        /// <summary>
        /// Le défaut que ce document doit être incapable de produire : présenter comme réglé un
        /// constat que personne n'a revérifié.
        /// </summary>
        [Fact]
        public void Un_constat_non_reverifie_n_apparait_jamais_comme_regle()
        {
            var delta = Delta(Fixtures.SlowMachine(), Fixtures.Empty());
            var html = ComparisonReport.Render(delta);
            var text = StripMarkup(html);

            Assert.True(delta.Count(FindingChangeKind.NotRechecked) > 0);
            Assert.DoesNotContain("Ce qui a été réglé", text);
            Assert.Contains("Ce qui n'a pas pu être revérifié", text);
            Assert.Contains("ni réglés ni aggravés", text);
        }

        /// <summary>
        /// Les réserves cadrent la lecture du chiffre : en bas de page, elles ne servent à rien.
        /// </summary>
        [Fact]
        public void Les_reserves_precedent_le_resultat()
        {
            var before = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var after = new AnalysisEngine().Analyze(Fixtures.SlowMachine(), new DiagnosticProfile
            {
                Version = "atelier-2026",
                Limits = new Thresholds { SystemVolumeFreePercentWarning = 60 },
            });

            var html = ComparisonReport.Render(SnapshotComparer.Compare(before, after));

            var caveat = html.IndexOf("À lire avant le reste", StringComparison.Ordinal);
            var score = html.IndexOf("État général", StringComparison.Ordinal);

            Assert.True(caveat >= 0, "le comparatif n'affiche pas les réserves.");
            Assert.True(caveat < score, "les réserves sont affichées après le résultat.");
        }

        /// <summary>
        /// Deux barèmes différents : les deux notes s'affichent, leur différence non.
        /// </summary>
        [Fact]
        public void Deux_baremes_differents_ne_donnent_aucun_ecart_de_note()
        {
            var before = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var after = new AnalysisEngine().Analyze(Fixtures.SlowMachine(), new DiagnosticProfile
            {
                Version = "atelier-2026",
                Limits = new Thresholds { SystemVolumeFreePercentWarning = 60 },
            });

            var text = StripMarkup(ComparisonReport.Render(SnapshotComparer.Compare(before, after)));

            Assert.Contains("leur différence ne veut rien dire", text);
            Assert.DoesNotContain("points de mieux", text);
            Assert.DoesNotContain("points de moins", text);
        }

        [Fact]
        public void Le_comparatif_echappe_le_texte_venu_de_la_machine()
        {
            var hostile = Fixtures.Build(b =>
            {
                b.Windows11();
                b.Volume("<script>alert(1)</script>", totalGb: 500, freeGb: 4, isSystem: true);
            });

            var html = ComparisonReport.Render(Delta(hostile, Fixtures.Healthy()));

            Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Un comparatif entre deux machines différentes ne s'interprète pas en silence.
        /// </summary>
        [Fact]
        public void Deux_machines_differentes_sont_annoncees_en_tete()
        {
            var text = StripMarkup(ComparisonReport.Render(Delta(Fixtures.Healthy(), Fixtures.DyingDisk())));

            // Sans empreinte ni numéro de série, la question ne peut pas être tranchée : le
            // document le dit plutôt que de laisser croire à une continuité.
            Assert.Contains("Rien ne permet d'établir avec certitude", text);
        }

        private static SnapshotDelta Delta(SystemSnapshot before, SystemSnapshot after)
        {
            var engine = new AnalysisEngine();
            return SnapshotComparer.Compare(engine.Analyze(before), engine.Analyze(after));
        }

        private static string StripMarkup(string html)
        {
            var withoutStyle = Regex.Replace(html, "<style.*?</style>", " ", RegexOptions.Singleline);
            var withoutTags = Regex.Replace(withoutStyle, "<.*?>", " ", RegexOptions.Singleline);
            var decoded = withoutTags
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&#39;", "'");
            return Normalise(decoded);
        }

        private static string Normalise(string value)
            => Regex.Replace(value, @"\s+", " ").Trim();

        private static string Escape(string value) => Html_Escape(value);

        private static string Html_Escape(string value)
            => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
