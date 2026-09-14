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
    /// <summary>
    /// La barre d'avancement et le temps restant d'une copie.
    /// </summary>
    /// <remarks>
    /// Demandé depuis l'atelier, pendant une copie chez un client : une barre, une estimation du
    /// temps restant, et rien d'autre à l'écran tant que ça copie.
    /// </remarks>
    public class CopyProgressTests
    {
        private const long Mo = 1024 * 1024;

        [Fact]
        public void Une_video_de_plusieurs_gigaoctets_fait_avancer_la_barre_pendant_sa_copie()
        {
            // Compté entre deux fichiers, l'avancement resterait figé deux minutes sur ce fichier.
            var clock = new Clock();
            var reports = new List<ActionProgress>();
            var progress = new CopyProgress(new Sink(reports), 4096 * Mo, 1, "Copie de", clock.Now);
            progress.Folder("Vidéos");

            for (var i = 0; i < 100; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                progress.Bytes(40 * Mo);
            }

            Assert.True(progress.Fraction > 0.45 && progress.Fraction < 0.5, "Fraction : " + progress.Fraction);
            Assert.True(reports.Count > 50, "Rapports pendant la copie : " + reports.Count);
            Assert.Equal("Copie de « Vidéos »", reports.Last().Text);
            Assert.Contains("sur 4 Go", reports.Last().Detail);
        }

        [Fact]
        public void Le_temps_restant_n_est_pas_annonce_avant_d_avoir_mesure()
        {
            var clock = new Clock();
            var progress = new CopyProgress(null, 1000 * Mo, 10, "Copie de", clock.Now);

            clock.Advance(TimeSpan.FromSeconds(2));
            progress.Bytes(100 * Mo);

            Assert.Null(progress.Remaining());
        }

        /// <summary>
        /// Relevés de l'essai réel : secondes, fichiers traités, mégaoctets écrits. Fin à 201 s.
        /// </summary>
        /// <remarks>
        /// Un profil Chrome : des milliers de petits fichiers, puis quelques gros. La première
        /// estimation, sur un débit de trente secondes, y annonçait trente et une minutes à 91 s.
        /// </remarks>
        private static readonly (int T, int Files, double Mb)[] RealTrial =
        {
            (5, 448, 113.6), (11, 892, 116.3), (16, 1301, 119.5), (21, 1823, 180.1), (27, 2228, 233.5),
            (32, 2586, 270.9), (37, 3015, 289.6), (42, 3429, 293.1), (48, 3858, 301.2), (53, 4413, 321.4),
            (58, 4547, 349.5), (64, 4856, 353.3), (70, 4975, 358.9), (75, 5154, 359.3), (80, 5268, 362),
            (86, 5358, 362.6), (91, 5513, 364.2), (96, 5607, 369), (102, 5693, 372.1), (107, 5807, 375.9),
            (112, 5894, 377.6), (118, 5961, 381.9), (124, 6030, 460.7), (131, 6073, 599.9), (137, 6093, 639.3),
            (143, 6162, 659.4), (149, 6241, 666), (155, 6312, 678.7), (161, 6408, 700.7), (167, 6501, 721.9),
            (172, 6540, 837.2), (178, 6587, 968.4), (183, 6698, 996.7), (188, 6831, 1008.8), (193, 7122, 1030),
        };

        [Fact]
        public void Le_temps_restant_d_une_vraie_copie_ne_s_affole_pas_sur_les_petits_fichiers()
        {
            var clock = new Clock();
            var progress = new CopyProgress(null, (long)(1100 * Mo), 7519, "Copie de", clock.Now);
            var errors = new List<double>();
            int files = 0;
            double mb = 0;

            foreach (var (t, rowFiles, rowMb) in RealTrial)
            {
                clock.Advance(TimeSpan.FromSeconds(t) - clock.Now());
                progress.Bytes((long)((rowMb - mb) * 2 * Mo));
                for (; files < rowFiles; files++) progress.FileDone(0);
                mb = rowMb;

                var estimate = progress.Remaining();
                if (estimate == null) continue;

                var real = 201.0 - t;
                errors.Add(Math.Abs(estimate.Value.TotalSeconds - real) / Math.Max(real, 30));

                // Là où l'ancienne estimation annonçait trente et une minutes.
                if (t == 91) Assert.True(estimate.Value < TimeSpan.FromMinutes(5), "À 91 s : " + estimate);
            }

            errors.Sort();
            Assert.True(errors[errors.Count / 2] < 0.6, "Erreur médiane : " + errors[errors.Count / 2]);
            Assert.True(errors[errors.Count - 1] < 2.0, "Pire erreur : " + errors[errors.Count - 1]);
        }

        [Fact]
        public void Les_fichiers_deja_presents_ou_refuses_menent_la_barre_a_cent_pour_cent()
        {
            var progress = new CopyProgress(null, 30 * Mo, 3, "Copie de", new Clock().Now);

            progress.Bytes(20 * Mo);
            progress.FileDone(10 * Mo);   // copié et relu
            progress.FileDone(10 * Mo);   // déjà présent, rien écrit
            progress.Skip(new[] { new FileEntry { SizeBytes = 10 * Mo } });

            Assert.Equal(1, progress.Fraction);
        }

        [Fact]
        public async Task La_sauvegarde_rapporte_son_avancement_en_octets_jusqu_au_bout()
        {
            var desktop = UserDataSurveyor.PersonalFolders().First(folder => folder.Label == "Bureau").Path;
            var files = new FakeFileSystemGateway()
                .WithFile(desktop, Path.Combine(desktop, "video.mp4"), 900 * Mo)
                .WithFile(desktop, Path.Combine(desktop, "note.txt"), 1024);

            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [BackupUserDataAction.DestinationParameter] = @"E:\Sauvegarde",
                [BackupUserDataAction.ApplicationsParameter] = "0",
            });

            var reports = new List<ActionProgress>();
            var action = new BackupUserDataAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            await action.ExecuteAsync(context, preview, new Sink(reports), CancellationToken.None);

            var copying = reports.Where(report => report.Detail != null).ToList();
            Assert.NotEmpty(copying);
            Assert.Equal(1, copying.Last().Fraction);
            Assert.Contains("2 fichier(s) sur 2", copying.Last().Detail);
        }

        [Fact]
        public void La_vraie_copie_remonte_l_ecriture_puis_la_relecture()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-avancement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "source.bin");
                File.WriteAllBytes(source, new byte[3 * Mo + 17]);
                var info = new FileInfo(source);

                long counted = 0;
                var result = new FileSystemGateway(NullLogger.Instance).Copy(
                    new FileCopyRequest(new FileEntry { Path = source, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc },
                        Path.Combine(root, "copie.bin")) { Progressed = bytes => counted += bytes },
                    CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
                Assert.Equal(info.Length * 2, counted);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        private sealed class Clock
        {
            private TimeSpan _now;
            public TimeSpan Now() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        /// <summary>Synchrone, contrairement à Progress&lt;T&gt; qui poste sur un contexte.</summary>
        private sealed class Sink : IProgress<ActionProgress>
        {
            private readonly List<ActionProgress> _reports;
            public Sink(List<ActionProgress> reports) => _reports = reports;
            public void Report(ActionProgress value) => _reports.Add(value);
        }
    }
}
