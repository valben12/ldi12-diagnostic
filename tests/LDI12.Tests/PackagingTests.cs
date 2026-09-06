using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LDI12.Core.Runtime;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'exécutable unique : ce qui se vérifie ici, et ce qui ne se vérifie qu'en le lançant.
    /// </summary>
    /// <remarks>
    /// Le résolveur de dépendances embarquées ne peut pas être testé depuis ce projet : il est
    /// compilé <b>dans</b> chaque exécutable, il lit les ressources de l'assembly d'entrée, et
    /// l'assembly d'entrée d'une exécution de tests est le lanceur de tests. La preuve qu'il
    /// fonctionne est ailleurs, et elle est plus solide que n'importe quel test unitaire :
    /// l'exécutable publié est copié seul dans un dossier vide, lancé, et il produit un
    /// diagnostic complet.
    /// <para>
    /// Ce qui se teste ici est ce qui ferait échouer ce mécanisme en silence : les deux
    /// constantes qui doivent rester égales de part et d'autre d'une frontière que le
    /// compilateur ne surveille pas, et l'indépendance du fichier qui porte le résolveur.
    /// </para>
    /// </remarks>
    public class PackagingTests
    {
        [Fact]
        public void Le_prefixe_des_ressources_est_le_meme_des_deux_cotes()
        {
            // AssemblyBundle ne peut pas référencer LDI12.Core : il charge justement LDI12.Core.
            // La constante est donc écrite deux fois, et rien dans le compilateur ne le remarque
            // si l'une des deux change. Un préfixe désaccordé produirait un exécutable qui se
            // lance, ne trouve rien, et se plante à la première dépendance.
            var shared = File.ReadAllText(Path.Combine(SourceRoot(), "Shared", "AssemblyBundle.cs"));
            var core = File.ReadAllText(Path.Combine(SourceRoot(), "LDI12.Core", "Runtime", "BundledFiles.cs"));

            var expected = "ResourcePrefix = \"" + BundledFiles.ResourcePrefix + "\";";

            Assert.Contains(expected, shared, StringComparison.Ordinal);
            Assert.Contains(expected, core, StringComparison.Ordinal);
        }

        [Fact]
        public void Le_resolveur_ne_depend_d_aucun_projet_du_logiciel()
        {
            // Il s'exécute avant que la première bibliothèque soit chargée. La moindre référence
            // à LDI12.Core l'obligerait à charger ce qu'il est censé rendre.
            var lines = File.ReadAllLines(Path.Combine(SourceRoot(), "Shared", "AssemblyBundle.cs"));

            var offenders = lines
                .Where(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                .Where(line => line.Contains("LDI12."))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Le résolveur doit se suffire du framework. Écarts : " + string.Join(", ", offenders));
        }

        [Fact]
        public void Sans_extracteur_pose_l_appelant_recoit_un_refus_et_non_une_exception()
        {
            // C'est l'état d'une compilation de développement : les fichiers sont côte à côte, il
            // n'y a rien à extraire, et l'appelant a un autre chemin à essayer.
            BundledFiles.Extractor = null;

            Assert.Null(BundledFiles.Extract("LDI12.ProbeHost.exe"));
            Assert.Null(BundledFiles.Extract(string.Empty));
        }

        [Fact]
        public void Un_extracteur_qui_echoue_ne_fait_pas_tomber_l_appelant()
        {
            // Disque plein, profil en lecture seule : l'élévation ne pourra pas se faire, et
            // c'est au canal d'élévation de le dire, pas à une exception de traverser l'écran.
            BundledFiles.Extractor = _ => throw new IOException("Disque plein.");

            try
            {
                Assert.Null(BundledFiles.Extract("LDI12.ProbeHost.exe"));
            }
            finally
            {
                BundledFiles.Extractor = null;
            }
        }

        [Fact]
        public void La_publication_est_scriptee_et_ne_versionne_pas_ses_dossiers_de_travail()
        {
            var root = RepositoryRoot();

            Assert.True(File.Exists(Path.Combine(root, "build", "publish.ps1")),
                "La production de l'exécutable unique doit être reproductible sans mode d'emploi.");

            var ignored = File.ReadAllText(Path.Combine(root, ".gitignore"));

            // Un dossier Bundle laissé dans le dépôt embarquerait des bibliothèques périmées
            // dans la publication suivante, sans que rien ne le dise.
            Assert.Contains("src/LDI12.App/Bundle/", ignored, StringComparison.Ordinal);
            Assert.Contains("src/LDI12.ProbeHost/Bundle/", ignored, StringComparison.Ordinal);
        }

        // ============================================================ composants tiers

        [Fact]
        public void Chaque_paquet_du_projet_figure_dans_les_mentions()
        {
            // L'exécutable unique embarque ses dépendances : elles sont donc distribuées, et une
            // bibliothèque distribuée sans sa licence est un manquement : silencieux, puisque
            // rien dans la compilation ne s'en aperçoit.
            var missing = new List<string>();
            var seen = 0;

            foreach (var project in Directory.GetFiles(SourceRoot(), "*.csproj", SearchOption.AllDirectories))
                foreach (var name in PackageReferences(project))
                {
                    seen++;
                    if (Credits.Find(name) == null) missing.Add(name + " (" + Path.GetFileName(project) + ")");
                }

            // Sans ce compte, le test passerait aussi le jour où la lecture des projets casserait,
            // et il passerait en ne vérifiant plus rien du tout.
            Assert.True(seen > 0, "Aucune référence de paquet lue : la lecture des projets ne fonctionne plus.");

            Assert.True(missing.Count == 0,
                "Paquets absents de Credits.Components : " + string.Join(", ", missing));
        }

        [Fact]
        public void Les_mentions_du_programme_et_le_fichier_de_licences_disent_la_meme_chose()
        {
            // Deux listes tenues à la main finissent toujours par diverger, et c'est l'écran que
            // le client lit qui aurait raison contre le fichier qu'il télécharge, ou l'inverse.
            var notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENCES-TIERCES.md"));

            foreach (var component in Credits.Components)
            {
                Assert.Contains(component.Name, notices, StringComparison.Ordinal);
                Assert.Contains(component.Version, notices, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void La_licence_du_programme_est_livree_avec_lui()
        {
            var root = RepositoryRoot();

            Assert.True(File.Exists(Path.Combine(root, "LICENCE.md")), "LICENCE.md est absent de la racine.");
            Assert.True(File.Exists(Path.Combine(root, "LICENCES-TIERCES.md")),
                "LICENCES-TIERCES.md est absent de la racine.");

            // La MPL oblige à indiquer où trouver le source de ce qu'on redistribue. Le lien est
            // la seule partie de cette page qui ne soit pas qu'une politesse.
            Assert.Contains("mozilla.org/MPL/2.0",
                File.ReadAllText(Path.Combine(root, "LICENCES-TIERCES.md")), StringComparison.Ordinal);
        }

        private static IEnumerable<string> PackageReferences(string project)
        {
            foreach (var line in File.ReadAllLines(project))
            {
                var at = line.IndexOf("PackageReference Include=\"", StringComparison.Ordinal);
                if (at < 0) continue;

                var start = at + "PackageReference Include=\"".Length;
                var end = line.IndexOf('"', start);
                if (end > start) yield return line.Substring(start, end - start);
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LDI12.Diagnostic.sln")))
                directory = directory.Parent;

            Assert.True(directory != null, "Racine de la solution introuvable depuis " + AppContext.BaseDirectory);
            return directory!.FullName;
        }

        private static string SourceRoot() => Path.Combine(RepositoryRoot(), "src");
    }
}
