using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Repairs
{
    /// <summary>
    /// Ce qu'on fait sur un réseau qui ne va pas, une fois le diagnostic posé.
    /// </summary>
    /// <remarks>
    /// <b>Chacune de ces opérations coupe quelque chose.</b> Renouveler une adresse interrompt la
    /// liaison, redémarrer une carte la fait disparaître le temps de revenir, réinitialiser le
    /// pare-feu efface des règles que quelqu'un a peut-être posées. C'est pourquoi la
    /// prévisualisation de chacune dit ce qui va tomber, et pas seulement ce qui va être fait :
    /// un technicien connecté à distance doit pouvoir refuser en connaissance de cause.
    /// </remarks>
    internal static class NetworkRepairHelp
    {
        /// <summary>
        /// Avertissement commun à toute opération qui coupe la liaison.
        /// </summary>
        /// <remarks>
        /// Il revient à l'identique dans quatre prévisualisations. Le factoriser n'est pas une
        /// économie de lignes : c'est la garantie qu'aucune des quatre ne l'oubliera le jour où
        /// on en ajoutera une cinquième.
        /// </remarks>
        public static PreviewLine RemoteSession =>
            new PreviewLine(
                "Intervention à distance",
                "cette opération coupe la liaison réseau quelques secondes : une session de prise en main " +
                "à distance sera interrompue, et ne reviendra que si la machine retrouve seule son réseau",
                PreviewLineKind.Caution);

        /// <summary>Décrit l'issue d'un netsh.</summary>
        /// <remarks>
        /// <b>Le code 1 n'est pas une réussite.</b> Cette fonction a longtemps accepté 0 <i>ou</i>
        /// 1, par généralisation d'une observation qui ne valait que pour <c>netsh int ip
        /// reset</c>. Mesuré sur Windows 11 : <c>netsh interface ip delete arpcache</c> lancé sans
        /// privilèges rend <b>1</b> et « L'opération demandée requiert une élévation » ;
        /// <c>netsh commande inexistante</c> rend <b>1</b> lui aussi. Un pare-feu qu'on n'a pas eu
        /// le droit de réinitialiser était donc annoncé « revenu à ses règles d'origine ».
        /// <para>
        /// La tolérance d'origine reste où elle a été mesurée, dans la réinitialisation de la pile
        /// réseau, et elle y porte sa raison.
        /// </para>
        /// </remarks>
        public static bool Succeeded(ProcessResult result)
            => result.Completed && result.ExitCode == 0;

        /// <summary>
        /// Les adresses IPv4 exploitables qu'<c>ipconfig</c> a écrites.
        /// </summary>
        /// <remarks>
        /// <b>Le code de sortie d'<c>ipconfig /renew</c> ne dit rien.</b> Mesuré sur Windows 11 :
        /// « L'opération a échoué, car aucun adaptateur n'est dans l'état permettant cette
        /// opération » sort avec le code <b>0</b>. Le texte, lui, est traduit et ne peut pas
        /// servir de critère. Restent les chiffres, qui ne sont traduits nulle part.
        /// <para>
        /// Une adresse en 169.254 n'est pas un bail : c'est celle que Windows s'attribue quand
        /// personne ne lui répond, et c'est justement la panne qu'on venait réparer. Les masques,
        /// la boucle locale et la diffusion sont écartés pour la même raison : ils ne prouvent
        /// pas qu'un serveur a répondu.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> LeasedAddresses(string? output)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(output)) return found;

            foreach (Match match in Address.Matches(output!))
            {
                var text = match.Value;
                if (found.Contains(text)) continue;

                var parts = text.Split('.');
                var first = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var second = int.Parse(parts[1], CultureInfo.InvariantCulture);

                // Écartés : la route par défaut, la boucle locale, la diffusion et les masques
                // (255.255.255.0 en tête), enfin l'auto-configuration.
                if (first == 0 || first == 127 || first >= 224) continue;
                if (first == 169 && second == 254) continue;

                var valide = true;
                foreach (var part in parts)
                    if (int.Parse(part, CultureInfo.InvariantCulture) > 255) valide = false;

                if (valide) found.Add(text);
            }

            return found;
        }

        /// <summary>Vrai si la sortie ne porte qu'une adresse d'auto-configuration.</summary>
        public static bool SelfAssignedOnly(string? output)
            => output != null && output.IndexOf("169.254.", StringComparison.Ordinal) >= 0 &&
               LeasedAddresses(output).Count == 0;

        private static readonly Regex Address = new Regex(
            @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);
    }

    /// <summary>
    /// Renouveler l'adresse obtenue du serveur DHCP.
    /// </summary>
    /// <remarks>
    /// Le geste qui répare le plus de « je n'ai plus Internet » d'un coup : une machine restée
    /// avec une adresse d'auto-configuration, un bail obtenu d'une box qu'on a changée, une
    /// adresse en conflit avec une autre machine. Elle reprend une adresse propre et repart.
    /// </remarks>
    public sealed class RenewAddressAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RenewAddress,
            DisplayName = "Renouveler l'adresse réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Moderate,
            Purpose = "Lance ipconfig /release puis /renew : la machine rend son adresse et en redemande une.",
            PlainPurpose = "L'ordinateur redemande une adresse à la box ou au routeur, comme s'il venait " +
                           "d'être branché.",
            TypicalDuration = TimeSpan.FromSeconds(10),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "le renouvellement d'un bail DHCP exige les privilèges administrateur");

            var dhcp = false;
            foreach (var adapter in Adapters(context))
                if (adapter.DhcpEnabled.Or(false)) dhcp = true;

            return dhcp
                ? ActionReadiness.Ready
                : ActionReadiness.No(
                    "Aucune interface active n'obtient son adresse d'un serveur DHCP.",
                    "Les adresses sont fixées à la main sur cette machine : les renouveler n'a pas de sens. " +
                    "La configuration se change dans les propriétés de la carte réseau.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var lines = new List<PreviewLine>
            {
                new PreviewLine("Commande", "ipconfig /release puis ipconfig /renew"),
                new PreviewLine("Durée", "quelques secondes, une minute si le serveur tarde"),
            };

            foreach (var adapter in Adapters(context))
            {
                if (!adapter.DhcpEnabled.Or(false)) continue;

                var address = adapter.IPv4Addresses.Count > 0 ? adapter.IPv4Addresses[0] : "aucune adresse";
                lines.Add(new PreviewLine(adapter.Name, address + " : sera rendue puis redemandée"));
            }

            lines.Add(NetworkRepairHelp.RemoteSession);

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Les adresses obtenues automatiquement vont être rendues, puis redemandées au " +
                          "serveur qui les distribue.",
                WillDo = new[]
                {
                    "Rendre les adresses obtenues par DHCP sur toutes les interfaces concernées.",
                    "En redemander de nouvelles au serveur qui les distribue, la box, en général.",
                },
                WillNotDo = new[]
                {
                    "Ne touche pas aux adresses fixées à la main.",
                    "Ne modifie aucun réglage : ni serveurs DNS, ni Wi-Fi enregistré, ni pare-feu.",
                    "N'exige aucun redémarrage.",
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

            progress?.Report(new ActionProgress("Restitution des adresses…"));
            var release = await context.Processes.RunAsync(
                new ProcessRequest("ipconfig.exe", "/release")
                {
                    Timeout = TimeSpan.FromSeconds(45),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            output.AppendLine("ipconfig /release").AppendLine(release.StandardOutput).AppendLine();

            progress?.Report(new ActionProgress("Demande de nouvelles adresses…"));
            var renew = await context.Processes.RunAsync(
                new ProcessRequest("ipconfig.exe", "/renew")
                {
                    Timeout = TimeSpan.FromSeconds(75),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            output.AppendLine("ipconfig /renew").AppendLine(renew.StandardOutput);

            // Le succès se juge sur le renouvellement seul : la restitution échoue sur les
            // interfaces sans bail, ce qui est sans conséquence et arrive sur toute machine
            // portant une carte virtuelle. Et il se juge sur les adresses obtenues, pas sur le
            // code de sortie, qui vaut zéro même quand rien n'a été renouvelé.
            var addresses = NetworkRepairHelp.LeasedAddresses(renew.StandardOutput);
            var apipa = NetworkRepairHelp.SelfAssignedOnly(renew.StandardOutput);
            var ok = renew.Completed && addresses.Count > 0;

            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "Les adresses ont été renouvelées."
                    : apipa
                        ? "Aucun serveur n'a répondu : la machine s'est attribué une adresse à elle-même, " +
                          "ce qui ne mène nulle part."
                        : "Le renouvellement n'a pas abouti : aucune adresse n'a été obtenue.",
                Details = ok
                    ? new[] { "Adresses obtenues : " + string.Join(", ", new List<string>(addresses).ToArray()) + "." }
                    : new[] { "Vérifier le câble, la box, et que le serveur DHCP distribue encore des adresses." },
                RawOutput = output.ToString(),
                Duration = stopwatch.Elapsed,
            };
        }

        private static IReadOnlyList<NetworkAdapterInfo> Adapters(ActionContext context)
        {
            var adapters = new List<NetworkAdapterInfo>();
            if (context.Snapshot?.Network == null) return adapters;

            foreach (var adapter in context.Snapshot.Network.Adapters)
                if (adapter.IsUp) adapters.Add(adapter);

            return adapters;
        }
    }

    /// <summary>
    /// Remettre le mandataire de la machine en accès direct.
    /// </summary>
    /// <remarks>
    /// La suite du constat qui signale deux configurations de mandataire divergentes. Celle de la
    /// machine est la moins visible des deux (aucun écran de Windows ne la montre) et c'est
    /// elle qui empêche les mises à jour de s'installer pendant que le navigateur fonctionne
    /// parfaitement.
    /// </remarks>
    public sealed class ResetMachineProxyAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.ResetMachineProxy,
            DisplayName = "Remettre le mandataire de la machine en accès direct",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Low,
            Purpose = "Lance netsh winhttp reset proxy : les services de Windows sortent de nouveau " +
                      "en direct, sans passer par un intermédiaire.",
            PlainPurpose = "Les composants de Windows (dont les mises à jour) cessent de passer par " +
                           "un intermédiaire qui ne répond peut-être plus.",
            TypicalDuration = TimeSpan.FromSeconds(3),
            HardTimeout = TimeSpan.FromSeconds(45),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "la configuration de mandataire de la machine exige les privilèges administrateur");

            var proxy = context.Snapshot?.Network?.Environment?.MachineProxy;
            if (proxy == null) return ActionReadiness.Ready;

            return proxy.Configured
                ? ActionReadiness.Ready
                : ActionReadiness.No(
                    "La machine sort déjà en accès direct : aucun mandataire n'est déclaré pour ses services.",
                    "Si le web ne fonctionne pas, c'est du côté du mandataire de la session qu'il faut " +
                    "regarder : celui que suivent les navigateurs.");
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var proxy = context.Snapshot?.Network?.Environment?.MachineProxy;
            var current = proxy != null && proxy.Server.HasValue ? proxy.Server.Value : "non lu";

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Les services de Windows cesseront de passer par un mandataire et sortiront " +
                          "directement.",
                WillDo = new[]
                {
                    "Effacer la configuration de mandataire de la machine (celle de WinHTTP).",
                },
                WillNotDo = new[]
                {
                    "Ne touche pas au mandataire de la session : les navigateurs gardent le leur.",
                    "Ne modifie ni les adresses, ni les serveurs DNS, ni le pare-feu.",
                    "Ne coupe pas la connexion et n'exige aucun redémarrage.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Commande", "netsh winhttp reset proxy"),
                    new PreviewLine("Mandataire actuel de la machine", current),
                    new PreviewLine(
                        "En entreprise",
                        "sur un réseau où la sortie passe obligatoirement par un mandataire, cette opération " +
                        "coupe les mises à jour au lieu de les rétablir : à ne faire qu'après avoir vérifié " +
                        "que la sortie directe est permise",
                        PreviewLineKind.Caution),
                },
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new ActionProgress("Remise en accès direct…"));

            var result = await context.Processes.RunAsync(
                new ProcessRequest("netsh.exe", "winhttp reset proxy")
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            var ok = NetworkRepairHelp.Succeeded(result);

            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "Les services de Windows sortent désormais en accès direct."
                    : "La configuration n'a pas pu être effacée.",
                Details = new[] { "netsh winhttp reset proxy : code " +
                                  result.ExitCode.ToString(CultureInfo.InvariantCulture) + "." },
                RawOutput = result.StandardOutput,
                Duration = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>
    /// Vider le cache des adresses physiques.
    /// </summary>
    /// <remarks>
    /// Répare une panne précise et déroutante : un appareil du réseau a changé d'adresse, ou deux
    /// en portent la même, et la machine continue d'envoyer ses trames à l'ancienne carte. Tout
    /// le reste fonctionne, seul cet appareil-là est injoignable, souvent la box, souvent après
    /// son remplacement.
    /// </remarks>
    public sealed class FlushArpCacheAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.FlushArpCache,
            DisplayName = "Vider le cache des adresses physiques",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Low,
            Purpose = "Lance netsh interface ip delete arpcache : la machine oublie la correspondance " +
                      "entre adresses IP et cartes réseau du réseau local.",
            PlainPurpose = "L'ordinateur oublie où se trouvent physiquement les autres appareils du réseau " +
                           "et le redemande. Utile après le remplacement d'une box.",
            TypicalDuration = TimeSpan.FromSeconds(3),
            HardTimeout = TimeSpan.FromSeconds(45),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("le cache d'adresses physiques exige les privilèges administrateur");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "La correspondance entre adresses IP et cartes du réseau local va être oubliée. " +
                          "Elle se reconstruit d'elle-même en quelques secondes.",
                WillDo = new[]
                {
                    "Effacer le cache des adresses physiques de toutes les interfaces.",
                },
                WillNotDo = new[]
                {
                    "Ne change aucune adresse et ne coupe aucune connexion établie.",
                    "Ne modifie aucun réglage et n'exige aucun redémarrage.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Commande", "netsh interface ip delete arpcache"),
                    new PreviewLine("Durée", "immédiat, reconstruction automatique"),
                    new PreviewLine(
                        "Quand cela sert",
                        "un appareil du réseau est injoignable alors que tout le reste fonctionne : " +
                        "typiquement après le remplacement d'une box ou d'un routeur"),
                },
            });

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new ActionProgress("Vidage du cache d'adresses physiques…"));

            var result = await context.Processes.RunAsync(
                new ProcessRequest("netsh.exe", "interface ip delete arpcache")
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            var ok = NetworkRepairHelp.Succeeded(result);

            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "Le cache d'adresses physiques a été vidé."
                    : "Le cache n'a pas pu être vidé.",
                Details = new[] { "netsh interface ip delete arpcache : code " +
                                  result.ExitCode.ToString(CultureInfo.InvariantCulture) + "." },
                RawOutput = result.StandardOutput,
                Duration = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>
    /// Éteindre puis rallumer une carte réseau.
    /// </summary>
    /// <remarks>
    /// L'équivalent logiciel du débranchement de câble, et il répare souvent ce qu'aucun réglage
    /// n'explique : une carte qui a perdu son adressage, un pilote resté dans un état bancal
    /// après une veille. Le paramètre nomme la carte, parce qu'éteindre celle par laquelle on
    /// travaille et éteindre une carte virtuelle inutilisée ne se décident pas de la même façon.
    /// </remarks>
    public sealed class RestartAdapterAction : IRepairAction
    {
        /// <summary>Nom de la carte, tel que Windows l'affiche : « Ethernet », « Wi-Fi ».</summary>
        public const string AdapterParameter = "adapter";

        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.RestartAdapter,
            DisplayName = "Redémarrer une carte réseau",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.Moderate,
            Purpose = "Désactive puis réactive l'interface choisie, comme un débranchement de câble.",
            PlainPurpose = "La carte réseau est éteinte puis rallumée. C'est l'équivalent de débrancher " +
                           "le câble et de le rebrancher.",
            TypicalDuration = TimeSpan.FromSeconds(15),
            HardTimeout = TimeSpan.FromMinutes(2),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
        {
            if (!context.Platform.IsElevated)
                return ActionReadiness.Elevation(
                    "l'activation d'une interface réseau exige les privilèges administrateur");

            var name = context.Parameter(AdapterParameter);
            if (string.IsNullOrWhiteSpace(name))
                return ActionReadiness.No(
                    "Aucune carte réseau n'a été choisie.",
                    "Choisir la carte à redémarrer dans la liste : celle qui pose problème, pas celle " +
                    "qui fonctionne.");

            return Find(context, name!) == null
                ? ActionReadiness.No(
                    "Aucune carte ne porte le nom « " + name + " » sur cette machine.",
                    "Relancer une analyse : la liste des cartes date peut-être d'avant un changement " +
                    "de matériel.")
                : ActionReadiness.Ready;
        }

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var name = context.Parameter(AdapterParameter);
            var adapter = string.IsNullOrWhiteSpace(name) ? null : Find(context, name!);

            if (adapter == null)
                return Task.FromResult(new ActionPreview
                {
                    Outcome = PreviewOutcome.Blocked,
                    Summary = "Aucune carte réseau n'a été choisie, ou celle qui l'a été n'existe plus.",
                });

            var lines = new List<PreviewLine>
            {
                new PreviewLine("Carte", adapter.Name + " : " + adapter.Description),
                new PreviewLine("Commande",
                    "netsh interface set interface « " + adapter.Name + " » disable, puis enable"),
                new PreviewLine("Durée", "une dizaine de secondes"),
            };

            if (adapter.IsPrimary)
                lines.Add(new PreviewLine(
                    "Carte principale",
                    "c'est par cette carte que la machine sort : toute la connectivité tombe le temps " +
                    "du redémarrage",
                    PreviewLineKind.Caution));

            lines.Add(NetworkRepairHelp.RemoteSession);

            return Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "La carte « " + adapter.Name + " » va être éteinte puis rallumée.",
                WillDo = new[]
                {
                    "Désactiver l'interface « " + adapter.Name + " ».",
                    "La réactiver aussitôt, et laisser Windows la reconfigurer.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucune autre carte.",
                    "Ne modifie aucun réglage de la carte : adresses, DNS et Wi-Fi enregistré sont conservés.",
                    "N'exige aucun redémarrage de la machine.",
                },
                Measurements = lines,
            });
        }

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var name = context.Parameter(AdapterParameter);
            if (string.IsNullOrWhiteSpace(name))
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "Aucune carte réseau n'a été choisie.",
                };

            var stopwatch = Stopwatch.StartNew();
            var output = new StringBuilder();

            progress?.Report(new ActionProgress("Extinction de la carte…"));
            var down = await Set(context, name!, "disable", cancellationToken).ConfigureAwait(false);
            output.AppendLine(down.StandardOutput);

            if (!NetworkRepairHelp.Succeeded(down))
                return new ActionOutcome
                {
                    Status = ActionStatus.Failed,
                    Summary = "La carte n'a pas pu être éteinte : elle reste en service.",
                    Details = new[] { "Aucun changement : la carte n'a pas été touchée." },
                    RawOutput = output.ToString(),
                    Duration = stopwatch.Elapsed,
                };

            progress?.Report(new ActionProgress("Rallumage de la carte…"));
            var up = await Set(context, name!, "enable", cancellationToken).ConfigureAwait(false);
            output.AppendLine(up.StandardOutput);

            var ok = NetworkRepairHelp.Succeeded(up);

            return new ActionOutcome
            {
                // Une carte éteinte et non rallumée est le pire résultat possible : il est dit
                // comme tel, avec le geste qui le corrige, plutôt que noyé dans un « échec ».
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "La carte « " + name + " » a été éteinte puis rallumée."
                    : "La carte a été éteinte mais n'a pas pu être rallumée.",
                Details = ok
                    ? new[] { "Windows la reconfigure : compter quelques secondes avant que le réseau revienne." }
                    : new[]
                    {
                        "À rallumer sans attendre depuis les connexions réseau de Windows, ou en relançant " +
                        "cette opération.",
                    },
                RawOutput = output.ToString(),
                Duration = stopwatch.Elapsed,
            };
        }

        private static Task<ProcessResult> Set(
            ActionContext context, string name, string state, CancellationToken cancellationToken)
            => context.Processes.RunAsync(
                new ProcessRequest("netsh.exe",
                    "interface set interface name=\"" + name + "\" admin=" + state)
                {
                    Timeout = TimeSpan.FromSeconds(45),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken);

        private static NetworkAdapterInfo? Find(ActionContext context, string name)
        {
            if (context.Snapshot?.Network == null) return null;

            foreach (var adapter in context.Snapshot.Network.Adapters)
                if (string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)) return adapter;

            return null;
        }
    }

    /// <summary>
    /// Remettre le pare-feu de Windows dans son état d'origine.
    /// </summary>
    /// <remarks>
    /// À réserver aux machines dont les règles ont été abîmées : un antivirus tiers mal
    /// désinstallé en laisse régulièrement derrière lui, et plus rien ne communique sans qu'on
    /// sache pourquoi. L'opération efface aussi les règles que quelqu'un a posées volontairement,
    /// et c'est pour cela qu'elle est classée au risque le plus élevé.
    /// </remarks>
    public sealed class ResetFirewallAction : IRepairAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor
        {
            Id = ActionIds.ResetFirewall,
            DisplayName = "Réinitialiser le pare-feu de Windows",
            Kind = ActionKind.Repair,
            Category = DiagnosticCategory.Network,
            Risk = ActionRisk.High,
            Purpose = "Lance netsh advfirewall reset : toutes les règles reviennent à celles d'une " +
                      "installation neuve.",
            PlainPurpose = "Le pare-feu retrouve ses réglages d'origine. Les autorisations ajoutées " +
                           "depuis (par vous ou par un logiciel) disparaissent.",
            TypicalDuration = TimeSpan.FromSeconds(5),
            HardTimeout = TimeSpan.FromMinutes(1),
            Requirements = new ActionRequirements { RequiresElevation = true },
        };

        public ActionReadiness CheckReadiness(ActionContext context)
            => context.Platform.IsElevated
                ? ActionReadiness.Ready
                : ActionReadiness.Elevation("le pare-feu exige les privilèges administrateur");

        public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ActionPreview
            {
                Outcome = PreviewOutcome.Ready,
                Summary = "Le pare-feu de Windows va retrouver exactement les règles d'une installation " +
                          "neuve. Tout ce qui a été autorisé depuis sera à réautoriser.",
                WillDo = new[]
                {
                    "Supprimer toutes les règles ajoutées, par un logiciel ou à la main.",
                    "Rétablir les règles d'origine de Windows et réactiver le pare-feu sur les trois profils.",
                },
                WillNotDo = new[]
                {
                    "Ne touche à aucun fichier personnel ni à aucun logiciel installé.",
                    "Ne modifie ni les adresses, ni les serveurs DNS, ni les réseaux Wi-Fi enregistrés.",
                },
                Measurements = new[]
                {
                    new PreviewLine("Commande", "netsh advfirewall reset"),
                    new PreviewLine(
                        "Ce qui sera perdu",
                        "les autorisations d'un logiciel de sauvegarde, d'un jeu en réseau, d'une imprimante " +
                        "partagée ou d'un poste distant : chacun redemandera l'accès à sa prochaine utilisation",
                        PreviewLineKind.Caution),
                    new PreviewLine(
                        "En entreprise",
                        "les règles distribuées par stratégie de groupe reviendront à la prochaine " +
                        "application ; celles posées à la main sur ce poste, non",
                        PreviewLineKind.Caution),
                    new PreviewLine(
                        "À faire avant",
                        "créer un point de restauration : la case est prévue en haut de l'écran",
                        PreviewLineKind.Caution),
                },
            });

        public async Task<ActionOutcome> ExecuteAsync(
            ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new ActionProgress("Réinitialisation du pare-feu…"));

            var result = await context.Processes.RunAsync(
                new ProcessRequest("netsh.exe", "advfirewall reset")
                {
                    Timeout = TimeSpan.FromSeconds(45),
                    OutputEncoding = ConsoleOutputEncoding.OemCodePage,
                },
                cancellationToken).ConfigureAwait(false);

            var ok = NetworkRepairHelp.Succeeded(result);

            return new ActionOutcome
            {
                Status = ok ? ActionStatus.Succeeded : ActionStatus.Failed,
                Summary = ok
                    ? "Le pare-feu a retrouvé ses règles d'origine."
                    : "Le pare-feu n'a pas pu être réinitialisé.",
                Details = ok
                    ? new[]
                    {
                        "Les logiciels qui ont besoin du réseau redemanderont l'autorisation à leur " +
                        "prochaine utilisation.",
                    }
                    : new[] { "Code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "." },
                RawOutput = result.StandardOutput,
                Duration = stopwatch.Elapsed,
            };
        }
    }
}
