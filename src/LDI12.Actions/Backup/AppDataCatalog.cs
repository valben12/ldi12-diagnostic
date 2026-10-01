using System;
using System.Collections.Generic;
using System.IO;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>Un dossier de données d'une application, et ce qu'on en garde.</summary>
    public sealed class AppDataSource
    {
        public string Root { get; init; } = string.Empty;

        /// <summary>Emplacement dans la sauvegarde, relatif à son dossier racine.</summary>
        public string Target { get; init; } = string.Empty;

        /// <summary>Caches et fichiers de travail écartés pendant le balayage, relatifs à la racine.</summary>
        public IReadOnlyList<string> ExcludeRelative { get; init; } = Array.Empty<string>();

        /// <summary>Ne relever que les fichiers posés directement dans la racine.</summary>
        public bool TopLevelOnly { get; init; }

        /// <summary>Filtre sur le nom de fichier. Nul : tout ce que le balayage rend.</summary>
        public Func<string, bool>? Keep { get; init; }
    }

    /// <summary>
    /// Une application dont les données comptent avant une réinstallation.
    /// </summary>
    /// <remarks>
    /// Chaque entrée porte trois textes, et aucun n'est facultatif : ce qui part, ce qui ne suit
    /// pas (et pourquoi), et comment le remettre en place. Une copie de profil de navigateur sans
    /// la phrase sur les mots de passe laisserait croire au technicien qu'ils sont sauvegardés,
    /// et il ne le découvrirait qu'une fois le disque effacé.
    /// </remarks>
    public sealed class AppDataApplication
    {
        public string Name { get; init; } = string.Empty;

        public IReadOnlyList<AppDataSource> Sources { get; init; } = Array.Empty<AppDataSource>();

        /// <summary>Précision sur ce qui a été trouvé : « 2 profils », par exemple.</summary>
        public string? Detail { get; init; }

        /// <summary>Ce qu'il faut faire tant que l'ancien disque existe encore, faute de quoi c'est perdu.</summary>
        public IReadOnlyList<string> BeforeWipe { get; init; } = Array.Empty<string>();

        /// <summary>Ce que la copie n'emporte pas, ou n'emporte pas utilement.</summary>
        public IReadOnlyList<string> Limits { get; init; } = Array.Empty<string>();

        /// <summary>Comment remettre les données en place sur la machine réinstallée.</summary>
        public IReadOnlyList<string> Restore { get; init; } = Array.Empty<string>();

        /// <summary>Noms d'image des processus qui tiennent ces fichiers ouverts.</summary>
        public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Les données d'applications qu'une sauvegarde avant réinstallation doit emporter.
    /// </summary>
    /// <remarks>
    /// <b>Une liste fermée, pas une exploration.</b> Copier tout <c>AppData</c> emporterait des
    /// dizaines de gigaoctets de caches, de fichiers de mise à jour et de bases que rien ne sait
    /// relire sur une autre installation. Chaque application est décrite ici avec ce qui compte
    /// chez elle, ce qui s'écarte, et ce qui ne survit pas à une réinstallation quoi qu'on copie.
    /// <para>
    /// Rien n'est annoncé sans avoir été trouvé : une application ne figure dans la sauvegarde
    /// que si son dossier existe sur cette machine, pour ce compte.
    /// </para>
    /// </remarks>
    public static class AppDataCatalog
    {
        /// <summary>Dossier de la sauvegarde qui reçoit les données d'applications.</summary>
        public const string Folder = "Applications";

        /// <summary>
        /// Caches d'un profil Chromium, relatifs au dossier du profil.
        /// </summary>
        /// <remarks>
        /// Mesuré sur un profil Edge ordinaire : 870 Mo de <c>Service Worker\CacheStorage</c> et
        /// 210 Mo de <c>Cache</c>, pour quelques mégaoctets de favoris, d'historique et de
        /// réglages. Tout se reconstruit à la première navigation.
        /// </remarks>
        internal static readonly string[] ChromiumCaches =
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GraphiteDawnCache", "GrShaderCache", "ShaderCache", "Media Cache", "Application Cache",
            @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "blob_storage",
            "optimization_guide_model_store", @"Shared Dictionary\cache",

            // Le rangement récent de Chrome : un dossier numéroté par site sous WebStorage, et
            // son cache de service worker dedans. Relevé à l'essai réel, 104 dossiers.
            @"WebStorage\*\CacheStorage",
        };

        /// <summary>Caches d'un profil Mozilla, relatifs au dossier du profil.</summary>
        internal static readonly string[] MozillaCaches =
        {
            "cache2", "startupCache", "thumbnails", "shader-cache", "crashes", "minidumps",
            "datareporting", "saved-telemetry-pings", @"storage\temporary",
        };

        /// <summary>Profils Chromium qui ne sont ceux de personne.</summary>
        private static readonly string[] ChromiumSystemProfiles = { "System Profile", "Guest Profile" };

        /// <summary>Les applications présentes sur cette machine, dans l'ordre où la fiche les présente.</summary>
        public static IReadOnlyList<AppDataApplication> Detect(IFileSystemGateway files)
            => Detect(files, ProfileRoots.Current());

        /// <summary>Les applications d'un compte donné : celui de la session, ou celui d'un autre Windows.</summary>
        public static IReadOnlyList<AppDataApplication> Detect(IFileSystemGateway files, ProfileRoots roots)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (roots == null) throw new ArgumentNullException(nameof(roots));

            var local = roots.LocalAppData;
            var roaming = roots.RoamingAppData;
            var profile = roots.Profile;
            var documents = roots.Documents;

            var found = new List<AppDataApplication>();

            Add(found, Chromium(files, "Google Chrome", Combine(local, @"Google\Chrome\User Data"), "chrome.exe",
                "le gestionnaire de mots de passe de Google (chrome://password-manager/settings)"));
            Add(found, Chromium(files, "Microsoft Edge", Combine(local, @"Microsoft\Edge\User Data"), "msedge.exe",
                "les paramètres de mots de passe d'Edge (edge://settings/passwords)"));
            Add(found, Chromium(files, "Brave", Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "brave.exe",
                "les paramètres de mots de passe de Brave (brave://settings/passwords)"));
            Add(found, Chromium(files, "Vivaldi", Combine(local, @"Vivaldi\User Data"), "vivaldi.exe",
                "les paramètres de mots de passe de Vivaldi (vivaldi://settings/passwords)"));
            Add(found, Opera(files, "Opera", Combine(roaming, @"Opera Software\Opera Stable"), "opera.exe"));
            Add(found, Opera(files, "Opera GX", Combine(roaming, @"Opera Software\Opera GX Stable"), "opera.exe"));
            Add(found, Mozilla(files, "Mozilla Firefox", Combine(roaming, @"Mozilla\Firefox"), "firefox.exe", mail: false));
            Add(found, Mozilla(files, "Mozilla Thunderbird", Combine(roaming, "Thunderbird"), "thunderbird.exe", mail: true));
            Add(found, Outlook(files, local, roaming, documents));
            Add(found, StickyNotes(files, local, roaming));
            Add(found, OneNote(files, local));
            Add(found, Teams(files, local, roaming));
            Add(found, SavedGames(files, profile));
            Add(found, Office(files, roaming));
            Add(found, Desktop(files, local, roaming));

            return found;
        }

        private static AppDataApplication? Chromium(
            IFileSystemGateway files, string name, string userData, string process, string passwordExport)
        {
            if (!files.DirectoryExists(userData)) return null;

            var target = Path.Combine(Folder, name);
            var sources = new List<AppDataSource>
            {
                // La liste des profils et leurs noms affichés : sans elle, un profil recopié
                // réapparaît sous un nom générique, ou pas du tout.
                new AppDataSource
                {
                    Root = userData,
                    Target = target,
                    TopLevelOnly = true,
                    Keep = file => string.Equals(file, "Local State", StringComparison.OrdinalIgnoreCase),
                },
            };

            var profiles = 0;
            foreach (var directory in files.EnumerateDirectories(userData))
            {
                var profileName = Path.GetFileName(directory);
                if (Contains(ChromiumSystemProfiles, profileName)) continue;

                // Un profil se reconnaît à son fichier de préférences. Les autres dossiers du même
                // niveau sont des composants téléchargés par le navigateur : listes de sécurité,
                // correcteurs, modèles d'aide à la saisie.
                if (!files.FileExists(Path.Combine(directory, "Preferences"))) continue;

                profiles++;
                sources.Add(new AppDataSource
                {
                    Root = directory,
                    Target = Path.Combine(target, profileName),
                    ExcludeRelative = ChromiumCaches,
                });
            }

            if (profiles == 0) return null;

            return new AppDataApplication
            {
                Name = name,
                Detail = profiles == 1 ? "1 profil" : profiles + " profils",
                Sources = sources,
                Processes = new[] { process },
                BeforeWipe = new[]
                {
                    "Mots de passe de " + name + " : vérifier que la synchronisation du compte est active, ou " +
                    "les exporter depuis " + passwordExport + ".",
                },
                Limits = new[]
                {
                    "Les mots de passe sont chiffrés par une clé que Windows protège pour ce compte : ils ne " +
                    "suivent que si l'option « Mots de passe des navigateurs » est cochée, qui emporte cette clé.",
                    "Certaines connexions aux sites seront à rouvrir : les cookies récents sont aussi liés à " +
                    "l'installation du navigateur. Favoris, historique, extensions et réglages suivent la copie.",
                },
                Restore = new[]
                {
                    "Installer " + name + ", l'ouvrir une fois puis le fermer complètement.",
                    "Copier le contenu du dossier « " + target + " » de la sauvegarde dans " +
                    Display(userData) + ", en remplaçant les fichiers existants.",
                },
            };
        }

        private static AppDataApplication? Mozilla(
            IFileSystemGateway files, string name, string root, string process, bool mail)
        {
            var profilesRoot = Path.Combine(root, "Profiles");
            if (!files.DirectoryExists(profilesRoot)) return null;

            var target = Path.Combine(Folder, name);
            var sources = new List<AppDataSource>
            {
                // profiles.ini et installs.ini désignent le profil par défaut : sans eux, le
                // logiciel réinstallé crée un profil vide et ignore celui qu'on vient de replacer.
                new AppDataSource
                {
                    Root = root,
                    Target = target,
                    TopLevelOnly = true,
                    Keep = file => file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase),
                },
            };

            var profiles = 0;
            foreach (var directory in files.EnumerateDirectories(profilesRoot))
            {
                profiles++;
                sources.Add(new AppDataSource
                {
                    Root = directory,
                    Target = Path.Combine(target, "Profiles", Path.GetFileName(directory)),
                    ExcludeRelative = MozillaCaches,
                });
            }

            if (profiles == 0) return null;

            var limits = new List<string>
            {
                "Les mots de passe sont chiffrés avec une clé rangée dans le profil lui-même, pas par " +
                "Windows : ils suivent la copie. Si un mot de passe principal a été défini, il faudra le " +
                "connaître pour les relire.",
            };

            if (mail)
                limits.Add(
                    "Les comptes IMAP et Exchange se resynchronisent depuis le serveur. Les dossiers locaux " +
                    "et les comptes POP n'existent, eux, que dans cette copie.");

            return new AppDataApplication
            {
                Name = name,
                Detail = profiles == 1 ? "1 profil" : profiles + " profils",
                Sources = sources,
                Processes = new[] { process },
                Limits = limits,
                Restore = new[]
                {
                    "Installer " + name + ", l'ouvrir une fois puis le fermer.",
                    "Remplacer le contenu de " + Display(root) + " par celui du dossier « " + target +
                    " » de la sauvegarde.",
                },
            };
        }

        private static AppDataApplication? Outlook(
            IFileSystemGateway files, string local, string roaming, string documents)
        {
            var dataFiles = Combine(local, @"Microsoft\Outlook");
            var signatures = Combine(roaming, @"Microsoft\Signatures");

            var hasData = files.DirectoryExists(dataFiles);
            var hasSignatures = files.DirectoryExists(signatures);
            if (!hasData && !hasSignatures) return null;

            var target = Path.Combine(Folder, "Outlook");
            var sources = new List<AppDataSource>();

            // Seulement les .pst : les .ost du même dossier sont des copies locales d'une boîte en
            // ligne, souvent de plusieurs gigaoctets, et Outlook les reconstruit à la reconnexion.
            if (hasData)
                sources.Add(new AppDataSource
                {
                    Root = dataFiles,
                    Target = Path.Combine(target, "Fichiers de données"),
                    Keep = file => file.EndsWith(".pst", StringComparison.OrdinalIgnoreCase),
                });

            if (hasSignatures)
                sources.Add(new AppDataSource { Root = signatures, Target = Path.Combine(target, "Signatures") });

            var limits = new List<string>
            {
                "Les fichiers .ost ne sont pas copiés : ce sont des copies locales d'une boîte Microsoft 365, " +
                "Exchange ou IMAP, qu'Outlook reconstruit en se reconnectant.",
                "Les comptes eux-mêmes (adresses, serveurs, mots de passe) sont à reconfigurer.",
            };

            foreach (var name in new[] { "Fichiers Outlook", "Outlook Files" })
                if (files.DirectoryExists(Combine(documents, name)))
                {
                    limits.Add("Le dossier Documents\\" + name + " part avec les Documents : les archives .pst qui " +
                               "s'y trouvent sont déjà comprises.");
                    break;
                }

            var restore = new List<string>
            {
                "Archives .pst : dans Outlook, Fichier, Ouvrir et exporter, Ouvrir un fichier de données Outlook.",
            };

            if (hasSignatures)
                restore.Add("Signatures : copier le contenu de « " + Path.Combine(target, "Signatures") + " » dans " +
                            Display(signatures) + ".");

            return new AppDataApplication
            {
                Name = "Microsoft Outlook",
                Sources = sources,
                Processes = new[] { "outlook.exe" },
                BeforeWipe = new[]
                {
                    "Comptes de messagerie : s'assurer que le client connaît ses adresses et mots de passe, ou " +
                    "relever les paramètres des serveurs dans Outlook.",
                },
                Limits = limits,
                Restore = restore,
            };
        }

        private static AppDataApplication? StickyNotes(IFileSystemGateway files, string local, string roaming)
        {
            var modern = Combine(local, @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState");
            var legacy = Combine(roaming, @"Microsoft\Sticky Notes");
            var target = Path.Combine(Folder, "Pense-bêtes");

            var sources = new List<AppDataSource>();
            if (files.DirectoryExists(modern)) sources.Add(new AppDataSource { Root = modern, Target = target });
            if (files.DirectoryExists(legacy)) sources.Add(new AppDataSource { Root = legacy, Target = target });
            if (sources.Count == 0) return null;

            return new AppDataApplication
            {
                Name = "Pense-bêtes",
                Sources = sources,
                Processes = new[] { "Microsoft.Notes.exe" },
                Limits = new[]
                {
                    "Avec un compte Microsoft connecté, les pense-bêtes sont aussi synchronisés en ligne et " +
                    "reviennent seuls à la connexion.",
                },
                Restore = new[]
                {
                    "Ouvrir Pense-bêtes une fois, le fermer, puis replacer le contenu de « " + target + " » dans " +
                    Display(sources[0].Root) + ".",
                },
            };
        }

        private static AppDataApplication? OneNote(IFileSystemGateway files, string local)
        {
            var root = Combine(local, @"Microsoft\OneNote");
            if (!files.DirectoryExists(root)) return null;

            var sources = new List<AppDataSource>();
            foreach (var version in files.EnumerateDirectories(root))
            {
                var backup = Path.Combine(version, "Backup");
                if (!files.DirectoryExists(backup)) continue;

                sources.Add(new AppDataSource
                {
                    Root = backup,
                    Target = Path.Combine(Folder, "OneNote", "Sauvegardes " + Path.GetFileName(version)),
                });
            }

            if (sources.Count == 0) return null;

            return new AppDataApplication
            {
                Name = "OneNote",
                Sources = sources,
                Processes = new[] { "onenote.exe" },
                Limits = new[]
                {
                    "Ce sont les sauvegardes automatiques d'OneNote. Les blocs-notes enregistrés sur OneDrive " +
                    "ou SharePoint sont en ligne ; les blocs-notes locaux sont en général dans Documents, et " +
                    "partent avec lui.",
                },
                Restore = new[]
                {
                    "Dans OneNote : Fichier, Informations, Ouvrir les sauvegardes, puis choisir le dossier « " +
                    Path.Combine(Folder, "OneNote") + " » de la sauvegarde.",
                },
            };
        }

        private static AppDataApplication? Teams(IFileSystemGateway files, string local, string roaming)
        {
            var modern = Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe");
            var classic = Combine(roaming, @"Microsoft\Teams");

            var hasModern = files.DirectoryExists(modern);
            var hasClassic = files.DirectoryExists(classic);
            if (!hasModern && !hasClassic) return null;

            var target = Path.Combine(Folder, "Microsoft Teams", "Arrière-plans");
            var sources = new List<AppDataSource>();

            // Les arrière-plans importés pour les visioconférences : la seule chose que Teams garde
            // sur la machine et ne retrouve pas en ligne.
            var modernBackgrounds = Path.Combine(modern, @"LocalCache\Microsoft\MSTeams\Backgrounds\Uploads");
            var classicBackgrounds = Path.Combine(classic, @"Backgrounds\Uploads");

            if (hasModern && files.DirectoryExists(modernBackgrounds))
                sources.Add(new AppDataSource { Root = modernBackgrounds, Target = target });
            if (hasClassic && files.DirectoryExists(classicBackgrounds))
                sources.Add(new AppDataSource { Root = classicBackgrounds, Target = target });

            return new AppDataApplication
            {
                Name = "Microsoft Teams",
                Sources = sources,
                Processes = new[] { "ms-teams.exe", "teams.exe" },
                Limits = new[]
                {
                    "Conversations, fichiers et réunions sont sur les serveurs de Microsoft : il n'y a rien à " +
                    "copier, le dossier local n'est qu'un cache. Il suffit de se reconnecter avec le même compte.",
                },
                Restore = sources.Count == 0
                    ? new[] { "Installer Teams et se connecter avec le compte du client." }
                    : new[]
                    {
                        "Installer Teams et se connecter avec le compte du client.",
                        "Arrière-plans : les réimporter depuis « " + target + " » dans les effets vidéo d'une réunion.",
                    },
            };
        }

        private static AppDataApplication? SavedGames(IFileSystemGateway files, string profile)
        {
            var root = Combine(profile, "Saved Games");
            if (!files.DirectoryExists(root)) return null;

            var target = Path.Combine(Folder, "Sauvegardes de jeux");

            return new AppDataApplication
            {
                Name = "Sauvegardes de jeux",
                Sources = new[] { new AppDataSource { Root = root, Target = target } },
                Limits = new[]
                {
                    "Seul le dossier « Parties enregistrées » de Windows est copié. Beaucoup de jeux rangent " +
                    "leurs parties ailleurs : dans Documents\\My Games (copié avec Documents), dans AppData, ou " +
                    "dans le nuage de Steam, Xbox ou Epic.",
                },
                Restore = new[] { "Copier le contenu de « " + target + " » dans " + Display(root) + "." },
            };
        }

        /// <summary>
        /// Opera : un seul profil, rangé directement dans son dossier, sans dossier « Default ».
        /// </summary>
        /// <remarks>
        /// Le dossier entier part, sans ses caches, et revient tel quel. Sa clé de mots de passe
        /// n'est pas reprise : Opera la range avec le profil, et la rechiffrer demanderait de
        /// toucher au fichier qu'Opera relit à chaque démarrage. Sa synchronisation les ramène.
        /// </remarks>
        private static AppDataApplication? Opera(IFileSystemGateway files, string name, string root, string process)
        {
            if (!files.FileExists(Path.Combine(root, "Preferences"))) return null;

            var target = Path.Combine(Folder, name);
            return new AppDataApplication
            {
                Name = name,
                Sources = new[] { new AppDataSource { Root = root, Target = target, ExcludeRelative = ChromiumCaches } },
                Processes = new[] { process },
                BeforeWipe = new[] { "Mots de passe de " + name + " : vérifier que la synchronisation du compte Opera est active." },
                Limits = new[]
                {
                    "Les mots de passe sont chiffrés pour cette installation de Windows : ils reviendront par la " +
                    "synchronisation du compte Opera. Favoris, historique, extensions et réglages suivent la copie.",
                },
                Restore = new[]
                {
                    "Installer " + name + ", l'ouvrir une fois puis le fermer.",
                    "Remplacer le contenu de " + Display(root) + " par celui du dossier « " + target + " » de la sauvegarde.",
                },
            };
        }

        /// <summary>
        /// Ce que Word et Outlook apprennent avec le temps : le dictionnaire personnel, les modèles,
        /// les insertions automatiques et la correction automatique.
        /// </summary>
        private static AppDataApplication? Office(IFileSystemGateway files, string roaming)
        {
            var target = Path.Combine(Folder, "Office");
            var sources = new List<AppDataSource>();

            void Take(string relative, string name, bool topLevel = false, Func<string, bool>? keep = null)
            {
                var root = Combine(roaming, relative);
                if (root.Length > 0 && files.DirectoryExists(root))
                    sources.Add(new AppDataSource { Root = root, Target = Path.Combine(target, name), TopLevelOnly = topLevel, Keep = keep });
            }

            Take(@"Microsoft\UProof", "Dictionnaires");
            Take(@"Microsoft\Templates", "Modèles");
            Take(@"Microsoft\Document Building Blocks", "Insertions automatiques");
            Take(@"Microsoft\Office", "Correction automatique", topLevel: true,
                keep: file => file.EndsWith(".acl", StringComparison.OrdinalIgnoreCase));
            if (sources.Count == 0) return null;

            return new AppDataApplication
            {
                Name = "Office : dictionnaire et modèles",
                Sources = sources,
                Processes = new[] { "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe" },
                Limits = new[]
                {
                    "Si Word a déjà été ouvert sur le nouveau PC, son modèle Normal.dotm neuf est gardé : celui " +
                    "de la sauvegarde reste dans le dossier « " + Path.Combine(target, "Modèles") + " ».",
                },
                Restore = new[] { "Fermer Word, Excel et Outlook avant la restauration." },
            };
        }

        /// <summary>Le fond d'écran et les polices installées pour le compte seul.</summary>
        private static AppDataApplication? Desktop(IFileSystemGateway files, string local, string roaming)
        {
            var target = Path.Combine(Folder, "Environnement");
            var sources = new List<AppDataSource>();

            var themes = Combine(roaming, @"Microsoft\Windows\Themes");
            if (themes.Length > 0 && files.FileExists(Path.Combine(themes, "TranscodedWallpaper")))
                sources.Add(new AppDataSource
                {
                    Root = themes,
                    Target = Path.Combine(target, "Fond d'écran"),
                    TopLevelOnly = true,
                    Keep = file => string.Equals(file, "TranscodedWallpaper", StringComparison.OrdinalIgnoreCase),
                });

            var fonts = Combine(local, @"Microsoft\Windows\Fonts");
            if (fonts.Length > 0 && files.DirectoryExists(fonts))
                sources.Add(new AppDataSource { Root = fonts, Target = Path.Combine(target, "Polices"), Keep = IsFont });

            if (sources.Count == 0) return null;

            return new AppDataApplication
            {
                Name = "Fond d'écran et polices",
                Sources = sources,
                Limits = new[]
                {
                    "Les polices installées pour tous les utilisateurs (C:\\Windows\\Fonts) ne sont pas copiées : " +
                    "elles viennent des logiciels, qui les réinstallent.",
                },
                Restore = new[] { "La restauration remet le fond d'écran et réinscrit les polices pour le compte." },
            };
        }

        internal static bool IsFont(string file)
            => file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
               file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".fon", StringComparison.OrdinalIgnoreCase);

        private static void Add(ICollection<AppDataApplication> found, AppDataApplication? application)
        {
            if (application != null) found.Add(application);
        }

        private static bool Contains(IEnumerable<string> names, string name)
        {
            foreach (var candidate in names)
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Le chemin tel qu'on le tape sur la machine réinstallée, où le nom de compte peut changer.</summary>
        internal static string Display(string path)
        {
            foreach (var (folder, variable) in new[]
                     {
                         (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
                         (Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
                         (Environment.SpecialFolder.UserProfile, "%USERPROFILE%"),
                     })
            {
                var root = Special(folder);
                if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return variable + path.Substring(root.Length);
            }

            return path;
        }

        private static string Combine(string root, string relative)
            => string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, relative);

        private static string Special(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
