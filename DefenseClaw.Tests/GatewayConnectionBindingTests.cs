using System.Net;
using System.Runtime.InteropServices;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-247: the bearer-token peer check is bound to the connection the token rides on. The TCP table is always synthetic here:
/// fake owner lookups and a synthetic table buffer, plus a loopback test listener that stands in for "whatever answered" - never the
/// live gateway port, never the live token.
/// </summary>
public class GatewayConnectionBindingTests
{
    private const string Token = "fixture-bearer-token-0123456789";
    private const string Bin = @"C:\fake\install\bin";
    private const string GatewayImage = Bin + @"\defenseclaw-gateway.exe";

    private sealed class FakeOwners : IPortOwnerInspector, IConnectionOwnerInspector
    {
        public List<PortOwner> Listeners { get; } = new();

        public Func<IPEndPoint, IPEndPoint, PortOwner?> Connection { get; set; } = (_, _) => null;

        public List<(IPEndPoint Server, IPEndPoint Client)> Lookups { get; } = new();

        public PortOwner? FindListener(int port) => Listeners.FirstOrDefault();

        public IReadOnlyList<PortOwner> FindListeners(int port) => Listeners.ToArray();

        public PortOwner? FindConnectionOwner(IPEndPoint server, IPEndPoint client)
        {
            lock (Lookups)
            {
                Lookups.Add((server, client));
            }

            return Connection(server, client);
        }
    }

    private static PortOwner Gateway(int pid = 4242) => new(pid, "defenseclaw-gateway", "127.0.0.1", 18970, GatewayImage);

    private static PortOwner Stranger(int pid = 9001) => new(pid, "SomeRandomServer", "127.0.0.1", 18970, @"C:\tools\server.exe");

    private static GatewayPeerVerifier Verifier(FakeOwners owners, string? dataDirectory = null, IEnumerable<string>? searchPath = null, Func<string, bool>? fileExists = null) =>
        new(
            new DefenseClawPaths(
                dataDirectory: dataDirectory ?? @"C:\fake\data",
                binDirectory: Bin,
                searchPath: searchPath ?? Array.Empty<string>(),
                fileExists: fileExists ?? (_ => false)),
            owners);

    // ---- trust is the installer's directory, not PATH ---------------------------------------------------

    [Fact]
    public void A_gateway_beside_the_one_PATH_resolves_to_is_no_longer_trusted()
    {
        var onPath = @"D:\tools\defenseclaw-gateway.exe";
        var verifier = Verifier(new FakeOwners(), searchPath: new[] { @"D:\tools" }, fileExists: p => p == onPath);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(new PortOwner(4242, "defenseclaw-gateway", "127.0.0.1", 18970, onPath)));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Gateway()));
    }

    // ---- every listener row counts ------------------------------------------------------------------------

    [Fact]
    public void A_second_listener_on_the_port_spoils_the_verdict_whichever_row_comes_first()
    {
        var owners = new FakeOwners();
        var check = Verifier(owners).ForPort(18970);

        owners.Listeners.Add(Gateway());
        Assert.Equal(PortOwnerTrust.Gateway, check());

        // SO_REUSEADDR on a wildcard-bound port: a second socket, listed after (or before) the real one.
        owners.Listeners.Add(Stranger());
        Assert.Equal(PortOwnerTrust.Other, check());

        owners.Listeners.Reverse();
        Assert.Equal(PortOwnerTrust.Other, check());
    }

    [Fact]
    public void Two_gateway_rows_stay_trusted_and_no_rows_stay_unknown()
    {
        var owners = new FakeOwners();
        var check = Verifier(owners).ForPort(18970);

        Assert.Equal(PortOwnerTrust.Unknown, check());

        owners.Listeners.Add(Gateway(1));
        owners.Listeners.Add(Gateway(2));
        Assert.Equal(PortOwnerTrust.Gateway, check());
    }

    [Fact]
    public void A_relay_row_beside_the_gateway_is_health_only()
    {
        var owners = new FakeOwners();
        owners.Listeners.Add(Gateway());
        owners.Listeners.Add(new PortOwner(7, "wslrelay", "0.0.0.0", 18970, @"C:\Windows\System32\wslrelay.exe"));

        Assert.Equal(PortOwnerTrust.WslRelay, Verifier(owners).ForPort(18970)());
    }

    // ---- the connection's own row ---------------------------------------------------------------------------

    [Fact]
    public void ForConnection_judges_the_owner_of_that_connections_row()
    {
        var owners = new FakeOwners();
        var server = new IPEndPoint(IPAddress.Loopback, 18970);
        var client = new IPEndPoint(IPAddress.Loopback, 50123);
        var check = Verifier(owners).ForConnection(18970);

        owners.Connection = (_, _) => Gateway();
        Assert.Equal(PortOwnerTrust.Gateway, check(server, client));

        owners.Connection = (_, _) => Stranger();
        Assert.Equal(PortOwnerTrust.Other, check(server, client));

        owners.Connection = (_, _) => new PortOwner(4242, "defenseclaw-gateway", "127.0.0.1", 18970, @"C:\Users\x\Downloads\defenseclaw-gateway.exe");
        Assert.Equal(PortOwnerTrust.Other, check(server, client));

        Assert.Equal(server, owners.Lookups[0].Server);
        Assert.Equal(client, owners.Lookups[0].Client);
    }

    [Fact]
    public void ForConnection_with_no_row_is_unknown_after_a_few_looks_and_fails_closed()
    {
        var owners = new FakeOwners();
        var check = Verifier(owners).ForConnection(18970);

        var trust = check(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123));

        Assert.Equal(PortOwnerTrust.Unknown, trust);
        Assert.Equal(GatewayPeerVerifier.ConnectionLookupAttempts, owners.Lookups.Count);
    }

    [Fact]
    public void ForConnection_with_no_connection_lookup_available_is_unknown()
    {
        var verifier = new GatewayPeerVerifier(
            new DefenseClawPaths(dataDirectory: @"C:\fake\data", binDirectory: Bin, searchPath: Array.Empty<string>(), fileExists: _ => false),
            new ListenersOnly());

        Assert.Equal(
            PortOwnerTrust.Unknown,
            verifier.ForConnection(18970)(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123)));
    }

    private sealed class ListenersOnly : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    // ---- the connect callback, over a loopback test listener --------------------------------------------------

    private static (GatewayClient Client, RawHttpListener Listener, List<(IPEndPoint Server, IPEndPoint Client)> Seen) Connect(
        Func<IPEndPoint, IPEndPoint, PortOwnerTrust> verify,
        RawHttpListener listener)
    {
        var seen = new List<(IPEndPoint, IPEndPoint)>();
        var client = GatewayClient.Create(
            listener.Port,
            () => new SecretValue(Token),
            verifyPeer: () => PortOwnerTrust.Gateway,
            verifyConnection: (server, peer) =>
            {
                lock (seen)
                {
                    seen.Add((server, peer));
                }

                return verify(server, peer);
            });
        return (client, listener, seen);
    }

    private static RawHttpListener HealthyListener() => new(request => request.Path switch
    {
        "/health" => RawHttpListener.Response(200, FixtureFiles.ReadText(FixtureFiles.Health)),
        "/status" => RawHttpListener.Response(200, FixtureFiles.ReadText(FixtureFiles.Status)),
        _ => RawHttpListener.Response(404, "{\"error\":\"not found\"}"),
    });

    [Fact]
    public async Task A_verified_connection_carries_the_token_and_the_check_saw_both_ends_of_it()
    {
        using var listener = HealthyListener();
        var (client, _, seen) = Connect((_, _) => PortOwnerTrust.Gateway, listener);
        using var _ = client;

        var result = await client.GetStatusAsync();

        Assert.True(result.IsOk);
        var status = Assert.Single(listener.Requests, r => r.Path == "/status");
        Assert.Equal($"Bearer {Token}", status.Authorization);
        var (server, peer) = Assert.Single(seen);
        Assert.Equal(listener.Port, server.Port);
        Assert.True(IPAddress.IsLoopback(server.Address));
        Assert.NotEqual(listener.Port, peer.Port);
    }

    [Theory]
    [InlineData(PortOwnerTrust.Other)]
    [InlineData(PortOwnerTrust.WslRelay)]
    public async Task A_connection_owned_by_anything_else_is_refused_before_a_byte_is_written(PortOwnerTrust trust)
    {
        using var listener = HealthyListener();
        var (client, _, _) = Connect((_, _) => trust, listener);
        using var _ = client;

        // The pre-check passes (the swapped-in listener answered /health and looked fine from the port), the connection's own row does not.
        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.PeerRefusedMessage, result.ErrorMessage);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(listener.Requests, r => r.Path == "/status");
    }

    [Fact]
    public async Task A_connection_that_cannot_be_tied_to_a_process_is_refused_and_says_so()
    {
        using var listener = HealthyListener();
        var (client, _, _) = Connect((_, _) => PortOwnerTrust.Unknown, listener);
        using var _ = client;

        var result = await client.GetAlertsAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.ConnectionUnverifiedMessage, result.ErrorMessage);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task A_swapped_in_listener_is_caught_on_its_own_connection_even_though_the_last_one_passed()
    {
        using var listener = HealthyListener();
        var trust = PortOwnerTrust.Gateway;
        var (client, _, seen) = Connect((_, _) => trust, listener);
        using var _ = client;

        Assert.True((await client.GetStatusAsync()).IsOk);

        trust = PortOwnerTrust.Other;
        var second = await client.GetStatusAsync();

        // Each authenticated request is its own connection, so the earlier verdict cannot be carried over to it.
        Assert.Equal(GatewayStatus.Error, second.Status);
        Assert.Equal(2, seen.Count);
        Assert.Single(listener.Requests, r => r.Authorization is not null);
    }

    [Fact]
    public async Task Health_is_not_held_to_the_connection_check_so_a_wsl_relay_still_reads()
    {
        using var listener = HealthyListener();
        var (client, _, seen) = Connect((_, _) => PortOwnerTrust.WslRelay, listener);
        using var _ = client;

        var health = await client.GetHealthAsync();

        Assert.True(health.IsOk);
        Assert.Empty(seen);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
    }

    // ---- the TCP-table read, over a synthetic buffer ------------------------------------------------------------

    private static uint NetPort(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    private static uint V4(string address) => BitConverter.ToUInt32(IPAddress.Parse(address).GetAddressBytes(), 0);

    private static PortOwnerInspector.TcpTableReader Table(params (uint Local, int LocalPort, uint Remote, int RemotePort, int Pid)[] rows) =>
        (IntPtr table, ref int size, int family) =>
        {
            if (family != 2)
            {
                size = 0;
                return 0;
            }

            var needed = 4 + (rows.Length * 24);
            if (table == IntPtr.Zero || size < needed)
            {
                size = needed;
                return 122;
            }

            Marshal.WriteInt32(table, rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                var at = table + 4 + (i * 24);
                Marshal.WriteInt32(at, 0, 5);
                Marshal.WriteInt32(at, 4, (int)rows[i].Local);
                Marshal.WriteInt32(at, 8, (int)NetPort(rows[i].LocalPort));
                Marshal.WriteInt32(at, 12, (int)rows[i].Remote);
                Marshal.WriteInt32(at, 16, (int)NetPort(rows[i].RemotePort));
                Marshal.WriteInt32(at, 20, rows[i].Pid);
            }

            return 0;
        };

    private static PortOwnerInspector Inspector(PortOwnerInspector.TcpTableReader connections) =>
        new((IntPtr table, ref int size, int family) => { size = 0; return 0; }, connections);

    [Fact]
    public void The_row_for_the_connection_is_matched_on_both_endpoints_and_the_far_side_wins()
    {
        // The client's own row (local = client) is the same connection seen from our side; only the row whose local end is the server counts.
        var me = Environment.ProcessId;
        var inspector = Inspector(Table(
            (V4("127.0.0.1"), 50123, V4("127.0.0.1"), 18970, 1111),
            (V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50123, me),
            (V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50999, 2222)));

        var owner = inspector.FindConnectionOwner(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123));

        Assert.NotNull(owner);
        Assert.Equal(me, owner!.Pid);
    }

    [Fact]
    public void An_ipv4_mapped_endpoint_matches_the_ipv4_row()
    {
        var me = Environment.ProcessId;
        var inspector = Inspector(Table((V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50123, me)));

        var owner = inspector.FindConnectionOwner(
            new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 18970),
            new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 50123));

        Assert.Equal(me, owner?.Pid);
    }

    [Fact]
    public void No_row_for_the_connection_is_no_owner()
    {
        var inspector = Inspector(Table((V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50999, Environment.ProcessId)));

        Assert.Null(inspector.FindConnectionOwner(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123)));
    }

    [Fact]
    public void Rows_that_name_two_different_owners_for_one_connection_are_no_answer()
    {
        var inspector = Inspector(Table(
            (V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50123, Environment.ProcessId),
            (V4("127.0.0.1"), 18970, V4("127.0.0.1"), 50123, 2222)));

        Assert.Null(inspector.FindConnectionOwner(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123)));
    }

    [Fact]
    public void An_unreadable_connection_table_is_no_owner()
    {
        var inspector = Inspector((IntPtr table, ref int size, int family) => 5);

        Assert.Null(inspector.FindConnectionOwner(new IPEndPoint(IPAddress.Loopback, 18970), new IPEndPoint(IPAddress.Loopback, 50123)));
    }
}
