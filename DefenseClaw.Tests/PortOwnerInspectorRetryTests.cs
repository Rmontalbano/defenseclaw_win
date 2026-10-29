using System.Net;
using System.Net.Sockets;
using DefenseClaw.Core.Net;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>GetExtendedTcpTable</c> is asked twice — how big, then the table — and a socket that opens in between
/// makes the second answer <c>ERROR_INSUFFICIENT_BUFFER</c> again. That used to read as "no table", so the
/// port had no owner for one poll and the state flickered.
/// </summary>
public class PortOwnerInspectorRetryTests
{
    private const uint NoError = ModuleTcpTables.NoError;
    private const uint InsufficientBuffer = ModuleTcpTables.InsufficientBuffer;
    private const int AfInet = ModuleTcpTables.AfInet;

    /// <summary>One MIB_TCPROW_OWNER_MODULE after the table's header (CUST-392).</summary>
    private static readonly int OneRowTable = ModuleTcpTables.Size(AfInet, 1);

    private static void WriteTable(IntPtr table, int port, int pid) =>
        ModuleTcpTables.Write(table, AfInet, new[] { ModuleTcpTables.Listener(port, pid, DateTime.UtcNow) });

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
                size = calls.Count(c => c == "size") == 1 ? 8 : OneRowTable;
                return InsufficientBuffer;
            }

            calls.Add("fetch");
            if (calls.Count(c => c == "fetch") == 1)
            {
                // A socket appeared after we sized it: the buffer is now too small.
                size = OneRowTable;
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

            size = OneRowTable;
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
