using System;
using System.Globalization;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// La durée d'une sauvegarde, annoncée avant de la lancer.
    /// </summary>
    /// <remarks>
    /// <b>Ce que ça sert à décider.</b> Quatre heures sur une clé USB 2, quarante minutes sur un
    /// SSD externe branché sur un port USB 3 : c'est avant de lancer qu'il faut le savoir, pour
    /// changer de support ou de port, pas au bout de la première heure.
    /// <para>
    /// Le modèle est celui du temps restant de la copie : chaque octet est écrit puis relu sur le
    /// support, et chaque fichier paie un prix fixe. Les trois prix viennent d'une mesure du support
    /// lui-même. La lecture du disque du client n'y figure pas : elle se fait pendant l'écriture,
    /// et ne pèse que si ce disque est plus lent que le support. D'où la fourchette, et la phrase
    /// qui l'accompagne.
    /// </para>
    /// </remarks>
    public static class CopyEstimate
    {
        /// <summary>
        /// Écart annoncé autour de l'estimation.
        /// </summary>
        /// <remarks>
        /// L'erreur médiane du même modèle, relevée sur l'essai réel d'une copie (voir
        /// <c>CopyProgress</c>), était de 37 % en cours de route, avec une mesure bien plus riche
        /// que quinze secondes d'essai : annoncer moins serait promettre plus qu'on ne sait.
        /// </remarks>
        private const double Spread = 0.35;

        /// <summary>
        /// En dessous, le support est presque certainement une clé USB 2, ou branché sur un port USB 2.
        /// </summary>
        private const double SlowWriteBytesPerSecond = 40e6;

        public static TimeSpan Duration(long bytes, int files, CopySpeed speed)
        {
            if (speed == null) throw new ArgumentNullException(nameof(speed));

            var seconds = bytes / speed.WriteBytesPerSecond + bytes / speed.ReadBytesPerSecond +
                          files * speed.PerFile.TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        /// <summary>
        /// La durée d'une restauration.
        /// </summary>
        /// <remarks>
        /// Les rôles s'inversent : la sauvegarde est lue pendant que la machine écrit, et c'est le
        /// plus lent des deux qui fixe le rythme ; puis la machine relit ce qu'elle a écrit. Chaque
        /// fichier paie son prix des deux côtés : ouvert sur la sauvegarde, créé, renommé et relu
        /// sur la machine.
        /// </remarks>
        public static TimeSpan RestoreDuration(long bytes, int files, ReadSpeed source, CopySpeed target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var seconds = bytes / Math.Min(source.ReadBytesPerSecond, target.WriteBytesPerSecond) +
                          bytes / target.ReadBytesPerSecond +
                          files * (source.PerFile.TotalSeconds + target.PerFile.TotalSeconds);
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        /// <summary>La lecture de la sauvegarde, comme on la lit sur une tuile.</summary>
        public static string Speeds(ReadSpeed speed)
            => "lecture " + Rate(speed.ReadBytesPerSecond) + ", " +
               speed.PerFile.TotalMilliseconds.ToString("0", CultureInfo.GetCultureInfo("fr-FR")) + " ms par fichier";

        /// <summary>Ce qu'il faut dire d'un support de sauvegarde lent à lire, ou nul.</summary>
        public static string? Advice(ReadSpeed speed)
            => speed.ReadBytesPerSecond < SlowWriteBytesPerSecond
                ? "Support de sauvegarde lent à lire : clé USB 2, ou branché sur un port USB 2. Le brancher sur un " +
                  "port USB 3 (souvent bleu) peut beaucoup raccourcir la restauration."
                : null;

        /// <summary>« environ 3 h 40 (entre 2 h 20 et 5 h) ».</summary>
        public static string Describe(TimeSpan duration)
        {
            if (duration < TimeSpan.FromMinutes(1)) return "moins d'une minute";

            var low = TimeSpan.FromTicks((long)(duration.Ticks * (1 - Spread)));
            var high = TimeSpan.FromTicks((long)(duration.Ticks * (1 + Spread)));
            return "environ " + Round(duration) + " (entre " + Round(low) + " et " + Round(high) + ")";
        }

        /// <summary>Les débits et le prix par fichier, comme on les lit sur une tuile.</summary>
        public static string Speeds(CopySpeed speed)
            => "écriture " + Rate(speed.WriteBytesPerSecond) + ", relecture " + Rate(speed.ReadBytesPerSecond) + ", " +
               speed.PerFile.TotalMilliseconds.ToString("0", CultureInfo.GetCultureInfo("fr-FR")) + " ms par fichier";

        /// <summary>Ce qu'il faut dire d'un support lent, ou nul s'il ne l'est pas.</summary>
        public static string? Advice(CopySpeed speed)
            => speed.WriteBytesPerSecond < SlowWriteBytesPerSecond
                ? "Support lent : clé USB 2, ou branché sur un port USB 2. Un port USB 3 (souvent bleu) ou un SSD " +
                  "externe peut diviser la durée par dix."
                : null;

        private static string Rate(double bytesPerSecond) => ValueFormat.Bytes((long)bytesPerSecond) + "/s";

        /// <summary>Arrondi comme on le dirait : à la minute sous dix minutes, à cinq sous l'heure, à dix au-delà.</summary>
        internal static string Round(TimeSpan value)
        {
            var minutes = value.TotalMinutes;
            if (minutes < 1) return "moins d'une minute";
            if (minutes < 10) return (int)Math.Round(minutes, MidpointRounding.AwayFromZero) + " min";
            if (minutes < 60)
            {
                var rounded = (int)(Math.Round(minutes / 5, MidpointRounding.AwayFromZero) * 5);
                return rounded >= 60 ? "1 h" : rounded + " min";
            }

            var total = (int)(Math.Round(minutes / 10, MidpointRounding.AwayFromZero) * 10);
            var hours = total / 60;
            var rest = total % 60;
            return rest == 0 ? hours + " h" : hours + " h " + rest.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
