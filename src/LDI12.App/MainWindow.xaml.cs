using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LDI12.App.Theming;

namespace LDI12.App
{
    /// <summary>
    /// Coquille de l'application. Aucune logique de diagnostic ici : la navigation, l'exécution
    /// et l'état global vivent dans <see cref="ViewModels.ShellViewModel"/>. Ne reste que ce qui
    /// relève de la fenêtre elle-même, et, la fenêtre n'ayant plus de cadre système, il faut en
    /// reprendre deux comportements à la main.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            FitToWorkArea();

            SourceInitialized += OnSourceInitialized;
            ContentRendered += OnContentRendered;
            Closed += OnClosed;
            ThemeManager.Changed += OnThemeChanged;
        }

        // ------------------------------------------------------------------ dimensions

        /// <summary>
        /// Ramène la fenêtre dans l'écran, quitte à descendre sous son propre plancher.
        /// </summary>
        /// <remarks>
        /// <b>La fenêtre s'ouvre à sa plus petite taille</b> (celle qui a été éprouvée) plutôt
        /// qu'à une taille confortable choisie ici : une mise en page qui déborde se voit tout de
        /// suite, une mise en page trop à l'aise cache ce qui casserait ailleurs. Le technicien
        /// agrandit s'il le souhaite, et son geste est plus fiable que notre supposition.
        /// <para>
        /// Le plancher lui-même cède devant l'écran. Un 1366 × 768 affiché à 125 % ne laisse que
        /// 1093 points de large : imposer 1220 y produirait une fenêtre plus large que le bureau,
        /// dont le bord droit (et donc la croix de fermeture) sortirait de l'écran. Une mise en
        /// page à l'étroit se lit ; une fenêtre qu'on ne peut pas fermer, non.
        /// </para>
        /// </remarks>
        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            if (area.Width <= 0 || area.Height <= 0) return;

            if (MinWidth > area.Width) MinWidth = area.Width;
            if (MinHeight > area.Height) MinHeight = area.Height;

            Width = MinWidth;
            Height = MinHeight;
        }

        // ------------------------------------------------------------------ boutons de fenêtre

        private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------------ apparitions

        private void OnContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnContentRendered;
            Motion.FadeIn(Root, 0, Motion.Normal);
        }

        /// <summary>
        /// La bascule de thème remplace un dictionnaire entier : toutes les surfaces changent dans
        /// la même image. Un fondu court rattache le nouvel état à l'ancien, au lieu du claquement
        /// qu'on obtient sinon.
        /// </summary>
        private void OnThemeChanged(object? sender, EventArgs e) => Motion.FadeIn(Root, 0.35, Motion.Normal);

        private void OnClosed(object? sender, EventArgs e) => ThemeManager.Changed -= OnThemeChanged;

        // ------------------------------------------------------------------ zone non cliente

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
            ApplyRoundedCorners(handle);
        }

        /// <summary>
        /// Windows 11 arrondit les angles des fenêtres à cadre standard. Une fenêtre sans cadre
        /// n'y a pas droit d'office : on le demande explicitement. L'attribut n'existe pas avant
        /// la version 22000 : l'appel y échoue proprement, sans conséquence, mais autant ne pas
        /// le faire.
        /// </summary>
        private static void ApplyRoundedCorners(IntPtr handle)
        {
            const int DwmWindowCornerPreference = 33;
            const int CornerPreferenceRound = 2;

            if (handle == IntPtr.Zero) return;
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 22000) return;

            try
            {
                var preference = CornerPreferenceRound;
                DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
            }
            catch (DllNotFoundException)
            {
                // Composition de bureau absente : la fenêtre reste à angles droits, sans plus.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private const int WmGetMinMaxInfo = 0x0024;
        private const int MonitorDefaultToNearest = 0x00000002;

        /// <summary>
        /// Bornes d'une fenêtre sans cadre : ce qu'elle peut atteindre, et ce en deçà de quoi
        /// elle ne descend pas.
        /// </summary>
        /// <remarks>
        /// <b>Le maximum.</b> Livrée à elle-même, une fenêtre <c>WindowStyle="None"</c> s'agrandit
        /// à la taille de l'écran entier : elle recouvre la barre des tâches et déborde de
        /// quelques pixels de chaque côté, ceux qu'occupent normalement les poignées de
        /// redimensionnement invisibles. On impose donc la zone de travail de l'écran <i>le plus
        /// proche</i>, et non de l'écran principal : un portable branché sur un écran externe plus
        /// grand est un cas ordinaire, et WPF calcule sa borne supérieure sur le seul écran
        /// principal.
        ///
        /// <b>Le minimum, et c'est cette méthode qui le faisait disparaître.</b> Répondre à
        /// WM_GETMINMAXINFO en se déclarant traité empêche WPF de répondre à son tour, donc de
        /// reporter <c>MinWidth</c> et <c>MinHeight</c> dans <c>ptMinTrackSize</c>. Mesuré : la
        /// fenêtre annonçait un minimum de zéro par zéro et se laissait réduire à n'importe quelle
        /// taille, quoi qu'en dise le XAML. Les deux bornes sont donc posées ici, ensemble.
        ///
        /// Les valeurs de MINMAXINFO sont en <b>pixels physiques</b>, comme celles de MONITORINFO :
        /// les tailles maximales n'ont aucune conversion à subir, mais les minimales, elles,
        /// viennent de WPF et sont en unités indépendantes de la résolution. Sans la conversion,
        /// un écran à 150 % laisserait descendre la fenêtre à deux tiers du plancher voulu.
        /// </remarks>
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

            var work = monitorInfo.rcWork;
            var screen = monitorInfo.rcMonitor;
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            info.ptMaxPosition.X = work.Left - screen.Left;
            info.ptMaxPosition.Y = work.Top - screen.Top;
            info.ptMaxSize.X = work.Right - work.Left;
            info.ptMaxSize.Y = work.Bottom - work.Top;

            // WPF a déjà renseigné ptMaxTrackSize d'après l'écran principal. On le relève si
            // l'écran courant est plus grand, sans jamais le réduire.
            if (info.ptMaxTrackSize.X < info.ptMaxSize.X) info.ptMaxTrackSize.X = info.ptMaxSize.X;
            if (info.ptMaxTrackSize.Y < info.ptMaxSize.Y) info.ptMaxTrackSize.Y = info.ptMaxSize.Y;

            ApplyMinimum(hwnd, ref info);

            Marshal.StructureToPtr(info, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Reporte <c>MinWidth</c> et <c>MinHeight</c> dans la borne que Windows fait respecter au
        /// glissement de la souris.
        /// </summary>
        /// <remarks>
        /// L'échelle est relue à chaque message plutôt que retenue une fois : déplacer la fenêtre
        /// d'un écran à 100 % vers un écran à 150 % change le facteur sans passer par le
        /// redémarrage de l'application.
        /// </remarks>
        private void ApplyMinimum(IntPtr hwnd, ref MINMAXINFO info)
        {
            var scaleX = 1d;
            var scaleY = 1d;

            var transform = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice;
            if (transform.HasValue)
            {
                if (transform.Value.M11 > 0) scaleX = transform.Value.M11;
                if (transform.Value.M22 > 0) scaleY = transform.Value.M22;
            }

            if (!double.IsNaN(MinWidth) && MinWidth > 0)
                info.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scaleX);

            if (!double.IsNaN(MinHeight) && MinHeight > 0)
                info.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scaleY);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
    }
}
