using System.Net;
using System.Runtime.InteropServices;
using DefenseClaw.Core.Net;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// Synthetic <c>GetExtendedTcpTable</c> answers in the <c>OWNER_MODULE</c> layout the inspector reads (CUST-392): a dword count, rows from byte 8,
/// <c>MIB_TCPROW_OWNER_MODULE</c> (160 bytes) for IPv4 and <c>MIB_TCP6ROW_OWNER_MODULE</c> (192 bytes) for IPv6, each with its socket's creation
/// time as a FILETIME. Nothing here reads the real table.
/// </summary>
internal static class ModuleTcpTables
{
    public const uint NoError = 0;
    public const uint InsufficientBuffer = 122;
    public const int AfInet = 2;
    public const int AfInet6 = 23;

    /// <summary>MIB_TCP_STATE_LISTEN.</summary>
    public const int Listen = 2;

    /// <summary>MIB_TCP_STATE_ESTAB.</summary>
    public const int Established = 5;

    /// <summary>One socket. <paramref name="Created"/> null writes a zero timestamp (Windows gave none).</summary>
    public readonly record struct Row(string Local, int LocalPort, string Remote, int RemotePort, int Pid, DateTime? Created, int State = Listen)
    {
        public int Family => IPAddress.Parse(Local).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? AfInet6 : AfInet;
    }

    /// <summary>A listening socket on 127.0.0.1 (or <paramref name="address"/>).</summary>
    public static Row Listener(int port, int pid, DateTime? created, string address = "127.0.0.1") =>
        new(address, port, address.Contains(':', StringComparison.Ordinal) ? "::" : "0.0.0.0", 0, pid, created);

    /// <summary>The bytes a table of <paramref name="count"/> rows of <paramref name="family"/> takes.</summary>
    public static int Size(int family, int count) =>
        PortOwnerInspector.TableRowsOffset + (count * (family == AfInet ? PortOwnerInspector.Ipv4RowBytes : PortOwnerInspector.Ipv6RowBytes));

    /// <summary>A reader that answers the size query and then fills the buffer with the rows of the family asked for.</summary>
    public static PortOwnerInspector.TcpTableReader Reader(params Row[] rows) =>
        (IntPtr table, ref int size, int family) =>
        {
            var mine = rows.Where(row => row.Family == family).ToArray();
            var needed = Size(family, mine.Length);
            if (table == IntPtr.Zero || size < needed)
            {
                size = needed;
                return InsufficientBuffer;
            }

            Write(table, family, mine);
            return NoError;
        };

    /// <summary>Writes <paramref name="rows"/> as a table of <paramref name="family"/> at <paramref name="table"/>.</summary>
    public static void Write(IntPtr table, int family, IReadOnlyList<Row> rows)
    {
        var rowBytes = family == AfInet ? PortOwnerInspector.Ipv4RowBytes : PortOwnerInspector.Ipv6RowBytes;
        var zero = new byte[Size(family, rows.Count)];
        Marshal.Copy(zero, 0, table, zero.Length);
        Marshal.WriteInt32(table, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var at = table + PortOwnerInspector.TableRowsOffset + (i * rowBytes);
            var row = rows[i];
            var stamp = row.Created is { } created ? created.ToFileTimeUtc() : 0L;
            if (family == AfInet)
            {
                Marshal.WriteInt32(at, 0, row.State);
                Marshal.WriteInt32(at, 4, V4(row.Local));
                Marshal.WriteInt32(at, 8, NetPort(row.LocalPort));
                Marshal.WriteInt32(at, 12, V4(row.Remote));
                Marshal.WriteInt32(at, 16, NetPort(row.RemotePort));
                Marshal.WriteInt32(at, 20, row.Pid);
                Marshal.WriteInt64(at, 24, stamp);
            }
            else
            {
                Marshal.Copy(IPAddress.Parse(row.Local).GetAddressBytes(), 0, at, 16);
                Marshal.WriteInt32(at, 20, NetPort(row.LocalPort));
                Marshal.Copy(IPAddress.Parse(row.Remote).GetAddressBytes(), 0, at + 24, 16);
                Marshal.WriteInt32(at, 44, NetPort(row.RemotePort));
                Marshal.WriteInt32(at, 48, row.State);
                Marshal.WriteInt32(at, 52, row.Pid);
                Marshal.WriteInt64(at, 56, stamp);
            }
        }
    }

    /// <summary>The port in the low two bytes, network byte order.</summary>
    public static int NetPort(int port) => ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);

    private static int V4(string address) => BitConverter.ToInt32(IPAddress.Parse(address).GetAddressBytes(), 0);
}
