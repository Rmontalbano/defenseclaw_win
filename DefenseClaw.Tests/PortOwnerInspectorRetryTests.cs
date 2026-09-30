using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DefenseClaw.Core.Net;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>GetExtendedTcpTable</c> is asked twice — how big, then the table — and a socket that opens in between
/// makes the second answer <c>ERROR_INSUFFICIENT_BUFFER</c> again. That used to read as "no table", so the
/// port had no owner for one poll and the state flickered.
/// </summary>
public class PortOwnerInspectorRetryTests
{
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;
    private const int AfInet = 2;

    /// <summary>MIB_TCPROW_OWNER_PID is six dwords.</summary>
    private const int RowSize = 24;

    private static void WriteTable(IntPtr table, int port, int pid)
    {
        Marshal.WriteInt32(table, 0, 1);                                   // dwNumEntries
        Marshal.WriteInt32(table, 4, 2);                                   // state: listen
        Marshal.WriteInt32(table, 8, 0x0100007F);                          // 127.0.0.1
        Marshal.WriteInt32(table, 12, ((port & 0xFF) << 8) | (port >> 8)); // network byte order
        Marshal.WriteInt32(table, 16, 0);
        Marshal.WriteInt32(table, 20, 0);
        Marshal.WriteInt32(table, 24, pid);
    }

    [Fact]
    public void A_table_that_grows_between_the_size_query_and_the_fetch_is_read_on_the_next_round()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var calls = new List<string>();
        var inspector = new PortOwnerInspector((IntPtr table, ref int size, int family) =>
        {
            if (family != AfInet)
            {
                return NoError;
            }

            if (table == IntPtr.Zero)
            {
                // Sizing call. The first time it reports a small table; by the second it has grown.
                calls.Add("size");
                size = calls.Count(c => c == "size") == 1 ? 8 : 4 + RowSize;
                return InsufficientBuffer;
            }

            calls.Add("fetch");
            if (calls.Count(c => c == "fetch") == 1)
            {
                // A socket appeared after we sized it: the buffer is now too small.
                size = 4 + RowSize;
                return InsufficientBuffer;
            }

            WriteTable(table, port: 4711, pid: Environment.ProcessId);
            return NoError;
        });

        var owner = inspector.FindListener(4711);

        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.Equal(4711, owner.Port);
        Assert.Equal(new[] { "size", "fetch", "size", "fetch" }, calls);
    }

    [Fact]
    public void A_table_that_never_settles_gives_up_after_a_bounded_number_of_rounds_without_throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fetches = 0;
        var inspector = new PortOwnerInspector((IntPtr table, ref int size, int family) =>
        {
            if (table != IntPtr.Zero)
            {
                fetches++;
            }

            size = 4 + RowSize;
            return InsufficientBuffer;
        });

        Assert.Null(inspector.FindListener(4711));

        // Both address families were tried, each at most MaxTableReadAttempts times.
        Assert.Equal(2 * PortOwnerInspector.MaxTableReadAttempts, fetches);
    }

    [Fact]
    public void An_error_that_is_not_a_size_problem_is_not_retried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var calls = 0;
        var inspector = new PortOwnerInspector((IntPtr table, ref int size, int family) =>
        {
            calls++;
            return 87; // ERROR_INVALID_PARAMETER
        });

        Assert.Null(inspector.FindListener(4711));
        Assert.Equal(2, calls); // one size query per address family, nothing more
    }

    [Fact]
    public void The_owner_carries_the_path_of_its_executable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = new PortOwnerInspector().FindListener(port);

            Assert.NotNull(owner);
            Assert.False(string.IsNullOrEmpty(owner!.ImagePath));
            Assert.Equal(
                Path.GetFileName(Environment.ProcessPath),
                Path.GetFileName(owner.ImagePath),
                ignoreCase: true);
        }
        finally
        {
            listener.Stop();
        }
    }
}
