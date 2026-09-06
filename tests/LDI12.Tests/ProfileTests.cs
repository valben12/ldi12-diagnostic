using System;
using System.Collections.Generic;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'état des profils, la catégorie de panne où le client est le plus démuni.
    /// </summary>
    public class ProfileTests
    {
        // ============================================================ profil provisoire

        [Fact]
        public void Un_profil_provisoire_est_le_constat_le_plus_grave_sans_materiel_en_cause()
        {
            // Tout ce que l'utilisateur enregistre disparaît à la fermeture de session, et rien
            // ne le lui dit. Chaque jour qui passe est une journée de travail perdue.
            var finding = Single(Evaluate(b =>
                b.Profile("Marie", ProfileState.Temporary, current: true, path: @"C:\Users\TEMP")), "PRO-001");

            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains("effacé à la fermeture", finding.PlainExplanation);
            Assert.Contains("BACKUP-NOW", finding.Recommendations);
        }

        [Fact]
        public void Le_constat_dit_de_sauvegarder_avant_de_redemarrer()
        {
            // L'ordre compte : redémarrer efface ce qu'on n'a pas encore mis à l'abri.
            var finding = Single(Evaluate(b =>
                b.Profile("Marie", ProfileState.Temporary, current: true)), "PRO-001");

            Assert.Contains("ne pas redémarrer", finding.PlainExplanation);
        }

        // ============================================================ profil mis de côté

        [Fact]
        public void Un_profil_mis_de_cote_dont_le_dossier_reste_rassure_au_lieu_d_alarmer()
        {
            // C'est le « j'ai tout perdu » le plus fréquent, et le plus facile à corriger : le
            // dossier d'origine est intact à côté du nouveau.
            var finding = Single(Evaluate(b =>
                b.Profile("Marie", ProfileState.SetAside, folderExists: true)), "PRO-002");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("Rien n'est perdu", finding.PlainExplanation);
        }

        [Fact]
        public void Un_profil_mis_de_cote_sans_dossier_est_un_probleme()
        {
            var finding = Single(Evaluate(b =>
                b.Profile("Marie", ProfileState.SetAside, folderExists: false)), "PRO-002");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("sauvegarde", finding.PlainExplanation);
        }

        // ============================================================ profils orphelins

        [Fact]
        public void Des_profils_sans_dossier_sont_une_information_et_rien_de_plus()
        {
            var finding = Single(Evaluate(b =>
            {
                b.Profile("Marie", current: true);
                b.Profile("Ancien", ProfileState.Orphaned, folderExists: false);
            }), "PRO-003");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("Ce n'est pas une panne", finding.PlainExplanation);
        }

        // ============================================================ sessions ouvertes

        [Fact]
        public void Plusieurs_sessions_ouvertes_expliquent_une_machine_qui_rame()
        {
            // Une session verrouillée n'est pas une session fermée : sa mémoire reste occupée.
            var finding = Single(Evaluate(b =>
            {
                b.Profile("Marie", current: true);
                b.Profile("Paul", loaded: true);
            }), "PRO-004");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("Paul", finding.TechnicalDetail);
        }

        [Fact]
        public void Une_seule_session_ouverte_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.Profile("Marie", current: true)),
                finding => finding.RuleId == "PRO-004");
        }

        // ============================================================ profils inutilisés

        [Fact]
        public void Des_profils_inutilises_depuis_longtemps_orientent_sans_proposer_de_supprimer()
        {
            // Un profil contient les documents de quelqu'un, et ce quelqu'un n'est pas toujours
            // celui qui apporte la machine.
            var finding = Single(Evaluate(b =>
            {
                b.Profile("Marie", current: true);
                b.Profile("Ancien", monthsSinceUse: 30);
            }), "PRO-005");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("se décide avec le client", finding.PlainExplanation);
            Assert.Empty(finding.Recommendations);
        }

        [Fact]
        public void Un_profil_recent_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b =>
                {
                    b.Profile("Marie", current: true);
                    b.Profile("Paul", monthsSinceUse: 3);
                }),
                finding => finding.RuleId == "PRO-005");
        }

        [Fact]
        public void La_session_en_cours_n_est_jamais_comptee_comme_inutilisee()
        {
            // Elle est ouverte : sa date de dernière fermeture est forcément ancienne, et la
            // compter ferait sonner le constat sur la machine de tout le monde.
            var findings = Evaluate(b => b.Profile("Marie", current: true, monthsSinceUse: 40));

            Assert.DoesNotContain(findings, finding => finding.RuleId == "PRO-005");
        }

        // ============================================================ sans relevé

        [Fact]
        public void Sans_profil_releve_rien_n_est_conclu()
        {
            // Une machine dont la clé de registre n'a pas pu être lue ne doit pas être déclarée
            // saine sur ces points : elle est muette.
            var score = new AnalysisEngine().Analyze(Fixtures.Build(b => b.Windows11())).Score!;

            foreach (var id in new[] { "PRO-001", "PRO-002", "PRO-003", "PRO-005" })
                Assert.DoesNotContain(id, score.EvaluatedRuleIds);
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_ne_montre_les_etats_anormaux_que_lorsqu_ils_existent()
        {
            // Une ligne « profils provisoires : 0 » sur les machines saines apprendrait à ne plus
            // la lire, et c'est celle qu'il ne faut pas manquer le jour où elle change.
            var sain = Facts(b => b.Profile("Marie", current: true));

            Assert.DoesNotContain(sain, fact => fact.Label == "Profils provisoires");
            Assert.Equal("1", Value(sain, "Profils normaux"));

            var malade = Facts(b =>
            {
                b.Profile("Marie", ProfileState.Temporary, current: true);
                b.Profile("Marie", ProfileState.SetAside);
            });

            Assert.Equal("1", Value(malade, "Profils provisoires"));
            Assert.Equal("1", Value(malade, "Profils mis de côté"));
        }

        // ============================================================ montage

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            })).Findings;

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static IReadOnlyList<Fact> Facts(Action<Fixtures.Builder> configure)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == "Comptes et profils") return group.Facts;

            throw new InvalidOperationException("Le cadre « Comptes et profils » est absent.");
        }

        private static string Value(IReadOnlyList<Fact> facts, string label)
            => Assert.Single(facts.Where(fact => fact.Label == label)).Value;
    }
}
