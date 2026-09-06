using System;

namespace LDI12.Core.Model
{
    /// <summary>
    /// Conversions du domaine sans fil : fréquence, canal, bande, débit de référence.
    /// </summary>
    /// <remarks>
    /// Ces calculs vivent dans le cœur plutôt que dans la passerelle Windows parce qu'ils ne
    /// dépendent d'aucune API : ce sont les tables du 802.11. La passerelle rapporte ce que la
    /// carte dit (fréquence en kHz, numéro de canal, débit négocié) et l'interprétation se
    /// fait ici, donc elle se teste sans matériel sans fil.
    /// </remarks>
    public static class WifiChannels
    {
        /// <summary>
        /// Bande déduite de la fréquence centrale, en kHz. C'est la voie sûre : la fréquence ne
        /// souffre d'aucune ambiguïté.
        /// </summary>
        public static WifiBand BandFromFrequency(int kilohertz)
        {
            if (kilohertz >= 2_401_000 && kilohertz <= 2_495_000) return WifiBand.Band24;
            if (kilohertz >= 5_150_000 && kilohertz <= 5_895_000) return WifiBand.Band5;
            if (kilohertz >= 5_925_000 && kilohertz <= 7_125_000) return WifiBand.Band6;
            return WifiBand.Unknown;
        }

        /// <summary>
        /// Bande déduite du seul numéro de canal.
        /// </summary>
        /// <remarks>
        /// Déduction incomplète et c'est important : la bande 6 GHz reprend la numérotation à 1.
        /// Un canal 6 peut donc désigner 2,437 GHz ou 5,975 GHz. La fonction rend
        /// <see cref="WifiBand.Unknown"/> pour les numéros ambigus quand la norme annoncée permet
        /// le 6 GHz, au lieu d'affirmer une bande qu'elle n'a pas mesurée.
        /// </remarks>
        public static WifiBand BandFromChannel(int channel, bool sixGigahertzCapable)
        {
            if (channel <= 0) return WifiBand.Unknown;
            if (channel >= 32) return WifiBand.Band5;
            if (channel <= 14) return sixGigahertzCapable ? WifiBand.Unknown : WifiBand.Band24;
            return WifiBand.Unknown;
        }

        /// <summary>Numéro de canal correspondant à une fréquence centrale exprimée en kHz.</summary>
        public static int ChannelFromFrequency(int kilohertz)
        {
            var megahertz = kilohertz / 1000;
            switch (BandFromFrequency(kilohertz))
            {
                case WifiBand.Band24:
                    if (megahertz == 2484) return 14;
                    if (megahertz < 2412) return 0;
                    return (megahertz - 2412) / 5 + 1;
                case WifiBand.Band5:
                    return (megahertz - 5000) / 5;
                case WifiBand.Band6:
                    return megahertz == 5935 ? 2 : (megahertz - 5950) / 5;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Deux canaux se gênent-ils mutuellement ?
        /// </summary>
        /// <remarks>
        /// En 2,4 GHz les canaux sont espacés de 5 MHz pour une largeur de 20 : quatre canaux
        /// d'écart ne suffisent pas à les séparer, d'où les trois seuls canaux réellement
        /// indépendants (1, 6, 11). En 5 et 6 GHz ils ne se recouvrent pas, seul le canal
        /// identique compte.
        /// </remarks>
        public static bool Overlaps(WifiBand band, int first, int second)
        {
            if (first <= 0 || second <= 0) return false;
            if (band != WifiBand.Band24) return first == second;
            return Math.Abs(first - second) < 5;
        }

        public static string Describe(WifiBand band) => band switch
        {
            WifiBand.Band24 => "2,4 GHz",
            WifiBand.Band5 => "5 GHz",
            WifiBand.Band6 => "6 GHz",
            _ => "bande indéterminée",
        };

        /// <summary>
        /// Débit qu'une carte à une seule antenne atteint sur cette norme, en Mbit/s.
        /// </summary>
        /// <remarks>
        /// Volontairement la borne <b>basse</b> du matériel courant, et non le maximum théorique
        /// de la norme. Une carte 802.11n à une antenne plafonne à 72 Mbit/s : la comparer aux
        /// 300 Mbit/s d'une carte à deux antennes ferait signaler comme défaillante la moitié des
        /// portables d'entrée de gamme, qui fonctionnent pourtant à leur plein régime.
        /// </remarks>
        public static int SingleStreamRateMbps(string? radioType) => Letters(radioType) switch
        {
            "be" => 1000,
            "ax" => 600,
            "ac" => 433,
            "n" => 72,
            "g" => 54,
            "a" => 54,
            "b" => 11,
            _ => 0,
        };

        /// <summary>Norme d'avant 2009, dont le débit plafonne à 54 Mbit/s partagés.</summary>
        public static bool IsLegacyRadio(string? radioType)
        {
            var letters = Letters(radioType);
            return letters == "a" || letters == "b" || letters == "g";
        }

        /// <summary>
        /// Lettres de la norme : « ac » pour « 802.11ac », « a » pour « 802.11a (OFDM) ».
        /// </summary>
        /// <remarks>
        /// Une comparaison par sous-chaîne se tromperait : « 802.11a » est contenu dans
        /// « 802.11ac » comme dans « 802.11ax », et une carte Wi-Fi 6 serait alors annoncée comme
        /// matériel d'avant 2009.
        /// </remarks>
        private static string Letters(string? radioType)
        {
            if (string.IsNullOrEmpty(radioType)) return string.Empty;

            var start = radioType!.IndexOf("802.11", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;

            var index = start + "802.11".Length;
            var end = index;
            while (end < radioType.Length && char.IsLetter(radioType[end])) end++;

            return radioType.Substring(index, end - index).ToLowerInvariant();
        }
    }
}
