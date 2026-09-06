using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Storage
{
    /// <summary>
    /// Ce qui occupe le disque système sans figurer dans aucun explorateur.
    /// </summary>
    /// <remarks>
    /// <b>Le relevé qui transforme « le disque est plein » en quelque chose à faire.</b> Les plus
    /// gros occupants d'un disque système ne sont presque jamais les documents du client : ce
    /// sont des fichiers que Windows crée sans le dire, cachés et protégés, dont la taille se lit
    /// pourtant sans le moindre privilège, l'énumération de la racine d'un volume suffit.
    /// <para>
    /// Rien de ce qui est mesuré ailleurs n'est repris : les fichiers temporaires et la corbeille
    /// relèvent du nettoyage, les documents de l'utilisateur du relevé des données. Ce qui reste
    /// est exactement ce que personne ne regardait.
    /// </para>
    /// </remarks>
    public sealed class SystemSpaceProbe : IDiagnosticProbe
    {
        /// <summary>
        /// Taille à partir de laquelle un fichier posé à la racine mérite d'être nommé.
        /// </summary>
        /// <remarks>
        /// Un giga-octet : en dessous, la racine d'un volume système contient une dizaine de
        /// fichiers de service dont l'énumération n'apprendrait rien. Au-dessus, il n'y a plus
        /// que des choses qu'on a posées là et oubliées, une image disque, une sauvegarde, une
        /// machine virtuelle.
        /// </remarks>
        private const long LooseFileFloor = 1024L * 1024 * 1024;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SystemSpace,
            DisplayName = "Occupation du disque système",
            Category = DiagnosticCategory.Storage,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var root = SystemRoot();
            if (root == null)
                return Task.FromResult(ProbeOutcome.Failed("La racine du volume système n'a pas pu être déterminée."));

            var consumers = new List<SpaceConsumer>();

            AddKnownFiles(consumers, root);
            AddCrashDump(consumers, cancellationToken);
            AddPreviousWindows(consumers, root);
            AddLooseFiles(consumers, root, cancellationToken);

            context.Draft.SetSystemSpace(new SystemSpaceSnapshot
            {
                Consumers = consumers,
                Volume = root,
            });

            return Task.FromResult(Summarize(consumers));
        }

        private static ProbeOutcome Summarize(IReadOnlyList<SpaceConsumer> consumers)
        {
            if (consumers.Count == 0)
                return ProbeOutcome.Ok("Aucun occupant notable en dehors de ce qui est déjà relevé ailleurs.");

            var total = 0L;
            var parts = new List<string>();

            foreach (var consumer in consumers)
            {
                if (!consumer.Bytes.HasValue) continue;
                total += consumer.Bytes.Value;
                parts.Add(consumer.Name + " " + ValueFormat.Bytes(consumer.Bytes.Value));
            }

            return ProbeOutcome.Ok(
                ValueFormat.Bytes(total) + " relevés : " + string.Join(", ", parts));
        }

        // ================================================================= les fichiers de Windows

        /// <summary>
        /// Les trois fichiers que Windows pose à la racine et que personne ne voit.
        /// </summary>
        /// <remarks>
        /// Ils sont cachés et protégés : un explorateur ne les montre pas, et un client qui
        /// cherche ce qui remplit son disque ne les trouvera jamais. Leur taille, elle, se lit
        /// sans privilège : l'énumération du répertoire suffit, sans jamais ouvrir les fichiers.
        /// </remarks>
        private static void AddKnownFiles(ICollection<SpaceConsumer> consumers, string root)
        {
            Add(consumers, root, "hiberfil.sys", SpaceKind.Hibernation,
                "Mémoire de la machine, recopiée sur le disque pour la mise en veille prolongée. " +
                "Sa taille suit celle de la mémoire installée.",
                SpaceReclaim.Setting,
                "Désactiver la mise en veille prolongée libère ce fichier, mais retire aussi le démarrage " +
                "rapide : la machine s'éteindra et redémarrera vraiment, donc plus lentement.");

            Add(consumers, root, "pagefile.sys", SpaceKind.PageFile,
                "Extension de la mémoire sur le disque. Windows en fixe la taille tout seul, en " +
                "fonction de la mémoire installée et de la place disponible.",
                SpaceReclaim.Setting,
                "Se réduit dans les paramètres système avancés. À ne faire qu'en connaissance de cause : " +
                "un fichier trop petit fait échouer des programmes qui fonctionnaient.");

            Add(consumers, root, "swapfile.sys", SpaceKind.PageFile,
                "Fichier d'échange réservé aux applications du Windows Store. Toujours petit.",
                SpaceReclaim.None);
        }

        /// <summary>
        /// Le vidage mémoire d'un écran bleu.
        /// </summary>
        /// <remarks>
        /// Un vidage complet fait la taille de la mémoire installée : seize giga-octets pour une
        /// machine de seize. Il n'a d'intérêt que le temps de l'analyser, et il reste pourtant
        /// des années. Sa date est aussi une information à part entière : elle date le dernier
        /// écran bleu à la seconde près.
        /// </remarks>
        private static void AddCrashDump(ICollection<SpaceConsumer> consumers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string windows;
            try
            {
                windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(windows)) return;

            Add(consumers, windows, "MEMORY.DMP", SpaceKind.CrashDump,
                "Copie de la mémoire prise lors d'un écran bleu, conservée pour analyse. Sa taille " +
                "suit celle de la mémoire installée.",
                SpaceReclaim.Removable,
                "Se supprime sans risque une fois l'écran bleu compris, ou tout de suite, si personne " +
                "ne compte l'analyser.");
        }

        /// <summary>
        /// L'installation précédente de Windows, laissée après une mise à niveau.
        /// </summary>
        /// <remarks>
        /// Windows la supprime seul au bout de dix jours. Passé ce délai elle ne devrait plus
        /// être là : quand elle y est encore, la suppression automatique a échoué, et vingt à
        /// trente giga-octets dorment sur le disque sans que rien ne le signale.
        /// <para>
        /// Sa taille n'est pas mesurée : la parcourir demande de traverser des dossiers dont
        /// l'accès est refusé sans élévation, et un total incomplet donné pour un total complet
        /// serait pire que pas de total du tout. La date, elle, suffit à décider.
        /// </para>
        /// </remarks>
        private static void AddPreviousWindows(ICollection<SpaceConsumer> consumers, string root)
        {
            var path = Path.Combine(root, "Windows.old");

            DateTimeOffset created;
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists) return;
                created = directory.CreationTime;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException)
            {
                return;
            }

            consumers.Add(new SpaceConsumer
            {
                Name = "Windows.old",
                Path = path,
                Kind = SpaceKind.PreviousWindows,
                Bytes = Measured.Missing<long>(
                    "La taille de ce dossier ne se mesure pas sans privilèges : une partie de son contenu " +
                    "n'est pas lisible, et un total incomplet tromperait plus qu'il n'aiderait."),
                Since = Measured.Ok(created, DataSource.FileSystem),
                Purpose = "Installation précédente de Windows, conservée après une mise à niveau pour " +
                          "permettre un retour en arrière. Compte en général vingt à trente giga-octets.",
                Reclaim = SpaceReclaim.Automatic,
                Cost = "Windows la supprime seul au bout de dix jours. Le nettoyage de disque de Windows " +
                       "s'en charge aussi, et le retour à la version précédente devient alors impossible.",
            });
        }

        /// <summary>
        /// Tout fichier volumineux posé à la racine du volume.
        /// </summary>
        /// <remarks>
        /// Le filet qui rattrape ce qu'aucune liste ne prévoit : une image disque téléchargée
        /// deux ans plus tôt, une sauvegarde faite « en attendant », le disque d'une machine
        /// virtuelle. Ces fichiers n'ont rien de commun sinon d'être gros, d'être à la racine, et
        /// d'être oubliés.
        /// </remarks>
        private static void AddLooseFiles(
            ICollection<SpaceConsumer> consumers, string root, CancellationToken cancellationToken)
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(root).GetFiles();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long length;
                try
                {
                    length = file.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                if (length < LooseFileFloor) continue;
                if (IsKnown(file.Name)) continue;

                consumers.Add(new SpaceConsumer
                {
                    Name = file.Name,
                    Path = file.FullName,
                    Kind = SpaceKind.LooseFile,
                    Bytes = Measured.Ok(length, DataSource.FileSystem),
                    Since = Safe(file),
                    Purpose = "Fichier volumineux posé à la racine du disque. Ce n'est pas un fichier de " +
                              "Windows : quelqu'un l'a mis là.",
                    Reclaim = SpaceReclaim.Unknown,
                    Cost = "À montrer au client avant toute décision : lui seul sait si ce fichier compte.",
                });
            }
        }

        /// <summary>Les fichiers déjà décrits nommément, pour ne pas les compter deux fois.</summary>
        private static bool IsKnown(string name)
            => string.Equals(name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "swapfile.sys", StringComparison.OrdinalIgnoreCase);

        // ================================================================= aides

        private static void Add(
            ICollection<SpaceConsumer> consumers, string folder, string name, SpaceKind kind,
            string purpose, SpaceReclaim reclaim, string? cost = null)
        {
            var path = Path.Combine(folder, name);

            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!file.Exists) return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException)
            {
                return;
            }

            long length;
            try
            {
                length = file.Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Le fichier existe et sa taille est refusée : c'est le seul cas où l'on porte
                // l'occupant sans sa taille, parce que son existence compte déjà.
                consumers.Add(new SpaceConsumer
                {
                    Name = name, Path = path, Kind = kind, Purpose = purpose, Reclaim = reclaim, Cost = cost,
                    Bytes = Measured.Missing<long>("La taille de ce fichier n'a pas pu être lue."),
                });
                return;
            }

            consumers.Add(new SpaceConsumer
            {
                Name = name,
                Path = path,
                Kind = kind,
                Bytes = Measured.Ok(length, DataSource.FileSystem),
                Since = Safe(file),
                Purpose = purpose,
                Reclaim = reclaim,
                Cost = cost,
            });
        }

        private static Measured<DateTimeOffset> Safe(FileInfo file)
        {
            try
            {
                return Measured.Ok(new DateTimeOffset(file.LastWriteTime), DataSource.FileSystem);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentOutOfRangeException)
            {
                return Measured.Missing<DateTimeOffset>("La date de ce fichier n'a pas pu être lue.");
            }
        }

        private static string? SystemRoot()
        {
            try
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var root = Path.GetPathRoot(system);
                return string.IsNullOrEmpty(root) ? null : root;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
