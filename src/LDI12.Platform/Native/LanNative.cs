using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Network;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Ce qu'Advanced IP Scanner demande à chaque adresse : l'ARP pour l'adresse matérielle (même
    /// d'un appareil qui ne répond pas au ping), le nom NetBIOS, les dossiers partagés ; et le
    /// réveil par le réseau. Plus la table des connexions de ce PC, programme par programme.
    /// </summary>
    public static class LanNative
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destination, uint source, byte[] mac, ref int length);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref int resume);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShareInfo1
        {
            public string Name;
            public uint Type;
            public string Remark;
        }

        /// <summary>L'adresse matérielle, ou nul si l'appareil n'est pas sur le même segment ou ne répond pas.</summary>
        public static string? Mac(IPAddress address)
        {
            try
            {
                var mac = new byte[6];
                var length = mac.Length;
                var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
                if (SendARP(destination, 0, mac, ref length) != 0 || length < 6) return null;
                if (mac.All(b => b == 0)) return null;
                return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Les dossiers partagés visibles (ni administratifs ni cachés).</summary>
        public static IReadOnlyList<string> Shares(string host)
        {
            var shares = new List<string>();
            var resume = 0;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (NetShareEnum(host, 1, out buffer, -1, out var read, out _, ref resume) != 0) return shares;
                var size = Marshal.SizeOf(typeof(ShareInfo1));
                for (var index = 0; index < read; index++)
                {
                    var info = (ShareInfo1)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + index * size), typeof(ShareInfo1))!;
                    var type = info.Type & 0xFF;
                    if (info.Name == null || info.Name.EndsWith("$", StringComparison.Ordinal)) continue;
                    shares.Add(type == 1 ? info.Name + " (imprimante)" : info.Name);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
            return shares;
        }

        /// <summary>
        /// Le nom NetBIOS et le groupe de travail, par une requête « NBSTAT » sur le port 137 :
        /// c'est ainsi qu'un PC Windows dit son nom sans serveur DNS.
        /// </summary>
        public static async Task<(string? Name, string? Workgroup)> NetBiosAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken)
        {
            using var client = new UdpClient();
            var request = new byte[50];
            request[0] = 0x4C; request[1] = 0x44; // identifiant
            request[5] = 1;                       // une question
            request[12] = 0x20;                   // nom encodé de 32 octets
            request[13] = (byte)('C' + 0);        // « * » encodé : 0x2A → 'C','K' puis des 'A'
            request[14] = (byte)('A' + 0x0A);
            for (var index = 15; index < 45; index++) request[index] = (byte)'A';
            request[46] = 0x00; request[47] = 0x21; // type NBSTAT
            request[48] = 0x00; request[49] = 0x01; // classe IN
            try
            {
                await client.SendAsync(request, request.Length, new IPEndPoint(address, 137)).ConfigureAwait(false);
                var receive = client.ReceiveAsync();
                var finished = await Task.WhenAny(receive, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
                if (finished != receive) return (null, null);
                return ParseNbstat(receive.Result.Buffer);
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                return (null, null);
            }
        }

        internal static (string? Name, string? Workgroup) ParseNbstat(byte[] data)
        {
            // En-tête de 12 octets, nom de 34, type, classe, durée, longueur : 56 octets avant le nombre de noms.
            if (data.Length < 57) return (null, null);
            var count = data[56];
            string? name = null, group = null;
            for (var index = 0; index < count; index++)
            {
                var at = 57 + index * 18;
                if (at + 18 > data.Length) break;
                var text = Encoding.ASCII.GetString(data, at, 15).TrimEnd(' ', '\0');
                var suffix = data[at + 15];
                var isGroup = (data[at + 16] & 0x80) != 0;
                if (suffix == 0x00 && !isGroup && name == null) name = text;
                else if (suffix == 0x00 && isGroup && group == null) group = text;
            }
            return (name, group);
        }

        /// <summary>Le « paquet magique » du réveil par le réseau : six 0xFF, puis seize fois l'adresse matérielle.</summary>
        public static bool WakeOnLan(string mac)
        {
            var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length != 12) return false;
            var bytes = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
            var packet = new byte[102];
            for (var index = 0; index < 6; index++) packet[index] = 0xFF;
            for (var repeat = 0; repeat < 16; repeat++) Array.Copy(bytes, 0, packet, 6 + repeat * 6, 6);
            try
            {
                using var client = new UdpClient { EnableBroadcast = true };
                client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ connexions

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRow
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public int OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UdpRow
        {
            public uint LocalAddress;
            public uint LocalPort;
            public int OwningPid;
        }

        private static readonly string[] States =
        {
            "?", "fermée", "en écoute", "ouverture", "ouverture", "établie", "fermeture", "fermeture", "fermeture", "fermeture", "fermeture", "attente", "suppression",
        };

        /// <summary>Les connexions TCP et les points UDP de ce PC (IPv4), avec leur programme.</summary>
        public static IReadOnlyList<ConnectionEntry> Connections()
        {
            var entries = new List<ConnectionEntry>();
            foreach (var row in Read<TcpRow>((IntPtr t, ref int s) => GetExtendedTcpTable(t, ref s, true, 2, 5, 0)))
                entries.Add(new ConnectionEntry
                {
                    Protocol = "TCP",
                    LocalAddress = Address(row.LocalAddress),
                    LocalPort = Port(row.LocalPort),
                    RemoteAddress = row.State == 2 ? string.Empty : Address(row.RemoteAddress),
                    RemotePort = row.State == 2 ? 0 : Port(row.RemotePort),
                    State = row.State < States.Length ? States[row.State] : "?",
                    ProcessId = row.OwningPid,
                });
            foreach (var row in Read<UdpRow>((IntPtr t, ref int s) => GetExtendedUdpTable(t, ref s, true, 2, 1, 0)))
                entries.Add(new ConnectionEntry
                {
                    Protocol = "UDP",
                    LocalAddress = Address(row.LocalAddress),
                    LocalPort = Port(row.LocalPort),
                    ProcessId = row.OwningPid,
                });
            return entries;
        }

        private delegate uint TableCall(IntPtr table, ref int size);

        private static List<T> Read<T>(TableCall call) where T : struct
        {
            var rows = new List<T>();
            var size = 0;
            call(IntPtr.Zero, ref size);
            if (size <= 0) return rows;
            var buffer = Marshal.AllocHGlobal(size + 4096);
            try
            {
                size += 4096;
                if (call(buffer, ref size) != 0) return rows;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf(typeof(T));
                for (var index = 0; index < count; index++)
                    rows.Add((T)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + 4 + index * rowSize), typeof(T))!);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return rows;
        }

        private static string Address(uint value) => new IPAddress(value).ToString();

        private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
    }
}
