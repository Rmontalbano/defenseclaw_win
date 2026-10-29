using System.Net;
using System.Net.Sockets;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-392, finding 1. Windows keeps naming a listening socket by the pid of the process that opened it after that process exits, while a child
/// it handed the socket to still serves it. Another account could bind the port, hand the socket on and exit; if this account's gateway (started,
/// failing to bind) then got that pid, every lookup by pid would describe the real gateway while the token went to the other account's socket.
/// The inspector now reads each socket's creation time (the <c>OWNER_MODULE</c> tables) and each owner's creation time (the process list, no
/// handle), and the verifier refuses an owner younger than its socket by more than one clock tick, or one whose times are unknown. A connection is
/// held to its pid's listener rows in both tables, and refused when either cannot be read. The TCP tables and process lists here are
/// synthetic, except two checks against this test process's own loopback sockets.
/// </summary>
public class GatewayPeerStaleOwnerTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string Installed = Bin + @"\defenseclaw-gateway.exe";
    private const int Port = 18970;
    private const int GatewayPid = 4242;
    private const int AppSession = 2;

    private static readonly DateTime Started = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private static DefenseClawPaths Paths() =>
        new(dataDirectory: @"C:\fake\data", binDirectory: Bin, searchPath: Array.Empty<string>(), fileExists: _ => false);

    /// <summary>The process the list shows for the gateway's pid: the installed gateway, in this app's session, created at <paramref name="created"/>.</summary>
    private static PortOwnerInspector.ProcessReader Process(DateTime? created, List<string>? calls = null) =>
        pid =>
        {
            calls?.Add("process:" + pid);
            return pid == GatewayPid ? new ProcessFacts(pid, GatewayPeerVerifier.GatewayProcessName, AppSession, created) : null;
        };

    private static PortOwnerInspector.ImagePathReader Image(List<string>? calls = null) =>
        pid =>
        {
            calls?.Add("image:" + pid);
            return pid == GatewayPid ? new ProcessImage(Installed, WithoutHandle: false) : null;
        };

    private static PortOwnerInspector Inspector(
        ModuleTcpTables.Row[] listeners,
        PortOwnerInspector.ProcessReader process,
        ModuleTcpTables.Row[]? connections = null,
        List<string>? calls = null) =>
        new(ModuleTcpTables.Reader(listeners), ModuleTcpTables.Reader(connections ?? []), Image(calls), process);

    private static GatewayPeerVerifier Verifier(PortOwnerInspector inspector) =>
        new(Paths(), inspector, currentSessionId: AppSession, userProfile: PeerFixtures.FakeProfile);

    // ---- listeners --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_gateway_that_opened_its_own_socket_is_trusted()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started.AddMilliseconds(350))], Process(Started));

        var owner = Assert.Single(inspector.FindListeners(Port));

        Assert.Equal(Started, owner.ProcessCreatedUtc);
        Assert.Equal(Started.AddMilliseconds(350), owner.SocketCreatedUtc);
        Assert.True(owner.PidMatchesSocket);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(inspector).ForPort(Port)());
    }

    [Fact]
    public void Equal_times_count_so_the_clocks_granularity_never_refuses_the_real_gateway()
    {
        // FILETIME counts 100 ns, but the clock behind both times moves in coarser steps: a socket opened in the same tick the process started.
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], Process(Started));

        Assert.Equal(PortOwnerTrust.Gateway, Verifier(inspector).ForPort(Port)());
    }

    [Fact]
    public void A_socket_older_than_the_process_that_holds_its_pid_is_refused_and_says_why()
    {
        // Another account bound the port at 08:00, handed the socket to a child and exited; this account's gateway started at 08:05 with the
        // reused pid and could not bind. Name, path and session all describe the real gateway, and are not the socket's.
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], Process(Started.AddMinutes(5)));
        var verifier = Verifier(inspector);

        var owner = Assert.Single(inspector.FindListeners(Port));

        Assert.False(owner.PidMatchesSocket);
        Assert.Equal(PortOwnerTrust.Other, verifier.ForPort(Port)());
        Assert.Equal(
            "defenseclaw-gateway (pid 4242) did not open the socket on the API port: the socket is older than the process, so another process opened it with that pid",
            verifier.DescribeUntrusted(owner));
    }

    [Fact]
    public void A_process_up_to_one_clock_tick_younger_than_its_socket_still_counts()
    {
        // One stamp rounded to the 15.6 ms tick, the other precise: a gateway that binds within a tick of starting can look younger by that much.
        Assert.Equal(TimeSpan.FromMilliseconds(16), PortOwner.ClockSlack);

        var withinOneTick = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], Process(Started + PortOwner.ClockSlack));

        Assert.True(Assert.Single(withinOneTick.FindListeners(Port)).PidMatchesSocket);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(withinOneTick).ForPort(Port)());
    }

    [Fact]
    public void A_process_younger_than_its_socket_by_more_than_one_tick_is_refused()
    {
        var justPast = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], Process(Started + PortOwner.ClockSlack + TimeSpan.FromTicks(1)));

        Assert.False(Assert.Single(justPast.FindListeners(Port)).PidMatchesSocket);
        Assert.Equal(PortOwnerTrust.Other, Verifier(justPast).ForPort(Port)());
    }

    [Fact]
    public void A_socket_with_no_creation_time_is_refused()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, created: null)], Process(Started));
        var verifier = Verifier(inspector);

        var owner = Assert.Single(inspector.FindListeners(Port));

        Assert.Null(owner.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(owner));
        Assert.Contains("Windows did not say when the process started or when its socket on the API port was opened", verifier.DescribeUntrusted(owner), StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_with_no_creation_time_is_refused()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], Process(created: null));

        var owner = Assert.Single(inspector.FindListeners(Port));

        Assert.Null(owner.ProcessCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).Classify(owner));
    }

    [Fact]
    public void A_pid_the_process_list_no_longer_has_is_refused()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started)], _ => null);

        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForPort(Port)());
    }

    [Fact]
    public void A_hand_built_owner_without_times_is_refused_however_good_the_rest_is()
    {
        // Any inspector that gives no times (or a test that forgets them) gets nothing: the times are proof, not decoration.
        var owner = new PortOwner(GatewayPid, GatewayPeerVerifier.GatewayProcessName, "127.0.0.1", Port, Installed, AppSession);

        Assert.Equal(PortOwnerTrust.Other, Verifier(Inspector([], Process(Started))).Classify(owner));
    }

    [Fact]
    public void One_stale_row_among_good_ones_spoils_the_port()
    {
        var inspector = Inspector(
            [
                ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1)),
                ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(-30), "0.0.0.0"),
            ],
            Process(Started));

        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForPort(Port)());
    }

    [Fact]
    public void The_image_is_read_before_the_process_list()
    {
        // The list is the last look: a process it shows alive and no younger than the socket held the pid while the image was read.
        var calls = new List<string>();
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1))], Process(Started, calls), calls: calls);

        _ = inspector.FindListener(Port);

        Assert.Equal(new[] { "image:" + GatewayPid, "process:" + GatewayPid }, calls);
    }

    [Fact]
    public void An_ipv6_listener_row_carries_its_creation_time()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(2), "::1")], Process(Started));

        var owner = Assert.Single(inspector.FindListeners(Port));

        Assert.Equal("::1", owner.LocalAddress);
        Assert.Equal(Started.AddSeconds(2), owner.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(inspector).Classify(owner));
    }

    // ---- connections ------------------------------------------------------------------------------------------------------

    private static readonly IPEndPoint Server = new(IPAddress.Loopback, Port);
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 50123);

    private static ModuleTcpTables.Row Connection(DateTime? created, int pid = GatewayPid) =>
        new("127.0.0.1", Port, "127.0.0.1", Client.Port, pid, created, ModuleTcpTables.Established);

    [Fact]
    public void A_connection_is_held_to_the_listener_it_came_from_not_only_to_its_own_new_row()
    {
        // The accepted connection is minutes younger than the process, but the listener that carries the same pid is older than the process:
        // the pid was reused, whoever accepted.
        var inspector = Inspector(
            [ModuleTcpTables.Listener(Port, GatewayPid, Started)],
            Process(Started.AddMinutes(5)),
            [Connection(Started.AddMinutes(10))]);

        var owner = inspector.FindConnectionOwner(Server, Client);

        Assert.NotNull(owner);
        Assert.Equal(Started, owner!.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Fact]
    public void A_connection_to_the_gateways_own_listener_is_trusted_and_carries_the_earliest_time()
    {
        var inspector = Inspector(
            [ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1))],
            Process(Started),
            [Connection(Started.AddMinutes(10))]);

        var owner = inspector.FindConnectionOwner(Server, Client);

        Assert.Equal(Started.AddSeconds(1), owner?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Fact]
    public void A_connection_older_than_its_process_is_refused_even_without_a_listener_row()
    {
        var inspector = Inspector([], Process(Started.AddMinutes(5)), [Connection(Started)]);

        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Fact]
    public void A_connection_without_its_own_time_is_refused()
    {
        var inspector = Inspector([ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1))], Process(Started), [Connection(created: null)]);

        Assert.Null(inspector.FindConnectionOwner(Server, Client)?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Fact]
    public void An_ipv4_connection_is_held_to_its_pids_dual_mode_listener_in_the_ipv6_table()
    {
        // A dual-mode [::] listener sits in the IPv6 table; the IPv4 connections it accepts sit in the IPv4 one. Its old row still counts.
        var stale = Inspector(
            [ModuleTcpTables.Listener(Port, GatewayPid, Started, "::")],
            Process(Started.AddMinutes(5)),
            [Connection(Started.AddMinutes(10))]);

        Assert.Equal(Started, stale.FindConnectionOwner(Server, Client)?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(stale).ForConnection(Port)(Server, Client));

        var own = Inspector(
            [ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1), "::")],
            Process(Started),
            [Connection(Started.AddMinutes(10))]);

        Assert.Equal(Started.AddSeconds(1), own.FindConnectionOwner(Server, Client)?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(own).ForConnection(Port)(Server, Client));
    }

    [Theory]
    [InlineData(ModuleTcpTables.AfInet)]
    [InlineData(ModuleTcpTables.AfInet6)]
    public void A_connection_is_refused_when_either_listener_table_cannot_be_read(int unreadable)
    {
        // The readable table has the gateway's own good listener; the rows the other one hides could be the old ones.
        var listeners = ModuleTcpTables.Reader(
            ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1)),
            ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1), "::"));
        var inspector = new PortOwnerInspector(
            (IntPtr table, ref int size, int family) => family == unreadable ? 31u : listeners(table, ref size, family),
            ModuleTcpTables.Reader(Connection(Started.AddMinutes(10))),
            Image(),
            Process(Started),
            ipv6StackPresent: () => true);

        var owner = inspector.FindConnectionOwner(Server, Client);

        Assert.NotNull(owner);
        Assert.Null(owner!.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    /// <summary>The gateway's good IPv4 listener and connection, with the IPv6 listener table failing with <paramref name="ipv6Status"/>.</summary>
    private static PortOwnerInspector WithoutIpv6Table(uint ipv6Status, bool stackPresent, uint ipv4Status = 0)
    {
        var listeners = ModuleTcpTables.Reader(ModuleTcpTables.Listener(Port, GatewayPid, Started.AddSeconds(1)));
        return new PortOwnerInspector(
            (IntPtr table, ref int size, int family) => family switch
            {
                ModuleTcpTables.AfInet6 => ipv6Status,
                _ when ipv4Status != 0 => ipv4Status,
                _ => listeners(table, ref size, family),
            },
            ModuleTcpTables.Reader(Connection(Started.AddMinutes(10))),
            Image(),
            Process(Started),
            ipv6StackPresent: () => stackPresent);
    }

    [Theory]
    [InlineData(50u)]
    [InlineData(87u)]
    [InlineData(10047u)]
    public void On_a_PC_without_an_IPv6_stack_the_IPv6_listener_table_is_empty_not_unread(uint status)
    {
        // Nothing can listen on IPv6 where no IPv6 socket can be made: refusing here would refuse every connection on such a PC.
        var inspector = WithoutIpv6Table(status, stackPresent: false);

        Assert.Equal(Started.AddSeconds(1), inspector.FindConnectionOwner(Server, Client)?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Theory]
    [InlineData(50u, true)]
    [InlineData(87u, true)]
    [InlineData(10047u, true)]
    [InlineData(31u, false)]
    [InlineData(122u, false)]
    public void An_IPv6_table_that_fails_on_a_PC_with_the_stack_or_for_another_reason_is_unread_and_refused(uint status, bool stackPresent)
    {
        // A "family not available" answer where IPv6 sockets can be made, or any other failure (122: still outgrowing its buffer after every round).
        var inspector = WithoutIpv6Table(status, stackPresent);

        Assert.Null(inspector.FindConnectionOwner(Server, Client)?.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Theory]
    [InlineData(50u)]
    [InlineData(87u)]
    [InlineData(10047u)]
    public void A_missing_IPv4_listener_table_is_always_unread_and_refused(uint status)
    {
        // The gateway listens on 127.0.0.1: an IPv4 table Windows will not give is never "nothing there".
        var inspector = WithoutIpv6Table(ipv6Status: status, stackPresent: false, ipv4Status: status);

        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    [Fact]
    public void A_connection_whose_listener_table_cannot_be_read_has_no_time_and_is_refused()
    {
        var inspector = new PortOwnerInspector(
            (IntPtr table, ref int size, int family) => 87,
            ModuleTcpTables.Reader(Connection(Started.AddMinutes(10))),
            Image(),
            Process(Started));

        var owner = inspector.FindConnectionOwner(Server, Client);

        Assert.NotNull(owner);
        Assert.Null(owner!.SocketCreatedUtc);
        Assert.Equal(PortOwnerTrust.Other, Verifier(inspector).ForConnection(Port)(Server, Client));
    }

    // ---- this test process's own sockets: the real tables and the real process list --------------------------------------

    [Fact]
    public void The_process_list_names_this_process_as_Process_does_and_knows_when_it_started()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var self = System.Diagnostics.Process.GetCurrentProcess();

        var facts = ProcessList.Read(Environment.ProcessId);

        Assert.NotNull(facts);
        Assert.Equal(self.ProcessName, facts!.Name);
        Assert.Equal(self.SessionId, facts.SessionId);
        Assert.NotNull(facts.CreatedUtc);
        Assert.InRange((facts.CreatedUtc!.Value - self.StartTime.ToUniversalTime()).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Null(ProcessList.Read(int.MaxValue - 3));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void A_socket_this_process_opens_now_is_younger_than_the_process(string address)
    {
        if (!OperatingSystem.IsWindows() || (address == "::1" && !Socket.OSSupportsIPv6))
        {
            return;
        }

        var before = DateTime.UtcNow.AddMinutes(-1);
        var listener = new TcpListener(IPAddress.Parse(address), 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = Assert.Single(new PortOwnerInspector().FindListeners(port), o => o.Pid == Environment.ProcessId);

            Assert.NotNull(owner.SocketCreatedUtc);
            Assert.InRange(owner.SocketCreatedUtc!.Value, before, DateTime.UtcNow.AddMinutes(1));
            Assert.True(owner.PidMatchesSocket, $"process {owner.ProcessCreatedUtc:O}, socket {owner.SocketCreatedUtc:O}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_connection_to_a_socket_this_process_listens_on_is_tied_to_this_process_with_its_times()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();

            PortOwner? owner = null;
            for (var attempt = 0; attempt < 20 && owner is null; attempt++)
            {
                owner = new PortOwnerInspector().FindConnectionOwner((IPEndPoint)listener.LocalEndpoint, (IPEndPoint)client.Client.LocalEndPoint!);
                if (owner is null)
                {
                    await Task.Delay(25);
                }
            }

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner!.Pid);
            Assert.True(owner.PidMatchesSocket, $"process {owner.ProcessCreatedUtc:O}, socket {owner.SocketCreatedUtc:O}");
        }
        finally
        {
            listener.Stop();
        }
    }
}
