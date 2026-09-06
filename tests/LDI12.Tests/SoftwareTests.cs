using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'inventaire logiciel, et la ligne qu'il ne doit pas franchir : constater ce qui est
    /// installé sans devenir un antivirus.
    /// </summary>
    public class SoftwareTests
    {
        [Theory]
        [InlineData("Adobe Flash Player 32 NPAPI", "Adobe Flash Player")]
        [InlineData("Java(TM) 7 Update 80", "Java 7")]
        [InlineData("Microsoft Office Professional Plus 2010", "Microsoft Office 2010")]
        [InlineData("QuickTime 7", "QuickTime pour Windows")]
        [InlineData("Advanced SystemCare Free", "Advanced SystemCare")]
        [InlineData("IObit Driver Booster 9", "Driver Booster")]
        public void Les_logiciels_du_catalogue_se_reconnaissent_sous_leur_nom_reel(string installed, string expected)
        {
            var note = SoftwareCatalog.Describe(installed);

            Assert.NotNull(note);
            Assert.Equal(expected, note!.Label);
        }

        [Theory]
        [InlineData("JavaScript Development Kit")]      // « java » n'est pas un morceau de mot
        [InlineData("LibreOffice 25.8.3.2")]            // « office » non plus
        [InlineData("Java 8 Update 361")]               // toujours suivi, jamais abandonné
        [InlineData("Microsoft Office LTSC 2024")]      // millésime courant
        [InlineData("Adobe Acrobat (64-bit)")]
        public void Un_nom_voisin_n_est_jamais_pris_pour_un_autre(string installed)
        {
            // La leçon vient du Wi-Fi, où « 802.11a » se trouvait dans « 802.11ac ». Ici, la
            // même erreur ferait dire à un client que son tableur n'est plus corrigé.
            Assert.Null(SoftwareCatalog.Describe(installed));
        }

        [Fact]
        public void Une_version_de_java_ne_se_confond_pas_avec_un_numero_de_mise_a_jour()
        {
            // « Java 8 Update 6 » contient bien les mots « java » et « 6 » : sans exigence
            // d'adjacence, il passerait pour une version abandonnée en 2013.
            Assert.Null(SoftwareCatalog.Describe("Java 8 Update 6"));
            Assert.NotNull(SoftwareCatalog.Describe("Java 6 Update 45"));
        }

        [Fact]
        public void Le_catalogue_ne_juge_que_deux_choses()
        {
            // Garde-fou de périmètre : le logiciel n'est pas un antivirus. Toute catégorie
            // ajoutée ici devra être défendue devant l'éditeur concerné.
            var concerns = new HashSet<SoftwareConcern>();

            foreach (var name in new[]
                     {
                         "Adobe Flash Player", "Java 6", "Microsoft Office 2007", "QuickTime",
                         "Advanced SystemCare", "Restoro",
                     })
            {
                var note = SoftwareCatalog.Describe(name);
                if (note != null) concerns.Add(note.Concern);
            }

            Assert.Equal(2, concerns.Count);
        }

        [Fact]
        public void Un_utilitaire_d_optimisation_n_est_jamais_presente_comme_malveillant()
        {
            var note = SoftwareCatalog.Describe("Advanced SystemCare Free");

            Assert.NotNull(note);
            Assert.Contains("n'est pas un logiciel malveillant", note!.Explanation);
            Assert.Contains("conversation à avoir avec le client", note.Explanation);
        }

        [Fact]
        public void Un_logiciel_abandonne_depuis_longtemps_est_un_probleme_les_autres_une_alerte()
        {
            var ancient = Evaluate(Program("Adobe Flash Player 32 NPAPI"));
            var recent = Evaluate(Program("Microsoft Office Professional Plus 2013"));

            Assert.Equal(Severity.Problem, Single(ancient, "SFT-001").Severity);
            Assert.Equal(Severity.Warning, Single(recent, "SFT-001").Severity);
        }

        [Fact]
        public void Le_constat_nomme_les_logiciels_et_l_annee_de_leur_abandon()
        {
            var findings = Evaluate(Program("Adobe Flash Player 32 NPAPI"), Program("QuickTime 7"));
            var finding = Single(findings, "SFT-001");

            Assert.Contains("Adobe Flash Player", finding.TechnicalDetail);
            Assert.Contains("2020", finding.TechnicalDetail);
            Assert.Contains("QuickTime", finding.TechnicalDetail);
        }

        [Fact]
        public void Deux_versions_d_un_environnement_ne_sont_pas_un_constat()
        {
            // Un logiciel métier peut exiger une version précise : envoyer le technicien
            // désinstaller ce qui fait tourner la comptabilité serait pire que le défaut.
            var findings = Evaluate(
                Program("Java 8 Update 361", "8.0.3610.9"),
                Program("Java 8 Update 202", "8.0.2020.8"));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "SFT-002");
        }

        [Fact]
        public void Trois_versions_accumulees_le_sont()
        {
            var findings = Evaluate(
                Program("Java 8 Update 361", "8.0.3610.9"),
                Program("Java 8 Update 202", "8.0.2020.8"),
                Program("Java 8 Update 51", "8.0.510.16"));

            var finding = Single(findings, "SFT-002");
            Assert.Contains("Java", finding.Title);
            Assert.Equal(Severity.Warning, finding.Severity);
        }

        [Fact]
        public void Un_utilitaire_d_optimisation_ne_pese_pas_sur_la_note()
        {
            var findings = Evaluate(Program("Advanced SystemCare Free"));
            var finding = Single(findings, "SFT-003");

            Assert.Equal(Severity.Info, finding.Severity);
        }

        [Fact]
        public void Un_inventaire_vide_ne_conclut_rien()
        {
            // Un accès refusé au registre n'est pas une machine sans logiciel : les trois règles
            // doivent se taire, et non annoncer qu'il n'y a rien à signaler.
            var findings = new AnalysisEngine().Analyze(Fixtures.Healthy()).Findings;

            foreach (var id in new[] { "SFT-001", "SFT-002", "SFT-003" })
                Assert.DoesNotContain(findings, finding => finding.RuleId == id);
        }

        private static InstalledProgram Program(string name, string? version = null)
            => Fixtures.Builder.Installed(name, version);

        private static IReadOnlyList<Finding> Evaluate(params InstalledProgram[] programs)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                builder.Software(programs);
            });

            return new AnalysisEngine().Analyze(snapshot).Findings;
        }

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));
    }
}
