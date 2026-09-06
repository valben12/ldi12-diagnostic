using System;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Décodage de la réponse ATA « IDENTIFY DEVICE ».
    /// </summary>
    /// <remarks>
    /// Deux cent cinquante-six mots de seize bits, dont trois nous intéressent. Le reste décrit
    /// des capacités que Windows expose déjà autrement, et les relire ici ne ferait qu'ajouter
    /// une seconde source susceptible de contredire la première.
    /// <para>
    /// La norme ATA réserve <c>0x0000</c> et <c>0xFFFF</c> pour « non renseigné » sur presque
    /// tous ses champs. Un disque qui ne remplit pas un mot le laisse à l'une de ces deux
    /// valeurs, et les prendre pour des mesures produirait des disques à zéro tour par minute ou
    /// à soixante-cinq mille.
    /// </para>
    /// </remarks>
    internal static class AtaIdentityParser
    {
        /// <summary>Mot 76 : capacités SATA du disque. Absent des disques non SATA.</summary>
        private const int WordSataCapabilities = 76;

        /// <summary>Mot 77 : état de la liaison SATA, dont la génération négociée.</summary>
        private const int WordSataStatus = 77;

        /// <summary>Mot 217 : vitesse de rotation nominale du support.</summary>
        private const int WordRotationRate = 217;

        /// <summary>Valeur du mot 217 pour un support qui ne tourne pas.</summary>
        private const int NonRotating = 0x0001;

        /// <summary>En deçà, la valeur du mot 217 est réservée par la norme, pas une vitesse.</summary>
        private const int LowestValidRpm = 0x0401;

        public static AtaIdentity Parse(byte[] identify)
        {
            if (identify == null || identify.Length < 512)
            {
                var reason = "La réponse d'identification du disque est incomplète.";
                return new AtaIdentity
                {
                    RotationRpm = Measured.Missing<int>(reason, DataSource.NativeApi),
                    LinkGigabitsPerSecond = Measured.Missing<double>(reason, DataSource.NativeApi),
                    MaximumGigabitsPerSecond = Measured.Missing<double>(reason, DataSource.NativeApi),
                };
            }

            var capabilities = Word(identify, WordSataCapabilities);
            var status = Word(identify, WordSataStatus);

            return new AtaIdentity
            {
                RotationRpm = Rotation(Word(identify, WordRotationRate)),
                LinkGigabitsPerSecond = Negotiated(capabilities, status),
                MaximumGigabitsPerSecond = Supported(capabilities),
            };
        }

        /// <summary>
        /// Vitesse de rotation, ou la raison pour laquelle il n'y en a pas.
        /// </summary>
        /// <remarks>
        /// Un disque à mémoire flash déclare explicitement « rien ne tourne » : c'est une
        /// confirmation, pas une lacune, et elle est formulée comme telle.
        /// </remarks>
        internal static Measured<int> Rotation(int word)
        {
            if (word == NonRotating)
                return Measured.Missing<int>(
                    "Ce disque déclare n'avoir aucun plateau : c'est une mémoire flash.",
                    DataSource.NativeApi);

            if (word < LowestValidRpm || word == 0xFFFF)
                return Measured.Missing<int>(
                    "Ce disque ne déclare pas sa vitesse de rotation.", DataSource.NativeApi);

            return Measured.Ok(word, DataSource.NativeApi);
        }

        /// <summary>
        /// Génération de liaison réellement négociée entre le disque et son port.
        /// </summary>
        /// <remarks>
        /// Les bits 1 à 3 du mot 77 portent la génération courante : un, deux ou trois, soit
        /// 1,5, 3 ou 6 gigabits par seconde. Le bit 0 est réservé : le décaler serait diviser
        /// chaque débit par deux sans que rien ne le signale.
        /// </remarks>
        internal static Measured<double> Negotiated(int capabilities, int status)
        {
            if (!IsSata(capabilities))
                return Measured.Missing<double>(
                    "Ce disque n'est pas relié en SATA : la notion de génération de liaison ne " +
                    "s'y applique pas.", DataSource.NativeApi);

            var generation = (status >> 1) & 0x07;
            var speed = Speed(generation);

            return speed > 0
                ? Measured.Ok(speed, DataSource.NativeApi)
                : Measured.Missing<double>(
                    "Ce disque ne déclare pas la génération de liaison négociée.", DataSource.NativeApi);
        }

        /// <summary>
        /// Génération la plus élevée que le disque sait tenir.
        /// </summary>
        /// <remarks>
        /// Les bits 1 à 3 du mot 76 sont cumulatifs : un disque de troisième génération déclare
        /// aussi savoir parler aux deux précédentes. C'est le bit le plus haut qui compte.
        /// </remarks>
        internal static Measured<double> Supported(int capabilities)
        {
            if (!IsSata(capabilities))
                return Measured.Missing<double>(
                    "Ce disque n'est pas relié en SATA : la notion de génération de liaison ne " +
                    "s'y applique pas.", DataSource.NativeApi);

            for (var generation = 3; generation >= 1; generation--)
                if ((capabilities & (1 << generation)) != 0)
                    return Measured.Ok(Speed(generation), DataSource.NativeApi);

            return Measured.Missing<double>(
                "Ce disque ne déclare aucune génération de liaison.", DataSource.NativeApi);
        }

        /// <summary>
        /// Le mot des capacités SATA existe-t-il seulement ?
        /// </summary>
        /// <remarks>
        /// La norme le laisse à zéro sur un disque qui n'est pas SATA, et un contrôleur qui ne
        /// remplit rien le laisse à <c>0xFFFF</c>. Les deux signifient « la question ne se pose
        /// pas ici », et non « liaison de génération inconnue ».
        /// </remarks>
        private static bool IsSata(int capabilities)
            => capabilities != 0x0000 && capabilities != 0xFFFF;

        private static double Speed(int generation) => generation switch
        {
            1 => 1.5,
            2 => 3.0,
            3 => 6.0,
            _ => 0d,
        };

        private static int Word(byte[] identify, int index)
            => identify[index * 2] | (identify[(index * 2) + 1] << 8);
    }
}
