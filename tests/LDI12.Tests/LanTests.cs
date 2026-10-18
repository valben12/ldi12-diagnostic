using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Network;
using Newtonsoft.Json;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>L'écran Réseau local : appareils, fabricants, plages d'adresses, et l'analyse du trafic capturé.</summary>
    public partial class NativeRepairTests
    {
        // ============================================================ appareils

        [Fact]
        public void La_plage_d_un_reseau_exclut_l_adresse_du_reseau_et_celle_de_diffusion()
        {
            var range = LanClassifier.Range(IPAddress.Parse("192.168.1.24"), 24);

            Assert.Equal(254, range.Count);
            Assert.Equal("192.168.1.1", range.First().ToString());
            Assert.Equal("192.168.1.254", range.Last().ToString());
        }

        [Fact]
        public void Un_tres_grand_reseau_se_limite_aux_1024_adresses_autour_du_pc()
        {
            var range = LanClassifier.Range(IPAddress.Parse("10.20.30.40"), 8);

            Assert.Equal(1022, range.Count);
            Assert.Equal("10.20.28.1", range.First().ToString());
        }

        [Theory]
        [InlineData(new[] { 9100, 80 }, LanDeviceKind.Printer)]
        [InlineData(new[] { 445, 3389 }, LanDeviceKind.Computer)]
        [InlineData(new[] { 5000, 445 }, LanDeviceKind.Storage)]
        [InlineData(new[] { 62078 }, LanDeviceKind.Phone)]
        [InlineData(new[] { 8009 }, LanDeviceKind.MediaPlayer)]
        public void Les_ports_ouverts_disent_ce_qu_est_l_appareil(int[] ports, LanDeviceKind expected)
        {
            var device = new LanDevice { Address = "192.168.1.50" };
            device.OpenPorts.AddRange(ports);
            Assert.Equal(expected, LanClassifier.Classify(device));
        }

        [Fact]
        public void La_passerelle_est_la_box_et_une_adresse_mac_aleatoire_un_telephone()
        {
            Assert.Equal(LanDeviceKind.Router, LanClassifier.Classify(new LanDevice { Address = "192.168.1.1", IsGateway = true }));
            var phone = new LanDevice { Address = "192.168.1.57", Mac = "DA:A1:19:5E:6F:70" };
            Assert.True(phone.HasRandomMac);
            Assert.Equal(LanDeviceKind.Phone, LanClassifier.Classify(phone));
        }

        [Fact]
        public void La_base_ieee_donne_le_fabricant_d_une_adresse_mac()
        {
            var csv = "Registry,Assignment,Organization Name,Organization Address\n" +
                      "MA-L,30055C,\"Brother Industries, LTD.\",\"15-1 Naeshiro-cho Nagoya JP\"\n" +
                      "MA-L,001132,Synology Incorporated,\"Taipei TW\"\n";

            var database = OuiDatabase.Parse(new StringReader(csv));

            Assert.Equal(2, database.Count);
            Assert.Equal("Brother Industries, LTD.", database.Find("30:05:5c:12:34:56"));
            Assert.Equal("Synology Incorporated", database.Find("00-11-32-4A-5B-6C"));
            Assert.Null(database.Find("AA:BB:CC:00:00:00"));
        }

        [Fact]
        public void La_reponse_netbios_donne_le_nom_du_pc_et_son_groupe_de_travail()
        {
            var data = new byte[57 + 3 * 18];
            data[56] = 3;
            void Name(int index, string text, byte suffix, bool group)
            {
                var at = 57 + index * 18;
                Encoding.ASCII.GetBytes(text.PadRight(15)).CopyTo(data, at);
                data[at + 15] = suffix;
                data[at + 16] = (byte)(group ? 0x84 : 0x04);
            }
            Name(0, "COMPTA-01", 0x00, false);
            Name(1, "WORKGROUP", 0x00, true);
            Name(2, "COMPTA-01", 0x20, false);

            var (name, workgroup) = LDI12.Platform.Native.LanNative.ParseNbstat(data);

            Assert.Equal("COMPTA-01", name);
            Assert.Equal("WORKGROUP", workgroup);
        }

        // ============================================================ trafic

        [Fact]
        public void Le_trafic_se_resume_par_correspondant_service_et_nom_demande()
        {
            var analyzer = new TrafficAnalyzer("192.168.1.24");
            var query = Dns(0x1234, "www.example.com", response: false);
            var answer = Dns(0x1234, "www.example.com", response: true);

            Add(analyzer, Ip(17, "192.168.1.24", "192.168.1.1", Udp(53000, 53, query)));
            Add(analyzer, Ip(17, "192.168.1.1", "192.168.1.24", Udp(53, 53000, answer)));
            Add(analyzer, Ip(6, "192.168.1.24", "93.184.216.34", Tcp(52100, 443, 1200)));
            Add(analyzer, Ip(6, "93.184.216.34", "192.168.1.24", Tcp(443, 52100, 1400)));

            var summary = analyzer.Summarize(TimeSpan.FromSeconds(5));

            Assert.Equal(4, summary.Packets);
            var top = summary.Hosts.First();
            Assert.Equal("93.184.216.34", top.Key);
            Assert.Equal("www.example.com", top.Label);            // nommé par la réponse DNS vue passer
            Assert.Equal(2, top.Packets);
            Assert.Contains(summary.Services, s => s.Key == "TCP 443" && s.Label == "HTTPS" && s.Packets == 2);
            Assert.Contains(summary.Services, s => s.Key == "UDP 53" && s.Label == "DNS");
            Assert.Equal("www.example.com", Assert.Single(summary.DnsQueries).Key);
            Assert.Equal(new[] { "TCP", "UDP" }, summary.Protocols.Select(p => p.Key).OrderBy(k => k));
        }

        [Fact]
        public void Le_fichier_pcap_porte_l_entete_que_wireshark_attend()
        {
            using var stream = new MemoryStream();
            var packet = Ip(6, "192.168.1.24", "93.184.216.34", Tcp(52100, 443, 0));
            using (var writer = new PcapWriter(new NonClosing(stream)))
                writer.Write(new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc), packet, packet.Length);

            var bytes = stream.ToArray();
            Assert.Equal(24 + 16 + packet.Length, bytes.Length);
            Assert.Equal(0xA1B2C3D4u, BitConverter.ToUInt32(bytes, 0));
            Assert.Equal(101u, BitConverter.ToUInt32(bytes, 20));                // IP brut
            Assert.Equal((uint)packet.Length, BitConverter.ToUInt32(bytes, 24 + 8));
        }

        [Fact]
        public async Task La_capture_passe_par_l_hote_eleve_et_rend_un_resume_lisible()
        {
            var control = new FakeControl();
            var context = Context(control, (TrafficCaptureAction.AddressParameter, "192.168.1.24"), (TrafficCaptureAction.SecondsParameter, "999"));
            var action = new TrafficCaptureAction();

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(new[] { "capture 192.168.1.24 300" }, control.Calls);   // borné à cinq minutes
            Assert.Equal(12, JsonConvert.DeserializeObject<TrafficSummary>(outcome.RawOutput!)!.Packets);
        }

        // ------------------------------------------------------------ fabrication de paquets

        private static void Add(TrafficAnalyzer analyzer, byte[] packet) => analyzer.Add(packet, packet.Length);

        private static byte[] Ip(byte protocol, string source, string destination, byte[] payload)
        {
            var packet = new byte[20 + payload.Length];
            packet[0] = 0x45;
            packet[2] = (byte)(packet.Length >> 8);
            packet[3] = (byte)packet.Length;
            packet[8] = 64;
            packet[9] = protocol;
            IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
            IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
            payload.CopyTo(packet, 20);
            return packet;
        }

        private static byte[] Tcp(int source, int destination, int data)
        {
            var segment = new byte[20 + data];
            segment[0] = (byte)(source >> 8); segment[1] = (byte)source;
            segment[2] = (byte)(destination >> 8); segment[3] = (byte)destination;
            segment[12] = 0x50;
            return segment;
        }

        private static byte[] Udp(int source, int destination, byte[] data)
        {
            var datagram = new byte[8 + data.Length];
            datagram[0] = (byte)(source >> 8); datagram[1] = (byte)source;
            datagram[2] = (byte)(destination >> 8); datagram[3] = (byte)destination;
            datagram[4] = (byte)(datagram.Length >> 8); datagram[5] = (byte)datagram.Length;
            data.CopyTo(datagram, 8);
            return datagram;
        }

        /// <summary>Une requête DNS de type A, ou sa réponse avec une adresse (nom compressé par pointeur).</summary>
        private static byte[] Dns(int id, string name, bool response)
        {
            var bytes = new List<byte> { (byte)(id >> 8), (byte)id, (byte)(response ? 0x81 : 0x01), (byte)(response ? 0x80 : 0x00), 0, 1, 0, (byte)(response ? 1 : 0), 0, 0, 0, 0 };
            foreach (var label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(label));
            }
            bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            if (response)
                bytes.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4, 93, 184, 216, 34 });
            return bytes.ToArray();
        }

        private sealed class NonClosing : Stream
        {
            private readonly Stream _inner;
            public NonClosing(Stream inner) => _inner = inner;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        }
    }
}
