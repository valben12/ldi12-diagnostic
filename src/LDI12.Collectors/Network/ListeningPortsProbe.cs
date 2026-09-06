using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Network
{
    /// <summary>
    /// Ce que la machine écoute, et par quel programme.
    /// </summary>
    /// <remarks>
    /// <b>La question qu'on se pose sur tout poste dont on ne sait rien.</b> Un bureau à distance
    /// laissé ouvert, un serveur web installé par un logiciel métier, un partage de fichiers
    /// exposé sur le réseau de l'entreprise entière : rien de tout cela ne se voit dans une
    /// interface de Windows, et tout se lit en une requête.
    /// <para>
    /// Le nom du programme n'est pas toujours accessible : un processus d'un autre compte ou du
    /// système ne s'ouvre pas depuis une session ordinaire. L'identifiant, lui, est toujours
    /// rendu, et c'est déjà de quoi retrouver le coupable dans le gestionnaire des tâches.
    /// </para>
    /// </remarks>
    public sealed class ListeningPortsProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.ListeningPorts,
            DisplayName = "Ports en écoute",
            Category = DiagnosticCategory.Network,
            EstimatedDuration = TimeSpan.FromMilliseconds(300),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var table = context.Tcp.Read();
            if (!table.HasValue)
            {
                context.Draft.SetListeningPorts(new ListeningPortsInfo
                {
                    Count = Measured.Missing<int>(table.Reason ?? "La table des connexions n'a pas pu être lue."),
                    Established = Measured.Missing<int>(table.Reason ?? "La table des connexions n'a pas pu être lue."),
                });

                return Task.FromResult(ProbeOutcome.Partial(
                    table.Reason ?? "La table des connexions n'a pas pu être lue."));
            }

            var names = new Dictionary<int, string?>();
            var ports = new List<ListeningPort>();

            foreach (var listener in table.Value.Listeners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!names.TryGetValue(listener.ProcessId, out var name))
                {
                    name = ProcessName(listener.ProcessId);
                    names[listener.ProcessId] = name;
                }

                ports.Add(new ListeningPort
                {
                    Port = listener.Port,
                    Address = listener.Address,
                    ProcessId = listener.ProcessId,
                    ProcessName = name == null
                        ? Measured.Missing<string>(
                            "Ce programme appartient à un autre compte ou au système : son nom ne se lit " +
                            "pas depuis cette session.")
                        : Measured.Ok(name, DataSource.NativeApi),
                    AllInterfaces = listener.AllInterfaces,
                    Service = WellKnown.Describe(listener.Port),
                });
            }

            ports.Sort((left, right) => left.Port.CompareTo(right.Port));

            var exposed = 0;
            foreach (var port in ports) if (port.AllInterfaces) exposed++;

            context.Draft.SetListeningPorts(new ListeningPortsInfo
            {
                Ports = ports,
                Count = Measured.Ok(ports.Count, DataSource.NativeApi),
                Established = Measured.Ok(table.Value.EstablishedCount, DataSource.NativeApi),
            });

            return Task.FromResult(ProbeOutcome.Ok(
                ports.Count + " port(s) en écoute, dont " + exposed + " ouvert(s) sur le réseau, " +
                table.Value.EstablishedCount + " connexion(s) établie(s)"));
        }

        /// <summary>
        /// Nom du programme derrière un identifiant de processus.
        /// </summary>
        /// <remarks>
        /// Peut échouer pour deux raisons également ordinaires : le processus appartient à un
        /// autre compte, ou il s'est terminé entre la lecture de la table et cette question. Les
        /// deux donnent la même absence, et le motif ne prétend pas les distinguer.
        /// </remarks>
        private static string? ProcessName(int processId)
        {
            if (processId <= 0) return null;

            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
