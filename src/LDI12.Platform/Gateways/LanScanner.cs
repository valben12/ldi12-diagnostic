using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Network;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    /// <summary>Un réseau sur lequel ce PC est branché.</summary>
    public sealed class LanSegment
    {
        public string Adapter { get; init; } = string.Empty;
        public IPAddress Address { get; init; } = IPAddress.None;
        public int PrefixLength { get; init; }
        public IPAddress? Gateway { get; init; }

        public string Describe() => Adapter + " · " + Address + "/" + PrefixLength;
    }

    /// <summary>
    /// Le balayage du réseau local, à la manière d'Advanced IP Scanner : chaque adresse de la
    /// plage est interrogée (ping et ARP en parallèle), puis chaque appareil trouvé est décrit :
    /// nom, nom Windows, fabricant, ports ouverts, dossiers partagés.
    /// </summary>
    /// <remarks>
    /// L'ARP trouve aussi ce qui ne répond pas au ping (téléphones, PC au pare-feu fermé). Tout
    /// est borné dans le temps : un /24 se balaie en quelques secondes.
    /// </remarks>
    public static class LanScanner
    {
        /// <summary>Les réseaux de ce PC, celui qui a une passerelle en premier.</summary>
        public static IReadOnlyList<LanSegment> Segments()
        {
            var segments = new List<LanSegment>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }

                var gateway = properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // adresse automatique : pas de réseau réel
                    segments.Add(new LanSegment
                    {
                        Adapter = adapter.Name,
                        Address = unicast.Address,
                        PrefixLength = Prefix(unicast),
                        Gateway = gateway,
                    });
                }
            }
            return segments.OrderByDescending(s => s.Gateway != null).ToList();
        }

        private static int Prefix(UnicastIPAddressInformation unicast)
        {
            try
            {
                var mask = unicast.IPv4Mask?.GetAddressBytes();
                if (mask == null) return 24;
                return mask.Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
            }
            catch (NotImplementedException) { return 24; }
        }

        public static async Task<IReadOnlyList<LanDevice>> ScanAsync(
            LanSegment segment, OuiDatabase? vendors, IProgress<(int Done, int Total, LanDevice? Found)>? progress, CancellationToken cancellationToken)
        {
            var targets = LanClassifier.Range(segment.Address, segment.PrefixLength);
            var found = new List<LanDevice>();
            var gate = new SemaphoreSlim(64);
            var done = 0;

            async Task Probe(IPAddress target)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var self = target.Equals(segment.Address);
                    int? roundTrip = null;
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(target, 700).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success) roundTrip = (int)reply.RoundtripTime;
                    }
                    catch (Exception ex) when (ex is PingException || ex is InvalidOperationException) { }

                    var mac = await Task.Run(() => LanNative.Mac(target), cancellationToken).ConfigureAwait(false);
                    if (roundTrip == null && mac == null && !self) return;

                    var device = new LanDevice
                    {
                        Address = target.ToString(),
                        Mac = mac,
                        RoundTripMs = roundTrip,
                        IsSelf = self,
                        IsGateway = segment.Gateway != null && target.Equals(segment.Gateway),
                    };
                    lock (found) found.Add(device);
                    progress?.Report((done, targets.Count, device));
                }
                finally
                {
                    Interlocked.Increment(ref done);
                    progress?.Report((done, targets.Count, null));
                    gate.Release();
                }
            }

            await Task.WhenAll(targets.Select(Probe)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Deuxième temps, sur ce qui a répondu seulement : noms, ports, partages.
            var details = new SemaphoreSlim(16);
            await Task.WhenAll(found.Select(async device =>
            {
                await details.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await DescribeAsync(device, vendors, cancellationToken).ConfigureAwait(false); }
                finally { details.Release(); }
            })).ConfigureAwait(false);

            return found.OrderBy(d => d.SortKey).ToList();
        }

        private static async Task DescribeAsync(LanDevice device, OuiDatabase? vendors, CancellationToken cancellationToken)
        {
            var address = IPAddress.Parse(device.Address);
            if (device.Mac == null && device.IsSelf)
                device.Mac = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address)))
                    .Select(n => string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))))
                    .FirstOrDefault(m => m.Length > 0);
            device.Vendor = vendors?.Find(device.Mac);

            var dns = Task.Run(() =>
            {
                try { return Dns.GetHostEntry(address).HostName; }
                catch (Exception ex) when (ex is SocketException || ex is ArgumentException) { return null; }
            }, cancellationToken);
            var netbios = LanNative.NetBiosAsync(address, 800, cancellationToken);
            var ports = Task.WhenAll(LanClassifier.Ports.Select(port => Open(address, port, 400)));

            var finished = await Task.WhenAny(dns, Task.Delay(1500, cancellationToken)).ConfigureAwait(false);
            if (finished == dns && dns.Result != null && dns.Result != device.Address) device.HostName = dns.Result;
            var (name, group) = await netbios.ConfigureAwait(false);
            device.NetBiosName = name;
            device.Workgroup = group;
            var open = await ports.ConfigureAwait(false);
            for (var index = 0; index < open.Length; index++)
                if (open[index]) device.OpenPorts.Add(LanClassifier.Ports[index]);

            if (device.OpenPorts.Contains(445) && !device.IsSelf)
                foreach (var share in await Task.Run(() => LanNative.Shares(device.Address), cancellationToken).ConfigureAwait(false))
                    device.Shares.Add(share);

            device.Kind = LanClassifier.Classify(device);
        }

        private static async Task<bool> Open(IPAddress address, int port, int timeoutMs)
        {
            using var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(address, port);
                var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != connect) return false;
                await connect.ConfigureAwait(false);
                return client.Connected;
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return false;
            }
        }
    }
}
