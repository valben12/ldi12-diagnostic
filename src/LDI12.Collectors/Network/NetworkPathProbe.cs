using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Network
{
    /// <summary>
    /// Qualité du chemin réseau : pertes, gigue, trajet jusqu'à Internet, taille de paquet.
    /// </summary>
    /// <remarks>
    /// Trois mesures que le test de connectivité simple ne donne pas, et qui expliquent les
    /// pannes les plus difficiles à faire admettre au client comme à l'opérateur :
    /// <list type="bullet">
    /// <item>une ligne qui « marche » mais perd un paquet sur dix : les pages s'ouvrent, la
    /// visioconférence hache ;</item>
    /// <item>une latence en dents de scie, qui rend la voix inutilisable sans jamais couper ;</item>
    /// <item>une MTU réduite par un tunnel ou une box mal configurée : certaines pages se
    /// chargent à moitié, sans la moindre erreur affichée.</item>
    /// </list>
    /// <para>
    /// Une seule destination est contactée, la même que le test de connectivité : le résolveur
    /// public 1.1.1.1. Aucune donnée n'est transmise, un paquet ICMP ne porte qu'un remplissage.
    /// </para>
    /// </remarks>
    public sealed class NetworkPathProbe : IDiagnosticProbe
    {
        private const string Target = "1.1.1.1";

        private const int QualityPingCount = 15;
        private const int QualityTimeoutMs = 800;

        /// <summary>Au-delà, on est déjà loin dans le réseau de l'opérateur : plus rien d'actionnable.</summary>
        private const int MaxHops = 10;

        private const int HopTimeoutMs = 1000;

        /// <summary>1500 octets moins 20 d'en-tête IP et 8 d'en-tête ICMP.</summary>
        private const int StandardPayload = 1472;

        /// <summary>En deçà, la liaison serait inutilisable : inutile de chercher plus bas.</summary>
        private const int MinimumPayload = 1200;

        /// <summary>Remplissage sans signification : un paquet de test ne transporte aucune donnée.</summary>
        private static readonly byte[] Filler = new byte[StandardPayload];

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.NetworkPath,
            DisplayName = "Qualité et chemin réseau",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromSeconds(12),
            HardTimeout = TimeSpan.FromSeconds(75),
            FullScanOnly = true,
            DependsOn = new[] { ProbeIds.NetworkTests },
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var existing = context.Draft.BuildNetwork().Tests;

            var quality = await MeasureQualityAsync(cancellationToken).ConfigureAwait(false);
            var route = await TraceAsync(cancellationToken).ConfigureAwait(false);
            var mtu = await MeasureMtuAsync(cancellationToken).ConfigureAwait(false);

            context.Draft.SetNetworkTests(new NetworkTests
            {
                Gateway = existing.Gateway,
                Internet = existing.Internet,
                DnsResolutions = existing.DnsResolutions,
                HttpChecks = existing.HttpChecks,
                Quality = quality,
                Route = route,
                PathMtu = mtu,
            });

            return Conclude(quality, route, mtu);
        }

        private static ProbeOutcome Conclude(PathQuality? quality, IReadOnlyList<RouteHop> route, Measured<int> mtu)
        {
            if (quality == null || !quality.LossPercent.HasValue)
                return ProbeOutcome.Partial("La qualité de la liaison n'a pas pu être mesurée.");

            var loss = quality.LossPercent.Value;
            if (loss >= 100)
                return ProbeOutcome.Ok("Aucun paquet n'est revenu : la liaison ne porte rien vers Internet.");

            if (loss > 0)
                return ProbeOutcome.Ok(
                    "Perte de " + loss.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) +
                    " % sur " + quality.Sent.Or(0) + " paquets.");

            if (mtu.IsReliable && mtu.Value < 1500)
                return ProbeOutcome.Ok("Liaison sans perte, mais la taille de paquet est limitée à " + mtu.Value + " octets.");

            return ProbeOutcome.Ok(
                "Liaison sans perte" + (route.Count > 0 ? ", chemin relevé sur " + route.Count + " étape(s)." : "."));
        }

        /// <summary>
        /// Pertes, latence et gigue sur une série de paquets.
        /// </summary>
        /// <remarks>
        /// Quinze paquets et non quatre : sur une ligne qui perd un paquet sur dix, quatre
        /// paquets rendent régulièrement un résultat parfait. Le nombre envoyé est reporté avec
        /// le résultat, un taux de perte sans son échantillon ne veut rien dire.
        /// </remarks>
        private static async Task<PathQuality?> MeasureQualityAsync(CancellationToken cancellationToken)
        {
            var times = new List<double>(QualityPingCount);
            var sent = 0;

            using var ping = new Ping();

            for (var i = 0; i < QualityPingCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent++;

                try
                {
                    var reply = await ping.SendPingAsync(Target, QualityTimeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                }
                catch (PingException)
                {
                    // Paquet perdu ou pile réseau indisponible : compté comme une perte, ce
                    // qu'il est du point de vue de l'utilisateur.
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }

            if (sent == 0) return null;

            var received = times.Count;
            var loss = Math.Round(100d * (sent - received) / sent, 1);

            if (received == 0)
                return new PathQuality
                {
                    Target = Target,
                    Sent = Measured.Ok(sent, DataSource.NativeApi),
                    Received = Measured.Ok(0, DataSource.NativeApi),
                    LossPercent = Measured.Ok(100d, DataSource.NativeApi),
                    AverageMs = Measured.Missing<double>(
                        "Aucun paquet n'est revenu : aucune latence à mesurer.", DataSource.NativeApi),
                    JitterMs = Measured.Missing<double>(
                        "Aucun paquet n'est revenu : aucune gigue à mesurer.", DataSource.NativeApi),
                    MinMs = Measured.Missing<double>("Aucun paquet n'est revenu.", DataSource.NativeApi),
                    MaxMs = Measured.Missing<double>("Aucun paquet n'est revenu.", DataSource.NativeApi),
                };

            double total = 0, min = double.MaxValue, max = 0;
            foreach (var time in times)
            {
                total += time;
                if (time < min) min = time;
                if (time > max) max = time;
            }

            return new PathQuality
            {
                Target = Target,
                Sent = Measured.Ok(sent, DataSource.NativeApi),
                Received = Measured.Ok(received, DataSource.NativeApi),
                LossPercent = Measured.Ok(loss, DataSource.NativeApi),
                AverageMs = Measured.Ok(Math.Round(total / received, 1), DataSource.NativeApi),
                MinMs = Measured.Ok(min, DataSource.NativeApi),
                MaxMs = Measured.Ok(max, DataSource.NativeApi),
                JitterMs = Jitter(times),
            };
        }

        /// <summary>
        /// Gigue : écart moyen entre deux temps de réponse consécutifs.
        /// </summary>
        /// <remarks>
        /// Et non l'écart-type : c'est la variation d'un paquet au suivant qui rend la voix
        /// hachée, pas la dispersion générale. Une seule réponse ne permet aucun écart, et la
        /// mesure est alors déclarée absente plutôt que rendue à zéro.
        /// </remarks>
        internal static Measured<double> Jitter(IReadOnlyList<double> times)
        {
            if (times.Count < 2)
                return Measured.Missing<double>(
                    "Une seule réponse reçue : la gigue demande au moins deux mesures.", DataSource.NativeApi);

            double total = 0;
            for (var i = 1; i < times.Count; i++) total += Math.Abs(times[i] - times[i - 1]);

            return Measured.Ok(Math.Round(total / (times.Count - 1), 1), DataSource.NativeApi);
        }

        /// <summary>
        /// Chemin jusqu'à la cible, une étape à la fois.
        /// </summary>
        /// <remarks>
        /// Une étape muette est fréquente et rarement significative : beaucoup d'équipements sont
        /// configurés pour ne pas répondre aux paquets expirés. Elle est donc marquée comme
        /// silencieuse (jamais comme défaillante) et le relevé continue.
        /// </remarks>
        private static async Task<IReadOnlyList<RouteHop>> TraceAsync(CancellationToken cancellationToken)
        {
            var hops = new List<RouteHop>();

            using var ping = new Ping();

            for (var ttl = 1; ttl <= MaxHops; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PingReply reply;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    reply = await ping
                        .SendPingAsync(Target, HopTimeoutMs, new byte[32], new PingOptions(ttl, false))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PingException || ex is InvalidOperationException ||
                                           ex is NotSupportedException)
                {
                    break;
                }
                finally
                {
                    stopwatch.Stop();
                }

                var answered = reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success;
                var address = answered ? reply.Address?.ToString() : null;

                hops.Add(new RouteHop
                {
                    Distance = ttl,
                    Address = address == null
                        ? Measured.Missing<string>("Cette étape ne répond pas aux paquets expirés.", DataSource.NativeApi)
                        : Measured.Ok(address, DataSource.NativeApi),

                    // Chronométré ici et non lu dans la réponse : PingReply.RoundtripTime n'est
                    // renseigné que sur un statut Success et rend zéro sur une étape expirée.
                    // Afficher ce zéro reviendrait à annoncer « 0 ms » pour une durée qu'on n'a
                    // pas mesurée, exactement ce que le modèle de données interdit.
                    RoundTripMs = answered
                        ? Measured.Ok(Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), DataSource.NativeApi)
                        : Measured.Missing<double>("Étape silencieuse.", DataSource.NativeApi),

                    Silent = !answered,
                    IsLocal = address != null && IsPrivate(address),
                });

                if (reply.Status == IPStatus.Success) break;
            }

            return hops;
        }

        /// <summary>Adresse du réseau local : c'est la frontière entre « chez le client » et « chez l'opérateur ».</summary>
        internal static bool IsPrivate(string address)
        {
            if (!IPAddress.TryParse(address, out var parsed)) return false;

            var bytes = parsed.GetAddressBytes();
            if (bytes.Length != 4) return false;

            return bytes[0] == 10
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        /// <summary>
        /// Plus grand paquet transmis sans fragmentation, par recherche dichotomique.
        /// </summary>
        /// <remarks>
        /// Le cas usuel (1500 octets) est testé en premier et coûte un seul paquet : inutile
        /// d'engager six essais sur les machines dont la liaison est normale. La recherche ne
        /// descend pas sous 1200 octets : en deçà, la liaison serait inutilisable et le problème
        /// se verrait autrement.
        /// </remarks>
        private static async Task<Measured<int>> MeasureMtuAsync(CancellationToken cancellationToken)
        {
            using var ping = new Ping();

            var standard = await FitsAsync(ping, StandardPayload, cancellationToken).ConfigureAwait(false);
            if (standard == null)
                return Measured.Missing<int>(
                    "La taille de paquet n'a pas pu être mesurée : la cible ne répond pas aux paquets non fragmentables.",
                    DataSource.NativeApi);

            if (standard.Value) return Measured.Ok(StandardPayload + 28, DataSource.NativeApi);

            var low = MinimumPayload;
            var high = StandardPayload;

            while (high - low > 8)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var middle = (low + high) / 2;
                var fits = await FitsAsync(ping, middle, cancellationToken).ConfigureAwait(false);
                if (fits == null) break;

                if (fits.Value) low = middle;
                else high = middle;
            }

            return low == MinimumPayload
                ? Measured.Partial(low + 28, DataSource.NativeApi,
                    "La recherche s'arrête à 1228 octets : en deçà, la liaison serait inutilisable.")
                : Measured.Ok(low + 28, DataSource.NativeApi);
        }

        /// <summary>Vrai si le paquet passe, faux s'il est trop gros, nul si la cible reste muette.</summary>
        private static async Task<bool?> FitsAsync(Ping ping, int payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var buffer = new byte[payload];
                Array.Copy(Filler, buffer, payload);

                var reply = await ping
                    .SendPingAsync(Target, QualityTimeoutMs, buffer, new PingOptions(64, true))
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.Success) return true;
                if (reply.Status == IPStatus.PacketTooBig) return false;

                // Beaucoup d'équipements ne renvoient pas « paquet trop gros » et laissent
                // simplement expirer : un silence sur un paquet non fragmentable se lit comme
                // un refus de le transmettre.
                return reply.Status == IPStatus.TimedOut ? (bool?)false : null;
            }
            catch (Exception ex) when (ex is PingException || ex is InvalidOperationException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                return null;
            }
        }
    }
}
