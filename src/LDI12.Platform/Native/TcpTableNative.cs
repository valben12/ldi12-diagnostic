using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les points d'écoute et les connexions TCP, avec le processus qui les tient.
    /// </summary>
    /// <remarks>
    /// <c>GetExtendedTcpTable</c> et rien d'autre : c'est la seule interface qui rende, en une
    /// fois, le port et l'identifiant du processus propriétaire. La classe .NET
    /// <c>IPGlobalProperties</c> rend bien les points d'écoute, mais sans dire à qui ils sont,
    /// et « le port 3389 est ouvert » sans savoir par quoi n'apprend rien à personne.
    /// <para>
    /// Aucun privilège n'est nécessaire pour lister ses propres connexions et les points
    /// d'écoute ; l'identifiant du processus est rendu pour tous, son nom seulement pour ceux
    /// que la session peut ouvrir. C'est dit plutôt que comblé.
    /// </para>
    /// </remarks>
    public sealed class TcpTableNative : ITcpTableApi
    {
        private const string Category = "Platform.Tcp";

        /// <summary>TCP_TABLE_OWNER_PID_ALL : toutes les connexions, avec leur propriétaire.</summary>
        private const int OwnerPidAll = 5;

        private const int AfInet = 2;

        private const int NoError = 0;
        private const int InsufficientBuffer = 122;

        /// <summary>État MIB_TCP_STATE_LISTEN : le point d'écoute.</summary>
        private const int StateListen = 2;

        /// <summary>État MIB_TCP_STATE_ESTAB : une connexion établie.</summary>
        private const int StateEstablished = 5;

        private readonly IScopedLogger _log;

        public TcpTableNative(ILdiLogger logger)
        {
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public Measured<TcpTable> Read()
        {
            var buffer = IntPtr.Zero;

            try
            {
                var size = 0;
                var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPidAll, 0);

                if (status != InsufficientBuffer && status != NoError)
                    return Failed(status);

                buffer = Marshal.AllocHGlobal(size);
                status = GetExtendedTcpTable(buffer, ref size, false, AfInet, OwnerPidAll, 0);
                if (status != NoError) return Failed(status);

                return Measured.Ok(Decode(buffer), DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException ||
                                       ex is OutOfMemoryException || ex is SEHException)
            {
                _log.Warn("La table des connexions TCP n'a pas pu être lue.", ex);
                return Measured.Missing<TcpTable>("La table des connexions TCP n'a pas pu être lue.");
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static Measured<TcpTable> Failed(int status)
            => Measured.Missing<TcpTable>(
                "La table des connexions TCP a été refusée par le système (code " +
                status.ToString(CultureInfo.InvariantCulture) + ").");

        private static TcpTable Decode(IntPtr buffer)
        {
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            var cursor = buffer + sizeof(int);

            var listeners = new List<TcpListener>();
            var established = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(cursor);
                cursor += rowSize;

                if (row.State == StateEstablished) { established++; continue; }
                if (row.State != StateListen) continue;

                var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString();
                var port = Port(row.LocalPort);

                // Un même service écoute souvent sur plusieurs adresses de la machine : la ligne
                // qui compte est « ce port est ouvert, par ce programme », pas l'énumération des
                // interfaces sur lesquelles il l'est.
                var key = port.ToString(CultureInfo.InvariantCulture) + "/" + row.OwningPid;
                if (!seen.Add(key)) continue;

                listeners.Add(new TcpListener
                {
                    Port = port,
                    Address = address,
                    ProcessId = row.OwningPid,
                    AllInterfaces = string.Equals(address, "0.0.0.0", StringComparison.Ordinal),
                });
            }

            return new TcpTable { Listeners = listeners, EstablishedCount = established };
        }

        /// <summary>Le port est écrit en gros-boutiste dans les deux octets de poids faible.</summary>
        private static int Port(uint value) => ((int)(value & 0xFF) << 8) | (int)((value >> 8) & 0xFF);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(
            IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

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
    }
}
