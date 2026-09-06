using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Ce qu'on répare quand le chemin est faussé, et non la liaison.
    /// </summary>
    /// <remarks>
    /// Ces trois opérations touchent des réglages que rien dans Windows n'affiche : un fichier
    /// texte que Windows lit avant tout serveur de noms, une route qui survit aux redémarrages,
    /// des services de découverte arrêtés. Chacune montre ce qu'elle va changer avant de le
    /// changer, pour la première, ligne par ligne.
    /// </remarks>
    internal static class HostsFileHelp
    {
        public static string Path()
            => System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        /// <summary>
        /// Le contenu d'origine du fichier, tel que Windows le livre.
        /// </summary>
        /// <remarks>
        /// Les commentaires sont conservés parce qu'ils sont la seule documentation de ce fichier
        /// sur la machine : un technicien qui l'ouvrira dans six mois doit y trouver ce qu'il
        /// aurait trouvé sur une installation neuve.
        /// </remarks>
        public const string Original =
            "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
            "#\r\n" +
            "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
            "#\r\n" +
            "# This file contains the mappings of IP addresses to host names. Each\r\n" +
            "# entry should be kept on an individual line. The IP address should\r\n" +
            "# be placed in the first column followed by the corresponding host name.\r\n" +
            "# The IP address and the host name should be separated by at least one\r\n" +
            "# space.\r\n" +
            "#\r\n" +
            "# Additionally, comments (such as these) may be inserted on individual\r\n" +
            "# lines or following the machine name denoted by a '#' symbol.\r\n" +
            "#\r\n" +
            "# For example:\r\n" +
            "#\r\n" +
            "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
            "#       38.25.63.10     x.acme.com              # x client host\r\n" +
            "\r\n" +
            "# localhost name resolution is handled within DNS itself.\r\n" +
            "#\t127.0.0.1       localhost\r\n" +
            "#\t::1             localhost\r\n";
    }

    /// <summary>
    /// Rendre au fichier hosts son contenu d'origine, après avoir montré chaque ligne retirée.
    /// </summary>
    /// <remarks>
    /// <b>L'opération où la prévisualisation est le cœur du travail.</b> Ce fichier contient
    /// parfois des lignes que le client a mises lui-même (un site de test, un blocage de
    /// publicité) et parfois des lignes qui coupent ses mises à jour à son insu. Le logiciel ne
    /// sait pas trancher : il montre chaque ligne telle qu'elle est écrite, et laisse décider.
    /// <para>
    /// Le contenu retiré est conservé à côté du fichier, daté, plutôt que perdu.
    /// </para>
    /// </remarks>
    public sealed class RestoreHostsFileAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestoreHostsFile,
            DisplayName = "Rendre au fichier hosts son contenu d'origine",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Moderate,
            Purpose = "Sauvegarde le fichier hosts puis le remplace par le contenu livré avec Windows.",
            PlainPurpose = "Retire les noms de sites traités à part sur cet ordinateur, après en avoir " +
                           "gardé une copie.",
            TypicalDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(30),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "le fichier hosts appartient au système : le modifier exige les privilèges administrateur");

            var hosts = context.Snapshot?.Network.Paths.Hosts;
            if (hosts == null || !hosts.ActiveCount.HasValue)
                return ActionReadiness.No(
                    "Le fichier hosts n'a pas été relevé.",
                    "Lancez d'abord une analyse : cette opération ne retire que des lignes qu'elle a lues.");

            return hosts.ActiveCount.Value > 0
                ? ActionReadiness.Ready
                : ActionReadiness.No(
                    "Le fichier hosts ne contient aucune redirection.",
                    "Il est déjà dans son état d'origine : il n'y a rien à retirer.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var hosts = context.Snapshot?.Network.Paths.Hosts;

            if (hosts == null || hosts.Entries.Count == 0)
            {
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucune ligne à retirer n'a été relevée dans le fichier hosts.",
                    Measurements = new[]
                    {
                        new PreviewLine("Analyse",
                            "lancez une analyse avant cette opération : elle ne retire que ce qu'elle a lu",
                            PreviewLineKind.Caution),
                    },
                });
            }

            var lines = new List<PreviewLine>
            {
                new PreviewLine("Fichier", hosts.Path ?? HostsFileHelp.Path()),
                new PreviewLine("Sauvegarde",
                    "une copie datée est déposée à côté du fichier avant toute modification"),
            };

            // Chaque ligne est montrée telle qu'elle est écrite : c'est la seule façon de laisser
            // le technicien reconnaître ce qu'il a lui-même posé.
            foreach (var entry in hosts.Entries)
            {
                var serious = false;
                foreach (var name in entry.Names) if (HostsFile.IsConsequential(name)) serious = true;

                lines.Add(new PreviewLine(
                    "Ligne " + entry.Line.ToString(CultureInfo.CurrentCulture),
                    entry.Raw.Trim() + (serious ? "  (coupe un service de mise à jour)" : string.Empty),
                    serious ? PreviewLineKind.Caution : PreviewLineKind.Fact));
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = hosts.Entries.Count + " ligne(s) vont être retirées du fichier hosts, qui " +
                          "retrouvera le contenu livré avec Windows.",
                WillDo = new[]
                {
                    "Copier le fichier actuel à côté de lui, avec la date du jour.",
                    "Remplacer son contenu par celui d'une installation neuve.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucun autre réglage réseau.",
                    "Ne coupe pas la liaison : les connexions en cours ne sont pas interrompues.",
                    "N'exige aucun redémarrage.",
                },
                Measurements = lines,
            });
        }

        public Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var stopwatch = Stopwatch.StartNew();
            var path = context.Snapshot?.Network.Paths.Hosts.Path ?? HostsFileHelp.Path();

            progress?.Report(new ActionProgress("Relecture du fichier…"));

            // Entre la prévisualisation et l'exécution, quelques secondes passent : le fichier a
            // pu changer, et retirer des lignes que le technicien n'a pas vues serait exactement
            // ce que ce logiciel s'interdit.
            var current = context.Files.ReadText(path);
            if (!current.HasValue)
            {
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le fichier hosts n'a pas pu être relu : rien n'a été modifié.",
                    Details = new[] { current.Reason ?? "Lecture impossible." },
                    Duration = stopwatch.Elapsed,
                });
            }

            var announced = Announced(preview);
            var present = ActiveLines(current.Value);

            if (!SameSet(announced, present))
            {
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Le fichier a changé depuis l'aperçu : rien n'a été modifié.",
                    Details = new[]
                    {
                        "Les lignes présentes ne sont plus celles qui vous ont été montrées.",
                        "Relancez une analyse pour voir le contenu actuel, puis recommencez.",
                    },
                    Duration = stopwatch.Elapsed,
                });
            }

            progress?.Report(new ActionProgress("Sauvegarde du fichier actuel…"));
            var backup = path + "." + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".ldi12";
            var saved = context.Files.WriteText(backup, current.Value);

            if (!saved)
            {
                return Task.FromResult(new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "La copie de sauvegarde n'a pas pu être écrite : rien n'a été modifié.",
                    Details = new[]
                    {
                        "Aucune ligne n'est retirée tant que son contenu n'est pas mis de côté.",
                        "Emplacement visé : " + backup,
                    },
                    Duration = stopwatch.Elapsed,
                });
            }

            progress?.Report(new ActionProgress("Écriture du contenu d'origine…"));
            var written = context.Files.ReplaceText(path, HostsFileHelp.Original);

            return Task.FromResult(new ActionOutcome
            {
                Status = written ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = written
                    ? present.Count + " ligne(s) ont été retirées ; une copie a été conservée."
                    : "Le fichier n'a pas pu être réécrit : son contenu est inchangé.",
                Details = written
                    ? new[]
                    {
                        "Copie conservée : " + backup,
                        "Le fichier contient à nouveau ce que Windows y met à l'installation.",
                        "Les noms de sites concernés seront à nouveau demandés aux serveurs de noms.",
                    }
                    : new[]
                    {
                        "La sauvegarde, elle, a bien été écrite : " + backup,
                        "Un antivirus protège parfois ce fichier en écriture.",
                    },
                RawOutput = string.Join(Environment.NewLine, present),
                Duration = stopwatch.Elapsed,
            });
        }

        /// <summary>Lignes que la prévisualisation a montrées, sans leur commentaire ajouté.</summary>
        private static List<string> Announced(ActionPreview preview)
        {
            var lines = new List<string>();

            foreach (var line in preview.Measurements)
            {
                if (!line.Label.StartsWith("Ligne ", StringComparison.Ordinal)) continue;

                var text = line.Value;
                var marker = text.IndexOf("  (coupe", StringComparison.Ordinal);
                if (marker >= 0) text = text.Substring(0, marker);

                lines.Add(text.Trim());
            }

            return lines;
        }

        /// <summary>Lignes actives du fichier, dans la même forme que celles qui ont été montrées.</summary>
        private static List<string> ActiveLines(string content)
        {
            var lines = new List<string>();

            foreach (var raw in content.Split('\n'))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                lines.Add(trimmed);
            }

            return lines;
        }

        private static bool SameSet(List<string> left, List<string> right)
        {
            if (left.Count != right.Count) return false;

            var remaining = new List<string>(right);
            foreach (var line in left)
            {
                var at = remaining.IndexOf(line);
                if (at < 0) return false;
                remaining.RemoveAt(at);
            }

            return true;
        }
    }

    /// <summary>
    /// Relancer les services par lesquels une machine voit ses voisins.
    /// </summary>
    /// <remarks>
    /// La réponse à « je ne vois plus le NAS ni l'imprimante réseau ». Rien n'est cassé : les
    /// services qui cherchent le voisinage sont simplement arrêtés, et Windows n'en dit rien :
    /// la liste reste vide, comme si le réseau était vide.
    /// </remarks>
    public sealed class RestartDiscoveryAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestartDiscovery,
            DisplayName = "Relancer la découverte du voisinage réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Low,
            Purpose = "Démarre les services de découverte de fonctions et SSDP, arrêtés sur cette machine.",
            PlainPurpose = "Redonne à l'ordinateur la capacité de voir les autres machines, les disques " +
                           "réseau et les imprimantes partagées.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "démarrer un service de Windows exige les privilèges administrateur");

            var snapshot = context.Snapshot;
            if (snapshot == null)
                return ActionReadiness.No(
                    "Aucune analyse n'a été faite.",
                    "Cette opération ne démarre que les services qu'elle a vus à l'arrêt.");

            return DiscoveryServices.Stopped(snapshot.Windows.Services).Count > 0
                ? ActionReadiness.Ready
                : ActionReadiness.No(
                    "Les services de découverte fonctionnent déjà.",
                    "Si le voisinage reste vide, la cause est ailleurs : pare-feu, réseau classé public, " +
                    "ou partage désactivé sur l'appareil recherché.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var stopped = context.Snapshot == null
                ? Array.Empty<ServiceInfo>()
                : DiscoveryServices.Stopped(context.Snapshot.Windows.Services);

            if (stopped.Count == 0)
            {
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucun service de découverte n'a été relevé à l'arrêt.",
                });
            }

            var lines = new List<PreviewLine>();
            foreach (var service in stopped)
                lines.Add(new PreviewLine(service.Name,
                    DiscoveryServices.Describe(service.Name) + " : actuellement " +
                    (service.State.Length == 0 ? "à l'arrêt" : service.State.ToLowerInvariant())));

            lines.Add(new PreviewLine("Réseau public",
                "sur un réseau que la machine ne connaît pas, ces services restent volontairement " +
                "arrêtés : ne les lancez que sur un réseau de confiance",
                PreviewLineKind.Caution));

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = stopped.Count + " service(s) de découverte vont être démarrés.",
                WillDo = new[]
                {
                    "Démarrer les services arrêtés, sans changer leur mode de démarrage.",
                    "Laisser Windows repeupler la liste du voisinage, ce qui prend une minute environ.",
                },
                WillNotDo = new[]
                {
                    "Ne partage aucun dossier de cette machine.",
                    "Ne modifie ni le pare-feu, ni le classement du réseau.",
                    "Ne coupe pas la liaison réseau.",
                },
                Measurements = lines,
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var stopwatch = Stopwatch.StartNew();
            var output = new StringBuilder();
            var started = new List<string>();
            var refused = new List<string>();

            var stopped = context.Snapshot == null
                ? Array.Empty<ServiceInfo>()
                : DiscoveryServices.Stopped(context.Snapshot.Windows.Services);

            foreach (var service in stopped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress("Démarrage de " + service.Name + "…"));

                var result = await context.Processes.RunAsync(
                    new ProcessRequest("net.exe", "start \"" + service.Name + "\"")
                    {
                        Timeout = TimeSpan.FromSeconds(30),
                        OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                    },
                    cancellationToken).ConfigureAwait(false);

                output.AppendLine("net start " + service.Name).AppendLine(result.StandardOutput).AppendLine();

                if (result.Completed && result.ExitCode == 0) started.Add(service.Name);
                else refused.Add(service.Name);
            }

            var details = new List<string>();
            if (started.Count > 0) details.Add("Démarrés : " + string.Join(", ", started) + ".");
            if (refused.Count > 0)
                details.Add("Refusés : " + string.Join(", ", refused) +
                            " : un service désactivé doit d'abord être réautorisé dans les services de Windows.");

            details.Add("La liste du voisinage se remplit progressivement : comptez une minute.");

            return new ActionOutcome
            {
                Status = started.Count > 0 ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = started.Count > 0
                    ? started.Count + " service(s) de découverte ont été démarrés."
                    : "Aucun service n'a pu être démarré.",
                Details = details,
                RawOutput = output.ToString(),
                Duration = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>
    /// Retirer une route posée à la main.
    /// </summary>
    /// <remarks>
    /// <b>Une route à la fois, et jamais toutes.</b> Vider la table entière casserait une
    /// configuration volontaire aussi sûrement qu'elle réparerait un reliquat, et l'opération
    /// n'aurait aucun moyen de dire laquelle des deux elle vient de faire. Le technicien désigne
    /// la route, et la prévisualisation la lui montre entière avant qu'il ne confirme.
    /// </remarks>
    public sealed class RemoveRouteAction : IRepairAction
    {
        /// <summary>Le préfixe de la route à retirer, sous la forme « destination/longueur ».</summary>
        public const string Parameter = "route";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RemoveRoute,
            DisplayName = "Retirer un chemin ajouté à la main",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Moderate,
            Purpose = "Supprime une route statique désignée, avec route delete.",
            PlainPurpose = "Retire un passage particulier qu'on avait indiqué à l'ordinateur pour " +
                           "joindre certaines machines.",
            TypicalDuration = TimeSpan.FromSeconds(3),
            HardTimeout = TimeSpan.FromSeconds(60),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "modifier la table de routage exige les privilèges administrateur");

            var routing = context.Snapshot?.Network.Paths.Routing;
            if (routing == null || !routing.Count.HasValue)
                return ActionReadiness.No(
                    "La table de routage n'a pas été relevée.",
                    "Lancez d'abord une analyse : cette opération ne retire qu'une route qu'elle a lue.");

            return routing.Manual.Count > 0
                ? ActionReadiness.Ready
                : ActionReadiness.No(
                    "Aucune route n'a été posée à la main sur cette machine.",
                    "Toutes les routes présentes viennent de Windows ou de la configuration du réseau : " +
                    "elles se refont d'elles-mêmes et n'ont pas à être retirées.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var routing = context.Snapshot?.Network.Paths.Routing;
            var chosen = context.Parameters.TryGetValue(Parameter, out var prefix) ? prefix : null;

            if (routing == null || routing.Manual.Count == 0)
            {
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucune route posée à la main n'a été relevée.",
                });
            }

            RouteEntry? route = null;
            foreach (var candidate in routing.Manual)
                if (string.Equals(candidate.Prefix, chosen, StringComparison.Ordinal)) route = candidate;

            if (route == null)
            {
                var available = new List<PreviewLine>();
                foreach (var candidate in routing.Manual)
                    available.Add(new PreviewLine(candidate.Prefix,
                        "via " + candidate.NextHop + " sur " +
                        (candidate.InterfaceName ?? "interface " + candidate.InterfaceIndex)));

                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucune route n'a été désignée : choisissez celle à retirer.",
                    Measurements = available,
                });
            }

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le chemin vers " + route.Prefix + " va être retiré de la table de routage.",
                WillDo = new[]
                {
                    "Supprimer cette route, y compris sa version rendue permanente.",
                    "Laisser le trafic vers ces adresses repasser par le chemin ordinaire.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucune autre route.",
                    "Ne modifie ni les adresses, ni les serveurs de noms, ni le pare-feu.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Destination", route.Prefix),
                    new PreviewLine("Masque", route.Mask),
                    new PreviewLine("Passage", route.NextHop),
                    new PreviewLine("Interface",
                        route.InterfaceName ?? "interface " + route.InterfaceIndex),
                    new PreviewLine("Si elle était volontaire",
                        "les machines de " + route.Prefix + " ne seront plus joignables par ce passage : " +
                        "notez la ligne ci-dessus avant de confirmer, elle suffit à la remettre",
                        PreviewLineKind.Caution),
                },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (preview.Outcome != PreviewOutcome.Ready)
                throw new InvalidOperationException("Aucune route n'a été désignée.");

            var stopwatch = Stopwatch.StartNew();
            var destination = Value(preview, "Destination");
            var mask = Value(preview, "Masque");
            var gateway = Value(preview, "Passage");

            var slash = destination.IndexOf('/');
            if (slash > 0) destination = destination.Substring(0, slash);

            progress?.Report(new ActionProgress("Suppression du chemin…"));

            var result = await context.Processes.RunAsync(
                new ProcessRequest("route.exe", "delete " + destination + " mask " + mask + " " + gateway)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            var ok = result.Completed && result.ExitCode == 0;

            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "Le chemin vers " + destination + " a été retiré."
                    : "La route n'a pas pu être retirée.",
                Details = ok
                    ? new[]
                    {
                        "Le trafic vers ces adresses repasse par le chemin ordinaire.",
                        "Pour la remettre : route -p add " + destination + " mask " + mask + " " + gateway,
                    }
                    : new[]
                    {
                        "La route a pu être retirée entre-temps, ou être portée par une interface absente.",
                        "Relancez une analyse pour voir la table telle qu'elle est maintenant.",
                    },
                RawOutput = result.StandardOutput,
                Duration = stopwatch.Elapsed,
            };
        }

        private static string Value(ActionPreview preview, string label)
        {
            foreach (var line in preview.Measurements)
                if (string.Equals(line.Label, label, StringComparison.Ordinal)) return line.Value;

            throw new InvalidOperationException("L'aperçu ne porte pas « " + label + " ».");
        }
    }
}
