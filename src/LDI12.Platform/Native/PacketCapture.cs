using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LDI12.Core.Network;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Une capture de trafic sans pilote : une prise « brute » mise en réception de tout le trafic
    /// IP de l'interface (SIO_RCVALL), comme le faisaient les analyseurs avant Npcap. Windows y
    /// livre les paquets entrants et sortants de cette adresse. Réservé aux administrateurs.
    /// </summary>
    /// <remarks>
    /// Pas d'en-tête Ethernet, pas d'ARP : seulement IPv4. C'est assez pour savoir qui parle à
    /// qui, par quel service, et quels noms le PC demande ; le fichier pcap écrit en parallèle
    /// s'ouvre dans Wireshark pour aller plus loin.
    /// </remarks>
    public static class PacketCapture
    {
        /// <summary>Au-delà, la capture s'arrête d'elle-même : un fichier de travail, pas un enregistreur.</summary>
        private const long MaxBytes = 200L * 1024 * 1024;

        public static TrafficSummary Capture(string localAddress, TimeSpan duration, string? pcapPath, CancellationToken cancellationToken)
        {
            if (!IPAddress.TryParse(localAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Adresse IPv4 attendue.", nameof(localAddress));

            var analyzer = new TrafficAnalyzer(localAddress);
            var clock = Stopwatch.StartNew();
            var truncated = false;
            PcapWriter? pcap = null;
            try
            {
                if (pcapPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pcapPath)!);
                    pcap = new PcapWriter(new FileStream(pcapPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                }

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
                socket.ReceiveTimeout = 500;
                socket.ReceiveBufferSize = 4 * 1024 * 1024;

                var buffer = new byte[65535];
                long written = 0;
                while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
                {
                    int length;
                    try { length = socket.Receive(buffer); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
                    if (length <= 0) continue;

                    analyzer.Add(buffer, length);
                    if (pcap != null && written < MaxBytes)
                    {
                        pcap.Write(DateTime.UtcNow, buffer, length);
                        written += length + 16;
                    }
                    else if (pcap != null) truncated = true;
                }
            }
            finally
            {
                pcap?.Dispose();
            }

            var summary = analyzer.Summarize(clock.Elapsed);
            summary.PcapPath = pcapPath;
            summary.Truncated = truncated;
            return summary;
        }
    }
}
