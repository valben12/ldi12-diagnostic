using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Profile;
using LDI12.Engine.Rules;
using Xunit;

namespace LDI12.Tests
{
    public class RuleCatalogTests
    {
        [Fact]
        public void Les_identifiants_de_regle_sont_uniques()
        {
            // Les identifiants sont cités dans les rapports archivés : un doublon rendrait deux
            // constats indiscernables lors d'une comparaison avant / après.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in RuleCatalog.All())
                Assert.True(seen.Add(rule.Descriptor.Id), "Identifiant de règle en double : " + rule.Descriptor.Id);
        }

        [Fact]
        public void Chaque_regle_declare_une_dimension_notee()
        {
            foreach (var rule in RuleCatalog.All())
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Descriptor.Name),
                    rule.Descriptor.Id + " : libellé manquant.");
                Assert.NotEqual(DiagnosticCategory.Platform, rule.Descriptor.Category);
            }
        }

        [Fact]
        public void Chaque_famille_citee_par_une_regle_a_un_plafond()
        {
            // Une famille sans plafond ne limite rien : le cumul deviendrait illimité sans que
            // personne ne s'en aperçoive.
            var profile = DiagnosticProfile.Default;
            foreach (var rule in RuleCatalog.All())
            {
                if (rule.Descriptor.Family == null) continue;
                Assert.True(profile.FamilyCaps.ContainsKey(rule.Descriptor.Family),
                    rule.Descriptor.Id + " déclare la famille « " + rule.Descriptor.Family +
                    " », absente des plafonds du barème.");
            }
        }

        [Fact]
        public void Chaque_pénalité_du_barème_correspond_à_une_règle_existante()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in RuleCatalog.All()) ids.Add(rule.Descriptor.Id);

            foreach (var pair in DiagnosticProfile.Default.PenaltyOverrides)
                Assert.True(ids.Contains(pair.Key),
                    "Le barème définit une pénalité pour « " + pair.Key + " », qui ne correspond à aucune règle.");
        }

        [Theory]
        [MemberData(nameof(Fixtures_All))]
        public void Chaque_constat_porte_ses_deux_libelles(string name, SystemSnapshot snapshot)
        {
            // Le double libellé n'est pas une commodité : c'est lui qui produit le rapport
            // technicien ET le rapport client à partir d'une seule analyse.
            var analyzed = new AnalysisEngine().Analyze(snapshot);

            foreach (var finding in analyzed.Findings)
            {
                Assert.False(string.IsNullOrWhiteSpace(finding.Title), finding.Key + " : titre manquant.");
                Assert.False(string.IsNullOrWhiteSpace(finding.TechnicalDetail),
                    finding.Key + " : détail technique manquant.");
                Assert.False(string.IsNullOrWhiteSpace(finding.PlainExplanation),
                    finding.Key + " : explication client manquante.");

                // L'explication client ne doit pas se contenter de recopier le détail technique.
                Assert.NotEqual(finding.TechnicalDetail, finding.PlainExplanation);
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures_All))]
        public void Un_constat_non_informatif_porte_toujours_une_preuve(string name, SystemSnapshot snapshot)
        {
            var analyzed = new AnalysisEngine().Analyze(snapshot);

            foreach (var finding in analyzed.Findings)
            {
                if (finding.Severity == Severity.Info) continue;
                Assert.True(finding.Evidence.Count > 0,
                    name + " : le constat " + finding.Key + " ne cite aucune mesure à l'appui.");
            }
        }

        [Fact]
        public void La_corrélation_du_disque_mourant_se_déclenche_sur_le_cas_prévu()
        {
            var analyzed = new AnalysisEngine().Analyze(Fixtures.DyingDisk());

            var found = false;
            foreach (var correlation in analyzed.Correlations)
                if (correlation.Id == "COR-001") found = true;

            Assert.True(found,
                "SMART dégradé et erreurs disque dans les journaux devraient produire la corrélation COR-001.");
        }

        [Fact]
        public void La_corrélation_DNS_isole_le_bon_étage()
        {
            var analyzed = new AnalysisEngine().Analyze(Fixtures.DnsFailure());

            Core.Model.Correlation? dns = null;
            foreach (var correlation in analyzed.Correlations)
                if (correlation.Id == "COR-003") dns = correlation;

            Assert.NotNull(dns);
            Assert.Contains("résolution", dns!.Title, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void La_corrélation_de_lenteur_exige_au_moins_deux_facteurs()
        {
            // Une cause unique n'est pas une corrélation : elle est déjà portée par son constat.
            var healthy = new AnalysisEngine().Analyze(Fixtures.Healthy());
            foreach (var correlation in healthy.Correlations)
                Assert.NotEqual("COR-004", correlation.Id);

            var slow = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var found = false;
            foreach (var correlation in slow.Correlations)
                if (correlation.Id == "COR-004") found = true;

            Assert.True(found, "La machine lente cumule plusieurs facteurs : COR-004 devrait se déclencher.");
        }

        [Fact]
        public void Les_actions_sont_triees_par_priorite_puis_par_rapport_impact_effort()
        {
            var analyzed = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var recommendations = analyzed.Recommendations;

            for (var i = 1; i < recommendations.Count; i++)
            {
                var previous = recommendations[i - 1];
                var current = recommendations[i];

                Assert.True(previous.Priority <= current.Priority,
                    "Priorités désordonnées : " + previous.Id + " avant " + current.Id + ".");

                if (previous.Priority != current.Priority) continue;
                Assert.True(previous.ExpectedImpact >= current.ExpectedImpact,
                    "À priorité égale, l'impact le plus fort doit passer devant : " +
                    previous.Id + " avant " + current.Id + ".");
            }
        }

        [Fact]
        public void Les_actions_ne_sont_jamais_dupliquees()
        {
            var analyzed = new AnalysisEngine().Analyze(Fixtures.SlowMachine());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var recommendation in analyzed.Recommendations)
                Assert.True(seen.Add(recommendation.Id),
                    "Action en double dans le plan : " + recommendation.Id);
        }

        public static IEnumerable<object[]> Fixtures_All()
        {
            foreach (var fixture in Fixtures.All())
                yield return new object[] { fixture.Name, fixture.Snapshot };
        }
    }
}
