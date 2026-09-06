using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LDI12.Core.Execution;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// La table de routage IPv4, par <c>GetIpForwardTable</c>.
    /// </summary>
    /// <remarks>
    /// <b>Pourquoi l'ancienne fonction et pas <c>GetIpForwardTable2</c>.</b> La seconde rend aussi
    /// l'IPv6, au prix de structures portant des unions d'adresses dont la disposition change
    /// entre les versions de l'en-tête. La première est figée depuis Windows 2000, ne contient que
    /// des entiers de quatre octets, et couvre l'intégralité des pannes que ce logiciel cherche à
    /// expliquer. Une mesure dont on connaît la portée vaut mieux qu'une mesure large et fausse
    /// par endroits.
    /// <para>
    /// Aucun privilège n'est requis : la table de routage se lit depuis une session ordinaire.
    /// </para>
    /// </remarks>
    internal static class RouteTableNative
    {
        private const int NoError = 0;
        private const int ErrorInsufficientBuffer = 122;

        /// <summary>Quatorze entiers de quatre octets, figés depuis Windows 2000.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_IPFORWARDROW
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public uint dwForwardMetric1;
            public uint dwForwardMetric2;
            public uint dwForwardMetric3;
            public uint dwForwardMetric4;
            public uint dwForwardMetric5;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

        /// <summary>Protocole ayant installé la route, tel que la pile IP le note.</summary>
        private const uint ProtoLocal = 2;
        private const uint ProtoNetMgmt = 3;
        private const uint ProtoIcmp = 4;
        private const uint ProtoNtAutostatic = 10002;
        private const uint ProtoNtStatic = 10006;
        private const uint ProtoNtStaticNonDod = 10007;

        public static IReadOnlyList<IpRoute> Read()
        {
            var size = 0;
            var probe = GetIpForwardTable(IntPtr.Zero, ref size, true);

            // Une table vide est une réponse, pas une erreur : une machine sans aucune carte
            // active en a une.
            if (probe != ErrorInsufficientBuffer && probe != NoError)
                throw new InvalidOperationException("GetIpForwardTable a répondu " + probe + ".");

            if (size <= 0) return Array.Empty<IpRoute>();

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var read = GetIpForwardTable(buffer, ref size, true);
                if (read != NoError)
                    throw new InvalidOperationException("GetIpForwardTable a répondu " + read + ".");

                var count = Marshal.ReadInt32(buffer);
                if (count <= 0) return Array.Empty<IpRoute>();

                var rowSize = Marshal.SizeOf(typeof(MIB_IPFORWARDROW));
                var routes = new List<IpRoute>(count);

                for (var index = 0; index < count; index++)
                {
                    var at = new IntPtr(buffer.ToInt64() + 4 + ((long)index * rowSize));
                    var row = (MIB_IPFORWARDROW)Marshal.PtrToStructure(at, typeof(MIB_IPFORWARDROW))!;

                    routes.Add(new IpRoute
                    {
                        Destination = Address(row.dwForwardDest),
                        Mask = Address(row.dwForwardMask),
                        NextHop = Address(row.dwForwardNextHop),
                        InterfaceIndex = (int)row.dwForwardIfIndex,
                        Metric = (int)row.dwForwardMetric1,
                        Protocol = Protocol(row.dwForwardProto),
                    });
                }

                return routes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Les adresses arrivent dans l'ordre du réseau, quatre octets de poids fort en tête.
        /// </summary>
        private static string Address(uint value)
            => (value & 0xFF) + "." + ((value >> 8) & 0xFF) + "." +
               ((value >> 16) & 0xFF) + "." + ((value >> 24) & 0xFF);

        private static IpRouteProtocol Protocol(uint proto)
        {
            switch (proto)
            {
                case ProtoLocal: return IpRouteProtocol.Local;
                case ProtoNetMgmt: return IpRouteProtocol.Configured;
                case ProtoIcmp: return IpRouteProtocol.Redirect;
                case ProtoNtAutostatic:
                case ProtoNtStatic:
                case ProtoNtStaticNonDod: return IpRouteProtocol.Static;
                default: return IpRouteProtocol.Other;
            }
        }
    }
}
