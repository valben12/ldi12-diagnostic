using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// De quoi le disque est plein, et pas seulement qu'il l'est.
    /// </summary>
    /// <remarks>
    /// <b>Le logiciel savait dire qu'un volume était plein à quatre-vingt-douze pour cent ; il ne
    /// savait pas dire de quoi.</b> Or les plus gros occupants d'un disque système ne sont
    /// presque jamais les documents du client : ce sont des fichiers que Windows crée sans le
    /// dire, cachés et protégés, qu'aucun explorateur ne montre. Un client qui cherche ce qui
    /// remplit son disque ne les trouvera jamais seul.
    /// <para>
    /// Aucun de ces constats ne propose de supprimer quoi que ce soit sans dire ce qu'il en
    /// coûte : la mise en veille prolongée se libère en perdant le démarrage rapide, le fichier
    /// d'échange en risquant de faire échouer des programmes. Une place récupérée contre une
    /// panne créée n'est pas une réparation.
    /// </para>
    /// </remarks>
    internal static class SystemSpaceRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Storage;

        /// <summary>Ces constats décrivent le même disque plein sous des angles différents.</summary>
        private const string Family = "storage.occupancy";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SPA-001", "Part des fichiers système", Cat, SystemFilesShare, Family);
            yield return new Rule("SPA-002", "Taille du fichier d'échange", Cat, PageFile, Family);
            yield return new Rule("SPA-003", "Ancienne installation de Windows", Cat, PreviousWindows, Family);
            yield return new Rule("SPA-004", "Vidage mémoire conservé", Cat, CrashDump, Family);
            yield return new Rule("SPA-005", "Fichiers volumineux à la racine", Cat, LooseFiles, Family);
        }

        /// <summary>
        /// Les fichiers de Windows pèsent une part notable du disque.
        /// </summary>
        /// <remarks>
        /// La part, et non la taille : cinquante giga-octets ne veulent rien dire seuls. Sur un
        /// disque d'un téraoctet c'est un détail, sur un portable à deux cent cinquante c'est un
        /// cinquième de la machine, et c'est là que la question se pose.
        /// </remarks>
        private static RuleResult SystemFilesShare(RuleContext c)
        {
            var space = c.Storage.SystemSpace;
            if (space.Consumers.Count == 0)
                return RuleResult.NotEvaluated("L'occupation du disque système n'a pas été relevée.");

            var volume = SystemVolume(c);
            if (volume == null || !volume.TotalBytes.IsReliable)
                return RuleResult.NotEvaluated("La taille du volume système n'a pas pu être lue.");

            var total = 0L;
            var named = new List<string>();

            foreach (var consumer in space.Consumers)
            {
                if (consumer.Kind == SpaceKind.LooseFile) continue;
                if (!consumer.Bytes.HasValue) continue;

                total += consumer.Bytes.Value;
                named.Add(consumer.Name + " " + Fmt.Bytes(consumer.Bytes.Value));
            }

            if (total == 0) return RuleResult.Clean;

            var share = 100d * total / volume.TotalBytes.Value;
            if (share < c.T.SystemFilesSharePercent) return RuleResult.Clean;

            // Le même fait se lit autrement selon la place qui reste : un cinquième du disque est
            // une curiosité quand il en reste la moitié, et le premier levier quand il n'en reste
            // plus rien.
            var tight = volume.FreeBytes.IsReliable && volume.TotalBytes.Value > 0 &&
                        100d * volume.FreeBytes.Value / volume.TotalBytes.Value <
                        c.T.SystemVolumeFreePercentWarning;

            return RuleResult.Of(c.Finding(tight ? Severity.Warning : Severity.Info,
                "Les fichiers cachés de Windows occupent une part notable du disque",
                Fmt.Bytes(total) + " soit " + Math.Round(share) + " % du volume système : " +
                string.Join(", ", named) + ".",
                "Ces fichiers ne sont visibles dans aucun explorateur : Windows les cache et les protège. " +
                "Ils ont chacun leur utilité et ne sont pas des déchets, mais leur taille est calculée sur " +
                "la mémoire de la machine, pas sur la place disponible, et sur un petit disque, cela finit " +
                "par compter." +
                (tight
                    ? " Le disque étant déjà à l'étroit, c'est ici qu'il y a le plus à récupérer, avant de " +
                      "chercher du côté des documents."
                    : " Rien ne presse tant que la place ne manque pas : c'est une information, pas un défaut."),
                evidence: RuleContext.Ev(
                    Evidence.Of("Fichiers système", Fmt.Bytes(total), DataSource.FileSystem,
                        c.T.SystemFilesSharePercent + " % du volume"),
                    Evidence.Of("Part du volume", Math.Round(share) + " %", DataSource.Inferred))));
        }

        /// <summary>
        /// Le fichier d'échange dépasse ce que Windows fixe de lui-même.
        /// </summary>
        /// <remarks>
        /// Windows le dimensionne au maximum à trois fois la mémoire installée. Au-delà, la
        /// valeur a été saisie à la main, souvent des années plus tôt, souvent en recopiant un
        /// conseil trouvé quelque part, et sur une machine qui avait alors quatre fois moins de
        /// mémoire.
        /// </remarks>
        private static RuleResult PageFile(RuleContext c)
        {
            var memory = c.Hardware.Memory.TotalBytes;
            if (!memory.IsReliable)
                return RuleResult.NotEvaluated("La quantité de mémoire installée n'a pas pu être lue.");

            SpaceConsumer? pageFile = null;
            foreach (var consumer in c.Storage.SystemSpace.Consumers)
                if (consumer.Kind == SpaceKind.PageFile && consumer.Bytes.HasValue &&
                    consumer.Name.StartsWith("pagefile", StringComparison.OrdinalIgnoreCase))
                    pageFile = consumer;

            if (pageFile == null)
                return RuleResult.NotEvaluated("Aucun fichier d'échange n'a été relevé sur le volume système.");

            var times = (double)pageFile.Bytes.Value / memory.Value;
            if (times <= c.T.PageFileTimesMemory) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Le fichier d'échange est plus grand que ce que Windows fixerait",
                Fmt.Bytes(pageFile.Bytes.Value) + " pour " + Fmt.Bytes(memory.Value) + " de mémoire, soit " +
                Math.Round(times, 1) + " fois (Windows s'arrête à " + c.T.PageFileTimesMemory + ").",
                "Windows dimensionne ce fichier tout seul, au maximum à trois fois la mémoire installée. " +
                "Au-delà, la taille a été saisie à la main, souvent des années plus tôt, sur une machine qui " +
                "avait beaucoup moins de mémoire. La ramener à une gestion automatique rend la place sans rien " +
                "changer au fonctionnement.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fichier d'échange", Fmt.Bytes(pageFile.Bytes.Value), DataSource.FileSystem),
                    Evidence.Of("Mémoire installée", Fmt.Bytes(memory.Value), DataSource.NativeApi,
                        c.T.PageFileTimesMemory + " fois au plus")),
                recommendations: RuleContext.Rec(Rec.RestorePageFile)));
        }

        /// <summary>
        /// Une installation précédente de Windows est restée bien au-delà du délai.
        /// </summary>
        /// <remarks>
        /// Windows la supprime seul au bout de dix jours. Quand elle est encore là un mois plus
        /// tard, la suppression automatique a échoué, et vingt à trente giga-octets dorment sans
        /// que rien ne le signale, sur une machine qui vient souvent en atelier parce qu'elle
        /// manque de place.
        /// </remarks>
        private static RuleResult PreviousWindows(RuleContext c)
        {
            foreach (var consumer in c.Storage.SystemSpace.Consumers)
            {
                if (consumer.Kind != SpaceKind.PreviousWindows) continue;
                if (!consumer.Since.IsReliable) continue;

                var days = (int)(c.Snapshot.Metadata.CreatedAt - consumer.Since.Value).TotalDays;
                if (days < c.T.PreviousWindowsDaysWarning) return RuleResult.Clean;

                return RuleResult.Of(c.Finding(Severity.Warning,
                    "Une ancienne installation de Windows occupe toujours le disque",
                    "Windows.old créé le " + Fmt.Date(consumer.Since.Value) + ", soit il y a " + days +
                    " jours (seuil " + c.T.PreviousWindowsDaysWarning + ").",
                    "Après une mise à niveau, Windows conserve l'ancienne installation pendant dix jours pour " +
                    "permettre un retour en arrière, puis la supprime seul. Ici, elle est toujours là bien " +
                    "après : la suppression automatique n'a pas eu lieu, et vingt à trente giga-octets sont " +
                    "immobilisés. Le nettoyage de disque de Windows la retire : le retour à la version " +
                    "précédente devient alors définitivement impossible, ce qui n'a plus d'intérêt à ce stade.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Créé le", Fmt.Date(consumer.Since.Value), DataSource.FileSystem,
                            c.T.PreviousWindowsDaysWarning + " jours"))));
            }

            return RuleResult.Clean;
        }

        /// <summary>
        /// Un vidage mémoire complet est conservé.
        /// </summary>
        /// <remarks>
        /// Il fait la taille de la mémoire installée et n'a d'intérêt que le temps de l'analyser.
        /// Sa date est une information à part entière : elle date le dernier écran bleu à la
        /// seconde, là où le journal se contente d'un identifiant d'événement.
        /// </remarks>
        private static RuleResult CrashDump(RuleContext c)
        {
            foreach (var consumer in c.Storage.SystemSpace.Consumers)
            {
                if (consumer.Kind != SpaceKind.CrashDump) continue;

                var size = consumer.Bytes.HasValue ? Fmt.Bytes(consumer.Bytes.Value) : "taille inconnue";
                var when = consumer.Since.HasValue ? Fmt.Date(consumer.Since.Value) : "date inconnue";

                return RuleResult.Of(c.Finding(Severity.Info,
                    "Un vidage mémoire d'écran bleu est conservé sur le disque",
                    "MEMORY.DMP : " + size + ", du " + when + ".",
                    "Lors d'un écran bleu, Windows recopie toute la mémoire sur le disque pour permettre " +
                    "l'analyse. Ce fichier fait la taille de la mémoire installée et reste ensuite " +
                    "indéfiniment. Sa date dit quand le dernier écran bleu a eu lieu ; une fois la cause " +
                    "comprise (ou si personne ne compte l'analyser), il se supprime sans risque.",
                    evidence: RuleContext.Ev(
                        Evidence.Of("Vidage mémoire", size, DataSource.FileSystem),
                        Evidence.Of("Date", when, DataSource.FileSystem)),
                    recommendations: RuleContext.Rec(Rec.AnalyzeCrashDumps)));
            }

            return RuleResult.Clean;
        }

        /// <summary>
        /// Des fichiers volumineux posés à la racine du disque.
        /// </summary>
        /// <remarks>
        /// Le filet qui rattrape ce qu'aucune liste ne prévoit : une image disque téléchargée
        /// deux ans plus tôt, une sauvegarde faite « en attendant », le disque d'une machine
        /// virtuelle. Le constat les nomme et s'arrête là : seul le client sait s'ils comptent.
        /// </remarks>
        private static RuleResult LooseFiles(RuleContext c)
        {
            var names = new List<string>();
            var total = 0L;

            foreach (var consumer in c.Storage.SystemSpace.Consumers)
            {
                if (consumer.Kind != SpaceKind.LooseFile || !consumer.Bytes.HasValue) continue;

                names.Add(consumer.Name + " (" + Fmt.Bytes(consumer.Bytes.Value) + ")");
                total += consumer.Bytes.Value;
            }

            if (names.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des fichiers volumineux sont posés à la racine du disque",
                names.Count + " fichier(s) pour " + Fmt.Bytes(total) + " : " + string.Join(", ", names) + ".",
                "Ces fichiers ne viennent pas de Windows : quelqu'un les a déposés là et ne les a pas " +
                "repris. C'est souvent une image disque, une sauvegarde faite « en attendant » ou le disque " +
                "d'une machine virtuelle. Rien n'est proposé ici : seul le client sait s'ils comptent encore, " +
                "et c'est à lui qu'il faut les montrer.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Fichiers à la racine", Fmt.Bytes(total), DataSource.FileSystem))));
        }

        private static VolumeInfo? SystemVolume(RuleContext c)
        {
            foreach (var volume in c.Storage.Volumes)
                if (volume.IsSystemVolume.Or(false)) return volume;

            return null;
        }
    }
}
