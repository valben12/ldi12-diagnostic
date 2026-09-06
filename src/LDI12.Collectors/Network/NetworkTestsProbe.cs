using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Network
{
    /// <summary>
    /// Tests de connectivité : passerelle, Internet, résolution de noms, HTTPS.
    /// </summary>
    /// <remarks>
    /// Ces quatre tests sont conçus pour se lire ensemble et désigner l'étage fautif. Passerelle
    /// joignable mais DNS muet : le problème est la résolution de noms, pas la connexion ; c'est
    /// exactement la conclusion qu'un client attend, et elle évite de démonter une box qui
    /// fonctionne.
    /// <para>
    /// Ce sont les <b>seules</b> sorties réseau du logiciel, et elles sont listées ici en clair :
    /// un technicien doit pouvoir dire à son client ce que l'outil contacte.
    /// </para>
    /// </remarks>
    public sealed class NetworkTestsProbe : IDiagnosticProbe
    {
        private const int PingCount = 4;
        private const int PingTimeoutMs = 1500;

        /// <summary>Résolveurs publics choisis pour leur stabilité, jamais pour collecter quoi que ce soit.</summary>
        private static readonly string[] InternetTargets = { "1.1.1.1", "9.9.9.9" };

        private static readonly string[] DnsTargets = { "www.microsoft.com", "windowsupdate.microsoft.com" };

        /// <summary>
        /// Point de test HTTPS. Volontairement pas msftconnecttest.com : ce domaine sert aux
        /// sondes HTTP de Windows et ne présente pas de certificat valide en HTTPS : le test
        /// aurait échoué sur toutes les machines et fait conclure à tort à un filtrage.
        /// </summary>
        private const string HttpsTarget = "https://www.microsoft.com/";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.NetworkTests,
            DisplayName = "Tests de connectivité",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromSeconds(8),
            HardTimeout = TimeSpan.FromSeconds(45),
            FullScanOnly = true,
            DependsOn = new[] { ProbeIds.NetworkAdapters },
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var gatewayAddress = FindGateway(context);

            var gateway = gatewayAddress == null
                ? null
                : await PingAsync(gatewayAddress, "Passerelle (box ou routeur)", cancellationToken).ConfigureAwait(false);

            var internet = await PingFirstReachableAsync(cancellationToken).ConfigureAwait(false);
            var dns = await ResolveAllAsync(cancellationToken).ConfigureAwait(false);
            var https = await CheckHttpsAsync(cancellationToken).ConfigureAwait(false);

            context.Draft.SetNetworkTests(new NetworkTests
            {
                Gateway = gateway,
                Internet = internet,
                DnsResolutions = dns,
                HttpChecks = new[] { https },
            });

            return Conclude(gatewayAddress, gateway, internet, dns, https);
        }

        /// <summary>
        /// Conclusion en une phrase, formulée du point de vue de ce que le technicien doit faire
        /// ensuite. Le moteur de règles affinera en phase 2, mais le module doit déjà être
        /// exploitable seul.
        /// </summary>
        private static ProbeOutcome Conclude(
            string? gatewayAddress, PingResult? gateway, PingResult? internet,
            IReadOnlyList<DnsResolutionResult> dns, HttpCheckResult https)
        {
            if (gatewayAddress == null)
                return ProbeOutcome.Partial("Aucune passerelle configurée : la machine n'est raccordée à aucun réseau.");

            var gatewayOk = gateway?.Reachable.Or(false) ?? false;
            var internetOk = internet?.Reachable.Or(false) ?? false;
            var httpsOk = https.Succeeded.Or(false);

            var dnsOk = false;
            foreach (var resolution in dns) if (resolution.Resolved.Or(false)) { dnsOk = true; break; }

            // On conclut du plus large vers le plus étroit : ce qui fonctionne au niveau
            // supérieur prouve que tout l'étage inférieur fonctionne aussi. Beaucoup de box et
            // de pare-feu ignorent les requêtes ICMP : annoncer une panne de passerelle sur ce
            // seul silence, alors que la navigation marche, serait un diagnostic faux.
            if (httpsOk && dnsOk)
            {
                return gatewayOk
                    ? ProbeOutcome.Ok("Connexion, résolution de noms et accès HTTPS fonctionnels.")
                    : ProbeOutcome.Ok(
                        "Connexion, résolution de noms et accès HTTPS fonctionnels. " +
                        "La passerelle ne répond pas aux requêtes ICMP, ce qui est un réglage courant et sans conséquence.");
            }

            if (dnsOk && !httpsOk)
                return ProbeOutcome.Ok("Connexion et résolution de noms fonctionnelles, mais l'accès HTTPS échoue : filtrage, proxy, ou date et heure du système erronées.");

            if (internetOk && !dnsOk)
                return ProbeOutcome.Ok("Internet répond mais la résolution de noms échoue : problème DNS, pas de connexion.");

            if (gatewayOk && !internetOk)
                return ProbeOutcome.Ok("La passerelle répond mais Internet est injoignable : la panne est en amont, côté box ou opérateur.");

            return ProbeOutcome.Ok(
                "Ni la passerelle, ni Internet, ni la résolution de noms ne répondent : " +
                "la liaison entre la machine et la box est à vérifier en premier.");
        }

        private static string? FindGateway(ProbeContext context)
        {
            foreach (var adapter in context.Draft.NetworkAdapters)
            {
                if (!adapter.IsPrimary) continue;
                foreach (var gateway in adapter.Gateways) return gateway;
            }

            // Pas d'interface principale identifiée : on prend la première passerelle trouvée.
            foreach (var adapter in context.Draft.NetworkAdapters)
                foreach (var gateway in adapter.Gateways) return gateway;

            return null;
        }

        private static async Task<PingResult?> PingFirstReachableAsync(CancellationToken cancellationToken)
        {
            PingResult? last = null;
            foreach (var target in InternetTargets)
            {
                var result = await PingAsync(target, "Internet", cancellationToken).ConfigureAwait(false);
                if (result.Reachable.Or(false)) return result;
                last = result;
            }
            return last;
        }

        private static async Task<PingResult> PingAsync(string target, string label, CancellationToken cancellationToken)
        {
            var times = new List<long>(PingCount);
            var received = 0;
            string? failure = null;

            for (var i = 0; i < PingCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, PingTimeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        received++;
                        times.Add(reply.RoundtripTime);
                    }
                    else if (failure == null)
                    {
                        failure = Describe(reply.Status);
                    }
                }
                catch (Exception ex) when (ex is PingException || ex is System.Net.Sockets.SocketException)
                {
                    failure ??= "La cible n'a pas pu être contactée (" + ex.GetType().Name + ").";
                }
            }

            var loss = 100d * (PingCount - received) / PingCount;
            var reachable = received > 0;

            long min = 0, max = 0, sum = 0;
            foreach (var time in times)
            {
                if (min == 0 || time < min) min = time;
                if (time > max) max = time;
                sum += time;
            }

            return new PingResult
            {
                Target = target,
                Label = label,
                Sent = Measured.Ok(PingCount, DataSource.NativeApi),
                Received = Measured.Ok(received, DataSource.NativeApi),
                LossPercent = Measured.Ok(Math.Round(loss, 1), DataSource.Inferred),
                Reachable = Measured.Ok(reachable, DataSource.NativeApi),
                MinMs = reachable ? Measured.Ok((double)min, DataSource.NativeApi)
                                  : Measured.Missing<double>(failure ?? "Aucune réponse."),
                MaxMs = reachable ? Measured.Ok((double)max, DataSource.NativeApi)
                                  : Measured.Missing<double>(failure ?? "Aucune réponse."),
                AverageMs = reachable ? Measured.Ok(Math.Round((double)sum / times.Count, 1), DataSource.Inferred)
                                      : Measured.Missing<double>(failure ?? "Aucune réponse."),
            };
        }

        private static async Task<IReadOnlyList<DnsResolutionResult>> ResolveAllAsync(CancellationToken cancellationToken)
        {
            var results = new List<DnsResolutionResult>(DnsTargets.Length);
            foreach (var host in DnsTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    stopwatch.Stop();

                    var texts = new List<string>(addresses.Length);
                    foreach (var address in addresses) texts.Add(address.ToString());

                    results.Add(new DnsResolutionResult
                    {
                        Host = host,
                        Resolved = Measured.Ok(texts.Count > 0, DataSource.NativeApi),
                        Addresses = texts,
                        DurationMs = Measured.Ok(stopwatch.ElapsedMilliseconds, DataSource.NativeApi),
                    });
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is ArgumentException)
                {
                    stopwatch.Stop();
                    results.Add(new DnsResolutionResult
                    {
                        Host = host,
                        Resolved = Measured.Ok(false, DataSource.NativeApi),
                        DurationMs = Measured.Ok(stopwatch.ElapsedMilliseconds, DataSource.NativeApi),
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// .NET Framework 4.6.2 négocie par défaut en TLS 1.0, que plus aucun serveur moderne
        /// n'accepte : sans cette ligne, le test HTTPS échouerait sur toutes les machines et
        /// ferait conclure à tort à un filtrage réseau. Sur un Windows 7 non à jour, TLS 1.2
        /// peut malgré tout manquer côté système : l'échec est alors un vrai constat de
        /// diagnostic, et il est rapporté comme tel.
        /// </summary>
        private static void EnsureModernTls()
        {
            if (_tlsConfigured) return;
            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls11 = (SecurityProtocolType)768;
                ServicePointManager.SecurityProtocol |= tls12 | tls11;
            }
            catch (NotSupportedException)
            {
                // Système antérieur à la prise en charge de TLS 1.2 : on garde le réglage par défaut.
            }
            _tlsConfigured = true;
        }

        private static bool _tlsConfigured;

        private static async Task<HttpCheckResult> CheckHttpsAsync(CancellationToken cancellationToken)
        {
            EnsureModernTls();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(HttpsTarget);
                request.Method = "GET";
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                request.UserAgent = "LDI12-Diagnostic";

                // Pas de proxy automatique : sa détection peut bloquer plusieurs secondes sur un
                // réseau d'entreprise mal configuré, et fausserait la mesure.
                request.Proxy = null;

                using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                stopwatch.Stop();

                return new HttpCheckResult
                {
                    Url = HttpsTarget,
                    StatusCode = Measured.Ok((int)response.StatusCode, DataSource.NativeApi),
                    DurationMs = Measured.Ok(stopwatch.ElapsedMilliseconds, DataSource.NativeApi),
                    Succeeded = Measured.Ok((int)response.StatusCode < 400, DataSource.Inferred),
                };
            }
            catch (WebException ex)
            {
                stopwatch.Stop();
                var status = ex.Response is HttpWebResponse response ? (int)response.StatusCode : 0;
                return new HttpCheckResult
                {
                    Url = HttpsTarget,
                    StatusCode = status > 0
                        ? Measured.Ok(status, DataSource.NativeApi)
                        : Measured.Missing<int>(DescribeWebFailure(ex)),
                    DurationMs = Measured.Ok(stopwatch.ElapsedMilliseconds, DataSource.NativeApi),
                    Succeeded = Measured.Ok(false, DataSource.NativeApi),
                };
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException)
            {
                stopwatch.Stop();
                return new HttpCheckResult
                {
                    Url = HttpsTarget,
                    StatusCode = Measured.Missing<int>("Le test HTTPS n'a pas pu être effectué."),
                    DurationMs = Measured.Ok(stopwatch.ElapsedMilliseconds, DataSource.NativeApi),
                    Succeeded = Measured.Ok(false, DataSource.NativeApi),
                };
            }
        }

        private static string DescribeWebFailure(WebException ex) => ex.Status switch
        {
            WebExceptionStatus.NameResolutionFailure => "Le nom du serveur n'a pas pu être résolu (DNS).",
            WebExceptionStatus.ConnectFailure => "La connexion au serveur a été refusée ou filtrée.",
            WebExceptionStatus.Timeout => "Le serveur n'a pas répondu dans le délai imparti.",
            WebExceptionStatus.TrustFailure => "Le certificat n'a pas pu être validé, vérifier la date et l'heure du système.",
            WebExceptionStatus.SecureChannelFailure => "La négociation sécurisée a échoué, protocole TLS trop ancien ou filtrage.",
            WebExceptionStatus.ProxyNameResolutionFailure => "Le proxy configuré est introuvable.",
            _ => "Le test HTTPS a échoué (" + ex.Status + ").",
        };

        private static string Describe(IPStatus status) => status switch
        {
            IPStatus.TimedOut => "Aucune réponse dans le délai imparti.",
            IPStatus.DestinationHostUnreachable => "Hôte injoignable.",
            IPStatus.DestinationNetworkUnreachable => "Réseau injoignable.",
            IPStatus.TtlExpired => "Durée de vie du paquet expirée : boucle de routage probable.",
            _ => "Échec du test (" + status + ").",
        };
    }
}
