using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Empêche la mise en veille tant qu'un transfert dure.
    /// </summary>
    /// <remarks>
    /// <b>La première cause de sauvegarde ratée en intervention.</b> Un portable sur batterie se
    /// met en veille au bout d'une demi-heure sans clavier ni souris, et coupe au passage
    /// l'alimentation du disque USB : la copie de trois heures s'arrête à la quarantième minute.
    /// Windows ne tient pas compte d'un programme qui travaille, seulement de celui qui le dit.
    /// <para>
    /// L'écran, lui, peut s'éteindre : c'est la machine qui doit rester éveillée, pas l'affichage.
    /// La consigne tient au fil qui l'a donnée ; elle est levée à la fin, et d'elle-même si le
    /// logiciel se ferme.
    /// </para>
    /// </remarks>
    public sealed class KeepAwake : IDisposable
    {
        private const uint Continuous = 0x80000000;
        private const uint SystemRequired = 0x00000001;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        private bool _active;

        private KeepAwake(bool active) => _active = active;

        /// <summary>Tient la machine éveillée jusqu'à <see cref="Dispose"/>. Sans effet hors de Windows.</summary>
        public static KeepAwake Start()
        {
            try
            {
                return new KeepAwake(SetThreadExecutionState(Continuous | SystemRequired) != 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return new KeepAwake(false);
            }
        }

        public void Dispose()
        {
            if (!_active) return;
            _active = false;

            try { SetThreadExecutionState(Continuous); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }
    }
}
