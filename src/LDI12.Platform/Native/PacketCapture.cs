using System;
using System.Collections.Generic;
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
        /// <summary>Au-delà, le fichier pour Wireshark n'est plus complété ; l'écoute et les statistiques continuent.</summary>
        private const long MaxBytes = 1024L * 1024 * 1024;

        /// <summary>Paquets listés par envoi à l'écran : au-delà, ils sont comptés sans être détaillés.</summary>
        private const int MaxPerBatch = 400;

        private static readonly TimeSpan BatchEvery = TimeSpan.FromMilliseconds(500);

        public static TrafficSummary Capture(string localAddress, TimeSpan duration, string? pcapPath, CancellationToken cancellationToken)
            => Capture(localAddress, duration, pcapPath, null, cancellationToken);

        /// <summary>
        /// Écoute jusqu'à la durée ou l'arrêt, et envoie à <paramref name="live"/>, deux fois par
        /// seconde, les paquets arrivés depuis l'envoi précédent et le résumé à jour.
        /// </summary>
        public static TrafficSummary Capture(string localAddress, TimeSpan duration, string? pcapPath, Action<TrafficLive>? live, CancellationToken cancellationToken)
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
                socket.ReceiveTimeout = 250;
                socket.ReceiveBufferSize = 4 * 1024 * 1024;

                var buffer = new byte[65535];
                long written = 0;
                var batch = new List<PacketRecord>();
                long skipped = 0;
                var sent = TimeSpan.Zero;
                while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
                {
                    if (live != null && clock.Elapsed - sent >= BatchEvery)
                    {
                        sent = clock.Elapsed;
                        live(new TrafficLive { Packets = batch, Skipped = skipped, Summary = analyzer.Summarize(clock.Elapsed, top: 10) });
                        batch = new List<PacketRecord>();
                        skipped = 0;
                    }

                    int length;
                    try { length = socket.Receive(buffer); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
                    if (length <= 0) continue;

                    var record = analyzer.Describe(buffer, length, clock.Elapsed.TotalSeconds);
                    if (record != null && live != null)
                    {
                        if (batch.Count < MaxPerBatch) batch.Add(record);
                        else skipped++;
                    }
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
