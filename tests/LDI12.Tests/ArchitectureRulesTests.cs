using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Règles d'architecture vérifiées par le compilateur de tests plutôt que par la relecture.
    /// </summary>
    public class ArchitectureRulesTests
    {
        /// <summary>
        /// Le seul fichier autorisé à employer Win32_Product : la passerelle qui l'interdit.
        /// </summary>
        /// <remarks>
        /// Les commentaires, eux, sont autorisés partout, et le sont volontairement. L'endroit
        /// où il faut expliquer pourquoi cette classe WMI est bannie est précisément la sonde
        /// qu'un développeur pressé écrirait avec elle : celle qui liste les logiciels installés.
        /// Interdire jusqu'à la mention effacerait l'avertissement du seul endroit où il sert.
        /// </remarks>
        private static readonly string[] AllowedFiles = { "WmiGateway.cs" };

        /// <summary>Vrai pour une ligne qui ne fait que commenter ou documenter.</summary>
        private static bool IsCommentary(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal) ||
                   trimmed.StartsWith("<", StringComparison.Ordinal);
        }

        [Fact]
        public void Win32_Product_n_apparait_nulle_part_dans_le_code_de_production()
        {
            // Énumérer Win32_Product déclenche une reconfiguration MSI de chaque logiciel
            // installé : jusqu'à vingt minutes de blocage et des installations cassées. C'est
            // le genre d'erreur qu'un développeur pressé réintroduit six mois plus tard.
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (AllowedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;

                foreach (var line in File.ReadAllLines(file))
                {
                    if (IsCommentary(line)) continue;
                    if (line.IndexOf("Win32_Product", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    offenders.Add(file);
                    break;
                }
            }

            Assert.True(offenders.Count == 0,
                "Win32_Product est interdit. Utiliser la clé de registre Uninstall pour lister les " +
                "logiciels installés. Fichiers concernés : " + string.Join(", ", offenders));
        }

        [Fact]
        public async Task La_passerelle_wmi_refuse_activement_une_requete_interdite()
        {
            var gateway = new WmiGateway(NullLogger.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.QueryAsync(WmiNamespaces.CimV2, "SELECT * FROM Win32_Product", TimeSpan.FromSeconds(1), CancellationToken.None));

            Assert.Contains("Uninstall", ex.Message);
        }

        [Fact]
        public void Le_noyau_ne_reference_aucune_bibliotheque_tierce()
        {
            // Contrat d'architecture : LDI12.Core ne dépend que du framework et des polyfills
            // de langage (System.ValueTuple, IsExternalInit). Une dépendance qui s'y glisserait
            // contaminerait tous les autres projets.
            var csproj = File.ReadAllText(Path.Combine(SourceRoot(), "LDI12.Core", "LDI12.Core.csproj"));

            Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<PackageReference", csproj, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Interdits dans <c>LDI12.Actions</c> : ce sont les appels par lesquels une action
        /// contournerait les passerelles.
        /// </summary>
        private static readonly string[] ForbiddenInActions =
        {
            "Process.Start",
            "ProcessStartInfo",
            "File.Delete",
            "File.WriteAllText",
            "Directory.Delete",
            "Directory.GetFiles",
            "Directory.EnumerateFiles",
            "RegistryKey",
        };

        [Fact]
        public void Une_action_ne_touche_jamais_l_OS_directement()
        {
            // Le pendant en écriture de la règle qui vaut pour les sondes, et il compte
            // davantage : une action qui supprimerait un fichier sans passer par la passerelle
            // échapperait au contrôle « ce qui est supprimé est exactement ce qui a été montré ».
            // Le journal d'intervention est la seule exception : il écrit son propre fichier.
            var offenders = new List<string>();
            var actionsRoot = Path.Combine(SourceRoot(), "LDI12.Actions");

            foreach (var file in Directory.GetFiles(actionsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (Path.GetFileName(file).Equals("InterventionJournal.cs", StringComparison.OrdinalIgnoreCase)) continue;

                var text = File.ReadAllText(file);
                foreach (var forbidden in ForbiddenInActions)
                    if (text.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
                        offenders.Add(Path.GetFileName(file) + " → " + forbidden);
            }

            Assert.True(offenders.Count == 0,
                "Une action doit passer par les passerelles du noyau. Écarts : " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Projets qui n'ont pas le droit de connaître l'implémentation de publication.
        /// </summary>
        /// <remarks>
        /// La promesse « aucune dépendance inutile à Internet » ne se tient pas par la discipline
        /// mais par l'absence de chemin de compilation : si le noyau, le moteur, les rapports, la
        /// collecte ou les actions référençaient le projet Klarvi, il deviendrait possible d'y
        /// glisser un envoi réseau sans que personne le remarque. Seule l'application le
        /// référence, et uniquement pour le construire.
        /// </remarks>
        private static readonly string[] OfflineProjects =
        {
            "LDI12.Core", "LDI12.Collectors", "LDI12.Engine",
            "LDI12.Reports", "LDI12.Actions", "LDI12.ProbeHost",
        };

        [Fact]
        public void Seule_l_application_connait_la_verification_de_mise_a_jour()
        {
            // Le pendant exact de la règle qui vaut pour la publication. Deux projets de cette
            // solution parlent HTTP, et deux seulement : si le noyau, le moteur, les rapports, la
            // collecte ou les actions référençaient celui-ci, il redeviendrait possible d'y
            // glisser une sortie réseau sans que personne le remarque.
            var offenders = new List<string>();

            foreach (var project in OfflineProjects)
            {
                var path = Path.Combine(SourceRoot(), project, project + ".csproj");
                if (!File.Exists(path)) continue;

                if (File.ReadAllText(path).IndexOf("LDI12.Updates", StringComparison.OrdinalIgnoreCase) >= 0)
                    offenders.Add(project);
            }

            Assert.True(offenders.Count == 0,
                "La vérification de mise à jour reste hors du cœur. Projets fautifs : " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void La_verification_de_mise_a_jour_ne_depend_que_du_noyau()
        {
            var csproj = File.ReadAllLines(Path.Combine(
                SourceRoot(), "LDI12.Updates", "LDI12.Updates.csproj"));

            var references = csproj.Where(line => line.Contains("<ProjectReference")).ToList();

            Assert.All(references, line => Assert.Contains("LDI12.Core.csproj", line));
            Assert.Single(references);
        }

        [Fact]
        public void Seule_l_application_connait_l_implementation_de_publication()
        {
            var offenders = new List<string>();

            foreach (var project in OfflineProjects)
            {
                var path = Path.Combine(SourceRoot(), project, project + ".csproj");
                if (!File.Exists(path)) continue;

                if (File.ReadAllText(path).IndexOf("Publishing.Klarvi", StringComparison.OrdinalIgnoreCase) >= 0)
                    offenders.Add(project);
            }

            Assert.True(offenders.Count == 0,
                "Le cœur ne connaît que IReportPublisher. Projets fautifs : " + string.Join(", ", offenders));
        }

        [Fact]
        public void L_implementation_de_publication_ne_depend_que_du_noyau()
        {
            // L'inverse de la règle précédente : un projet de publication qui référencerait le
            // moteur ou les rapports pourrait décider seul de ce qu'il envoie.
            var csproj = File.ReadAllLines(Path.Combine(
                SourceRoot(), "LDI12.Publishing.Klarvi", "LDI12.Publishing.Klarvi.csproj"));

            // Sur les lignes de référence, et non sur le texte entier : un commentaire qui cite
            // un autre projet n'est pas une dépendance.
            var references = csproj.Where(line => line.Contains("<ProjectReference")).ToList();

            Assert.All(references, line => Assert.Contains("LDI12.Core.csproj", line));
            Assert.Single(references);
        }

        private static string SourceRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LDI12.Diagnostic.sln")))
                directory = directory.Parent;

            Assert.True(directory != null, "Racine de la solution introuvable depuis " + AppContext.BaseDirectory);
            return Path.Combine(directory!.FullName, "src");
        }
    }
}
