using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace LDI12.App
{
    /// <summary>
    /// L'avancement d'une copie sur l'icône de la barre des tâches, et un signal à la fin.
    /// </summary>
    /// <remarks>
    /// Une sauvegarde dure une heure, et le technicien ne la regarde pas : il démonte un autre
    /// PC, il répond au téléphone. L'icône se remplit en vert comme celle de l'Explorateur
    /// pendant une copie, et clignote à la fin si la fenêtre n'est pas au premier plan : il sait
    /// où en est la copie sans ouvrir la fenêtre, et qu'elle est finie sans la surveiller.
    /// <para>
    /// Sans fenêtre principale (tests, banc de captures), rien ne se passe.
    /// </para>
    /// </remarks>
    internal static class Taskbar
    {
        /// <summary>Avancement de 0 à 1.</summary>
        public static void Show(double fraction)
        {
            var info = Info();
            if (info == null) return;

            info.ProgressState = TaskbarItemProgressState.Normal;
            info.ProgressValue = Math.Max(0, Math.Min(1, fraction));
        }

        /// <summary>
        /// Fin du transfert : l'icône redevient normale, et appelle l'attention si la fenêtre est
        /// derrière une autre.
        /// </summary>
        /// <param name="failed">Une fin en échec garde l'icône rouge jusqu'au retour du technicien.</param>
        public static void Finish(bool failed)
        {
            var info = Info();
            if (info == null) return;

            var window = Application.Current?.MainWindow;
            if (failed && window != null && !window.IsActive)
            {
                info.ProgressState = TaskbarItemProgressState.Error;
                info.ProgressValue = 1;
                window.Activated += Clear;
            }
            else
            {
                info.ProgressState = TaskbarItemProgressState.None;
            }

            if (window != null && !window.IsActive) Flash(window);
        }

        private static void Clear(object? sender, EventArgs e)
        {
            if (sender is Window window) window.Activated -= Clear;
            var info = Info();
            if (info != null) info.ProgressState = TaskbarItemProgressState.None;
        }

        private static TaskbarItemInfo? Info()
        {
            var window = Application.Current?.MainWindow;
            if (window == null || !window.IsLoaded) return null;
            return window.TaskbarItemInfo ??= new TaskbarItemInfo();
        }

        /// <summary>Fait clignoter le bouton de la fenêtre jusqu'à ce qu'elle revienne au premier plan.</summary>
        private static void Flash(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                    Window = handle,
                    Flags = FlashTray | FlashUntilForeground,
                    Count = uint.MaxValue,
                    Timeout = 0,
                };
                FlashWindowEx(ref info);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
            }
        }

        private const uint FlashTray = 0x2;
        private const uint FlashUntilForeground = 0xC;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
