using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
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
    /// Une vraie sauvegarde, puis une vraie restauration, sur le disque de la machine de test.
    /// </summary>
    /// <remarks>
    /// Aucune simulation : le système de fichiers, les processus, le registre et le chiffrement
    /// sont ceux de Windows. C'est ce que fait le technicien chez le client, sur un jeu de fichiers
    /// choisi pour piéger une copie : noms accentués, fichier vide, tailles aux bornes des blocs,
    /// chemin de plus de 260 caractères, fichier en lecture seule et caché, centaines de petits
    /// fichiers, fichier tenu ouvert. Chaque fichier revenu est comparé à l'original, empreinte
    /// et date.
    /// </remarks>
    public class RealRoundTripTests
    {
        [WindowsFact]
        public async Task Une_sauvegarde_interrompue_puis_reprise_puis_restauree_rend_chaque_fichier_a_l_identique()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-aller-retour-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var source = Path.Combine(root, "Données client");
            var support = Path.Combine(root, "Support");
            var newProfile = Path.Combine(root, "Nouveau PC");
            Directory.CreateDirectory(support);

            try
            {
                var original = Populate(source);
                var locked = Path.Combine(source, "Messagerie", "archive.pst");

                var context = Context(new Dictionary<string, string>
                {
                    [BackupUserDataAction.DestinationParameter] = support,
                    [BackupUserDataAction.PersonalParameter] = "0",
                    [BackupUserDataAction.ApplicationsParameter] = "0",
                    [BackupUserDataAction.ExtraFoldersParameter] = ExtraFolders.Encode(new[] { source }),
                });

                var action = new BackupUserDataAction();

                // Premier passage, l'archive tenue ouverte comme par Outlook.
                ActionOutcome first;
                BackupPlan plan;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var preview = await action.PreviewAsync(context, CancellationToken.None);
                    Assert.True(preview.CanExecute, preview.Blocker ?? preview.Summary);
                    plan = Assert.IsType<BackupPlan>(preview.Plan);
                    first = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

                    Assert.Single(plan.OpenFiles);

                    // Le fichier ouvert récupéré par un cliché instantané, quand la machine le permet.
                    if (IsAdministrator())
                    {
                        var shadowContext = Context(new Dictionary<string, string>
                        {
                            [OpenFilesAction.DestinationParameter] = plan.Destination,
                            [OpenFilesAction.FilesParameter] = OpenFilesAction.Encode(plan.OpenFiles),
                        });
                        var shadow = new OpenFilesAction();
                        var copied = await shadow.ExecuteAsync(shadowContext,
                            await shadow.PreviewAsync(shadowContext, CancellationToken.None), null, CancellationToken.None);
                        Assert.True(copied.Status == ActionStatus.Succeeded, copied.Summary + " " + string.Join(" ", copied.Details));
                    }
                }

                Assert.NotEqual(ActionStatus.Failed, first.Status);
                var backup = plan.Destination;
                var copy = Path.Combine(backup, ExtraFolders.Folder, "Données client");

                foreach (var file in new[] { "manifeste.csv", ReinstallSheet.FileName, ReinstallSheet.ReportFileName, BackupState.FileName })
                    Assert.True(File.Exists(Path.Combine(backup, file)), file + " manque dans la sauvegarde.");
                Assert.Contains("terminée", File.ReadAllText(Path.Combine(backup, BackupState.FileName)));
                Assert.Empty(Directory.GetFiles(Extended(backup), "*" + FileCopyRequest.PartialSuffix, SearchOption.AllDirectories));

                if (!IsAdministrator()) File.Copy(Extended(locked), Extended(Path.Combine(copy, "Messagerie", "archive.pst")));
                Same(original, copy);

                // Le support arraché au milieu : la sauvegarde est restée « en cours », deux fichiers manquent.
                File.WriteAllText(Path.Combine(backup, BackupState.FileName),
                    BackupState.Render(BackupProgressState.Running, plan.Machine, plan.Account));
                File.Delete(Extended(Path.Combine(copy, "Été 2024 — n°1.txt")));
                File.Delete(Extended(Path.Combine(copy, "Gros", "neuf-mo.bin")));

                var again = await action.PreviewAsync(context, CancellationToken.None);
                var resumed = Assert.IsType<BackupPlan>(again.Plan);
                Assert.True(resumed.Resumed);
                Assert.Equal(backup, resumed.Destination);
                var second = await action.ExecuteAsync(context, again, null, CancellationToken.None);
                Assert.Equal(ActionStatus.Succeeded, second.Status);
                Same(original, copy);
                Assert.Single(Directory.GetDirectories(support));

                // Le nouveau PC : la source n'existe plus, tout revient de la sauvegarde.
                Directory.Delete(Extended(source), recursive: true);
                var targets = new RestoreTargets
                {
                    Desktop = Path.Combine(newProfile, "Desktop"),
                    Documents = Path.Combine(newProfile, "Documents"),
                    Pictures = Path.Combine(newProfile, "Pictures"),
                    Music = Path.Combine(newProfile, "Music"),
                    Videos = Path.Combine(newProfile, "Videos"),
                    UserProfile = newProfile,
                    LocalAppData = Path.Combine(newProfile, @"AppData\Local"),
                    RoamingAppData = Path.Combine(newProfile, @"AppData\Roaming"),
                };
                Directory.CreateDirectory(newProfile);

                var restoreContext = Context(new Dictionary<string, string> { [RestoreUserDataAction.SourceParameter] = support });
                var restore = new RestoreUserDataAction(targets);
                var restorePreview = await restore.PreviewAsync(restoreContext, CancellationToken.None);
                Assert.True(restorePreview.CanExecute, restorePreview.Blocker ?? restorePreview.Summary);
                var restored = await restore.ExecuteAsync(restoreContext, restorePreview, null, CancellationToken.None);
                Assert.True(restored.Status == ActionStatus.Succeeded, restored.Summary + " " + string.Join(" ", restored.Details));

                // La sauvegarde est sur le même disque que le dossier d'origine : il revient dans les Documents.
                var back = Path.Combine(targets.Documents, ExtraFolders.Folder, "Données client");
                Same(original, back);

                // Un fichier caché revient caché : un desktop.ini ne s'affiche pas sur le Bureau du client.
                var attributes = File.GetAttributes(Extended(Path.Combine(back, "caché en lecture seule.ini")));
                Assert.True((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.ReadOnly) != 0, attributes.ToString());

                // Relancer la restauration ne change rien et n'écrase rien.
                var twice = await restore.ExecuteAsync(restoreContext,
                    await restore.PreviewAsync(restoreContext, CancellationToken.None), null, CancellationToken.None);
                Assert.Equal(ActionStatus.Succeeded, twice.Status);
            }
            finally
            {
                try { Directory.Delete(Extended(root), recursive: true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        /// <summary>Le jeu de fichiers, et l'empreinte et la date de chacun, par chemin relatif.</summary>
        private static Dictionary<string, (string Hash, DateTime Written)> Populate(string source)
        {
            var random = new Random(12);
            void Write(string relative, int size)
            {
                var path = Extended(Path.Combine(source, relative));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = new byte[size];
                random.NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, new DateTime(2023, 5, 17, 9, 30, 0, DateTimeKind.Utc).AddMinutes(size % 997));
            }

            Write("Été 2024 — n°1.txt", 2_000);
            Write("vide.txt", 0);
            Write("un-octet.bin", 1);
            Write(@"Gros\un-mo.bin", 1024 * 1024);
            Write(@"Gros\un-mo-plus-un.bin", 1024 * 1024 + 1);
            Write(@"Gros\neuf-mo.bin", 9 * 1024 * 1024 + 123);
            Write(@"Messagerie\archive.pst", 300_000);
            for (var index = 0; index < 300; index++) Write(@"Petits\fichier-" + index.ToString("000") + ".txt", 100 + index);

            // Plus de 260 caractères : la limite historique de Windows.
            var deep = string.Join("\\", Enumerable.Repeat("dossier-assez-long-pour-depasser", 8)) + @"\profond.docx";
            Write(deep, 5_000);

            var hidden = Extended(Path.Combine(source, "caché en lecture seule.ini"));
            File.WriteAllText(hidden, "réglage");
            File.SetAttributes(hidden, FileAttributes.ReadOnly | FileAttributes.Hidden);

            return Fingerprints(source);
        }

        private static Dictionary<string, (string Hash, DateTime Written)> Fingerprints(string folder)
        {
            var result = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
            var prefix = Extended(folder).TrimEnd('\\') + "\\";
            using var sha = SHA256.Create();
            foreach (var path in Directory.GetFiles(Extended(folder), "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(path);
                result[path.Substring(prefix.Length)] = (Convert.ToBase64String(sha.ComputeHash(stream)), File.GetLastWriteTimeUtc(path));
            }

            return result;
        }

        private static void Same(Dictionary<string, (string Hash, DateTime Written)> original, string copy)
        {
            var actual = Fingerprints(copy);
            Assert.Equal(original.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
            foreach (var pair in original)
            {
                Assert.True(pair.Value.Hash == actual[pair.Key].Hash, pair.Key + " : contenu différent.");
                Assert.True(Math.Abs((pair.Value.Written - actual[pair.Key].Written).TotalSeconds) <= 2, pair.Key + " : date différente.");
            }
        }

        private static ActionContext Context(IReadOnlyDictionary<string, string> parameters)
            => new ActionContext(
                new FakePlatformInfo(elevated: IsAdministrator()),
                new ProcessRunner(NullLogger.Instance),
                new FakeProcessLauncher(),
                new FileSystemGateway(NullLogger.Instance),
                new RegistryGateway(NullLogger.Instance),
                new FakeSystemRestoreGateway(),
                NullLogger.Instance,
                null,
                parameters,
                new DpapiSecretProtector());

        private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Ne tourne que sous Windows : c'est son système de fichiers qu'on éprouve.</summary>
        private sealed class WindowsFactAttribute : FactAttribute
        {
            public WindowsFactAttribute()
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) Skip = "Éprouve le système de fichiers de Windows.";
            }
        }
    }
}
