using System;
using System.IO;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Reports.Html;
using LDI12.Reports.Interventions;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le dossier d'intervention : de l'accueil au rapport, et à travers une réinstallation.
    /// </summary>
    public class InterventionTests
    {
        [Fact]
        public void Une_reinstallation_passe_par_la_sauvegarde_et_la_restauration()
        {
            var intervention = new InterventionCase { Path = InterventionPath.Reinstall, Stage = InterventionStage.Decision };

            Assert.Contains(InterventionStage.Backup, intervention.Stages());
            Assert.Contains(InterventionStage.Restore, intervention.Stages());
            Assert.Equal(InterventionStage.Backup, intervention.Next());
        }

        [Fact]
        public void Une_reparation_va_droit_au_controle()
        {
            var intervention = new InterventionCase { Path = InterventionPath.Repair, Stage = InterventionStage.Decision };

            Assert.DoesNotContain(InterventionStage.Backup, intervention.Stages());
            Assert.Equal(InterventionStage.Verification, intervention.Next());
        }

        [Fact]
        public void Le_dossier_se_relit_tel_qu_il_a_ete_ecrit()
        {
            Sandbox(root =>
            {
                var store = new InterventionStore(root);
                var intervention = new InterventionCase { Client = "Mme Dupont", Machine = "PC-SALON", Stage = InterventionStage.Restore };
                intervention.Reasons.Add("Lenteurs");
                intervention.Note("Écran fissuré, signalé au client.");
                store.Save(intervention);

                var read = Assert.Single(store.List());
                Assert.Equal("Mme Dupont", read.Client);
                Assert.Equal(InterventionStage.Restore, read.Stage);
                Assert.Equal("Lenteurs", Assert.Single(read.Reasons));
                Assert.Equal("Écran fissuré, signalé au client.", Assert.Single(read.Notes).Text);
            });
        }

        [Fact]
        public void Renommer_le_client_ne_perd_pas_le_dossier()
        {
            Sandbox(root =>
            {
                var store = new InterventionStore(root);
                var intervention = new InterventionCase { Client = "Dupont" };
                store.Save(intervention);
                var folder = store.FolderOf(intervention);

                intervention.Client = "Mme Dupont Marie";
                store.Save(intervention);

                Assert.Equal(folder, store.FolderOf(intervention));
                Assert.Single(store.List());
            });
        }

        [Fact]
        public void Le_dossier_suit_la_sauvegarde_et_se_reprend_depuis_elle()
        {
            // La clé de l'atelier perdue entre-temps : le dossier repart du disque de sauvegarde.
            Sandbox(root =>
            {
                var atelier = new InterventionStore(Path.Combine(root, "cle"));
                var intervention = new InterventionCase { Client = "M. Martin", Path = InterventionPath.Reinstall, Stage = InterventionStage.Reinstall };
                atelier.Save(intervention);

                var support = Path.Combine(root, "E", "LDI12-Sauvegarde-PC-2026-10-17-0900");
                Directory.CreateDirectory(support);
                Assert.True(atelier.CopyInto(intervention, support));

                var nouvelleCle = new InterventionStore(Path.Combine(root, "autre-cle"));
                var adopted = nouvelleCle.Adopt(Path.Combine(root, "E"));

                Assert.NotNull(adopted);
                Assert.Equal("M. Martin", adopted!.Client);
                Assert.Single(nouvelleCle.List());

                // Déjà là : rien n'est repris une seconde fois.
                Assert.Null(nouvelleCle.Adopt(support));
            });
        }

        [Fact]
        public void Le_rapport_porte_la_demande_la_decision_les_travaux_et_la_conclusion()
        {
            var intervention = new InterventionCase
            {
                Client = "Mme Dupont",
                Path = InterventionPath.Reinstall,
                Stage = InterventionStage.Done,
                Request = "Le PC est très lent depuis un mois.",
                BackupSummary = "48 213 fichier(s), 61,4 Go",
                RestoreSummary = "48 213 fichier(s), 61,4 Go",
            };
            intervention.Reasons.Add("Lenteurs");
            intervention.Conclusion = InterventionReport.DraftConclusion(intervention, null, null, null);

            var html = InterventionReport.Render(intervention, null, null, null);

            Assert.Contains("Votre demande", html);
            Assert.Contains("très lent", html);
            Assert.Contains("Réinstallation de Windows", html);
            Assert.Contains("Sauvegarde : 48 213", html);
            Assert.Contains("Windows a été réinstallé, après une sauvegarde vérifiée", html);
            Assert.Contains("En résumé", html);
        }

        [Fact]
        public void Le_rapport_ne_montre_que_les_explications_en_clair()
        {
            var before = new SystemSnapshot
            {
                Findings = new[]
                {
                    new Finding
                    {
                        RuleId = "STO-001", Severity = Severity.Problem, Title = "SMART",
                        TechnicalDetail = "Attribut 05 : 120 secteurs réalloués",
                        PlainExplanation = "Le disque montre des signes d'usure.",
                    },
                },
            };

            var html = InterventionReport.Render(new InterventionCase { Client = "X" }, before, null, null);

            Assert.Contains("Le disque montre des signes d'usure.", html);
            Assert.DoesNotContain("STO-001", html);
            Assert.DoesNotContain("secteurs réalloués", html);
        }

        private static void Sandbox(Action<string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-dossiers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
