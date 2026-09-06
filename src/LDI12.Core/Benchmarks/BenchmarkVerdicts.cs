using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Core.Benchmarks
{
    /// <summary>
    /// Ce qu'un débit mesuré permet d'affirmer.
    /// </summary>
    /// <remarks>
    /// <b>Un score de performance n'a de sens que rapporté à quelque chose.</b> Les logiciels de
    /// mesure classent les machines les unes contre les autres, ce qui suppose une base de
    /// références qu'un outil hors ligne n'a pas, et qui n'apprendrait rien à un client de toute
    /// façon. Les conclusions tirées ici ne viennent donc jamais d'un classement : elles viennent
    /// de la physique du matériel, ou de la machine comparée à elle-même.
    /// <para>
    /// Le temps d'un accès isolé en est le meilleur exemple. Un plateau qui tourne impose de
    /// déplacer une tête de lecture, ce qui coûte quelques millisecondes et le coûtera toujours ;
    /// une mémoire flash répond en une fraction de milliseconde parce qu'elle n'a rien à déplacer.
    /// L'écart est de deux ordres de grandeur, il ne dépend d'aucun barème, et c'est très
    /// exactement ce que l'utilisateur ressent comme de la lenteur.
    /// </para>
    /// </remarks>
    public static class BenchmarkVerdicts
    {
        /// <summary>Au-delà, un accès isolé a coûté un déplacement mécanique.</summary>
        public const double MechanicalMilliseconds = 5;

        /// <summary>En deçà, aucune mécanique n'est en jeu.</summary>
        public const double FlashMilliseconds = 1;

        /// <summary>Sous ce débit continu, même un disque mécanique sain fait mieux.</summary>
        public const double PoorSequentialMegabytes = 40;

        /// <summary>Sous ce débit continu, un disque à mémoire flash ne tient pas son rang.</summary>
        public const double PoorFlashSequentialMegabytes = 150;

        private const double Megabyte = 1024 * 1024;

        public static IReadOnlyList<BenchmarkVerdict> Describe(
            StorageThroughput storage, StorageMediaType declared, bool systemVolume)
        {
            var verdicts = new List<BenchmarkVerdict>();
            if (storage == null) return verdicts;

            var flashExpected = declared == StorageMediaType.Ssd || declared == StorageMediaType.Nvme;

            Latency(verdicts, storage, flashExpected, systemVolume);
            Sequential(verdicts, storage, flashExpected);

            return verdicts;
        }

        private static void Latency(
            ICollection<BenchmarkVerdict> verdicts, StorageThroughput storage,
            bool flashExpected, bool systemVolume)
        {
            if (!storage.RandomReadMilliseconds.HasValue) return;

            var latency = storage.RandomReadMilliseconds.Value;
            var measured = "Chaque accès isolé a demandé " + Milliseconds(latency) + " à ce disque.";

            if (latency >= MechanicalMilliseconds && flashExpected)
            {
                verdicts.Add(new BenchmarkVerdict
                {
                    Id = "disque-flash-lent",
                    Severity = Severity.Problem,
                    Statement = "Ce disque se déclare à mémoire flash, et " + Lower(measured),
                    Explanation =
                        "Une mémoire flash répond normalement en moins d'un dixième de milliseconde. " +
                        "Un écart pareil a trois causes habituelles : un disque presque plein, un " +
                        "disque en fin de vie, ou un boîtier externe qui bride celui qu'il contient.",
                });
                return;
            }

            if (latency >= MechanicalMilliseconds)
            {
                verdicts.Add(new BenchmarkVerdict
                {
                    Id = "disque-mecanique",
                    Severity = systemVolume ? Severity.Warning : Severity.Info,
                    Statement = measured,
                    Explanation = systemVolume
                        ? "C'est le temps qu'il faut à une tête de lecture pour se déplacer : ce " +
                          "disque est mécanique. Comme il porte Windows, chaque ouverture de " +
                          "fenêtre attend ces déplacements : c'est la première cause de lenteur " +
                          "d'une machine par ailleurs saine, et un disque à mémoire flash répond " +
                          "cent fois plus vite."
                        : "C'est le temps qu'il faut à une tête de lecture pour se déplacer : ce " +
                          "disque est mécanique. Sur un volume de données, cela se remarque peu ; " +
                          "sur celui qui porte Windows, cela se remarque tout le temps.",
                });
                return;
            }

            if (latency < FlashMilliseconds)
            {
                verdicts.Add(new BenchmarkVerdict
                {
                    Id = "disque-immediat",
                    Severity = Severity.Info,
                    Statement = measured,
                    Explanation =
                        "Aucune mécanique n'est en jeu : le disque répond immédiatement. Si la " +
                        "machine est lente, la cause est ailleurs.",
                });
            }
        }

        private static void Sequential(
            ICollection<BenchmarkVerdict> verdicts, StorageThroughput storage, bool flashExpected)
        {
            if (!storage.ReadBytesPerSecond.HasValue) return;

            var megabytes = storage.ReadBytesPerSecond.Value / Megabyte;

            if (flashExpected && megabytes < PoorFlashSequentialMegabytes)
            {
                verdicts.Add(new BenchmarkVerdict
                {
                    Id = "flash-sous-regime",
                    Severity = Severity.Warning,
                    Statement = "Ce disque à mémoire flash lit à " + Rate(megabytes) + " en continu.",
                    Explanation =
                        "C'est le débit d'un disque mécanique, pas d'une mémoire flash. Le disque " +
                        "est peut-être branché sur un port ancien, ou son mode de fonctionnement " +
                        "est dégradé.",
                });
                return;
            }

            if (megabytes < PoorSequentialMegabytes)
            {
                verdicts.Add(new BenchmarkVerdict
                {
                    Id = "debit-effondre",
                    Severity = Severity.Warning,
                    Statement = "Ce disque lit à " + Rate(megabytes) + " en continu.",
                    Explanation =
                        "En dessous de quarante mégaoctets par seconde, même un disque mécanique " +
                        "en bon état fait mieux. Le disque lui-même, sa nappe ou le port qui le " +
                        "relie sont à examiner.",
                });
            }
        }

        /// <summary>
        /// Gain obtenu en répartissant le même travail sur tous les cœurs.
        /// </summary>
        /// <remarks>
        /// Le seuil est délibérément bas. Un gain inférieur au nombre de cœurs est parfaitement
        /// normal (hyper-threading, mémoire partagée, fréquence qui redescend quand tous les
        /// cœurs travaillent) et prétendre lire une anomalie dans un gain de six sur huit cœurs
        /// serait inventer un diagnostic. Un gain inférieur à un et demi sur une machine qui
        /// annonce quatre cœurs logiques, en revanche, ne s'explique pas autrement que par une
        /// machine qui ne peut pas les utiliser.
        /// </remarks>
        public static BenchmarkVerdict? DescribeParallelGain(double gain, int logicalProcessors)
        {
            if (logicalProcessors < 4 || gain <= 0 || gain >= 1.5) return null;

            return new BenchmarkVerdict
            {
                Id = "coeurs-inexploitables",
                Severity = Severity.Warning,
                Statement =
                    "Le même travail réparti sur " + logicalProcessors + " cœurs logiques n'a été " +
                    "que " + gain.ToString("0.0", CultureInfo.CurrentCulture) + " fois plus rapide.",
                Explanation =
                    "Une machine qui ne tire presque rien de ses cœurs supplémentaires est bridée : " +
                    "par la chaleur, par un mode d'économie d'énergie, ou par un réglage de " +
                    "firmware. La puissance est là, elle n'est pas utilisable en l'état.",
            };
        }

        /// <summary>
        /// Un débit, dans l'unité où il se lit.
        /// </summary>
        /// <remarks>
        /// Le basculement en gigaoctets par seconde n'est pas cosmétique : « 20549 Mo/s » se
        /// compte à voix haute avant de vouloir dire quelque chose, « 20,1 Go/s » se lit.
        /// </remarks>
        public static string Rate(double megabytesPerSecond)
            => megabytesPerSecond >= 1024
                ? (megabytesPerSecond / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " Go/s"
                : megabytesPerSecond.ToString(megabytesPerSecond >= 100 ? "0" : "0.0",
                      CultureInfo.CurrentCulture) + " Mo/s";

        public static string Milliseconds(double value)
            => value.ToString(value >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " ms";

        private static string Lower(string sentence)
            => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);
    }
}
