using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LDI12.App.Services;
using LDI12.App.Theming;
using LDI12.Core.Runtime;
using LDI12.Hosting;
using LDI12.App.ViewModels;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Platform.Logging;

namespace LDI12.App
{
    /// <summary>
    /// Racine de composition de l'interface.
    /// </summary>
    public partial class App : Application
    {
        private RollingFileLogger? _logger;
        private ShellViewModel? _shell;
        private string? _screenshotPath;
        private double _scrollOffset;
        private int _captureDelayMs;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Une erreur confinée à une vue ne doit pas fermer l'application : le technicien est
            // chez un client, il doit pouvoir poursuivre son diagnostic et exporter son rapport.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // Avant tout le reste : ce processus est peut-être la nouvelle version, venue
            // prendre la place de l'ancienne. Ni fenêtre, ni diagnostic, ni journal d'analyse.
            var apply = LDI12.Updates.UpdateApplier.Parse(e.Args);
            if (apply != null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ApplyUpdate(apply.Value.Target, apply.Value.ProcessId);
                return;
            }

            _screenshotPath = ReadScreenshotPath(e.Args);
            _scrollOffset = ReadScrollOffset(e.Args);

            // Le logo, tout de suite, pendant que le reste s'installe.
            var splash = ShowSplash();

            // Avant toute chose : le logiciel écrit en français, ses chiffres et ses dates aussi.
            // Sur une machine réglée en anglais, sans cet appel, « 1.5 GB » s'afficherait sous
            // un titre « Espace libre ».
            Formats.Apply();
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Formats.French.IetfLanguageTag)));

            _logger = RollingFileLogger.CreateDefault();
            _logger.Info("App", "Démarrage de l'interface LDI12 Diagnostic. " + AssemblyBundle.Describe());

            // Le canal d'élévation est dans une couche basse : elle ne peut pas lire les
            // ressources de l'exécutable, mais elle a besoin d'y retrouver l'hôte de sondes.
            BundledFiles.Extractor = AssemblyBundle.Extract;
            BundledFiles.TemporaryExtractor = AssemblyBundle.ExtractTemporary;

            ThemeManager.Apply(ReadTheme(e.Args) ?? ThemeManager.DetectSystemPreference());
            ApplyRenderingProfile();

            // Une capture prise au hasard d'un fondu ne prouve rien : en mode capture, l'interface
            // est figée dans son état d'arrivée. Sauf si l'on demande explicitement une capture
            // différée, auquel cas c'est précisément un état transitoire que l'on veut voir,
            // l'anneau d'analyse en cours de remplissage, par exemple.
            _captureDelayMs = ReadCaptureDelay(e.Args);
            if (_screenshotPath != null && _captureDelayMs <= 0) Motion.Enabled = false;

            _shell = new ShellViewModel(new DiagnosticService(_logger), _logger);

            var window = new MainWindow { DataContext = _shell };
            ApplyForcedSize(window, e.Args);
            MainWindow = window;
            window.Show();

            // Fermé une fois la fenêtre réellement à l'écran, et non au premier signe de vie du
            // processus : c'est l'attente que l'écran d'attente doit couvrir.
            splash?.Close(TimeSpan.FromMilliseconds(300));

            // Critère de sortie de la phase : la fenêtre doit apparaître en moins de 800 ms,
            // y compris sur la machine la plus lente du parc. La mesure part du démarrage réel
            // du processus, pas de l'entrée dans OnStartup.
            var startupMs = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            _logger.Info("App", "Fenêtre affichée " + (int)startupMs + " ms après le démarrage du processus.");

            // Rien de bloquant avant le premier rendu : l'identification de la plateforme et
            // l'analyse rapide partent une fois la fenêtre à l'écran.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(async () => await _shell.InitializeAsync()));

            // La mise à jour part une fois la fenêtre à l'écran, jamais avant : la promesse
            // d'un affichage en moins de 800 ms ne se négocie pas contre une requête réseau.
            // En mode capture, rien du tout : un bandeau ne doit pas se glisser dans une image.
            if (_screenshotPath == null)
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                {
                    try
                    {
                        LDI12.Updates.UpdateApplier.CleanStaging(_logger);
                        await _shell.Updates.StartAsync(System.Threading.CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // Une mise à jour qui échoue ne doit jamais empêcher de travailler :
                        // l'incident est noté, et le diagnostic continue comme si de rien n'était.
                        _logger.Error("App", "Vérification de mise à jour interrompue.", ex);
                    }
                }));
            }

            var navIndex = ReadNavigationIndex(e.Args);
            if (navIndex > 0 && navIndex < _shell.Navigation.Count)
                _shell.SelectedNavigation = _shell.Navigation[navIndex];

            var dataMode = ReadDataMode(e.Args);
            if (dataMode != null) _shell.UserData?.ShowMode(dataMode);

            if (_screenshotPath != null)
            {
                if (_captureDelayMs > 0)
                {
                    // Capture à un instant choisi, sans attendre la fin de l'analyse : c'est le
                    // seul moyen d'observer une animation, qui par définition n'existe pas dans
                    // l'état final.
                    var shot = new DispatcherTimer(TimeSpan.FromMilliseconds(_captureDelayMs),
                        DispatcherPriority.Background,
                        (_, __) => OnScanCompletedForScreenshot(this, EventArgs.Empty), Dispatcher);
                    shot.Start();
                }
                else
                {
                    _shell.ScanCompleted += OnScanCompletedForScreenshot;

                    // Filet de sécurité : si l'analyse ne se termine jamais, on capture quand même
                    // et on sort, plutôt que de laisser un processus sans fenêtre visible.
                    var safety = new DispatcherTimer(TimeSpan.FromSeconds(90), DispatcherPriority.Background,
                        (_, __) => OnScanCompletedForScreenshot(this, EventArgs.Empty), Dispatcher);
                    safety.Start();
                }
            }
        }

        /// <summary>
        /// Sur une machine en rendu logiciel (bureau à distance, GPU sans pilote, machine
        /// virtuelle), les animations coûtent des images par seconde sans rien apporter.
        /// C'est exactement le parc que ce logiciel doit servir.
        /// </summary>
        /// <remarks>
        /// La coupure est graduée, pas totale. <see cref="Motion.Enabled"/> supprime les
        /// animations de grande surface (ouverture de la fenêtre, changement d'écran, balayage
        /// de l'arc, entrée en cascade des cartes) qui repeignent des milliers de pixels par
        /// image. Les micro-interactions décrites en XAML survivent : un voile de survol ou une
        /// pastille de 3 × 18 points ne coûte rien, et sans elles l'interface paraîtrait cassée
        /// plutôt qu'économe. La fréquence d'images plafonnée à 10 finit le travail.
        /// </remarks>
        /// <summary>
        /// Écran d'attente : le logo seul, le temps que la fenêtre se construise.
        /// </summary>
        /// <remarks>
        /// Écrit à la main plutôt que confié à l'action de génération « SplashScreen » du SDK,
        /// qui reste sans effet sur ce projet : vérifié en inspectant les ressources de
        /// l'assemblage produit, où rien de tel n'apparaît. Le faire soi-même a d'ailleurs un
        /// avantage : l'image se retire quand la fenêtre est prête, et non à un instant décidé
        /// par le chargeur.
        /// <para>
        /// Une image absente ne doit jamais empêcher le logiciel de démarrer : l'échec est
        /// silencieux, et l'application s'ouvre sans écran d'attente.
        /// </para>
        /// </remarks>
        private static SplashScreen? ShowSplash()
        {
            try
            {
                var splash = new SplashScreen("Assets/splash.png");
                splash.Show(autoClose: false, topMost: true);
                return splash;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ApplyRenderingProfile()
        {
            var tier = RenderCapability.Tier >> 16;
            if (tier != 0) return;

            _logger?.Info("App", "Rendu logiciel détecté (tier 0) : animations de grande surface désactivées.");
            Motion.Enabled = false;
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline), new FrameworkPropertyMetadata(10));
        }

        /// <summary>
        /// Forçage des dimensions de la fenêtre (<c>--size 1600x900</c>).
        /// </summary>
        /// <remarks>
        /// Une mise en page qui ne tient qu'à une seule largeur n'est pas une mise en page. Cet
        /// argument permet de capturer la même vue à plusieurs formats et de vérifier qu'elle
        /// se redistribue : contrôle qu'on ne peut pas faire en regardant une seule image.
        /// </remarks>
        private static void ApplyForcedSize(Window window, string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--size", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = args[i + 1].Split('x', 'X');
                if (parts.Length != 2) return;
                if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var width)) return;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var height)) return;

                // Le plancher cède devant la taille demandée, au lieu de la réécrire en
                // silence. Un contrôle qui rend toujours la même image quelle que soit la
                // largeur demandée ne contrôle rien, et c'est précisément sous le plancher
                // qu'on veut savoir ce que la mise en page devient.
                if (width < window.MinWidth) window.MinWidth = width;
                if (height < window.MinHeight) window.MinHeight = height;

                window.Width = width;
                window.Height = height;
                return;
            }
        }

        /// <summary>Forçage du thème, utilisé pour contrôler les deux rendus en capture.</summary>
        private static AppTheme? ReadTheme(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase)) continue;
                return string.Equals(args[i + 1], "light", StringComparison.OrdinalIgnoreCase)
                    ? AppTheme.Light
                    : AppTheme.Dark;
            }
            return null;
        }

        /// <summary>Écran à afficher au démarrage, pour capturer autre chose que l'accueil.</summary>
        private static int ReadNavigationIndex(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--nav", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out var index))
                    return index;
            return 0;
        }

        private static string? ReadScreenshotPath(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--screenshot", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>
        /// Décalage de défilement à appliquer avant la capture (<c>--scroll 400</c>).
        /// </summary>
        /// <remarks>
        /// Une capture prise en haut de page ne montre jamais ce qui se passe quand le contenu
        /// passe sous les bords : c'est pourtant là que se jouent les fondus de tête et de pied,
        /// et là qu'un défaut d'affichage se voit. Sans cet argument, le seul état vérifiable
        /// serait le seul où le problème n'apparaît pas.
        /// </remarks>
        private static double ReadScrollOffset(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--scroll", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var offset))
                    return offset;
            return 0;
        }

        /// <summary>Geste à montrer sur l'écran Données (<c>--data-mode restore</c>).</summary>
        private static string? ReadDataMode(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--data-mode", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>Délai avant capture (<c>--capture-delay 800</c>), en millisecondes.</summary>
        private static int ReadCaptureDelay(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--capture-delay", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var delay))
                    return delay;
            return 0;
        }

        private static void ApplyForcedScroll(DependencyObject root, double offset)
        {
            if (offset <= 0) return;

            var viewer = FindScrollViewer(root);
            if (viewer == null) return;

            viewer.ScrollToVerticalOffset(offset);
            viewer.UpdateLayout();
        }

        /// <summary>
        /// La zone défilante du contenu, c'est-à-dire la plus large.
        /// </summary>
        /// <remarks>
        /// Cette recherche prenait la première rencontrée dans l'arbre, en supposant que la barre
        /// latérale n'en contenait pas. L'hypothèse a tenu jusqu'à ce que la navigation compte
        /// seize écrans : elle s'est mise à déborder, et le défilement forcé des captures a
        /// commencé à faire glisser la barre latérale au lieu de la page, sans erreur, en
        /// rendant simplement toutes les captures identiques.
        /// <para>
        /// Le critère est donc la largeur : la barre latérale fait deux cent quarante points, le
        /// contenu occupe tout le reste.
        /// </para>
        /// </remarks>
        private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            System.Windows.Controls.ScrollViewer? widest = null;
            Collect(root, ref widest);
            return widest;
        }

        private static void Collect(DependencyObject root, ref System.Windows.Controls.ScrollViewer? widest)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is System.Windows.Controls.ScrollViewer viewer &&
                    viewer.ScrollableHeight > 0 &&
                    (widest == null || viewer.ActualWidth > widest.ActualWidth))
                {
                    widest = viewer;
                }

                Collect(child, ref widest);
            }
        }

        /// <summary>
        /// Capture de la fenêtre après la première analyse. Sert à contrôler le rendu réel de
        /// l'interface sans avoir à la regarder tourner, utile en revue comme en régression.
        /// </summary>
        private void OnScanCompletedForScreenshot(object? sender, EventArgs e)
        {
            if (_screenshotPath == null || MainWindow == null) return;
            _shell!.ScanCompleted -= OnScanCompletedForScreenshot;

            // L'écran d'analyse s'efface en fondu : capturer immédiatement le saisirait à
            // mi-disparition, superposé aux résultats. On laisse la transition se terminer.
            DispatcherTimer? settle = null;
            settle = new DispatcherTimer(TimeSpan.FromMilliseconds(420), DispatcherPriority.Background,
                (_, __) =>
                {
                    settle!.Stop();
                    CaptureNow();
                },
                MainWindow.Dispatcher);
            settle.Start();
        }

        private void CaptureNow()
        {
            if (_screenshotPath == null || MainWindow == null) return;

            MainWindow.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    var window = MainWindow!;
                    window.UpdateLayout();
                    ApplyForcedScroll(window, _scrollOffset);

                    var width = (int)Math.Ceiling(window.ActualWidth);
                    var height = (int)Math.Ceiling(window.ActualHeight);
                    if (width <= 0 || height <= 0) return;

                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(_screenshotPath)) encoder.Save(stream);

                    _logger?.Info("App", "Capture enregistrée : " + _screenshotPath);
                }
                catch (Exception ex)
                {
                    _logger?.Error("App", "La capture a échoué.", ex);
                }
                finally
                {
                    Shutdown();
                }
            }));
        }

        /// <summary>
        /// Ce processus est la nouvelle version : il attend, recopie, relance, et se termine.
        /// </summary>
        /// <remarks>
        /// Aucune interface n'est construite. Si la mise en place échoue, l'ancienne version est
        /// intacte et se relance : un technicien chez un client ne doit jamais se retrouver sans
        /// outil parce qu'une mise à jour a échoué.
        /// </remarks>
        private async void ApplyUpdate(string target, int processId)
        {
            var logger = RollingFileLogger.CreateDefault();

            try
            {
                var outcome = await LDI12.Updates.UpdateApplier
                    .ApplyAsync(logger, target, processId, System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);

                if (!outcome.Succeeded)
                    logger.Warn("App", "Mise en place non aboutie : " + outcome.Message);
            }
            catch (Exception ex)
            {
                logger.Error("App", "Mise en place interrompue.", ex);
            }
            finally
            {
                Shutdown(0);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger?.Error("App", "Exception non gérée sur le fil d'interface.", e.Exception);
            MessageBox.Show(
                "Une erreur inattendue s'est produite dans cette partie de l'application." + Environment.NewLine +
                Environment.NewLine + e.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Le diagnostic en cours n'est pas perdu. Le détail a été enregistré dans le journal.",
                "LDI12 Diagnostic", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
            => _logger?.Error("App", "Exception non gérée hors du fil d'interface.", e.ExceptionObject as Exception);

        protected override void OnExit(ExitEventArgs e)
        {
            _shell?.Dispose();
            _logger?.Dispose();
            base.OnExit(e);
        }
    }
}
