using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Platform;
using LDI12.Platform.Detection;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Tests d'intégration sur la machine courante. Ils ne vérifient pas une version précise de
    /// Windows : ils vérifient que la détection produit toujours un résultat cohérent et motivé,
    /// quelle que soit la machine qui exécute la suite.
    /// </summary>
    public class PlatformDetectionTests
    {
        [Fact]
        public void La_detection_produit_un_profil_coherent()
        {
            var profile = new WindowsProfileProvider(new RegistryGateway(NullLogger.Instance), NullLogger.Instance).Detect();

            Assert.NotEqual(WindowsFamily.Unknown, profile.Family);
            Assert.True(profile.Build > 0, "Le numéro de build doit toujours être déterminé.");
            Assert.NotEqual(ProcessorArchitecture.Unknown, profile.NativeArchitecture);
            Assert.False(string.IsNullOrWhiteSpace(profile.LevelReason),
                "Le niveau de compatibilité doit toujours être motivé : c'est ce texte qui s'affiche au technicien.");
            Assert.Contains("Windows", profile.DisplayName);
        }

        [Fact]
        public void Windows_11_n_est_jamais_confondu_avec_Windows_10()
        {
            var profile = new WindowsProfileProvider(new RegistryGateway(NullLogger.Instance), NullLogger.Instance).Detect();
            if (profile.Version.Major != 10) return;   // machine antérieure : rien à vérifier

            // ProductName affiche encore « Windows 10 » sur Windows 11 : seul le build fait foi.
            var expected = profile.Build >= 22000 ? WindowsFamily.Windows11 : WindowsFamily.Windows10;
            Assert.Equal(expected, profile.Family);
        }

        [Fact]
        public void L_edition_est_traduite_pour_le_rapport()
        {
            // « Core » est l'identifiant de registre de l'édition Famille : un rapport client ne
            // peut pas afficher « Windows 11 Core ».
            var profile = new WindowsProfile { EditionId = "Core" };
            Assert.Equal("Famille", profile.EditionLabel);

            Assert.Equal("Professionnel", new WindowsProfile { EditionId = "Professional" }.EditionLabel);
            Assert.Equal("Édition Intégrale", new WindowsProfile { EditionId = "Ultimate" }.EditionLabel);
            Assert.Null(new WindowsProfile { EditionId = null }.EditionLabel);

            // Une édition inconnue est reprise telle quelle plutôt que masquée.
            Assert.Equal("EditionInconnue", new WindowsProfile { EditionId = "EditionInconnue" }.EditionLabel);
        }

        [Fact]
        public async Task Chaque_fonctionnalite_sondee_porte_une_justification()
        {
            using var services = await PlatformServices.CreateAsync(NullLogger.Instance, CancellationToken.None);

            Assert.NotEmpty(services.Platform.Features.All);
            foreach (var feature in services.Platform.Features.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(feature.DisplayName), feature.Id + " : libellé manquant.");
                Assert.False(string.IsNullOrWhiteSpace(feature.Reason),
                    feature.Id + " : une fonctionnalité doit toujours dire pourquoi elle est dans cet état, " +
                    "y compris quand elle est disponible.");
                Assert.NotEqual(Availability.Unknown, feature.Availability);
            }
        }

        [Fact]
        public async Task Une_fonctionnalite_indisponible_propose_un_repli_ou_l_assume()
        {
            using var services = await PlatformServices.CreateAsync(NullLogger.Instance, CancellationToken.None);

            foreach (var feature in services.Platform.Features.All)
            {
                if (feature.Availability == Availability.Available) continue;

                // Soit il existe un repli documenté, soit la raison explique l'absence.
                // Dans les deux cas le technicien sait à quoi s'en tenir.
                Assert.True(
                    !string.IsNullOrWhiteSpace(feature.Workaround) || feature.Reason.Length > 20,
                    feature.Id + " : ni repli ni explication suffisante.");
            }
        }
    }
}
