using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LDI12.Hosting
{
    /// <summary>
    /// Charge les dépendances embarquées dans l'exécutable lui-même.
    /// </summary>
    /// <remarks>
    /// <b>Ce fichier existe pour tenir une promesse du cahier des charges : un exécutable
    /// unique.</b> Un dossier de cent treize DLL ne se copie pas sur la clé USB d'un atelier : il
    /// se copie à moitié, une DLL manque, et le logiciel se plante chez le client sur un message
    /// que personne ne peut lire. La compilation ordinaire produit ce dossier ; la publication
    /// embarque son contenu dans l'exécutable et ce résolveur le rend au runtime.
    ///
    /// <para>
    /// <b>Il est écrit à la main plutôt qu'emprunté.</b> Costura.Fody fait exactement cela, mais
    /// en réécrivant l'assembly après compilation, au moyen d'un greffon de build : c'est-à-dire
    /// la catégorie d'outil qui a déjà cassé ce projet une fois, quand les générateurs de source
    /// de CommunityToolkit.Mvvm se sont révélés absents pendant la compilation du balisage XAML.
    /// Quatre-vingts lignes sous contrôle valent mieux qu'une étape de build qu'on ne sait pas
    /// déboguer chez un client.
    /// </para>
    ///
    /// <para>
    /// <b>Il ne peut dépendre de rien.</b> Pas même de <c>LDI12.Core</c> : le résolveur doit être
    /// en place avant la première dépendance demandée, et <c>LDI12.Core.dll</c> est justement
    /// l'une des dépendances embarquées. C'est pourquoi ce fichier est lié en source dans les
    /// deux hôtes (l'interface et l'hôte de sondes) plutôt que partagé par une bibliothèque.
    /// </para>
    /// </remarks>
    internal static class AssemblyBundle
    {
        /// <summary>
        /// Préfixe des ressources embarquées. Doit rester identique à celui de
        /// <c>LDI12.Core.Runtime.BundledFiles</c>, qu'un test compare à celui-ci.
        /// </summary>
        internal const string ResourcePrefix = "LDI12.Bundle.";

        private static readonly Dictionary<string, Assembly?> Loaded =
            new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        [ModuleInitializer]
        internal static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            // Les chemins longs, sous leur forme \\?\. Sans ces deux commutateurs, le processus
            // garde le traitement historique des chemins et refuse le préfixe comme un
            // « caractère non conforme ». C'est ce qui empêchait la sauvegarde de créer son
            // dossier et le nettoyage de supprimer quoi que ce soit, alors que la suite de tests,
            // exécutée par un autre hôte .NET, acceptait ces mêmes chemins sans broncher.
            // Ils doivent être posés ici, avant la première lecture d'un chemin : le framework
            // mémorise leur valeur au premier usage et ignore tout réglage ultérieur.
            try
            {
                AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
                AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            }
            catch (Exception)
            {
                // La passerelle de fichiers sait se passer de la forme longue : elle vérifie au
                // démarrage ce que le processus accepte réellement.
            }

            // WPF ne suit les changements de DPI par écran que si ce commutateur est à false.
            // Il est déjà posé par le fichier de configuration voisin de l'exécutable, sauf
            // qu'un exécutable unique n'a précisément pas de fichier voisin. Le poser ici aussi
            // est ce qui fait que la version publiée s'affiche comme celle du développement.
            try
            {
                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            }
            catch (Exception)
            {
                // Un commutateur inconnu d'une version de framework n'est pas une raison de ne
                // pas démarrer : sans lui, l'interface est nette sur un seul écran au lieu de
                // deux.
            }
        }

        /// <summary>
        /// Rend une dépendance embarquée quand le runtime ne l'a pas trouvée à côté de
        /// l'exécutable.
        /// </summary>
        /// <remarks>
        /// La recherche se fait sur le nom court, en ignorant la version demandée. C'est
        /// délibéré : c'est exactement ce que font les redirections de liaison du fichier de
        /// configuration, dont un exécutable unique est dépourvu. Les échecs sont mémorisés au
        /// même titre que les succès : le runtime redemande le même assembly des dizaines de
        /// fois, et relire les ressources à chaque tentative coûterait sans rien apporter.
        /// </remarks>
        private static Assembly? Resolve(object sender, ResolveEventArgs args)
        {
            var requested = args?.Name;
            if (string.IsNullOrEmpty(requested)) return null;

            string name;
            try
            {
                name = new AssemblyName(requested).Name;
            }
            catch (Exception)
            {
                return null;
            }

            // Les ressources satellites d'un assembly embarqué n'existent pas : les chercher
            // provoquerait une seconde résolution pour rien à chaque texte localisé.
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

            lock (Gate)
            {
                if (Loaded.TryGetValue(name, out var known)) return known;

                var assembly = Load(name);
                Loaded[name] = assembly;
                return assembly;
            }
        }

        private static Assembly? Load(string name)
        {
            var bytes = Read(name + ".dll");
            if (bytes == null) return null;

            try
            {
                return Assembly.Load(bytes);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Écrit un fichier embarqué sur le disque et rend son chemin.
        /// </summary>
        /// <remarks>
        /// Sert à l'hôte de sondes : c'est un second processus, il ne peut donc pas être chargé
        /// en mémoire comme une bibliothèque. Il est écrit dans le profil de l'utilisateur, sous
        /// un dossier qui porte la version (deux versions du logiciel lancées le même jour ne
        /// doivent pas se disputer le même fichier) et il n'est réécrit que si sa taille diffère,
        /// pour ne pas recopier six mégaoctets à chaque élévation.
        /// </remarks>
        internal static string? Extract(string fileName)
        {
            var bytes = Read(fileName);
            if (bytes == null) return null;

            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LDI12", "Diagnostic", "bin", Version());

                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);

                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length == bytes.Length) return path;

                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception exception) when (exception is IOException ||
                                              exception is UnauthorizedAccessException ||
                                              exception is ArgumentException)
            {
                // L'appelant sait dire pourquoi il n'a pas pu faire ce qu'il voulait faire ;
                // ici, il n'y a rien à ajouter.
                return null;
            }
        }

        /// <summary>
        /// Écrit un fichier embarqué dans un dossier temporaire à usage unique.
        /// </summary>
        /// <remarks>
        /// Pour l'hôte des sondes isolées, qui se lance à chaque analyse sans que personne ne le
        /// demande : rien de ce qu'il faut pour le faire tourner ne doit rester sur la machine du
        /// client. Le dossier porte un identifiant unique (deux analyses simultanées, ou une
        /// analyse pendant qu'une autre se termine, ne doivent pas se disputer le même fichier)
        /// et c'est l'appelant qui l'efface quand il en a fini.
        /// </remarks>
        internal static string? ExtractTemporary(string fileName)
        {
            var bytes = Read(fileName);
            if (bytes == null) return null;

            try
            {
                var directory = Path.Combine(
                    Path.GetTempPath(), "LDI12-" + Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception exception) when (exception is IOException ||
                                              exception is UnauthorizedAccessException ||
                                              exception is ArgumentException)
            {
                return null;
            }
        }

        private static byte[]? Read(string fileName)
        {
            var host = Assembly.GetEntryAssembly();
            if (host == null) return null;

            using (var stream = host.GetManifestResourceStream(ResourcePrefix + fileName))
            {
                if (stream == null) return null;

                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var step = stream.Read(bytes, read, bytes.Length - read);
                    if (step <= 0) break;
                    read += step;
                }

                return read == bytes.Length ? bytes : null;
            }
        }

        private static string Version()
        {
            try
            {
                var host = Assembly.GetEntryAssembly();
                var version = host?.GetName().Version;
                return version == null ? "0" : version.ToString(3);
            }
            catch (Exception)
            {
                return "0";
            }
        }

        /// <summary>Vrai quand l'exécutable porte ses dépendances : c'est-à-dire en version publiée.</summary>
        internal static bool IsBundled
        {
            get
            {
                var host = Assembly.GetEntryAssembly();
                if (host == null) return false;

                foreach (var resource in host.GetManifestResourceNames())
                    if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return true;

                return false;
            }
        }

        internal static string Describe()
            => IsBundled
                ? "Exécutable unique, version " + Version() + "."
                : "Exécutable accompagné de ses bibliothèques.";
    }
}
