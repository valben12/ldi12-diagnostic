using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Accès WMI protégé : délai maximal, conversion de tout échec en résultat exploitable,
    /// et interdiction des requêtes dangereuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Limite structurelle assumée : <b>un appel WMI bloqué n'est pas annulable</b>. Un
    /// CancellationToken ne libère pas un thread coincé dans un RPC. Le délai maximal appliqué ici
    /// permet à l'application de reprendre la main et de rendre un résultat en échec, mais le
    /// thread sous-jacent peut rester bloqué jusqu'à la fin du processus. C'est précisément pour
    /// cela que les sondes marquées <c>SeparateProcess</c> tournent dans LDI12.ProbeHost.
    /// </para>
    /// </remarks>
    public sealed class WmiGateway : IWmiGateway
    {
        private const string Category = "Platform.Wmi";

        /// <summary>
        /// Classes dont l'énumération a des effets de bord destructeurs.
        /// <c>Win32_Product</c> déclenche une reconfiguration MSI de chaque logiciel installé :
        /// vingt minutes de blocage et des installations potentiellement cassées. La liste des
        /// logiciels se lit dans le registre, sous Uninstall.
        /// </summary>
        private static readonly string[] ForbiddenClasses = { "Win32_Product" };

        private static readonly TimeSpan NamespaceProbeTimeout = TimeSpan.FromSeconds(2);

        private readonly ConcurrentDictionary<string, WmiNamespaceState> _namespaceCache =
            new ConcurrentDictionary<string, WmiNamespaceState>(StringComparer.OrdinalIgnoreCase);

        private readonly IScopedLogger _log;

        public WmiGateway(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public async Task<WmiQueryResult> QueryAsync(
            string @namespace, string query, TimeSpan timeout, CancellationToken cancellationToken)
        {
            GuardAgainstForbiddenClasses(query);

            var stopwatch = Stopwatch.StartNew();
            var work = Task.Run(() => Execute(@namespace, query, timeout), CancellationToken.None);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var abandon = Task.Delay(Timeout.Infinite, deadline.Token);

            if (await Task.WhenAny(work, abandon).ConfigureAwait(false) != work)
            {
                // La requête est abandonnée, pas annulée : le thread WMI peut rester bloqué.
                // On rend la main à l'appelant, qui poursuivra son diagnostic sans cette donnée.
                _log.Warn("Délai dépassé après " + timeout.TotalSeconds.ToString("0.#") + " s : " + query +
                          " (" + @namespace + "). La requête est abandonnée.");
                return WmiQueryResult.Failure(
                    "La requête système n'a pas répondu dans le délai imparti. " +
                    "Cela indique souvent un dépôt WMI dégradé sur cette machine.", stopwatch.Elapsed);
            }

            return await work.ConfigureAwait(false);
        }

        public Task<WmiNamespaceState> ProbeNamespaceAsync(string @namespace, CancellationToken cancellationToken)
        {
            if (_namespaceCache.TryGetValue(@namespace, out var cached)) return Task.FromResult(cached);

            return Task.Run(() =>
            {
                WmiNamespaceState state;
                try
                {
                    // Deux secondes suffisent : un espace de noms local répond immédiatement ou
                    // n'existe pas. Un délai plus généreux ne change rien au résultat et coûtait
                    // cinq secondes sur chaque édition de Windows dépourvue du composant,
                    // BitLocker sur une édition Famille, par exemple.
                    var scope = new ManagementScope(Path(@namespace), new ConnectionOptions
                    {
                        Timeout = NamespaceProbeTimeout,
                    });
                    scope.Connect();
                    state = scope.IsConnected ? WmiNamespaceState.Present : WmiNamespaceState.Absent;
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
                {
                    // L'espace de noms existe : c'est bien nous qui n'avons pas le droit d'y accéder.
                    state = WmiNamespaceState.AccessDenied;
                }
                catch (UnauthorizedAccessException)
                {
                    state = WmiNamespaceState.AccessDenied;
                }
                catch (Exception ex) when (IsWmiFailure(ex))
                {
                    state = WmiNamespaceState.Absent;
                }

                _namespaceCache[@namespace] = state;
                _log.Debug("Espace de noms " + @namespace + " : " + Describe(state) + ".");
                return state;
            }, CancellationToken.None);
        }

        public async Task<bool> NamespaceExistsAsync(string @namespace, CancellationToken cancellationToken)
            => await ProbeNamespaceAsync(@namespace, cancellationToken).ConfigureAwait(false) == WmiNamespaceState.Present;

        private static string Describe(WmiNamespaceState state) => state switch
        {
            WmiNamespaceState.Present => "présent",
            WmiNamespaceState.AccessDenied => "présent mais accès refusé (élévation requise)",
            _ => "absent",
        };

        public async Task<bool> ClassExistsAsync(string @namespace, string className, CancellationToken cancellationToken)
        {
            if (!await NamespaceExistsAsync(@namespace, cancellationToken).ConfigureAwait(false)) return false;

            // meta_class interroge le schéma, pas les instances : incomparablement plus rapide
            // et sans risque de bloquer sur un fournisseur défaillant.
            var result = await QueryAsync(
                @namespace,
                "SELECT * FROM meta_class WHERE __Class = '" + className.Replace("'", string.Empty) + "'",
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);

            return result.Succeeded && result.Records.Count > 0;
        }

        private WmiQueryResult Execute(string @namespace, string query, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var scope = new ManagementScope(Path(@namespace), new ConnectionOptions
                {
                    Timeout = timeout,
                    EnablePrivileges = false,
                });
                scope.Connect();

                var options = new EnumerationOptions
                {
                    Timeout = timeout,
                    ReturnImmediately = true,
                    Rewindable = false,
                    EnsureLocatable = false,
                    BlockSize = 16,
                };

                var records = new List<WmiRecord>();
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementBaseObject instance in collection)
                    {
                        using (instance)
                        {
                            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (PropertyData property in instance.Properties)
                                properties[property.Name] = property.Value;
                            records.Add(new WmiRecord(properties));
                        }
                    }
                }

                stopwatch.Stop();
                _log.Trace(query + " → " + records.Count + " instance(s) en " + stopwatch.ElapsedMilliseconds + " ms.");
                return WmiQueryResult.Ok(records, stopwatch.Elapsed);
            }
            catch (ManagementException ex)
            {
                stopwatch.Stop();
                var reason = Explain(ex);
                _log.Warn("WMI " + ex.ErrorCode + " sur « " + query + " » : " + reason);
                return WmiQueryResult.Failure(reason, stopwatch.Elapsed, ex);
            }
            catch (Exception ex) when (IsWmiFailure(ex))
            {
                stopwatch.Stop();
                _log.Warn("Échec WMI sur « " + query + " ».", ex);
                return WmiQueryResult.Failure(
                    "Le service WMI n'a pas pu être interrogé sur cette machine.", stopwatch.Elapsed, ex);
            }
        }

        /// <summary>Traduit un code WMI en explication utilisable telle quelle dans l'interface.</summary>
        private static string Explain(ManagementException ex) => ex.ErrorCode switch
        {
            ManagementStatus.InvalidNamespace =>
                "Cet espace de noms WMI n'existe pas sur cette version de Windows.",
            ManagementStatus.InvalidClass =>
                "Cette classe WMI n'existe pas sur cette version de Windows.",
            ManagementStatus.NotFound =>
                "L'élément demandé est introuvable sur cette machine.",
            ManagementStatus.AccessDenied =>
                "Accès refusé : cette information nécessite des privilèges administrateur.",
            ManagementStatus.ProviderLoadFailure or ManagementStatus.ProviderFailure =>
                "Le fournisseur WMI correspondant est défaillant, le dépôt WMI de cette machine est probablement endommagé.",
            ManagementStatus.Timedout =>
                "Le service WMI n'a pas répondu dans le délai imparti.",
            _ => "Le service WMI a renvoyé une erreur (" + ex.ErrorCode + ").",
        };

        private static bool IsWmiFailure(Exception ex) =>
            ex is ManagementException ||
            ex is COMException ||
            ex is UnauthorizedAccessException ||
            ex is InvalidOperationException ||
            ex is TypeInitializationException;

        private static string Path(string @namespace) => @"\\.\" + @namespace.TrimStart('\\', '.');

        private static void GuardAgainstForbiddenClasses(string query)
        {
            foreach (var forbidden in ForbiddenClasses)
            {
                if (query.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0) continue;
                throw new InvalidOperationException(
                    "Requête interdite : " + forbidden + ". Énumérer cette classe déclenche une " +
                    "reconfiguration MSI de chaque logiciel installé. Utiliser la clé de registre " +
                    "Uninstall pour lister les logiciels.");
            }
        }
    }
}
