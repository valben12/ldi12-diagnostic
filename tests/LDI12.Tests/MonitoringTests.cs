using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Monitoring;
using LDI12.Engine.Monitoring;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La surveillance en direct, et la seule faute qu'elle puisse commettre : conclure que tout
    /// va bien de ce qui ne s'est pas produit pendant qu'elle regardait.
    /// </summary>
    public class MonitoringTests
    {
        private static readonly DateTimeOffset Origin = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Une_mesure_non_collectee_n_est_ni_une_valeur_ni_une_lacune()
        {
            // La température n'est relevée qu'un tour sur cinq : la compter comme manquante
            // ferait apparaître un capteur en panne là où il n'y a qu'une cadence.
            var track = new WatchTrack("Température", "°C");

            track.Add(Measured.Ok(60d, DataSource.NativeApi));
            track.Add(Measured.NotCollected<double>());
            track.Add(Measured.NotCollected<double>());

            Assert.Equal(1, track.Count);
            Assert.Equal(0, track.Missing);
            Assert.Equal(2, track.Skipped);
            Assert.Equal(1d, track.Coverage);

            // Le point vide est tout de même inscrit : deux courbes de cadences différentes
            // doivent rester alignées dans le temps.
            Assert.Equal(3, track.Values.Count);
        }

        [Fact]
        public void Un_capteur_muet_se_voit_au_lieu_de_se_diluer()
        {
            var track = new WatchTrack("Température", "°C");

            track.Add(Measured.Ok(60d, DataSource.NativeApi));
            track.Add(Measured.Missing<double>("Les capteurs matériels sont désactivés."));

            Assert.Equal(1, track.Count);
            Assert.Equal(1, track.Missing);
            Assert.Equal(0.5d, track.Coverage);
            Assert.Equal(60d, track.Average);
            Assert.Contains("désactivés", track.Reason);
        }

        [Fact]
        public void Une_moyenne_conditionnelle_rapproche_les_memes_instants()
        {
            var temperature = new WatchTrack("Température", "°C");
            var frequency = new WatchTrack("Fréquence", "MHz");

            // Deux tours chauds, deux tours froids, et un tour où la température n'a pas été
            // demandée : celui-ci ne doit rejoindre aucun des deux camps.
            temperature.Add(95d); frequency.Add(1200d);
            temperature.Add(95d); frequency.Add(1200d);
            temperature.Skip(); frequency.Add(3600d);
            temperature.Add(50d); frequency.Add(3600d);
            temperature.Add(50d); frequency.Add(3400d);

            var hot = frequency.AverageWhere(temperature, 88, true, out var hotSamples);
            var cool = frequency.AverageWhere(temperature, 88, false, out var coolSamples);

            Assert.Equal(2, hotSamples);
            Assert.Equal(2, coolSamples);
            Assert.Equal(1200d, hot);
            Assert.Equal(3500d, cool);
        }

        [Fact]
        public void Une_session_trop_courte_ne_conclut_rien()
        {
            // Vingt secondes à cent pour cent de processeur : de quoi remplir tous les seuils,
            // et pas de quoi affirmer quoi que ce soit sur la machine.
            var session = Watch(Load(seconds: 20, cpu: 100));
            var report = session.Conclude();

            Assert.False(report.LongEnough);
            Assert.Empty(report.Observations);
            Assert.Contains(report.Caveats, caveat => caveat.Contains("trop peu pour conclure"));
            Assert.Contains("trop courte", report.Headline);
        }

        [Fact]
        public void Une_charge_soutenue_est_constatee_avec_sa_part_de_temps()
        {
            var report = Watch(Load(seconds: 120, cpu: 95)).Conclude();

            Assert.True(report.LongEnough);
            Assert.Contains(report.Observations, observation => observation.Id == "charge-soutenue");
        }

        [Fact]
        public void Une_machine_au_repos_le_dit_au_lieu_de_se_declarer_saine()
        {
            // Le piège de l'écran : rien ne s'est produit, et rien ne prouve que rien ne se
            // produit jamais.
            var report = Watch(Load(seconds: 120, cpu: 4)).Conclude();

            Assert.Empty(report.Observations);
            Assert.Equal(0, report.BusySamples);
            Assert.Contains("au repos", report.Headline);
            Assert.Contains(report.Caveats, caveat => caveat.Contains("jamais été réellement sollicitée"));
        }

        [Fact]
        public void Le_programme_en_tete_ne_se_lit_que_sur_les_releves_ou_la_machine_travaille()
        {
            var samples = new List<LiveSample>();

            // Cent tours au repos où un petit programme est en tête pour rien, puis trente tours
            // de charge réelle dominés par un autre : c'est le second qui explique la lenteur.
            for (var index = 0; index < 100; index++)
                samples.Add(Sample(index, cpu: 5, leader: "widget.exe", leaderCpu: 3));

            for (var index = 100; index < 130; index++)
                samples.Add(Sample(index, cpu: 90, leader: "sauvegarde.exe", leaderCpu: 70));

            var report = Watch(samples).Conclude();
            var named = Assert.Single(report.Observations, o => o.Id == "programme-en-tete");

            Assert.Contains("sauvegarde.exe", named.Statement);
            Assert.DoesNotContain("widget.exe", named.Statement);
            Assert.Equal(30, report.BusySamples);
        }

        [Fact]
        public void Notre_propre_processus_n_est_jamais_designe_responsable()
        {
            // Le logiciel de diagnostic consomme du processeur pendant qu'il surveille. Le nommer
            // coupable de la lenteur d'une machine qu'il est en train de mesurer n'apprendrait
            // rien à personne.
            var samples = new List<LiveSample>();
            for (var index = 0; index < 120; index++)
                samples.Add(Sample(index, cpu: 90, leader: "LDI12.Diagnostic", leaderCpu: 80, leaderIsSelf: true));

            var report = Watch(samples).Conclude();

            Assert.DoesNotContain(report.Observations, observation => observation.Id == "programme-en-tete");
        }

        [Fact]
        public void Une_frequence_figee_interdit_la_conclusion_de_bridage_dans_les_deux_sens()
        {
            // Beaucoup de machines renvoient leur fréquence nominale quoi qu'il arrive. En
            // conclure « pas de bridage » serait tirer une conclusion d'une source qui ne mesure
            // rien.
            var samples = new List<LiveSample>();
            for (var index = 0; index < 120; index++)
                samples.Add(Sample(index, cpu: 90, celsius: index < 60 ? 55 : 95, megahertz: 3600));

            var report = Watch(samples).Conclude();

            Assert.DoesNotContain(report.Observations, observation => observation.Id == "bridage-thermique");
            Assert.Contains(report.Caveats, caveat => caveat.Contains("fréquence nominale"));
        }

        [Fact]
        public void Le_bridage_thermique_compare_la_machine_a_elle_meme()
        {
            var samples = new List<LiveSample>();
            for (var index = 0; index < 60; index++)
                samples.Add(Sample(index, cpu: 90, celsius: 55, megahertz: 3600));
            for (var index = 60; index < 120; index++)
                samples.Add(Sample(index, cpu: 90, celsius: 96, megahertz: 1400));

            var report = Watch(samples).Conclude();
            var throttle = Assert.Single(report.Observations, o => o.Id == "bridage-thermique");

            Assert.Contains("1400", throttle.Statement);
            Assert.Contains("3600", throttle.Statement);
            Assert.Equal(Severity.Problem, throttle.Severity);
        }

        [Fact]
        public void Une_temperature_qui_monte_est_signalee_meme_sans_frequence()
        {
            var samples = new List<LiveSample>();
            for (var index = 0; index < 120; index++)
                samples.Add(Sample(index, cpu: 50, celsius: 97, megahertz: null));

            var report = Watch(samples).Conclude();
            var heat = Assert.Single(report.Observations, o => o.Id == "temperature-haute");

            Assert.Equal(Severity.Problem, heat.Severity);
            Assert.Contains("97", heat.Statement);
        }

        [Fact]
        public void La_surveillance_s_arrete_d_elle_meme_au_bout_du_temps_maximal()
        {
            // Une surveillance oubliée continuerait de consommer du processeur sur la machine
            // d'un client pendant que personne ne regarde l'écran.
            var limits = new WatchLimits { MaximumDuration = TimeSpan.FromMinutes(1) };
            var session = new WatchSession(limits);

            foreach (var sample in Load(seconds: 45, cpu: 20)) session.Add(sample);
            Assert.False(session.Exhausted);

            foreach (var sample in Load(seconds: 90, cpu: 20)) session.Add(sample);
            Assert.True(session.Exhausted);
        }

        [Fact]
        public void Une_session_vide_ne_pretend_rien_avoir_vu()
        {
            var report = new WatchSession().Conclude();

            Assert.Equal(0, report.SampleCount);
            Assert.Empty(report.Observations);
            Assert.Empty(report.Caveats);
            Assert.Contains("n'a pas encore commencé", report.Headline);
        }

        private static WatchSession Watch(IEnumerable<LiveSample> samples)
        {
            var session = new WatchSession();
            foreach (var sample in samples) session.Add(sample);
            return session;
        }

        private static List<LiveSample> Load(int seconds, double cpu)
        {
            var samples = new List<LiveSample>(seconds);
            for (var index = 0; index < seconds; index++) samples.Add(Sample(index, cpu));
            return samples;
        }

        private static LiveSample Sample(
            int second, double cpu, double? celsius = null, int? megahertz = null,
            string? leader = null, double leaderCpu = 0, bool leaderIsSelf = false)
        {
            var busiest = leader == null
                ? Array.Empty<LiveProcessLoad>()
                : new[]
                {
                    new LiveProcessLoad
                    {
                        Name = leader,
                        ProcessId = leaderIsSelf ? 1 : 2,
                        CpuPercent = Measured.Ok(leaderCpu, DataSource.NativeApi),
                        IsSelf = leaderIsSelf,
                    },
                };

            return new LiveSample
            {
                At = Origin.AddSeconds(second),
                CpuPercent = Measured.Ok(cpu, DataSource.NativeApi),
                MemoryPercent = Measured.Ok(50d, DataSource.NativeApi),
                CommitRatioPercent = Measured.Ok(60d, DataSource.NativeApi),
                CpuCelsius = celsius.HasValue
                    ? Measured.Ok(celsius.Value, DataSource.NativeApi)
                    : Measured.NotCollected<double>(),
                CpuMegahertz = megahertz.HasValue
                    ? Measured.Ok(megahertz.Value, DataSource.NativeApi)
                    : Measured.NotCollected<int>(),
                CpuMaxMegahertz = Measured.Ok(3600, DataSource.NativeApi),
                NetworkBytesPerSecond = Measured.Ok(0d, DataSource.NativeApi),
                Busiest = busiest,
            };
        }
    }
}
