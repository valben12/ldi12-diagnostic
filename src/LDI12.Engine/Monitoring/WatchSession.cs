using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Monitoring;

namespace LDI12.Engine.Monitoring
{
    /// <summary>
    /// Une session de surveillance : elle accumule les échantillons et sait ce qu'elle a le
    /// droit d'en conclure.
    /// </summary>
    /// <remarks>
    /// <b>La tentation de cet écran est symétrique de celle du comparatif.</b> Le comparatif veut
    /// compter comme réparé tout ce qui a disparu ; la surveillance veut conclure « rien à
    /// signaler » de tout ce qui ne s'est pas produit pendant qu'elle regardait. Une machine qui
    /// rame une fois par jour est parfaitement calme pendant les deux minutes d'observation, et
    /// l'écran vert qui en résulterait serait un faux diagnostic, le plus difficile à rattraper,
    /// puisqu'il rassure.
    /// <para>
    /// Trois garde-fous vivent donc ici et non dans l'affichage : rien n'est conclu avant une
    /// durée minimale, les responsabilités ne se lisent que sur les échantillons où la machine
    /// était réellement sollicitée, et une session où rien ne s'est passé le dit ainsi plutôt que
    /// de dire que tout va bien.
    /// </para>
    /// </remarks>
    public sealed class WatchSession
    {
        private readonly Dictionary<string, LeaderTally> _leaders =
            new Dictionary<string, LeaderTally>(StringComparer.OrdinalIgnoreCase);

        private DateTimeOffset _first;
        private DateTimeOffset _last;

        public WatchSession(WatchLimits? limits = null)
            => Limits = limits ?? new WatchLimits();

        public WatchLimits Limits { get; }

        public WatchTrack Cpu { get; } = new WatchTrack("Processeur", "%");
        public WatchTrack Memory { get; } = new WatchTrack("Mémoire", "%");
        public WatchTrack Commit { get; } = new WatchTrack("Mémoire réclamée", "%");
        public WatchTrack Temperature { get; } = new WatchTrack("Température", "°C");
        public WatchTrack Frequency { get; } = new WatchTrack("Fréquence", "MHz");
        public WatchTrack Network { get; } = new WatchTrack("Réseau", "o/s");

        public int SampleCount { get; private set; }

        /// <summary>Échantillons où la charge processeur atteignait le seuil de sollicitation.</summary>
        public int BusySamples { get; private set; }

        public TimeSpan Duration => SampleCount == 0 ? TimeSpan.Zero : _last - _first;

        /// <summary>La session a atteint sa durée maximale : elle doit s'arrêter.</summary>
        public bool Exhausted => Duration >= Limits.MaximumDuration;

        /// <summary>Fréquence maximale annoncée par le processeur, quand elle est connue.</summary>
        public int RatedMegahertz { get; private set; }

        public void Add(LiveSample sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));

            if (SampleCount == 0) _first = sample.At;
            _last = sample.At;
            SampleCount++;

            Cpu.Add(sample.CpuPercent);
            Memory.Add(sample.MemoryPercent);
            Commit.Add(sample.CommitRatioPercent);
            Temperature.Add(sample.CpuCelsius);
            Frequency.Add(sample.CpuMegahertz.Map(value => (double)value));
            Network.Add(sample.NetworkBytesPerSecond);

            if (sample.CpuMaxMegahertz.HasValue && sample.CpuMaxMegahertz.Value > RatedMegahertz)
                RatedMegahertz = sample.CpuMaxMegahertz.Value;

            if (!sample.CpuPercent.HasValue || sample.CpuPercent.Value < Limits.BusyCpuPercent) return;

            BusySamples++;
            TallyLeader(sample);
        }

        /// <summary>
        /// Le programme en tête, hors le nôtre.
        /// </summary>
        /// <remarks>
        /// Notre propre processus est écarté des conclusions, et de lui seul : il apparaît dans
        /// la liste affichée, marqué, parce que la charge qu'il produit est réelle. Mais nommer
        /// le logiciel de diagnostic comme responsable de la lenteur d'une machine qu'il est en
        /// train de mesurer n'apprendrait rien à personne.
        /// </remarks>
        private void TallyLeader(LiveSample sample)
        {
            LiveProcessLoad? leader = null;
            foreach (var process in sample.Busiest)
            {
                if (process.IsSelf || !process.CpuPercent.HasValue) continue;
                if (leader == null || process.CpuPercent.Value > leader.CpuPercent.Value) leader = process;
            }

            if (leader == null) return;

            if (!_leaders.TryGetValue(leader.Name, out var tally))
                _leaders[leader.Name] = tally = new LeaderTally();

            tally.Leads++;
            tally.CpuSum += leader.CpuPercent.Value;
        }

        public WatchReport Conclude()
        {
            var observations = new List<WatchObservation>();
            var caveats = new List<string>();

            var longEnough = Duration >= Limits.MinimumDuration && SampleCount >= 5;
            if (!longEnough && SampleCount > 0)
            {
                caveats.Add(
                    "La surveillance a duré " + ValueFormat.Duration(Duration) + " : trop peu pour " +
                    "conclure quoi que ce soit. Une lenteur qui ne se produit pas pendant qu'on " +
                    "regarde ne se mesure pas.");
            }

            Uncovered(caveats, Cpu, "la charge du processeur");
            Uncovered(caveats, Memory, "l'occupation de la mémoire");
            Uncovered(caveats, Temperature, "la température du processeur");
            Uncovered(caveats, Frequency, "la fréquence du processeur");

            if (longEnough)
            {
                Sustained(observations);
                Leader(observations);
                Heat(observations);
                Throttling(observations, caveats);
                Overcommitted(observations);

                if (BusySamples == 0)
                {
                    caveats.Add(
                        "La machine n'a jamais été réellement sollicitée pendant l'observation : ce " +
                        "qui la ralentit, s'il y a quelque chose, ne s'est pas produit ici.");
                }
            }

            return new WatchReport
            {
                Duration = Duration,
                SampleCount = SampleCount,
                BusySamples = BusySamples,
                LongEnough = longEnough,
                Observations = observations,
                Caveats = caveats,
                Headline = Headline(longEnough, observations),
            };
        }

        private void Sustained(ICollection<WatchObservation> observations)
        {
            if (!Cpu.HasData) return;

            var share = Cpu.ShareAtOrAbove(Limits.SustainedCpuPercent);
            if (share < Limits.SustainedShare) return;

            observations.Add(new WatchObservation
            {
                Id = "charge-soutenue",
                Severity = Severity.Warning,
                Statement =
                    "Le processeur est resté au-dessus de " + Whole(Limits.SustainedCpuPercent) +
                    " % pendant " + Whole(share * 100) + " % du temps observé.",
                Explanation =
                    "Une pointe de charge est normale ; une charge qui ne redescend pas signifie " +
                    "qu'un programme travaille en continu. Tout le reste attend son tour, et c'est " +
                    "ce que l'utilisateur ressent comme de la lenteur.",
            });
        }

        private void Leader(ICollection<WatchObservation> observations)
        {
            if (BusySamples == 0) return;

            string? name = null;
            LeaderTally? best = null;
            foreach (var pair in _leaders)
                if (best == null || pair.Value.Leads > best.Leads) { name = pair.Key; best = pair.Value; }

            if (name == null || best == null) return;

            var share = (double)best.Leads / BusySamples;
            if (share < Limits.LeaderShare) return;

            observations.Add(new WatchObservation
            {
                Id = "programme-en-tete",
                Severity = Severity.Warning,
                Statement =
                    "Sur les " + best.Leads + " relevés où la machine était occupée, « " + name +
                    " » était en tête, à " + Whole(best.CpuSum / best.Leads) +
                    " % de processeur en moyenne.",
                Explanation =
                    "Un même programme domine la charge : c'est de lui qu'il faut partir, et non de " +
                    "la machine elle-même. Reste à savoir s'il travaille pour l'utilisateur ou malgré lui.",
            });
        }

        private void Heat(ICollection<WatchObservation> observations)
        {
            if (!Temperature.HasData || Temperature.Max < Limits.HotCelsius) return;

            var share = Temperature.ShareAtOrAbove(Limits.HotCelsius);

            observations.Add(new WatchObservation
            {
                Id = "temperature-haute",
                Severity = Temperature.Max >= Limits.VeryHotCelsius ? Severity.Problem : Severity.Warning,
                Statement =
                    "Le processeur a atteint " + Whole(Temperature.Max) + " °C, et est resté au-dessus " +
                    "de " + Whole(Limits.HotCelsius) + " °C pendant " + Whole(share * 100) +
                    " % du temps observé.",
                Explanation =
                    "Un processeur chaud se protège en se ralentissant. Sur une machine de quelques " +
                    "années, la cause habituelle est la poussière accumulée dans le radiateur et la " +
                    "pâte thermique sèche : c'est réparable, et cela se voit à l'ouverture.",
            });
        }

        /// <summary>
        /// Ralentissement thermique : la fréquence tenue à chaud comparée à celle tenue à froid.
        /// </summary>
        /// <remarks>
        /// Comparer la machine à elle-même est la seule méthode honnête ici : la fréquence
        /// nominale d'un processeur ne dit rien de ce qu'il tient réellement, et la fiche du
        /// constructeur encore moins.
        /// <para>
        /// Beaucoup de machines rapportent une fréquence figée, Windows y renvoie la valeur
        /// nominale quoi qu'il arrive. La session le constate, la valeur n'ayant pas bougé d'un
        /// mégahertz, et le dit ; conclure de ce plat qu'il n'y a pas de bridage serait tirer une
        /// conclusion d'une source qui ne mesure rien.
        /// </para>
        /// </remarks>
        private void Throttling(ICollection<WatchObservation> observations, ICollection<string> caveats)
        {
            if (!Frequency.HasData) return;

            if (Frequency.Constant)
            {
                caveats.Add(
                    "La fréquence du processeur est restée rigoureusement identique sur les " +
                    Frequency.Count + " relevés : cette machine rapporte sa fréquence nominale et " +
                    "non sa fréquence réelle. Un ralentissement thermique ne peut être ni établi " +
                    "ni écarté ici.");
                return;
            }

            if (!Temperature.HasData) return;

            var hot = Frequency.AverageWhere(Temperature, Limits.HotCelsius, true, out var hotSamples);
            var cool = Frequency.AverageWhere(Temperature, Limits.HotCelsius, false, out var coolSamples);

            if (hotSamples < Limits.ThrottleSamples || coolSamples < Limits.ThrottleSamples) return;
            if (cool <= 0 || hot >= cool * Limits.ThrottleRatio) return;

            observations.Add(new WatchObservation
            {
                Id = "bridage-thermique",
                Severity = Severity.Problem,
                Statement =
                    "Au-dessus de " + Whole(Limits.HotCelsius) + " °C, le processeur tourne à " +
                    Whole(hot) + " MHz ; en dessous, il tient " + Whole(cool) + " MHz.",
                Explanation =
                    "La machine se protège de la chaleur en se ralentissant elle-même. Ce n'est pas " +
                    "une panne, c'est un refroidissement qui ne suit plus : la puissance perdue " +
                    "revient une fois le radiateur nettoyé.",
            });
        }

        private void Overcommitted(ICollection<WatchObservation> observations)
        {
            if (!Commit.HasData || Commit.Average < Limits.CommitRatioPercent) return;

            observations.Add(new WatchObservation
            {
                Id = "memoire-debordee",
                Severity = Severity.Warning,
                Statement =
                    "La machine a réclamé en moyenne " + Whole(Commit.Average) +
                    " % de la mémoire installée pendant l'observation.",
                Explanation =
                    "Au-delà de cent pour cent, Windows compense sur le disque, des centaines de " +
                    "fois plus lent que la mémoire. C'est la cause la plus fréquente d'une machine " +
                    "qui rame sans que le processeur soit saturé.",
            });
        }

        private string Headline(bool longEnough, IReadOnlyList<WatchObservation> observations)
        {
            if (SampleCount == 0) return "Aucune mesure : la surveillance n'a pas encore commencé.";

            var observed = ValueFormat.Duration(Duration);

            if (!longEnough) return "Observation de " + observed + " : trop courte pour conclure.";

            if (observations.Count == 0)
            {
                return BusySamples == 0
                    ? "Rien ne s'est produit pendant " + observed +
                      " d'observation : la machine est restée au repos."
                    : "Rien d'anormal après " + observed + " d'observation, sollicitation comprise.";
            }

            return observations.Count == 1
                ? "Une chose relevée en " + observed + " d'observation."
                : observations.Count + " choses relevées en " + observed + " d'observation.";
        }

        private static void Uncovered(ICollection<string> caveats, WatchTrack track, string what)
        {
            if (track.Missing == 0) return;

            // La raison vient d'une passerelle et commence par une majuscule : elle est jointe
            // comme une phrase à part entière, faute de quoi le deux-points en produirait deux.
            var reason = track.Reason ?? "La mesure n'a pas répondu.";

            caveats.Add(
                track.Count == 0
                    ? "Sur toute la session, " + what + " n'a jamais pu être lue. " + reason
                    : char.ToUpperInvariant(what[0]) + what.Substring(1) + " a manqué sur " +
                      track.Missing + " relevés sur " + (track.Count + track.Missing) + ". " + reason);
        }

        private static string Whole(double value)
            => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture);

        private sealed class LeaderTally
        {
            public int Leads;
            public double CpuSum;
        }
    }
}
