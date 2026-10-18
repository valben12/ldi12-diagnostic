using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LDI12.Assistant;
using LDI12.Core.Execution;

namespace LDI12.App.Services
{
    /// <summary>
    /// Ce que l'assistant relit à l'instant : l'état qui change pendant un dépannage, et que le
    /// diagnostic du début ne peut pas dire (le son après une opération, le réseau après une
    /// réinitialisation, un périphérique après sa réinstallation).
    /// </summary>
    /// <remarks>
    /// Lecture seule, en texte court : chaque ligne est renvoyée à toutes les étapes suivantes.
    /// Le réseau se lit par les classes du framework, le reste par la passerelle native : aucun
    /// script, aucun outil en ligne de commande.
    /// </remarks>
    public sealed class LiveMachine : ILiveMachine
    {
        private readonly ISystemControl _control;

        public LiveMachine(ISystemControl control) => _control = control ?? throw new ArgumentNullException(nameof(control));

        public string Read(string what, string? target) => (what ?? string.Empty).ToLowerInvariant() switch
        {
            "audio" => Audio(),
            "network" => Network(),
            "device" => Device(target),
            "service" => Service(target),
            _ => "Relevé inconnu : audio, network, device ou service.",
        };

        /// <summary>Les sorties et entrées audio, la sortie par défaut en tête de chaque groupe.</summary>
        public string Audio()
        {
            var endpoints = _control.AudioEndpoints();
            if (endpoints.Count == 0) return "Aucune sortie ni entrée audio déclarée, ou le service Audio Windows ne répond pas (état : "
                                              + (_control.ServiceStatus("Audiosrv") ?? "absent") + ").";

            var text = new StringBuilder();
            text.AppendLine("Service Audio Windows : " + (_control.ServiceStatus("Audiosrv") ?? "absent")
                            + " ; générateur de points de terminaison : " + (_control.ServiceStatus("AudioEndpointBuilder") ?? "absent"));
            foreach (var group in endpoints.GroupBy(e => e.IsCapture))
            {
                text.AppendLine(group.Key ? "Entrées (micros) :" : "Sorties :");
                foreach (var endpoint in group.OrderByDescending(e => e.IsDefault).ThenBy(e => e.State != "active"))
                    text.Append("- ").Append(endpoint.Name).Append(" | ").Append(endpoint.State)
                        .Append(endpoint.IsDefault ? " | PAR DÉFAUT" : string.Empty)
                        .Append(endpoint.IsDefaultCommunications && !endpoint.IsDefault ? " | défaut communications" : string.Empty)
                        .Append(endpoint.VolumePercent.HasValue ? " | volume " + endpoint.VolumePercent + " %" : string.Empty)
                        .Append(endpoint.Muted == true ? " | COUPÉ" : string.Empty)
                        .Append(" | endpoint ").AppendLine(endpoint.Id);
            }
            return text.ToString();
        }

        /// <summary>Les cartes réseau en service, leurs adresses, et un essai de connexion qui dit où ça coince.</summary>
        public static string Network()
        {
            var text = new StringBuilder();
            NetworkInterface[] adapters;
            try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException ex) { return "Cartes réseau illisibles : " + ex.Message; }

            IPAddress? gateway = null;
            foreach (var adapter in adapters.Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                                         && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel))
            {
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString());
                var gateways = properties.GatewayAddresses.Select(g => g.Address).Where(g => g.AddressFamily == AddressFamily.InterNetwork).ToList();
                var dns = properties.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString());
                if (adapter.OperationalStatus == OperationalStatus.Up && gateways.Count > 0) gateway ??= gateways[0];

                text.Append("- ").Append(adapter.Name).Append(" (").Append(adapter.Description).Append(") | ")
                    .Append(adapter.OperationalStatus).Append(" | ").Append(adapter.NetworkInterfaceType)
                    .Append(adapter.Speed > 0 && adapter.OperationalStatus == OperationalStatus.Up ? " | " + adapter.Speed / 1_000_000 + " Mb/s" : string.Empty)
                    .Append(" | IP ").Append(string.Join(", ", addresses)).Append(" | passerelle ").Append(string.Join(", ", gateways))
                    .Append(" | DNS ").AppendLine(string.Join(", ", dns));
            }

            text.AppendLine("Essai : passerelle " + Ping(gateway) + " ; Internet (1.1.1.1) " + Ping(IPAddress.Parse("1.1.1.1"))
                            + " ; résolution de noms " + Resolve("www.microsoft.com"));
            return text.ToString();
        }

        private string Device(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Périphérique non désigné : donnez son id.";
            var device = _control.FindDevice(id!);
            return device == null
                ? "Ce périphérique n'existe plus sur ce PC (débranché, désinstallé, ou id erroné)."
                : device.Name + " | classe " + (device.Class ?? "?") + " | " + (device.ProblemCode == 0 ? "fonctionne" : device.IsDisabled ? "désactivé" : "en erreur, code " + device.ProblemCode);
        }

        private string Service(string? name)
            => string.IsNullOrWhiteSpace(name) ? "Service non désigné." : name + " : " + (_control.ServiceStatus(name!) ?? "absent de ce PC");

        private static string Ping(IPAddress? address)
        {
            if (address == null) return "sans objet (aucune passerelle)";
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(address, 1500);
                return reply.Status == IPStatus.Success ? "répond (" + reply.RoundtripTime + " ms)" : "ne répond pas (" + reply.Status + ")";
            }
            catch (Exception ex) when (ex is PingException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                return "essai impossible";
            }
        }

        private static string Resolve(string host)
        {
            try { return Dns.GetHostAddresses(host).Length > 0 ? "fonctionne" : "ne donne rien"; }
            catch (SocketException ex) { return "échoue (" + ex.SocketErrorCode + ")"; }
        }
    }
}
