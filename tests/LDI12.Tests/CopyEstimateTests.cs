using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>La durée d'une sauvegarde, annoncée avant de la lancer.</summary>
    public class CopyEstimateTests
    {
        private static readonly CopySpeed UsbKey = new CopySpeed
        {
            WriteBytesPerSecond = 20e6,
            ReadBytesPerSecond = 30e6,
            PerFile = TimeSpan.FromMilliseconds(10),
        };

        private static string Desktop =>
            UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;

        [Fact]
        public void La_duree_compte_l_ecriture_la_relecture_et_chaque_fichier()
        {
            // 100 Go sur une clé à 20 Mo/s en écriture et 30 en relecture, et 50 000 fichiers :
            // 5 000 s + 3 333 s + 500 s.
            var duration = CopyEstimate.Duration(100_000_000_000, 50_000, UsbKey);

            Assert.InRange(duration.TotalSeconds, 8_830, 8_836);
        }

        [Theory]
        [InlineData(30, "moins d'une minute")]
        [InlineData(7 * 60, "7 min")]
        [InlineData(37 * 60, "35 min")]
        [InlineData(58 * 60, "1 h")]
        [InlineData(3 * 3600 + 38 * 60, "3 h 40")]
        [InlineData(2 * 3600 + 2 * 60, "2 h")]
        public void La_duree_s_arrondit_comme_on_la_dirait(int seconds, string expected)
            => Assert.Equal(expected, CopyEstimate.Round(TimeSpan.FromSeconds(seconds)));

        [Fact]
        public void La_duree_s_annonce_avec_sa_fourchette()
            => Assert.Equal("environ 1 h 40 (entre 1 h 10 et 2 h 20)", CopyEstimate.Describe(TimeSpan.FromMinutes(100)));

        [Fact]
        public void Un_support_lent_est_signale_avec_ce_qu_il_faut_faire()
        {
            Assert.Contains("USB 3", CopyEstimate.Advice(UsbKey));
            Assert.Null(CopyEstimate.Advice(new CopySpeed { WriteBytesPerSecond = 400e6, ReadBytesPerSecond = 450e6 }));
        }

        [Fact]
        public void La_mesure_traverse_les_parametres_sans_perte()
        {
            var decoded = CopySpeed.Decode(UsbKey.Encode());

            Assert.NotNull(decoded);
            Assert.Equal(UsbKey.WriteBytesPerSecond, decoded!.WriteBytesPerSecond);
            Assert.Equal(UsbKey.PerFile, decoded.PerFile);
            Assert.Null(CopySpeed.Decode("n'importe quoi"));
            Assert.Null(CopySpeed.Decode("0;0;0"));
        }

        [Fact]
        public async Task La_preparation_annonce_la_duree_quand_le_support_a_ete_mesure()
        {
            var preview = await Preview(UsbKey);

            Assert.Contains(preview.Measurements, line => line.Label == "Durée estimée" && line.Value.StartsWith("environ"));
            Assert.Contains(preview.Measurements, line => line.Label == "Support lent" && line.Kind == PreviewLineKind.Caution);
            Assert.Contains("Durée estimée : environ", preview.Summary);
        }

        [Fact]
        public async Task Sans_mesure_la_preparation_dit_comment_obtenir_la_duree()
        {
            var preview = await Preview(null);

            var line = Assert.Single(preview.Measurements, m => m.Label == "Durée estimée");
            Assert.Contains("pas encore été mesuré", line.Value);
        }

        // ============================================================ mesure réelle

        [Fact]
        public void La_mesure_d_un_support_aboutit_et_ne_laisse_rien_derriere_elle()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-vitesse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var speed = new CopySpeedProbe(NullLogger.Instance).Measure(root, null, CancellationToken.None);

                Assert.True(speed.IsValid, speed.Failure);
                Assert.True(speed.WriteBytesPerSecond > 0);
                Assert.True(speed.ReadBytesPerSecond > 0);
                Assert.Empty(Directory.GetFileSystemEntries(root));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        // ============================================================ restauration

        [Fact]
        public void La_restauration_va_au_rythme_du_plus_lent_des_deux_disques()
        {
            // Sauvegarde lue à 30 Mo/s, machine qui écrit à 500 et relit à 1 000 : c'est la clé
            // qui fixe le rythme. 30 Go et 10 000 fichiers : 1 000 s + 30 s + 10 000 × 3 ms.
            var source = new ReadSpeed { ReadBytesPerSecond = 30e6, PerFile = TimeSpan.FromMilliseconds(2) };
            var machine = new CopySpeed
            {
                WriteBytesPerSecond = 500e6,
                ReadBytesPerSecond = 1000e6,
                PerFile = TimeSpan.FromMilliseconds(1),
            };

            var duration = CopyEstimate.RestoreDuration(30_000_000_000, 10_000, source, machine);

            Assert.InRange(duration.TotalSeconds, 1_059, 1_061);
            Assert.Contains("USB 3", CopyEstimate.Advice(source));
        }

        [Fact]
        public void La_mesure_de_la_sauvegarde_traverse_les_parametres_sans_perte()
        {
            var speed = new ReadSpeed { ReadBytesPerSecond = 123.5e6, PerFile = TimeSpan.FromMilliseconds(4.5) };

            var decoded = ReadSpeed.Decode(speed.Encode());

            Assert.Equal(speed.ReadBytesPerSecond, decoded!.ReadBytesPerSecond);
            Assert.Equal(speed.PerFile, decoded.PerFile);
            Assert.Null(ReadSpeed.Decode("1;2;3"));
        }

        [Fact]
        public void La_mesure_d_une_sauvegarde_la_lit_sans_rien_y_changer()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-lecture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Bureau"));

            try
            {
                var random = new Random(5);
                var big = new byte[3 * 1024 * 1024 + 100];
                random.NextBytes(big);
                File.WriteAllBytes(Path.Combine(root, "Bureau", "gros.bin"), big);
                for (var i = 0; i < 20; i++)
                    File.WriteAllBytes(Path.Combine(root, "Bureau", "petit" + i + ".txt"), new byte[1000 + i]);

                var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks).OrderBy(x => x).ToArray();

                var speed = new CopySpeedProbe(NullLogger.Instance).MeasureSource(root, CancellationToken.None);

                Assert.True(speed.IsValid, speed.Failure);
                Assert.True(speed.ReadBytesPerSecond > 0);

                var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks).OrderBy(x => x).ToArray();
                Assert.Equal(before, after);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        private static Task<ActionPreview> Preview(CopySpeed? speed)
        {
            var files = new FakeFileSystemGateway()
                .WithFile(Desktop, Path.Combine(Desktop, "film.mkv"), 4_000_000_000)
                .WithFile(Desktop, Path.Combine(Desktop, "note.txt"), 2048);

            var parameters = new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\",
                [BackupUserDataAction.ApplicationsParameter] = "0",
            };
            if (speed != null) parameters[BackupUserDataAction.SpeedParameter] = speed.Encode();

            return new BackupUserDataAction().PreviewAsync(
                ActionFakes.Context(files: files, parameters: parameters), CancellationToken.None);
        }
    }
}
