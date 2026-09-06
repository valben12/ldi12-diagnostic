using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Fréquence courante du processeur, par <c>CallNtPowerInformation</c>.
    /// </summary>
    /// <remarks>
    /// Cet appel est le seul qui donne la fréquence sans compteur de performance ni WMI : il
    /// coûte quelques microsecondes et répond en session utilisateur depuis Windows 7, ce qui en
    /// fait la seule source utilisable pour un relevé par seconde.
    /// <para>
    /// <b>Sa valeur n'est pas fiable partout, et c'est assumé.</b> Beaucoup de machines y
    /// renvoient la fréquence nominale, figée, quoi que fasse le processeur. Rien ne permet de
    /// le savoir d'avance, ni la version de Windows, ni le constructeur. La surveillance le
    /// constate donc à l'usage : une valeur qui n'a pas bougé d'un mégahertz sur des centaines
    /// de relevés décrit une source qui ne mesure rien, et c'est dit comme tel plutôt que
    /// transformé en « aucun ralentissement détecté ».
    /// </para>
    /// </remarks>
    internal static class PowerNative
    {
        private const int ProcessorInformation = 11;

        /// <summary>Taille de <c>PROCESSOR_POWER_INFORMATION</c> : six entiers de 32 bits.</summary>
        private const int EntrySize = 24;

        private const int MaxMhzOffset = 4;
        private const int CurrentMhzOffset = 8;

        [DllImport("powrprof.dll", ExactSpelling = true)]
        private static extern int CallNtPowerInformation(
            int informationLevel, IntPtr inputBuffer, uint inputBufferLength,
            IntPtr outputBuffer, uint outputBufferLength);

        /// <summary>
        /// Fréquence courante et fréquence maximale, moyennées sur les cœurs logiques.
        /// </summary>
        /// <remarks>
        /// Moyenne et non maximum : sur un processeur moderne, un seul cœur monté en turbo
        /// pendant qu'un autre dort décrirait une machine rapide alors qu'elle ne l'est pas.
        /// C'est la fréquence que l'ensemble tient qui explique une lenteur.
        /// </remarks>
        internal static bool TryReadFrequency(out int currentMegahertz, out int maxMegahertz)
        {
            currentMegahertz = 0;
            maxMegahertz = 0;

            var count = Environment.ProcessorCount;
            if (count <= 0) return false;

            var bytes = EntrySize * count;
            var buffer = Marshal.AllocHGlobal(bytes);

            try
            {
                // Un statut non nul couvre aussi bien un refus qu'une plateforme qui n'expose pas
                // l'information : dans les deux cas, il n'y a rien à lire et rien à supposer.
                if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)bytes) != 0)
                    return false;

                long currentSum = 0;
                long maxSum = 0;

                for (var index = 0; index < count; index++)
                {
                    var entry = IntPtr.Add(buffer, index * EntrySize);
                    maxSum += (uint)Marshal.ReadInt32(entry, MaxMhzOffset);
                    currentSum += (uint)Marshal.ReadInt32(entry, CurrentMhzOffset);
                }

                currentMegahertz = (int)(currentSum / count);
                maxMegahertz = (int)(maxSum / count);

                return currentMegahertz > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
