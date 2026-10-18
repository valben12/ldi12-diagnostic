using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LDI12.Core.DiskUsage;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>L'analyse du disque : exploration, totaux, types de fichiers, carte.</summary>
    public class DiskUsageTests
    {
        [Fact]
        public void L_arbre_porte_les_totaux_et_trie_du_plus_lourd_au_plus_leger()
        {
            var tree = Scan(new FakeDisk()
                .File(@"R\a.mp4", 5000)
                .File(@"R\Docs\lettre.docx", 100)
                .File(@"R\Docs\photo.JPG", 900)
                .File(@"R\Docs\Vieux\scan.jpg", 300));

            Assert.Equal(6300, tree.Root.Size);
            Assert.Equal(4, tree.Root.Files);
            Assert.Equal(3, tree.Directories);
            Assert.Equal(new[] { "a.mp4", "Docs" }, tree.Root.Children.Select(c => c.Name));

            var docs = tree.Root.Children[1];
            Assert.Equal(1300, docs.Size);
            Assert.Equal(new[] { "photo.JPG", "Vieux", "lettre.docx" }, docs.Children.Select(c => c.Name));
            Assert.Equal(Path.Combine("R", "Docs", "Vieux", "scan.jpg"), docs.Children[1].Children[0].FullPath);
        }

        [Fact]
        public void Les_types_de_fichiers_se_regroupent_sans_tenir_compte_de_la_casse()
        {
            var tree = Scan(new FakeDisk()
                .File(@"R\a.mp4", 5000)
                .File(@"R\b.JPG", 900)
                .File(@"R\c.jpg", 300)
                .File(@"R\LISEZMOI", 10));

            var ranked = tree.RankedExtensions();
            Assert.Equal(new[] { ".mp4", ".jpg", "" }, ranked.Select(e => e.Extension));
            Assert.Equal(1200, ranked[1].Bytes);
            Assert.Equal(2, ranked[1].Files);
            Assert.Equal("Vidéo", ranked[0].Kind);
            Assert.Equal(0, ranked[0].Color);
            Assert.Equal(1, ranked[1].Color);
        }

        [Fact]
        public void Jonctions_et_fichiers_dans_le_nuage_ne_comptent_pas()
        {
            var disk = new FakeDisk()
                .File(@"R\vrai.bin", 1000)
                .File(@"R\OneDrive\photo.jpg", 4000, cloudOnly: true)
                .Junction(@"R\Documents and Settings");

            var tree = Scan(disk);

            Assert.Equal(1000, tree.Root.Size);
            Assert.DoesNotContain(tree.Root.Children, c => c.Name == "Documents and Settings");
        }

        [Fact]
        public void Un_dossier_illisible_est_signale_sans_arreter_l_analyse()
        {
            var disk = new FakeDisk().File(@"R\a.txt", 10).File(@"R\Secret\b.txt", 99);
            disk.Denied.Add(@"R\Secret");

            var tree = Scan(disk);

            Assert.Equal(1, tree.UnreadableDirectories);
            Assert.True(tree.Root.Children.Single(c => c.Name == "Secret").Unreadable);
            Assert.Equal(10, tree.Root.Size);
        }

        [Fact]
        public void Une_erreur_imprevue_sur_un_dossier_ne_fait_pas_tomber_l_analyse()
        {
            var disk = new FakeDisk().File(@"R\a.txt", 10).File(@"R\Bizarre\b.txt", 99).File(@"R\Autre\c.txt", 5);
            disk.Throwing.Add(@"R\Bizarre");

            var tree = Scan(disk);

            Assert.Equal(1, tree.UnreadableDirectories);
            Assert.True(tree.Root.Children.Single(c => c.Name == "Bizarre").Unreadable);
            Assert.Equal(15, tree.Root.Size);
        }

        [Fact]
        public void Retirer_un_element_met_a_jour_la_lignee_et_les_types()
        {
            var tree = Scan(new FakeDisk()
                .File(@"R\A\gros.iso", 8000)
                .File(@"R\A\petit.txt", 10)
                .File(@"R\B\moyen.iso", 3000));

            var iso = tree.Root.Children[0].Children[0];
            Assert.True(tree.Remove(iso));

            Assert.Equal(3010, tree.Root.Size);
            Assert.Equal("B", tree.Root.Children[0].Name);
            Assert.Equal(3000, tree.RankedExtensions().Single(e => e.Extension == ".iso").Bytes);
        }

        [Fact]
        public void Les_plus_gros_fichiers_sortent_dans_l_ordre()
        {
            var disk = new FakeDisk();
            for (var index = 1; index <= 50; index++) disk.File(@"R\D" + (index % 7) + @"\f" + index + ".bin", index * 10);

            var largest = Scan(disk).LargestFiles(5);

            Assert.Equal(new long[] { 500, 490, 480, 470, 460 }, largest.Select(f => f.Size));
        }

        [Fact]
        public void La_carte_couvre_toute_la_surface_et_retrouve_chaque_fichier()
        {
            var disk = new FakeDisk();
            var random = new Random(12);
            for (var index = 0; index < 400; index++)
                disk.File(@"R\D" + random.Next(6) + @"\S" + random.Next(4) + @"\f" + index + (index % 3 == 0 ? ".jpg" : ".dat"), 1 + random.Next(100000));
            var tree = Scan(disk);

            // Sans liserés entre dossiers, aucun pixel orphelin : chaque point appartient à un fichier ou à son dossier.
            var image = TreemapRenderer.Render(tree.Root, tree, 320, 200, new TreemapStyle { GapDepth = 0 });

            var orphan = 0;
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    if (image.HitTest(x, y) == null) orphan++;
            Assert.Equal(0, orphan);

            var biggest = tree.LargestFiles(1)[0];
            Assert.True(image.TryGetBounds(biggest, out var item));
            Assert.Same(biggest, image.HitTest(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
        }

        [Fact]
        public void Le_decoupage_respecte_les_proportions()
        {
            var tree = Scan(new FakeDisk().File(@"R\a", 600).File(@"R\b", 300).File(@"R\c", 100));

            var items = TreemapRenderer.Squarify(tree.Root.Children, new TreemapRect(0, 0, 100, 100));

            Assert.Equal(3, items.Count);
            Assert.Equal(6000, items[0].Bounds.Area, 3);
            Assert.Equal(3000, items[1].Bounds.Area, 3);
            Assert.Equal(1000, items[2].Bounds.Area, 3);
            Assert.Equal(10000, items.Sum(i => i.Bounds.Area), 3);
        }

        [Fact]
        public void La_lecture_reelle_du_disque_pese_un_vrai_dossier()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-disque-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "sous"));
            try
            {
                File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1234]);
                File.WriteAllBytes(Path.Combine(root, "sous", "b.bin"), new byte[766]);

                var tree = new DiskUsageScanner(ManagedDirectoryLister.Instance).Scan(root);

                Assert.Equal(2000, tree.Root.Size);
                Assert.Equal(2, tree.Root.Files);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Le_systeme_ne_part_jamais_a_la_corbeille_depuis_la_carte()
        {
            var tree = Scan(new FakeDisk("C:")
                .File(@"C:\Windows\System32\ntoskrnl.exe", 10)
                .File(@"C:\Windows\Temp\vieux.tmp", 10)
                .File(@"C:\hiberfil.sys", 10)
                .File(@"C:\Users\Marie\Videos\film.mkv", 10), "C:");

            DiskNode Find(params string[] names)
            {
                var node = tree.Root;
                foreach (var name in names) node = node.Children.Single(c => c.Name == name);
                return node;
            }

            Assert.NotNull(DiskUsageAdvisor.Refusal(tree.Root));
            Assert.NotNull(DiskUsageAdvisor.Refusal(Find("Windows", "System32", "ntoskrnl.exe")));
            Assert.NotNull(DiskUsageAdvisor.Refusal(Find("Windows")));
            Assert.Contains("powercfg", DiskUsageAdvisor.Refusal(Find("hiberfil.sys")));
            Assert.NotNull(DiskUsageAdvisor.Refusal(Find("Users", "Marie")));

            Assert.Null(DiskUsageAdvisor.Refusal(Find("Windows", "Temp", "vieux.tmp")));
            Assert.Null(DiskUsageAdvisor.Refusal(Find("Users", "Marie", "Videos", "film.mkv")));
        }

        [Fact]
        public void Les_pistes_signalent_les_gros_emplacements_connus()
        {
            const long Go = 1024L * 1024 * 1024;
            var tree = Scan(new FakeDisk("C:")
                .File(@"C:\Windows.old\Windows\a.dll", 12 * Go)
                .File(@"C:\Users\Marie\Downloads\setup.exe", 2 * Go)
                .File(@"C:\Users\Marie\AppData\Local\Temp\x.tmp", 10), "C:");

            var hints = DiskUsageAdvisor.Hints(tree);

            Assert.Equal(new[] { "Ancienne installation de Windows", "Téléchargements de Marie" }, hints.Select(h => h.Title));
        }

        private static DiskUsageTree Scan(FakeDisk disk, string root = "R") => new DiskUsageScanner(disk, workers: 4).Scan(root);

        /// <summary>Un disque en mémoire : chemins relatifs à « R », séparés par des barres obliques inverses.</summary>
        private sealed class FakeDisk : IDirectoryLister
        {
            private readonly Dictionary<string, List<DiskEntry>> _directories = new Dictionary<string, List<DiskEntry>>(StringComparer.OrdinalIgnoreCase);
            private readonly string _root;

            public FakeDisk(string root = "R") => _root = root;

            public HashSet<string> Denied { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Des dossiers dont la lecture lève une exception, comme un nom que le framework refuse.</summary>
            public HashSet<string> Throwing { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public FakeDisk File(string path, long size, bool cloudOnly = false)
            {
                var parent = EnsureDirectory(Parent(path));
                parent.Add(new DiskEntry { Name = Name(path), Size = size, IsCloudOnly = cloudOnly, LastWriteUtcTicks = DateTime.UtcNow.Ticks });
                return this;
            }

            public FakeDisk Junction(string path)
            {
                EnsureDirectory(Parent(path)).Add(new DiskEntry { Name = Name(path), IsDirectory = true, IsReparsePoint = true });
                return this;
            }

            public bool TryList(string path, List<DiskEntry> into)
            {
                var key = Normalize(path);
                if (Denied.Contains(key)) return false;
                if (Throwing.Contains(key)) throw new ArgumentException("Caractères non conformes dans le chemin.");
                if (_directories.TryGetValue(key, out var entries)) into.AddRange(entries);
                return true;
            }

            private List<DiskEntry> EnsureDirectory(string path)
            {
                if (_directories.TryGetValue(path, out var entries)) return entries;
                entries = new List<DiskEntry>();
                _directories[path] = entries;
                if (path != _root) EnsureDirectory(Parent(path)).Add(new DiskEntry { Name = Name(path), IsDirectory = true });
                return entries;
            }

            // Path.Combine("C:", "Windows") donne « C:Windows » sous Windows, « C:/Windows » ailleurs.
            private static string Normalize(string path)
            {
                path = path.Replace('/', '\\').Replace(@"\\", @"\");
                return path.Length > 2 && path[1] == ':' && path[2] != '\\' ? path.Insert(2, @"\") : path;
            }

            private static string Parent(string path) => path.Substring(0, path.LastIndexOf('\\'));

            private static string Name(string path) => path.Substring(path.LastIndexOf('\\') + 1);
        }
    }
}
