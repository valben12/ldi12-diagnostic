using System;
using System.IO;
using System.Threading;
using LDI12.Core.Benchmarks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Les mesures de performance, et la seule question qu'elles doivent trancher honnêtement :
    /// ce chiffre décrit-il le disque, ou le cache de Windows ?
    /// </summary>
    public class BenchmarkTests
    {
        [Fact]
        public void Un_acces_isole_lent_designe_un_disque_mecanique()
        {
            var verdicts = BenchmarkVerdicts.Describe(
                Throughput(readMegabytes: 120, writeMegabytes: 110, latencyMs: 14),
                StorageMediaType.Hdd, systemVolume: true);

            var verdict = Assert.Single(verdicts, v => v.Id == "disque-mecanique");

            Assert.Equal(Severity.Warning, verdict.Severity);
            Assert.Contains("tête de lecture", verdict.Explanation);
        }

        [Fact]
        public void Le_meme_disque_hors_volume_systeme_ne_declenche_pas_d_alerte()
        {
            // Un disque mécanique de données n'est pas un problème : c'est un choix. Le même
            // disque sous Windows en est un.
            var verdicts = BenchmarkVerdicts.Describe(
                Throughput(readMegabytes: 120, writeMegabytes: 110, latencyMs: 14),
                StorageMediaType.Hdd, systemVolume: false);

            var verdict = Assert.Single(verdicts, v => v.Id == "disque-mecanique");
            Assert.Equal(Severity.Info, verdict.Severity);
        }

        [Fact]
        public void Un_disque_qui_se_declare_flash_et_repond_comme_un_plateau_est_un_probleme()
        {
            // Le cas qui vaut la mesure : le disque annonce une chose, il en fait une autre.
            // Boîtier USB qui bride, disque presque plein, ou disque en fin de vie.
            var verdicts = BenchmarkVerdicts.Describe(
                Throughput(readMegabytes: 90, writeMegabytes: 80, latencyMs: 9),
                StorageMediaType.Ssd, systemVolume: true);

            var verdict = Assert.Single(verdicts, v => v.Id == "disque-flash-lent");

            Assert.Equal(Severity.Problem, verdict.Severity);
            Assert.DoesNotContain(verdicts, v => v.Id == "disque-mecanique");
        }

        [Fact]
        public void Un_disque_flash_sain_ne_declenche_aucune_alerte()
        {
            var verdicts = BenchmarkVerdicts.Describe(
                Throughput(readMegabytes: 2400, writeMegabytes: 1800, latencyMs: 0.09),
                StorageMediaType.Nvme, systemVolume: true);

            foreach (var verdict in verdicts) Assert.Equal(Severity.Info, verdict.Severity);
            Assert.Contains(verdicts, v => v.Id == "disque-immediat");
        }

        [Fact]
        public void Une_mesure_absente_ne_produit_aucune_conclusion()
        {
            // Un disque qui n'a pas pu être mesuré n'est pas un disque lent.
            var nothing = new StorageThroughput
            {
                ReadBytesPerSecond = Measured.Missing<double>("Volume protégé en écriture."),
                WriteBytesPerSecond = Measured.Missing<double>("Volume protégé en écriture."),
                RandomReadMilliseconds = Measured.Missing<double>("Volume protégé en écriture."),
            };

            Assert.Empty(BenchmarkVerdicts.Describe(nothing, StorageMediaType.Ssd, systemVolume: true));
        }

        [Theory]
        [InlineData(7.4, 8)]     // gain normal, rien à dire
        [InlineData(4.1, 8)]     // gain amputé mais explicable : rien à dire non plus
        [InlineData(1.2, 8)]     // huit cœurs pour un gain de 1,2 : la machine ne les utilise pas
        public void Le_gain_a_plusieurs_coeurs_n_est_signale_que_lorsqu_il_est_inexplicable(
            double gain, int processors)
        {
            var verdict = BenchmarkVerdicts.DescribeParallelGain(gain, processors);

            if (gain < 1.5) Assert.NotNull(verdict);
            else Assert.Null(verdict);
        }

        [Fact]
        public void Un_gain_faible_sur_deux_coeurs_ne_prouve_rien()
        {
            // Sur une machine à deux cœurs logiques, un gain médiocre s'explique de trop de
            // façons pour qu'on en tire un diagnostic.
            Assert.Null(BenchmarkVerdicts.DescribeParallelGain(1.1, 2));
        }

        [Fact]
        public void Un_volume_sans_place_est_refuse_avec_sa_raison()
        {
            // Remplir un disque déjà plein pour mesurer sa lenteur serait aggraver ce qu'on
            // vient de constater.
            var gateway = new StorageBenchmarkGateway(NullLogger.Instance);
            var plan = gateway.Plan(Root(), long.MaxValue / 2);

            Assert.False(plan.CanRun);
            Assert.NotNull(plan.Refusal);
        }

        [Fact]
        public void Le_plan_annonce_le_chemin_et_la_taille_avant_d_ecrire_quoi_que_ce_soit()
        {
            var gateway = new StorageBenchmarkGateway(NullLogger.Instance);
            var plan = gateway.Plan(Root(), 16 * 1024 * 1024);

            Assert.True(plan.CanRun, plan.Refusal);
            Assert.Equal(16 * 1024 * 1024, plan.Bytes);
            Assert.NotEmpty(plan.FilePath);
            Assert.False(File.Exists(plan.FilePath), "Établir un plan ne doit rien écrire.");
        }

        /// <summary>
        /// Mesure réelle, en petit format : c'est le seul test qui prouve que le chemin natif
        /// sans mémoire tampon fonctionne sur cette machine.
        /// </summary>
        /// <remarks>
        /// Seize mégaoctets suffisent à exercer l'ouverture alignée, l'écriture traversante, la
        /// relecture et les accès isolés. Les chiffres obtenus ne sont pas vérifiés (sur seize
        /// mégaoctets ils décriraient surtout la mémoire tampon du disque) mais leur existence
        /// l'est, et le fichier ne doit rien laisser derrière lui.
        /// </remarks>
        [Fact]
        public void Une_mesure_reelle_rend_des_chiffres_et_ne_laisse_aucun_fichier()
        {
            var gateway = new StorageBenchmarkGateway(NullLogger.Instance);
            var plan = gateway.Plan(Root(), 16 * 1024 * 1024);
            Assert.True(plan.CanRun, plan.Refusal);

            var throughput = gateway.Measure(plan, null, CancellationToken.None);

            Assert.True(throughput.WriteBytesPerSecond.HasValue, throughput.Limitation);
            Assert.True(throughput.ReadBytesPerSecond.HasValue, throughput.Limitation);
            Assert.True(throughput.WriteBytesPerSecond.Value > 0);
            Assert.True(throughput.ReadBytesPerSecond.Value > 0);
            Assert.True(throughput.RandomReadMilliseconds.HasValue);

            Assert.False(File.Exists(plan.FilePath), "Le fichier de mesure doit disparaître avec le descripteur.");
        }

        private static string Root() => Path.GetPathRoot(Path.GetTempPath())!;

        private static StorageThroughput Throughput(
            double readMegabytes, double writeMegabytes, double latencyMs)
            => new StorageThroughput
            {
                ReadBytesPerSecond = Measured.Ok(readMegabytes * 1024 * 1024, DataSource.FileSystem),
                WriteBytesPerSecond = Measured.Ok(writeMegabytes * 1024 * 1024, DataSource.FileSystem),
                RandomReadMilliseconds = Measured.Ok(latencyMs, DataSource.FileSystem),
                RandomReadsPerSecond = Measured.Ok(1000d / latencyMs, DataSource.FileSystem),
            };
    }
}
