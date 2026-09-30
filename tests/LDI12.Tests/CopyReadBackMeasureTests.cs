using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Gateways;
using Xunit;
using Xunit.Abstractions;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que coûte une relecture faite sur le support plutôt que dans le cache de Windows.
    /// </summary>
    /// <remarks>
    /// Une mesure, pas une vérification : elle n'a de sens que sur un vrai support externe, et
    /// ne tourne donc que si la variable <c>LDI12_MESURE_COPIE</c> désigne un dossier sur ce
    /// support, par exemple <c>E:\</c>. Sans elle, le test est annoncé ignoré, et non réussi.
    /// <code>
    /// set LDI12_MESURE_COPIE=E:\
    /// dotnet test tests\LDI12.Tests --filter CopyReadBackMeasureTests --logger "console;verbosity=detailed"
    /// </code>
    /// </remarks>
    public class CopyReadBackMeasureTests
    {
        private const string Variable = "LDI12_MESURE_COPIE";

        private readonly ITestOutputHelper _output;

        public CopyReadBackMeasureTests(ITestOutputHelper output) => _output = output;

        [MeasureFact]
        public void La_relecture_sur_le_support_coute_ce_que_dit_la_mesure()
        {
            var target = Environment.GetEnvironmentVariable(Variable)!;
            var root = Path.Combine(Path.GetTempPath(), "LDI12-mesure-source-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(target, "LDI12-mesure-copie-" + Guid.NewGuid().ToString("N"));

            try
            {
                // Les deux formes d'une vraie sauvegarde : une vidéo, et un profil de navigateur.
                var large = Populate(Path.Combine(root, "gros"), 1, 512 * 1024 * 1024);
                var small = Populate(Path.Combine(root, "petits"), 2000, 24 * 1024);

                foreach (var set in new[] { ("un fichier de 512 Mo", large), ("2 000 fichiers de 24 Ko", small) })
                {
                    // Source lue une fois avant de chronométrer : les deux passages la trouvent
                    // dans le même état, et l'écart ne mesure que la relecture.
                    foreach (var file in set.Item2) File.ReadAllBytes(file.Path);

                    var cached = Run(set.Item2, Path.Combine(destination, "cache"), throughCache: true);
                    var direct = Run(set.Item2, Path.Combine(destination, "support"), throughCache: false);

                    _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0} : relecture par le cache {1:0.0} s, sur le support {2:0.0} s, soit {3:+0;-0} %",
                        set.Item1, cached.TotalSeconds, direct.TotalSeconds,
                        (direct.TotalSeconds / cached.TotalSeconds - 1) * 100));
                }
            }
            finally
            {
                TryDelete(root);
                TryDelete(destination);
            }
        }

        private static TimeSpan Run(IReadOnlyList<FileEntry> files, string destination, bool throughCache)
        {
            var gateway = new FileSystemGateway(NullLogger.Instance) { ReadBackThroughCache = throughCache };
            var stopwatch = Stopwatch.StartNew();

            foreach (var file in files)
            {
                var result = gateway.Copy(
                    new FileCopyRequest(file, Path.Combine(destination, Path.GetFileName(file.Path))),
                    CancellationToken.None);

                Assert.Equal(FileCopyOutcome.Copied, result.Outcome);
            }

            return stopwatch.Elapsed;
        }

        private static IReadOnlyList<FileEntry> Populate(string folder, int count, int size)
        {
            Directory.CreateDirectory(folder);

            var random = new Random(20260930);
            var bytes = new byte[size];
            var entries = new List<FileEntry>(count);

            for (var index = 0; index < count; index++)
            {
                random.NextBytes(bytes);
                var path = Path.Combine(folder, index.ToString("D5", CultureInfo.InvariantCulture) + ".bin");
                File.WriteAllBytes(path, bytes);

                var info = new FileInfo(path);
                entries.Add(new FileEntry { Path = path, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            }

            return entries;
        }

        private static void TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Ignoré, et annoncé comme tel, tant qu'aucun support n'est désigné.</summary>
        private sealed class MeasureFactAttribute : FactAttribute
        {
            public MeasureFactAttribute()
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                    Skip = "Mesure manuelle : définir " + Variable + " sur un dossier du support externe à mesurer.";
            }
        }
    }
}
