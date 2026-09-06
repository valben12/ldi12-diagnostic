using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using LDI12.Actions.Backup;
using LDI12.Actions.Journal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le relevé des données à sauvegarder : ce qu'une copie emporterait, ce qu'elle laisserait,
    /// et ce que le relevé ne regarde pas.
    /// </summary>
    public class UserDataTests
    {
        [Theory]
        [InlineData(FileAttributes.Normal, false)]
        [InlineData(FileAttributes.Archive, false)]
        [InlineData(FileAttributes.Offline, true)]
        [InlineData((FileAttributes)0x00040000, true)]  // rappel à l'ouverture
        [InlineData((FileAttributes)0x00400000, true)]  // rappel à la lecture
        public void Un_fichier_reste_dans_le_nuage_quand_Windows_le_declare_ainsi(
            FileAttributes attributes, bool expected)
        {
            // Trois attributs signalent un fichier dont le disque ne porte que le nom. Les deux
            // derniers sont arrivés avec les fichiers « à la demande » de Windows 10 et ne
            // figurent pas dans l'énumération du framework.
            Assert.Equal(expected, FileSystemGateway.IsCloudOnly(attributes));
        }

        [Fact]
        public void Une_pesee_compte_les_octets_sans_retenir_un_seul_nom()
        {
            var root = TemporaryTree();

            try
            {
                var gateway = new FileSystemGateway(NullLogger.Instance);
                var measured = gateway.Measure(new DirectoryMeasureRequest(root), CancellationToken.None);

                Assert.True(measured.HasValue, measured.Reason);
                Assert.Equal(3, measured.Value.FileCount);
                Assert.Equal(600, measured.Value.TotalBytes);
                Assert.Equal(600, measured.Value.OnDiskBytes);
                Assert.False(measured.Value.Truncated);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Un_sous_dossier_exclu_n_est_pas_compte_deux_fois()
        {
            // C'est ainsi que « le reste du profil » se pèse sans repasser sur les dossiers déjà
            // mesurés : sans exclusion, un dossier d'images de deux cents gigaoctets serait
            // parcouru deux fois pour obtenir une soustraction.
            var root = TemporaryTree();

            try
            {
                var gateway = new FileSystemGateway(NullLogger.Instance);
                var request = new DirectoryMeasureRequest(root)
                {
                    Exclude = new[] { Path.Combine(root, "sous-dossier") },
                };

                var measured = gateway.Measure(request, CancellationToken.None);

                Assert.True(measured.HasValue, measured.Reason);
                Assert.Equal(2, measured.Value.FileCount);
                Assert.Equal(300, measured.Value.TotalBytes);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Le_releve_ne_peut_pas_transporter_un_nom_de_fichier()
        {
            // Garantie structurelle plutôt que discipline : ce que la passerelle rend ne contient
            // aucune collection, donc aucune liste de chemins. Le balayage du nettoyage, lui, en
            // rend une : parce qu'il doit prouver qu'il supprime ce qu'il a montré.
            var offenders = typeof(DirectoryMeasure).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType != typeof(string))
                .Where(property => typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType))
                .Select(property => property.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Une pesée de données personnelles ne doit rien rendre d'énumérable. Écarts : " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void Ce_qui_dort_dans_le_nuage_est_annonce_separement_de_ce_qui_est_sur_le_disque()
        {
            // La distinction qui décide d'une réinstallation : un dossier annonce deux cents
            // gigaoctets, le disque n'en porte que deux, et une copie n'emporterait rien.
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var gateway = new FakeFileSystemGateway().WithMeasure(desktop, new DirectoryMeasure
            {
                Root = desktop,
                TotalBytes = 200L * 1024 * 1024 * 1024,
                OnDiskBytes = 2L * 1024 * 1024 * 1024,
                CloudOnlyBytes = 198L * 1024 * 1024 * 1024,
                FileCount = 12000,
                CloudOnlyFileCount = 11000,
            });

            var survey = Survey(gateway);

            Assert.True(survey.HasCloudOnly);
            Assert.Contains(survey.Caveats, caveat => caveat.Contains("restent dans le nuage"));
            Assert.Contains(survey.Caveats, caveat => caveat.Contains("hors connexion"));
            Assert.Contains("dans le nuage", survey.Headline);
        }

        [Fact]
        public void Les_profils_des_autres_comptes_sont_annonces_et_jamais_peses()
        {
            // Lire le profil d'un autre compte demande des privilèges, et les données d'une autre
            // personne ne se comptent pas parce qu'on en a l'occasion.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var users = Path.GetDirectoryName(profile)!;

            var gateway = new FakeFileSystemGateway();
            gateway.Children[users] = new List<string>
            {
                profile,
                Path.Combine(users, "Public"),
                Path.Combine(users, "Mireille"),
            };

            var survey = Survey(gateway);

            var caveat = Assert.Single(survey.Caveats.Where(line => line.Contains("autre(s) profil(s)")));
            Assert.Contains("Mireille", caveat);
            Assert.DoesNotContain("Public", caveat);
            Assert.DoesNotContain(gateway.MeasuredRoots, root => root.Contains("Mireille"));
        }

        [Fact]
        public void Les_autres_volumes_sont_annonces_sans_etre_parcourus()
        {
            // Un disque de données de deux téraoctets n'est pas dans le profil, et le parcourir
            // prendrait des heures. N'en rien dire laisserait croire qu'il n'y a que le profil.
            var snapshot = Fixtures.Build(builder =>
            {
                builder.Windows11();
                builder.Volume("C:", totalGb: 500, freeGb: 200, isSystem: true);
                builder.Volume("D:", totalGb: 2000, freeGb: 100, isSystem: false);
            });

            var survey = Survey(new FakeFileSystemGateway(), snapshot);

            var caveat = Assert.Single(survey.Caveats.Where(line => line.Contains("D'autres volumes")));
            Assert.Contains("D:", caveat);
            Assert.DoesNotContain("C:", caveat);
        }

        [Fact]
        public void Un_releve_interrompu_annonce_un_minimum_et_non_une_mesure()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var gateway = new FakeFileSystemGateway().WithMeasure(desktop, new DirectoryMeasure
            {
                Root = desktop,
                TotalBytes = 90L * 1024 * 1024 * 1024,
                OnDiskBytes = 90L * 1024 * 1024 * 1024,
                FileCount = 400000,
                Truncated = true,
            });

            var survey = Survey(gateway);
            var folder = Assert.Single(survey.Folders.Where(entry => entry.Truncated));

            Assert.Equal(Availability.Partial, folder.TotalBytes.Availability);
            Assert.Contains(survey.Caveats, caveat => caveat.Contains("un minimum"));
        }

        private static UserDataSurvey Survey(FakeFileSystemGateway gateway, SystemSnapshot? snapshot = null)
            => new UserDataSurveyor(gateway, new InterventionJournal(path: null), NullLogger.Instance)
                .Run(snapshot, null, CancellationToken.None);

        /// <summary>Deux fichiers de 150 octets à la racine, un de 300 dans un sous-dossier.</summary>
        private static string TemporaryTree()
        {
            var root = Path.Combine(Path.GetTempPath(), "ldi12-donnees-" + Guid.NewGuid().ToString("N"));
            var child = Path.Combine(root, "sous-dossier");

            Directory.CreateDirectory(child);
            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[150]);
            File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[150]);
            File.WriteAllBytes(Path.Combine(child, "c.bin"), new byte[300]);

            return root;
        }
    }
}
