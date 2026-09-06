using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LDI12.Core.Model;
using LDI12.Engine;
using LDI12.Engine.Profile;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que l'ajustement des seuils doit garantir.
    /// </summary>
    /// <remarks>
    /// Un barème modifiable est utile et dangereux : c'est le seul endroit où un technicien peut,
    /// sans s'en rendre compte, faire dire à l'outil autre chose que ce qu'il mesure. Ces tests
    /// portent donc sur les deux dérives possibles : un seuil qu'on ne peut pas voir, et un seuil
    /// dont la modification n'aurait aucun effet.
    /// </remarks>
    public class SettingsTests
    {
        [Fact]
        public void Chaque_seuil_du_bareme_est_modifiable_depuis_l_ecran()
        {
            // Un seuil ajouté au moteur et oublié dans le catalogue serait invisible dans l'écran
            // de réglages, donc impossible à ajuster, sans que rien ne le signale. La réflexion
            // attrape l'oubli au premier lancement des tests.
            var described = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in ThresholdCatalog.All) described.Add(descriptor.Key);

            var missing = new List<string>();
            foreach (var property in typeof(Thresholds).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (!described.Contains(property.Name)) missing.Add(property.Name);

            Assert.True(missing.Count == 0,
                "Seuils absents du catalogue de réglages : " + string.Join(", ", missing));
        }

        [Fact]
        public void Un_seuil_libelle_en_octets_se_saisit_bien_en_gigaoctets()
        {
            // L'écran de réglages offre en gigaoctets tout seuil dont l'unité est « octets »,
            // pour éviter de faire taper 8 589 934 592 à un technicien. Un seuil en octets dont
            // les bornes ne sont pas à l'échelle du gigaoctet s'affiche donc « 0 Go, de 0 à 0 Go »
            // et se déclare hors des bornes sur sa propre valeur d'origine. C'est ce qui arrivait
            // à la taille de paquet attendue, qui se compte en centaines d'octets.
            const double Gigaoctet = 1024d * 1024 * 1024;

            foreach (var descriptor in ThresholdCatalog.All)
            {
                if (descriptor.Unit != "octets") continue;

                Assert.True(descriptor.Minimum >= Gigaoctet,
                    "« " + descriptor.Label + " » est libellé en octets mais se compte en petites " +
                    "valeurs : l'écran de réglages l'afficherait à zéro gigaoctet.");
            }
        }

        [Fact]
        public void Aucun_seuil_decrit_ne_designe_une_propriete_disparue()
        {
            foreach (var descriptor in ThresholdCatalog.All)
                Assert.NotNull(typeof(Thresholds).GetProperty(
                    descriptor.Key, BindingFlags.Public | BindingFlags.Instance));
        }

        [Fact]
        public void Chaque_seuil_dit_ce_que_son_deplacement_change()
        {
            // « Espace libre : 15 % » n'apprend rien ; « en deçà, le volume de Windows est
            // signalé à surveiller » se décide. C'est la différence entre un champ et un réglage.
            foreach (var descriptor in ThresholdCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Label), descriptor.Key + " : sans libellé.");
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect), descriptor.Key + " : sans effet décrit.");
                Assert.True(descriptor.Minimum < descriptor.Maximum, descriptor.Key + " : bornes incohérentes.");
            }
        }

        [Fact]
        public void La_valeur_d_origine_de_chaque_seuil_est_dans_ses_bornes()
        {
            // Sinon l'écran proposerait de « revenir à l'origine » vers une valeur qu'il refuse.
            var defaults = new Thresholds();

            foreach (var descriptor in ThresholdCatalog.All)
            {
                var value = ThresholdCatalog.Read(defaults, descriptor.Key);
                Assert.InRange(value, descriptor.Minimum, descriptor.Maximum);
                Assert.Equal(value, descriptor.Default);
            }
        }

        [Fact]
        public void Modifier_un_seuil_ne_touche_a_aucun_autre()
        {
            var original = new Thresholds();
            var modified = ThresholdCatalog.With(original, nameof(Thresholds.DiskTemperatureWarning), 55);

            Assert.Equal(55, ThresholdCatalog.Read(modified, nameof(Thresholds.DiskTemperatureWarning)));

            foreach (var descriptor in ThresholdCatalog.All)
            {
                if (descriptor.Key == nameof(Thresholds.DiskTemperatureWarning)) continue;
                Assert.Equal(
                    ThresholdCatalog.Read(original, descriptor.Key),
                    ThresholdCatalog.Read(modified, descriptor.Key));
            }
        }

        [Fact]
        public void Une_valeur_hors_bornes_est_ramenee_et_non_acceptee()
        {
            // Un champ de saisie accepte tout ; le barème, non. Une température de disque à
            // 500 °C rendrait la règle inopérante sans que personne ne s'en aperçoive.
            var thresholds = ThresholdCatalog.With(new Thresholds(), nameof(Thresholds.DiskTemperatureWarning), 500);
            var descriptor = ThresholdCatalog.Find(nameof(Thresholds.DiskTemperatureWarning))!;

            Assert.Equal(descriptor.Maximum, ThresholdCatalog.Read(thresholds, descriptor.Key));
        }

        [Fact]
        public void Un_seuil_entier_reste_entier_apres_modification()
        {
            // Les seuils sont typés int, long ou double selon ce qu'ils comptent. Passer par un
            // double unique à l'écran ne doit pas transformer « 3 événements » en « 3,4 ».
            var thresholds = ThresholdCatalog.With(new Thresholds(), nameof(Thresholds.CriticalEventsWarning), 4.7);

            Assert.Equal(5, thresholds.CriticalEventsWarning);
        }

        [Fact]
        public void Un_seuil_deplace_change_reellement_le_verdict()
        {
            // Le test qui compte : sans lui, l'écran de réglages pourrait n'être qu'une façade.
            // La machine saine a un volume système à 50 % libre et ne produit aucun constat
            // d'espace ; en exigeant 60 %, elle doit en produire un.
            var snapshot = Fixtures.Healthy();

            Assert.DoesNotContain(
                new AnalysisEngine().Analyze(snapshot, DiagnosticProfile.Default).Findings,
                finding => finding.RuleId == "STO-002" || finding.RuleId == "STO-001");

            var demanding = new DiagnosticProfile
            {
                Limits = ThresholdCatalog.With(
                    new Thresholds(), nameof(Thresholds.SystemVolumeFreePercentWarning), 60),
            };

            Assert.Contains(
                new AnalysisEngine().Analyze(snapshot, demanding).Findings,
                finding => finding.RuleId == "STO-001");
        }

        [Fact]
        public void Le_bareme_ajuste_se_relit_tel_qu_il_a_ete_ecrit()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ldi12-bareme-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                var profile = new DiagnosticProfile
                {
                    Limits = ThresholdCatalog.With(
                        new Thresholds(), nameof(Thresholds.JitterWarningMs), 42),
                };

                LDI12.Reports.Json.ProfileSerializer.Save(profile, path);
                var reloaded = LDI12.Reports.Json.ProfileSerializer.Load<DiagnosticProfile>(path);

                Assert.Equal(42, reloaded.Limits.JitterWarningMs);
                Assert.Equal(profile.Limits.DiskTemperatureWarning, reloaded.Limits.DiskTemperatureWarning);
                Assert.Equal(profile.GlobalCriticalCap, reloaded.GlobalCriticalCap);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void Les_seuils_d_un_meme_domaine_sont_ordonnes_du_moins_grave_au_plus_grave()
        {
            // Un seuil « problème » plus bas que son « avertissement » inverserait les gravités
            // sans erreur visible : le constat le plus grave se déclencherait le premier.
            var limits = new Thresholds();

            Assert.True(limits.SystemVolumeFreePercentWarning > limits.SystemVolumeFreePercentProblem);
            Assert.True(limits.SystemVolumeFreePercentProblem > limits.SystemVolumeFreePercentCritical);
            Assert.True(limits.DiskTemperatureWarning < limits.DiskTemperatureProblem);
            Assert.True(limits.DiskTemperatureProblem < limits.DiskTemperatureCritical);
            Assert.True(limits.ReallocatedSectorsWarning < limits.ReallocatedSectorsProblem);
            Assert.True(limits.ReallocatedSectorsProblem < limits.ReallocatedSectorsCritical);
            Assert.True(limits.MemoryUsageWarning < limits.MemoryUsageProblem);
            Assert.True(limits.StartupItemsWarning < limits.StartupItemsProblem);
            Assert.True(limits.BootDurationSecondsWarning < limits.BootDurationSecondsProblem);
            Assert.True(limits.CommitRatioWarning < limits.CommitRatioProblem);
            Assert.True(limits.SignatureAgeDaysWarning < limits.SignatureAgeDaysProblem);
            Assert.True(limits.PacketLossWarning < limits.PacketLossProblem);
            Assert.True(limits.ThermalZoneWarning < limits.ThermalZoneProblem);
            Assert.True(limits.WifiSignalProblemPercent < limits.WifiSignalWarningPercent);
            Assert.True(limits.UpdateDelayDaysWarning < limits.UpdateDelayDaysProblem);
            Assert.True(limits.CriticalEventsWarning < limits.CriticalEventsProblem);
            Assert.True(limits.SsdWearWarning < limits.SsdWearProblem);
            Assert.True(limits.SsdWearProblem < limits.SsdWearCritical);
            Assert.True(limits.BatteryWearWarning < limits.BatteryWearProblem);
        }
    }
}
