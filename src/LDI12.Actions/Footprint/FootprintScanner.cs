using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Footprint
{
    /// <summary>Un emplacement relevé, avec les fichiers qui le composent.</summary>
    /// <remarks>
    /// Les fichiers restent ici, du côté des actions, et ne remontent pas dans le modèle : la
    /// suppression a besoin de la liste exacte, l'affichage n'a besoin que des totaux.
    /// </remarks>
    public sealed class FootprintEntry
    {
        public FootprintItem Item { get; init; } = new FootprintItem();

        public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();
    }

    /// <summary>
    /// Relève ce que ce logiciel a écrit sur la machine.
    /// </summary>
    /// <remarks>
    /// <b>Un seul dossier, et la liste est exhaustive parce qu'elle est courte.</b> Tout ce que
    /// l'application écrit vit sous <c>%LOCALAPPDATA%\LDI12\Diagnostic</c> : les journaux, le
    /// barème ajusté, les diagnostics archivés, le journal d'intervention, et l'hôte de sondes
    /// quand une élévation l'a rendu nécessaire. Rien dans la base de registre, aucun service,
    /// aucune tâche planifiée, aucun dossier de programmes : c'est ce qui permet de répondre
    /// « rien d'autre » sans avoir à le supposer.
    /// <para>
    /// Le relevé passe par la passerelle de fichiers comme n'importe quelle action, et pour la
    /// même raison : ce sont les fichiers <i>relevés</i> qui seront supprimés, un par un, chacun
    /// vérifié identique à ce qu'il était au relevé. Un effacement de dossier entier ne pourrait
    /// pas le promettre.
    /// </para>
    /// <para>
    /// Les rapports exportés n'y figurent pas : ils sont écrits là où le technicien les a
    /// demandés, ils lui appartiennent, et les proposer à l'effacement reviendrait à effacer son
    /// travail.
    /// </para>
    /// </remarks>
    public static class FootprintScanner
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

        public static string DefaultRoot()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LDI12", "Diagnostic");

        public static IReadOnlyList<FootprintEntry> Scan(
            IFileSystemGateway files, CancellationToken cancellationToken)
            => Scan(files, DefaultRoot(), cancellationToken);

        public static IReadOnlyList<FootprintEntry> Scan(
            IFileSystemGateway files, string root, CancellationToken cancellationToken)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));

            return new[]
            {
                Folder(files, root, "logs", FootprintKind.Logs, "Journaux de l'application",
                    "Le journal technique repart de zéro. Aucun diagnostic n'est perdu.",
                    cancellationToken),

                Folder(files, root, "historique", FootprintKind.History, "Diagnostics archivés",
                    "Les analyses précédentes disparaissent, ainsi que la comparaison avant / après " +
                    "qui s'appuie sur elles.",
                    cancellationToken),

                Folder(files, root, "interventions", FootprintKind.Interventions, "Journal d'intervention",
                    "La trace des réparations menées sur cette machine disparaît.",
                    cancellationToken),

                Folder(files, root, "bin", FootprintKind.ExtractedHost,
                    "Hôte de sondes écrit lors d'une élévation",
                    "Il sera réécrit à la prochaine élévation. Rien n'est perdu.",
                    cancellationToken),

                Folder(files, root, null, FootprintKind.Settings, "Barème et réglages",
                    "Les seuils reviennent à leur valeur d'origine et les réglages de publication " +
                    "sont à ressaisir.",
                    cancellationToken),
            };
        }

        /// <summary>
        /// Relève un sous-dossier, ou la racine elle-même quand <paramref name="name"/> est nul.
        /// </summary>
        /// <remarks>
        /// La racine porte les fichiers de réglages, et elle seule : le balayage y est donc
        /// limité à son premier niveau, sans quoi il compterait une seconde fois tout ce que les
        /// sous-dossiers contiennent déjà.
        /// </remarks>
        private static FootprintEntry Folder(
            IFileSystemGateway files, string root, string? name, FootprintKind kind,
            string label, string consequence, CancellationToken cancellationToken)
        {
            var path = name == null ? root : Path.Combine(root, name);

            var item = new FootprintItem
            {
                Kind = kind,
                Label = label,
                Path = path,
                Consequence = consequence,
            };

            if (!files.DirectoryExists(path)) return new FootprintEntry { Item = item };

            var scan = files.Scan(
                new DirectoryScanRequest(path) { Budget = Budget, TopLevelOnly = name == null },
                cancellationToken);

            if (!scan.HasValue)
            {
                // Dossier présent mais illisible : il est annoncé présent, sans taille, plutôt
                // que passé sous silence.
                return new FootprintEntry
                {
                    Item = new FootprintItem
                    {
                        Kind = kind, Label = label, Path = path, Consequence = consequence, Exists = true,
                    },
                };
            }

            long size = 0;
            DateTime? last = null;
            foreach (var file in scan.Value.Files)
            {
                size += file.SizeBytes;
                if (last == null || file.LastWriteUtc > last.Value) last = file.LastWriteUtc;
            }

            return new FootprintEntry
            {
                Item = new FootprintItem
                {
                    Kind = kind,
                    Label = label,
                    Path = path,
                    Consequence = consequence,
                    Exists = scan.Value.Files.Count > 0,
                    FileCount = scan.Value.Files.Count,
                    SizeBytes = size,
                    LastWrite = last.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(last.Value, DateTimeKind.Utc)).ToLocalTime()
                        : (DateTimeOffset?)null,
                },
                Files = scan.Value.Files,
            };
        }
    }
}
