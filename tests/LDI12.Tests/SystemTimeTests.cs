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
    /// L'horloge, le point de défaillance unique le plus trompeur de Windows.
    /// </summary>
    public class SystemTimeTests
    {
        // ============================================================ justesse

        [Fact]
        public void Une_horloge_anterieure_aux_fichiers_du_noyau_est_fausse_et_c_est_demontre()
        {
            // La machine contient des fichiers plus récents que la date qu'elle affiche : ce
            // n'est pas une appréciation.
            var finding = Single(Evaluate(b => b.Clock(clockOffsetDays: -400, anchorDaysAgo: 10)), "TIM-001");

            Assert.Equal(Severity.Problem, finding.Severity);
            Assert.Contains("connexions sécurisées", finding.PlainExplanation);
            Assert.Contains("FIX-SYSTEM-CLOCK", finding.Recommendations);
        }

        [Fact]
        public void Le_constat_nomme_la_pile_de_carte_mere()
        {
            // C'est la cause matérielle la plus fréquente, et celle à laquelle personne ne pense.
            var finding = Single(Evaluate(b => b.Clock(clockOffsetDays: -400)), "TIM-001");

            Assert.Contains("pile de carte mère", finding.PlainExplanation);
        }

        [Fact]
        public void Une_horloge_juste_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => b.Clock()),
                finding => finding.RuleId == "TIM-001");
        }

        [Fact]
        public void Sans_reference_exterieure_rien_n_est_affirme()
        {
            // La date d'installation de Windows a été écrite par cette machine avec cette
            // horloge : s'en servir ferait déclarer fausse une horloge parfaitement juste.
            var score = Analyze(b => b.Clock(clockOffsetDays: -400, anchor: false)).Score!;

            Assert.DoesNotContain("TIM-001", score.EvaluatedRuleIds);
        }

        // ============================================================ le service

        [Fact]
        public void Un_service_de_temps_desactive_est_un_avertissement()
        {
            var finding = Single(Evaluate(b => b.Clock(serviceDisabled: true)), "TIM-002");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("dérive", finding.PlainExplanation);
        }

        [Fact]
        public void Un_service_de_temps_simplement_arrete_ne_dit_rien()
        {
            // Depuis Windows 10, W32Time démarre à la demande et s'arrête : le trouver arrêté est
            // l'état normal de presque toutes les machines. Le signaler reviendrait à ne plus
            // rien signaler.
            Assert.DoesNotContain(
                Evaluate(b => b.Clock(serviceDisabled: false)),
                finding => finding.RuleId == "TIM-002");
        }

        // ============================================================ synchronisation

        [Fact]
        public void Une_synchronisation_ancienne_est_signalee_sans_accuser_l_horloge()
        {
            var finding = Single(Evaluate(b => b.Clock(lastSyncDaysAgo: 200)), "TIM-003");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("pas forcément fausse", finding.PlainExplanation);
        }

        [Fact]
        public void Un_journal_vide_ne_permet_pas_de_conclure()
        {
            // Un journal purgé ou une machine réinstallée la veille : l'absence de trace ne
            // prouve rien, et un « jamais synchronisée » prononcé à tort enverrait chercher une
            // panne qui n'existe pas.
            var score = Analyze(b => b.Clock(synchronised: false)).Score!;

            Assert.DoesNotContain("TIM-003", score.EvaluatedRuleIds);
        }

        // ============================================================ heure d'été

        [Fact]
        public void L_ajustement_a_l_heure_d_ete_coupe_est_signale()
        {
            var finding = Single(Evaluate(b => b.Clock(daylightAdjustment: false)), "TIM-004");

            Assert.Equal(Severity.Info, finding.Severity);
            Assert.Contains("moitié de l'année", finding.PlainExplanation);
        }

        // ============================================================ domaine

        [Fact]
        public void Un_poste_de_domaine_regle_sur_un_serveur_exterieur_est_un_avertissement()
        {
            // Kerberos refuse toute session au-delà de cinq minutes d'écart, avec un message qui
            // ne parle jamais de l'heure.
            var finding = Single(Evaluate(b =>
            {
                b.DomainMember();
                b.Clock(source: TimeSource.Ntp);
            }), "TIM-005");

            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.Contains("cinq minutes", finding.PlainExplanation);
        }

        [Fact]
        public void Le_meme_reglage_hors_domaine_est_le_bon()
        {
            // Hors domaine, un serveur de temps public est exactement ce qu'il faut : la règle ne
            // doit pas se contenter de ne rien trouver, elle ne doit pas s'appliquer.
            var score = Analyze(b => b.Clock(source: TimeSource.Ntp)).Score!;

            Assert.DoesNotContain("TIM-005", score.EvaluatedRuleIds);
        }

        [Fact]
        public void Un_poste_de_domaine_synchronise_par_le_domaine_ne_dit_rien()
        {
            Assert.DoesNotContain(
                Evaluate(b => { b.DomainMember(); b.Clock(source: TimeSource.Domain); }),
                finding => finding.RuleId == "TIM-005");
        }

        // ============================================================ la fiche

        [Fact]
        public void La_fiche_met_l_heure_et_sa_reference_cote_a_cote()
        {
            // Seule, une heure n'apprend rien : elle a toujours l'air juste.
            var facts = Group(b => b.Clock());

            Assert.Contains(facts, fact => fact.Label == "Heure de la machine");
            Assert.Contains(facts, fact => fact.Label == "Fichiers du noyau datés du");
        }

        // ============================================================ montage

        private static IReadOnlyList<Finding> Evaluate(Action<Fixtures.Builder> configure)
            => Analyze(configure).Findings;

        private static SystemSnapshot Analyze(Action<Fixtures.Builder> configure)
            => new AnalysisEngine().Analyze(Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            }));

        private static Finding Single(IReadOnlyList<Finding> findings, string ruleId)
            => Assert.Single(findings.Where(finding => finding.RuleId == ruleId));

        private static IReadOnlyList<Fact> Group(Action<Fixtures.Builder> configure)
        {
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                configure(builder);
            });

            foreach (var sheet in FactSheetBuilder.Build(snapshot))
                foreach (var group in sheet.Groups)
                    if (group.Title == "Heure et synchronisation") return group.Facts;

            throw new InvalidOperationException("Le cadre « Heure et synchronisation » est absent.");
        }
    }
}
